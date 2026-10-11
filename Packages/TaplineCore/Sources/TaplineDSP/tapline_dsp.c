#include "tapline_dsp.h"

#include <float.h>
#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define TL_DIRTY 4
#define TL_LIMITER_MAX_LOOKAHEAD 1024
#define TL_XFEED_RING 128  // power of two

struct tl_kernel {
    // Parameter triple buffer. The writer owns `back`, the reader owns `front`,
    // `middle` is exchanged atomically and carries a dirty flag.
    tl_params slots[3];
    _Atomic int middle;
    int back;
    int front;

    tl_layout layout;
    double sample_rate;

    _Atomic float source_gain_target[TL_MAX_SOURCES];
    float source_gain_current[TL_MAX_SOURCES];

    // EQ state: [stage][channel][z1, z2]
    double z[TL_MAX_STAGES][2][2];
    int32_t was_active[TL_MAX_STAGES];
    float preamp_current;
    float output_current;
    float balance_current;
    float width_current;

    float buf_l[TL_MAX_FRAMES];
    float buf_r[TL_MAX_FRAMES];

    // Crossfeed
    float xf_ring_l[TL_XFEED_RING];
    float xf_ring_r[TL_XFEED_RING];
    uint32_t xf_pos;
    uint32_t xf_delay;
    float xf_coef;
    float xf_lp_l, xf_lp_r;
    float xf_amount_current;

    // Limiter (always in the path so latency stays constant)
    float lim_ring_l[TL_LIMITER_MAX_LOOKAHEAD];
    float lim_ring_r[TL_LIMITER_MAX_LOOKAHEAD];
    uint32_t lim_pos;
    uint32_t lim_lookahead;
    uint32_t lim_hold;
    float lim_held;
    float lim_gain;
    float lim_attack;
    float lim_release;

    // Meters, stored as float bit patterns (non-negative floats order like uints).
    _Atomic uint32_t m_peak_l, m_peak_r, m_rms_l, m_rms_r, m_gr;
    _Atomic uint32_t m_clips;
    _Atomic uint64_t m_cycles;
    _Atomic uint32_t m_source_peak[TL_MAX_SOURCES];

    // Analyzer rings
    float ana_pre[TL_ANALYZER_SIZE];
    float ana_post[TL_ANALYZER_SIZE];
    _Atomic uint64_t ana_pos;
};

static inline uint32_t f2u(float v) { uint32_t u; memcpy(&u, &v, 4); return u; }
static inline float u2f(uint32_t u) { float v; memcpy(&v, &u, 4); return v; }

static inline void atomic_max_float(_Atomic uint32_t *slot, float value) {
    // NaN reads as +inf so meters and the runaway guard see it instead of dropping it.
    if (isnan(value)) value = INFINITY;
    if (!(value > 0.0f)) return;
    uint32_t next = f2u(value);
    uint32_t current = atomic_load_explicit(slot, memory_order_relaxed);
    while (next > current &&
           !atomic_compare_exchange_weak_explicit(slot, &current, next,
                                                  memory_order_relaxed, memory_order_relaxed)) {
    }
}

static inline float take_float(_Atomic uint32_t *slot) {
    return u2f(atomic_exchange_explicit(slot, 0, memory_order_relaxed));
}

void tl_params_init(tl_params *p) {
    memset(p, 0, sizeof(*p));
    for (int i = 0; i < TL_MAX_STAGES; i++) {
        p->stages[i].b0 = 1.0;
    }
    p->preamp = 1.0f;
    p->output_gain = 1.0f;
    p->width = 1.0f;
    p->limiter_ceiling = 1.0f;
    p->eq_enabled = 1;
}

tl_kernel *tl_kernel_create(void) {
    tl_kernel *k = calloc(1, sizeof(tl_kernel));
    if (!k) return NULL;
    for (int i = 0; i < 3; i++) tl_params_init(&k->slots[i]);
    k->front = 0;
    atomic_store(&k->middle, 1);
    k->back = 2;
    for (int i = 0; i < TL_MAX_SOURCES; i++) {
        atomic_store(&k->source_gain_target[i], 1.0f);
        k->source_gain_current[i] = 1.0f;
    }
    tl_layout empty = { 0 };
    empty.out_buffer_l = -1;
    empty.out_buffer_r = -1;
    tl_kernel_configure(k, 48000.0, &empty);
    return k;
}

void tl_kernel_destroy(tl_kernel *k) {
    free(k);
}

void tl_kernel_configure(tl_kernel *k, double sample_rate, const tl_layout *layout) {
    if (sample_rate < 8000.0) sample_rate = 48000.0;
    k->sample_rate = sample_rate;
    k->layout = *layout;
    if (k->layout.source_count > TL_MAX_SOURCES) k->layout.source_count = TL_MAX_SOURCES;
    if (k->layout.source_count < 0) k->layout.source_count = 0;

    memset(k->z, 0, sizeof(k->z));
    memset(k->was_active, 0, sizeof(k->was_active));

    const tl_params *p = &k->slots[k->front];
    k->preamp_current = p->preamp;
    k->output_current = p->output_gain;
    k->balance_current = p->balance;
    k->width_current = p->width;
    k->xf_amount_current = p->crossfeed_enabled ? p->crossfeed : 0.0f;
    for (int i = 0; i < TL_MAX_SOURCES; i++) {
        k->source_gain_current[i] = atomic_load(&k->source_gain_target[i]);
    }

    // Crossfeed: ~300 us interaural delay, ~700 Hz one-pole low-pass on the cross path.
    memset(k->xf_ring_l, 0, sizeof(k->xf_ring_l));
    memset(k->xf_ring_r, 0, sizeof(k->xf_ring_r));
    k->xf_pos = 0;
    uint32_t delay = (uint32_t)lround(0.0003 * sample_rate);
    if (delay < 1) delay = 1;
    if (delay >= TL_XFEED_RING) delay = TL_XFEED_RING - 1;
    k->xf_delay = delay;
    k->xf_coef = (float)(1.0 - exp(-2.0 * M_PI * 700.0 / sample_rate));
    k->xf_lp_l = k->xf_lp_r = 0.0f;

    // Limiter: 1.5 ms look-ahead, attack reaches ~95% within the look-ahead, 120 ms release.
    memset(k->lim_ring_l, 0, sizeof(k->lim_ring_l));
    memset(k->lim_ring_r, 0, sizeof(k->lim_ring_r));
    uint32_t lookahead = (uint32_t)lround(0.0015 * sample_rate);
    if (lookahead < 1) lookahead = 1;
    if (lookahead > TL_LIMITER_MAX_LOOKAHEAD) lookahead = TL_LIMITER_MAX_LOOKAHEAD;
    k->lim_lookahead = lookahead;
    k->lim_pos = 0;
    k->lim_hold = 0;
    k->lim_held = 1.0f;
    k->lim_gain = 1.0f;
    k->lim_attack = (float)exp(-3.0 / (double)lookahead);
    k->lim_release = (float)exp(-1.0 / (0.120 * sample_rate));
}

// Non-finite parameters would latch the gain ramps and filter states: replace them with
// neutral values here, on the writer's thread, so the IO thread never has to check.
static void sanitize_params(tl_params *p) {
    for (int i = 0; i < TL_MAX_STAGES; i++) {
        tl_biquad *c = &p->stages[i];
        if (!isfinite(c->b0) || !isfinite(c->b1) || !isfinite(c->b2) || !isfinite(c->a1) || !isfinite(c->a2)) {
            c->active = 0;
        }
    }
    if (!isfinite(p->preamp)) p->preamp = 0.0f;
    if (!isfinite(p->output_gain)) p->output_gain = 0.0f;
    if (!isfinite(p->balance)) p->balance = 0.0f;
    if (!isfinite(p->width)) p->width = 1.0f;
    if (!isfinite(p->crossfeed)) p->crossfeed = 0.0f;
    if (!(p->limiter_ceiling > 0.0f && p->limiter_ceiling <= 1.0f)) p->limiter_ceiling = 1.0f;
}

void tl_kernel_set_params(tl_kernel *k, const tl_params *params) {
    k->slots[k->back] = *params;
    sanitize_params(&k->slots[k->back]);
    int previous = atomic_exchange_explicit(&k->middle, k->back | TL_DIRTY, memory_order_acq_rel);
    k->back = previous & 3;
}

void tl_kernel_set_source_gain(tl_kernel *k, int32_t source, float gain) {
    if (source < 0 || source >= TL_MAX_SOURCES) return;
    if (!(gain >= 0.0f) || !isfinite(gain)) gain = 0.0f;
    atomic_store_explicit(&k->source_gain_target[source], gain, memory_order_relaxed);
}

void tl_kernel_read_meters(tl_kernel *k, tl_meters *out) {
    out->peak_l = take_float(&k->m_peak_l);
    out->peak_r = take_float(&k->m_peak_r);
    out->rms_l = take_float(&k->m_rms_l);
    out->rms_r = take_float(&k->m_rms_r);
    out->gain_reduction_db = take_float(&k->m_gr);
    out->clipped_samples = atomic_exchange_explicit(&k->m_clips, 0, memory_order_relaxed);
    out->io_cycles = atomic_load_explicit(&k->m_cycles, memory_order_relaxed);
    for (int i = 0; i < TL_MAX_SOURCES; i++) {
        out->source_peak[i] = take_float(&k->m_source_peak[i]);
    }
}

uint64_t tl_kernel_copy_analyzer(tl_kernel *k, int32_t which, float *dst, uint32_t count) {
    if (count > TL_ANALYZER_SIZE) count = TL_ANALYZER_SIZE;
    const float *ring = which == 0 ? k->ana_pre : k->ana_post;
    uint64_t end = atomic_load_explicit(&k->ana_pos, memory_order_acquire);
    uint64_t start = end >= count ? end - count : 0;
    uint32_t available = (uint32_t)(end - start);
    uint32_t missing = count - available;
    if (missing) memset(dst, 0, missing * sizeof(float));
    for (uint32_t i = 0; i < available; i++) {
        dst[missing + i] = ring[(start + i) & (TL_ANALYZER_SIZE - 1)];
    }
    return end;
}

// MARK: - Processing helpers

static void run_biquads(tl_kernel *k, const tl_params *p, uint32_t frames) {
    for (int s = 0; s < TL_MAX_STAGES; s++) {
        const tl_biquad *c = &p->stages[s];
        if (!c->active) {
            k->was_active[s] = 0;
            continue;
        }
        if (!k->was_active[s]) {
            memset(k->z[s], 0, sizeof(k->z[s]));
            k->was_active[s] = 1;
        }
        const double b0 = c->b0, b1 = c->b1, b2 = c->b2, a1 = c->a1, a2 = c->a2;
        for (int ch = 0; ch < 2; ch++) {
            if ((c->channels == 1 && ch == 1) || (c->channels == 2 && ch == 0)) {
                k->z[s][ch][0] = k->z[s][ch][1] = 0;
                continue;
            }
            float *x = ch == 0 ? k->buf_l : k->buf_r;
            double z1 = k->z[s][ch][0];
            double z2 = k->z[s][ch][1];
            for (uint32_t i = 0; i < frames; i++) {
                const double in = x[i];
                const double out = b0 * in + z1;
                z1 = b1 * in - a1 * out + z2;
                z2 = b2 * in - a2 * out;
                x[i] = (float)out;
            }
            // Keep denormals and runaway states out of the recursion.
            if (!isfinite(z1) || !isfinite(z2)) { z1 = 0; z2 = 0; }
            if (fabs(z1) < 1e-30) z1 = 0;
            if (fabs(z2) < 1e-30) z2 = 0;
            k->z[s][ch][0] = z1;
            k->z[s][ch][1] = z2;
        }
    }
}

static inline void apply_gain_ramp(float *x, float *y, uint32_t frames, float *current, float target) {
    float g = *current;
    if (fabsf(target - g) < 1e-6f) {
        if (g != 1.0f) {
            for (uint32_t i = 0; i < frames; i++) { x[i] *= g; y[i] *= g; }
        }
        *current = target;
        return;
    }
    const float step = (target - g) / (float)frames;
    for (uint32_t i = 0; i < frames; i++) {
        g += step;
        x[i] *= g;
        y[i] *= g;
    }
    *current = target;
}

// Clamps to +-limit; NaN and infinities become silence.
static inline float guard_sample(float x, float limit) {
    if (fabsf(x) <= limit) return x;
    return isfinite(x) ? copysignf(limit, x) : 0.0f;
}

// MARK: - IOProc

OSStatus tl_ioproc(AudioObjectID device,
                   const AudioTimeStamp *now,
                   const AudioBufferList *input,
                   const AudioTimeStamp *input_time,
                   AudioBufferList *output,
                   const AudioTimeStamp *output_time,
                   void *client_data) {
    (void)device; (void)now; (void)input_time; (void)output_time;
    tl_kernel *k = (tl_kernel *)client_data;
    if (!k || !output) return noErr;

    // Pick up the newest parameter block, if any.
    if (atomic_load_explicit(&k->middle, memory_order_acquire) & TL_DIRTY) {
        int previous = atomic_exchange_explicit(&k->middle, k->front, memory_order_acq_rel);
        k->front = previous & 3;
    }
    const tl_params *p = &k->slots[k->front];
    const tl_layout *L = &k->layout;

    for (UInt32 b = 0; b < output->mNumberBuffers; b++) {
        if (output->mBuffers[b].mData) memset(output->mBuffers[b].mData, 0, output->mBuffers[b].mDataByteSize);
    }
    atomic_fetch_add_explicit(&k->m_cycles, 1, memory_order_relaxed);

    if (L->out_buffer_l < 0 || (UInt32)L->out_buffer_l >= output->mNumberBuffers) return noErr;
    AudioBuffer *ob_l = &output->mBuffers[L->out_buffer_l];
    if (!ob_l->mData || ob_l->mNumberChannels == 0) return noErr;
    uint32_t frames = ob_l->mDataByteSize / (uint32_t)(sizeof(float) * ob_l->mNumberChannels);
    AudioBuffer *ob_r = NULL;
    if (L->out_buffer_r >= 0 && (UInt32)L->out_buffer_r < output->mNumberBuffers) {
        ob_r = &output->mBuffers[L->out_buffer_r];
        if (!ob_r->mData || ob_r->mNumberChannels == 0) ob_r = NULL;
        else {
            uint32_t fr = ob_r->mDataByteSize / (uint32_t)(sizeof(float) * ob_r->mNumberChannels);
            if (fr < frames) frames = fr;
        }
    }
    if (frames == 0) return noErr;
    if (frames > TL_MAX_FRAMES) frames = TL_MAX_FRAMES;

    float *l = k->buf_l;
    float *r = k->buf_r;
    memset(l, 0, frames * sizeof(float));
    memset(r, 0, frames * sizeof(float));

    // 1. Mix the tapped sources with their own gains.
    if (input) {
        for (int s = 0; s < L->source_count; s++) {
            const int idx = L->source_buffer_index[s];
            float target = atomic_load_explicit(&k->source_gain_target[s], memory_order_relaxed);
            float g = k->source_gain_current[s];
            if (idx < 0 || (UInt32)idx >= input->mNumberBuffers) { k->source_gain_current[s] = target; continue; }
            const AudioBuffer *ib = &input->mBuffers[idx];
            if (!ib->mData || ib->mNumberChannels == 0) { k->source_gain_current[s] = target; continue; }
            const float *src = (const float *)ib->mData;
            const uint32_t ch = ib->mNumberChannels;
            uint32_t n = ib->mDataByteSize / (uint32_t)(sizeof(float) * ch);
            if (n > frames) n = frames;
            const uint32_t cl = (L->source_channel_l >= 0 && (uint32_t)L->source_channel_l < ch) ? (uint32_t)L->source_channel_l : 0;
            const uint32_t cr = (L->source_channel_r >= 0 && (uint32_t)L->source_channel_r < ch) ? (uint32_t)L->source_channel_r : cl;
            const float step = (target - g) / (float)(n ? n : 1);
            float peak = 0.0f;
            for (uint32_t i = 0; i < n; i++) {
                float sl = src[i * ch + cl];
                float sr = src[i * ch + cr];
                // A broken stream must not poison the mix: drop its NaN / inf samples,
                // but let the meter (and the runaway guard) see them.
                if (!isfinite(sl)) { sl = 0.0f; peak = INFINITY; }
                if (!isfinite(sr)) { sr = 0.0f; peak = INFINITY; }
                const float al = fabsf(sl), ar = fabsf(sr);
                if (al > peak) peak = al;
                if (ar > peak) peak = ar;
                g += step;
                l[i] += sl * g;
                r[i] += sr * g;
            }
            k->source_gain_current[s] = target;
            atomic_max_float(&k->m_source_peak[s], peak);
        }
    }

    // 2. Pre analyzer
    uint64_t pos = atomic_load_explicit(&k->ana_pos, memory_order_relaxed);
    for (uint32_t i = 0; i < frames; i++) {
        k->ana_pre[(pos + i) & (TL_ANALYZER_SIZE - 1)] = 0.5f * (l[i] + r[i]);
    }

    // 3. Preamp + EQ
    if (p->eq_enabled) {
        apply_gain_ramp(l, r, frames, &k->preamp_current, p->preamp);
        run_biquads(k, p, frames);
    } else {
        k->preamp_current = p->preamp;
        for (int s = 0; s < TL_MAX_STAGES; s++) k->was_active[s] = 0;
    }

    // 4. Channel utilities
    if (p->swap_channels) {
        for (uint32_t i = 0; i < frames; i++) { float t = l[i]; l[i] = r[i]; r[i] = t; }
    }
    float width_target = p->mono ? 0.0f : p->width;
    if (fabsf(width_target - 1.0f) > 1e-6f || fabsf(k->width_current - 1.0f) > 1e-6f) {
        float w = k->width_current;
        const float step = (width_target - w) / (float)frames;
        for (uint32_t i = 0; i < frames; i++) {
            w += step;
            const float m = 0.5f * (l[i] + r[i]);
            const float sd = 0.5f * (l[i] - r[i]) * w;
            l[i] = m + sd;
            r[i] = m - sd;
        }
    }
    k->width_current = width_target;

    // 5. Crossfeed (keeps running while fading out so toggling never clicks)
    float xf_target = p->crossfeed_enabled ? p->crossfeed : 0.0f;
    if (xf_target > 0.0f || k->xf_amount_current > 0.0f) {
        float amount = k->xf_amount_current;
        const float step = (xf_target - amount) / (float)frames;
        const uint32_t mask = TL_XFEED_RING - 1;
        const float a = k->xf_coef;
        for (uint32_t i = 0; i < frames; i++) {
            amount += step;
            const float in_l = l[i], in_r = r[i];
            const uint32_t read = (k->xf_pos - k->xf_delay) & mask;
            const float dl = k->xf_ring_l[read], dr = k->xf_ring_r[read];
            k->xf_ring_l[k->xf_pos] = in_l;
            k->xf_ring_r[k->xf_pos] = in_r;
            k->xf_pos = (k->xf_pos + 1) & mask;
            k->xf_lp_l += a * (dl - k->xf_lp_l);
            k->xf_lp_r += a * (dr - k->xf_lp_r);
            const float kx = 0.45f * amount;
            const float norm = 1.0f / (1.0f + 0.7f * kx);
            l[i] = (in_l + kx * k->xf_lp_r) * norm;
            r[i] = (in_r + kx * k->xf_lp_l) * norm;
        }
        k->xf_amount_current = xf_target;
        if (xf_target == 0.0f) { k->xf_lp_l = k->xf_lp_r = 0.0f; }
        // A NaN that got this far (an unstable filter) would latch in the low-pass forever.
        if (!isfinite(k->xf_lp_l) || !isfinite(k->xf_lp_r)) {
            k->xf_lp_l = k->xf_lp_r = 0.0f;
            memset(k->xf_ring_l, 0, sizeof(k->xf_ring_l));
            memset(k->xf_ring_r, 0, sizeof(k->xf_ring_r));
        }
    }

    // 6. Balance
    {
        float b = k->balance_current;
        const float target = p->balance;
        if (fabsf(target) > 1e-6f || fabsf(b) > 1e-6f) {
            const float step = (target - b) / (float)frames;
            for (uint32_t i = 0; i < frames; i++) {
                b += step;
                if (b > 0.0f) l[i] *= (1.0f - b);
                else r[i] *= (1.0f + b);
            }
        }
        k->balance_current = target;
    }

    // 7. Output gain
    apply_gain_ramp(l, r, frames, &k->output_current, p->output_gain);

    // 8. Look-ahead limiter, then the output guard: nothing non-finite reaches the device.
    {
        const float ceiling = p->limiter_enabled ? p->limiter_ceiling : FLT_MAX;
        const uint32_t n = k->lim_lookahead;
        float held = k->lim_held, gain = k->lim_gain, min_gain = 1.0f;
        uint32_t hold = k->lim_hold, lp = k->lim_pos;
        const float att = k->lim_attack, rel = k->lim_release;
        for (uint32_t i = 0; i < frames; i++) {
            const float in_l = l[i], in_r = r[i];
            float target = 1.0f;
            if (p->limiter_enabled) {
                const float pk = fmaxf(fabsf(in_l), fabsf(in_r));
                if (pk > ceiling) target = ceiling / pk;
            }
            if (target <= held) { held = target; hold = n; }
            else if (hold > 0) { hold--; }
            else { held = target; }
            gain = held < gain ? held + (gain - held) * att : held + (gain - held) * rel;
            const float dl = k->lim_ring_l[lp], dr = k->lim_ring_r[lp];
            k->lim_ring_l[lp] = in_l;
            k->lim_ring_r[lp] = in_r;
            lp = (lp + 1 >= n) ? 0 : lp + 1;
            l[i] = guard_sample(dl * gain, ceiling);
            r[i] = guard_sample(dr * gain, ceiling);
            if (gain < min_gain) min_gain = gain;
        }
        k->lim_held = held; k->lim_gain = gain; k->lim_hold = hold; k->lim_pos = lp;
        if (min_gain < 1.0f) atomic_max_float(&k->m_gr, -20.0f * log10f(fmaxf(min_gain, 1e-6f)));
    }

    // 9. Meters + post analyzer
    {
        float pl = 0, pr = 0;
        double sl = 0, sr = 0;
        uint32_t clips = 0;
        for (uint32_t i = 0; i < frames; i++) {
            const float a = fabsf(l[i]), b = fabsf(r[i]);
            if (a > pl) pl = a;
            if (b > pr) pr = b;
            sl += (double)l[i] * l[i];
            sr += (double)r[i] * r[i];
            if (a >= 1.0f || b >= 1.0f) clips++;
            k->ana_post[(pos + i) & (TL_ANALYZER_SIZE - 1)] = 0.5f * (l[i] + r[i]);
        }
        atomic_store_explicit(&k->ana_pos, pos + frames, memory_order_release);
        atomic_max_float(&k->m_peak_l, pl);
        atomic_max_float(&k->m_peak_r, pr);
        atomic_max_float(&k->m_rms_l, (float)sqrt(sl / frames));
        atomic_max_float(&k->m_rms_r, (float)sqrt(sr / frames));
        if (clips) atomic_fetch_add_explicit(&k->m_clips, clips, memory_order_relaxed);
    }

    // 10. Write to the device's stereo channels.
    {
        float *dst = (float *)ob_l->mData;
        const uint32_t stride = ob_l->mNumberChannels;
        const uint32_t c = (uint32_t)L->out_channel_l;
        if (ob_r) {
            for (uint32_t i = 0; i < frames; i++) dst[i * stride + c] = l[i];
            float *dst_r = (float *)ob_r->mData;
            const uint32_t stride_r = ob_r->mNumberChannels;
            const uint32_t cr = (uint32_t)L->out_channel_r;
            for (uint32_t i = 0; i < frames; i++) dst_r[i * stride_r + cr] = r[i];
        } else {
            for (uint32_t i = 0; i < frames; i++) dst[i * stride + c] = 0.5f * (l[i] + r[i]);
        }
    }

    // 11. Pass the other channels of the stream straight through (multichannel interfaces).
    if (L->passthrough && input && ob_l->mNumberChannels > 2) {
        float *dst = (float *)ob_l->mData;
        const uint32_t stride = ob_l->mNumberChannels;
        const uint32_t skip_l = (uint32_t)L->out_channel_l;
        const uint32_t skip_r = ob_r == ob_l ? (uint32_t)L->out_channel_r : skip_l;
        for (int s = 0; s < L->source_count; s++) {
            const int idx = L->source_buffer_index[s];
            if (idx < 0 || (UInt32)idx >= input->mNumberBuffers) continue;
            const AudioBuffer *ib = &input->mBuffers[idx];
            if (!ib->mData || ib->mNumberChannels != stride) continue;
            const float *src = (const float *)ib->mData;
            const float g = k->source_gain_current[s];
            uint32_t n = ib->mDataByteSize / (uint32_t)(sizeof(float) * stride);
            if (n > frames) n = frames;
            for (uint32_t i = 0; i < n; i++) {
                for (uint32_t c = 0; c < stride; c++) {
                    if (c == skip_l || c == skip_r) continue;
                    dst[i * stride + c] += src[i * stride + c] * g;
                }
            }
        }
    }
    return noErr;
}

OSStatus tl_set_stream_usage(AudioObjectID device,
                             AudioDeviceIOProcID proc,
                             int32_t is_input,
                             const uint32_t *stream_on,
                             uint32_t stream_count) {
    const size_t size = offsetof(AudioHardwareIOProcStreamUsage, mStreamIsOn) + sizeof(UInt32) * (stream_count ? stream_count : 1);
    AudioHardwareIOProcStreamUsage *usage = calloc(1, size);
    if (!usage) return kAudioHardwareUnspecifiedError;
    usage->mIOProc = (void *)proc;
    usage->mNumberStreams = stream_count;
    for (uint32_t i = 0; i < stream_count; i++) usage->mStreamIsOn[i] = stream_on[i];
    AudioObjectPropertyAddress address = {
        kAudioDevicePropertyIOProcStreamUsage,
        is_input ? kAudioObjectPropertyScopeInput : kAudioObjectPropertyScopeOutput,
        kAudioObjectPropertyElementMain
    };
    OSStatus status = AudioObjectSetPropertyData(device, &address, 0, NULL, (UInt32)size, usage);
    free(usage);
    return status;
}
