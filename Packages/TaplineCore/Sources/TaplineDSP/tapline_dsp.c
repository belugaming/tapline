#include "tapline_dsp.h"

#include <Accelerate/Accelerate.h>
#include <float.h>
#include <math.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define TL_DIRTY 4
#define TL_LIMITER_MAX_LOOKAHEAD 1024
#define TL_XFEED_RING 128  // power of two

// Virtual speakers: partitions of TL_CONV_BLOCK samples, up to TL_CONV_MAX_TAPS in all.
#define TL_CONV_MAX_PARTS (TL_CONV_MAX_TAPS / TL_CONV_BLOCK)  // power of two
_Static_assert((TL_CONV_MAX_PARTS & (TL_CONV_MAX_PARTS - 1)) == 0, "the delay line is a power-of-two ring");
_Static_assert((TL_LIN_PARTS & (TL_LIN_PARTS - 1)) == 0, "the delay line is a power-of-two ring");
_Static_assert(TL_LIN_MIN_TAPS / TL_LIN_PARTS >= 16, "partitions of at least 16 samples");

struct tl_ir_set {
    double sample_rate;
    uint32_t block;         // partition size the spectra were made for
    uint32_t parts;
    // 4: a 2x2 matrix, path (input * 2 + output); 2: input c -> output c; 1: one filter for both.
    uint32_t paths;
    // Spectrum of every partition in vDSP's packed real-FFT layout (bin 0 holds DC in re
    // and Nyquist in im): [(path * parts + part) * block + bin]. Pre-scaled so the
    // inverse transform needs no further gain.
    float *re;
    float *im;
};

// Marks "remove the current set" in the pending slot (NULL there means "no change").
static tl_ir_set tl_no_set;
#define TL_NO_SET (&tl_no_set)

// Uniformly partitioned overlap-save convolution of a stereo signal. Each block of `block`
// input samples is transformed once (2 * block point real FFT) and kept in a frequency-domain
// delay line; every output block is the sum over partitions of IR spectrum x delayed input
// spectrum, transformed back, so the output lags the input by `block` samples. The main
// thread publishes sets through `pending` and frees what the IO thread returns through
// `retired`; everything else is IO-thread state. Virtual Speakers and the linear-phase EQ
// each have one.
typedef struct {
    uint32_t block;
    uint32_t log2n;                // of the transform size, 2 * block
    uint32_t max_parts;            // delay-line slots, a power of two
    int32_t matrix;                // takes 2x2 matrix sets, else per-channel ones
    FFTSetup fft;
    _Atomic(tl_ir_set *) pending;
    _Atomic(tl_ir_set *) retired;
    tl_ir_set *cur;
    tl_ir_set *next;               // set being crossfaded to
    int32_t removing;              // the owner fades out, then drops cur
    int32_t running;               // the input blocks and delay line hold signal
    float swap;                    // crossfade progress cur -> next
    float swap_step;
    uint32_t pos;                  // samples in the current input block
    uint32_t head;                 // delay-line slot of the newest block
    uint32_t filled;               // delay-line slots holding signal
    float *in[2];                  // per channel: previous block, then the current one
    float *out[2];
    float *out_next[2];
    float *acc_re[2];
    float *acc_im[2];
    float *time;
    float *fdl_re[2];              // [slot * block + bin]
    float *fdl_im[2];
    float *memory;                 // all of the buffers above
} tl_conv;

// One length of the linear-phase EQ: the FIR in `conv`, and delay lines of the same latency
// for the bypassed signal and the passthrough channels, so that switching between them and
// the EQ neither shifts the time nor comb-filters.
typedef struct {
    tl_conv conv;
    uint32_t taps;
    uint32_t latency;              // samples: the FIR's own delay plus one partition
    float *ring;                   // delay lines: [channel][ring_size], the pair, then passthrough
    uint32_t ring_size;            // power of two above latency
    uint32_t ring_channels;
    uint32_t ring_pos;
    uint32_t cycle_pos;            // ring_pos at the start of this IO cycle
    int32_t cycle_active;          // this cycle wrote the delay lines
    uint32_t warmup;               // samples before the FIR's output may fade in
    float gain;                    // share of this path, 0 ... 1
    float bypass_current;
    int32_t eq_on;
    float g[TL_MAX_FRAMES];        // per-sample shares of this cycle (for the passthrough channels)
} tl_lin_path;

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
    float bypass_current;
    float output_current;
    float balance_current;
    float width_current;
    int32_t eq_on;          // whether the last buffer went through the EQ

    // Device simulation state: [stage][channel][z1, z2]
    double sim_z[TL_MAX_SIM_STAGES][2][2];
    int32_t sim_was_active[TL_MAX_SIM_STAGES];
    float sim_gain_current;
    float sim_width_current;
    int32_t sim_on;         // whether the last buffer went through the simulation

    float buf_l[TL_MAX_FRAMES];
    float buf_r[TL_MAX_FRAMES];
    // The bypassed signal while switching the EQ or the simulation on or off (crossfaded over one buffer).
    float dry_l[TL_MAX_FRAMES];
    float dry_r[TL_MAX_FRAMES];
    // Convolution output (Virtual Speakers, linear-phase EQ).
    float wet_l[TL_MAX_FRAMES];
    float wet_r[TL_MAX_FRAMES];

    // Crossfeed
    float xf_ring_l[TL_XFEED_RING];
    float xf_ring_r[TL_XFEED_RING];
    uint32_t xf_pos;
    uint32_t xf_delay;
    float xf_coef;
    float xf_lp_l, xf_lp_r;
    float xf_amount_current;

    // Virtual speakers
    tl_conv vs;
    float vs_mix;                  // 0 = dry ... 1 = convolved
    float vs_mix_step;
    uint32_t vs_warmup;            // samples before the convolved signal may fade in

    // Linear-phase EQ: a path per FIR length (see tl_lin_path). The stages (minimum phase)
    // play until the requested FIR is there.
    tl_lin_path lin[TL_LIN_MODES];
    int32_t eq_path;               // what the EQ plays: 0 = stages, else lin[eq_path - 1]
    float min_gain;                // share of the stages, 0 ... 1
    float lin_fade_step;
    float in_l[TL_MAX_FRAMES];     // the EQ's input
    float in_r[TL_MAX_FRAMES];
    float dly_l[TL_MAX_FRAMES];    // ... late by a linear-phase path's latency
    float dly_r[TL_MAX_FRAMES];
    float lin_l[TL_MAX_FRAMES];    // a linear-phase path's output
    float lin_r[TL_MAX_FRAMES];
    float min_g[TL_MAX_FRAMES];    // per-sample share of the stages in this cycle

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

    // Multichannel speakers. Their settings have a triple buffer of their own, like the
    // parameters. The front pair runs through buf_l / buf_r, the other channels through mc_buf.
    tl_speakers spk_slots[3];
    _Atomic int spk_middle;
    int spk_back;
    int spk_front;
    int32_t mc_active;                  // whether the last buffer was processed as multichannel
    float mc_buf[TL_MAX_CHANNELS][TL_MAX_FRAMES];
    // The EQ on the other speakers: [channel][stage][z1, z2], and their gain ramps.
    double mc_z[TL_MAX_CHANNELS][TL_MAX_STAGES][2];
    int32_t mc_was_active[TL_MAX_CHANNELS][TL_MAX_STAGES];
    float mc_preamp[TL_MAX_CHANNELS];
    float mc_dry[TL_MAX_CHANNELS];      // their gain while the EQ is bypassed or not played on them
    float mc_output[TL_MAX_CHANNELS];
    int32_t mc_wet[TL_MAX_CHANNELS];    // whether the last buffer went through the EQ
    // Each speaker's correction: [channel][stage][z1, z2]
    double spk_z[TL_MAX_CHANNELS][TL_MAX_SPEAKER_STAGES][2];
    int32_t spk_was_active[TL_MAX_CHANNELS][TL_MAX_SPEAKER_STAGES];
    int32_t spk_on[TL_MAX_CHANNELS];    // whether the last buffer went through its filters
    float spk_gain[TL_MAX_CHANNELS];
    // Delays: a ring per channel; a change crossfades from the old tap to the new one.
    float dl_ring[TL_MAX_CHANNELS][TL_MAX_DELAY];
    uint32_t dl_pos;
    uint32_t dl_cur[TL_MAX_CHANNELS];
    uint32_t dl_next[TL_MAX_CHANNELS];
    int32_t dl_fading[TL_MAX_CHANNELS];
    float dl_t[TL_MAX_CHANNELS];
    float dl_fade_step;
    // The limiter's look-ahead for every channel of a multichannel output.
    float mc_lim_ring[TL_MAX_CHANNELS][TL_LIMITER_MAX_LOOKAHEAD];

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

static inline uint32_t log2_ceil(uint32_t n) {
    uint32_t log2n = 0;
    while ((1u << log2n) < n) log2n++;
    return log2n;
}

void tl_params_init(tl_params *p) {
    memset(p, 0, sizeof(*p));
    for (int i = 0; i < TL_MAX_STAGES; i++) {
        p->stages[i].b0 = 1.0;
    }
    for (int i = 0; i < TL_MAX_SIM_STAGES; i++) {
        p->simulation.stages[i].b0 = 1.0;
    }
    p->simulation.gain = 1.0f;
    p->simulation.width = 1.0f;
    p->preamp = 1.0f;
    p->output_gain = 1.0f;
    p->width = 1.0f;
    p->limiter_ceiling = 1.0f;
    p->bypass_gain = 1.0f;
    p->eq_enabled = 1;
}

// MARK: - Impulse-response sets (non-realtime)

static tl_ir_set *ir_set_build(const float *const *irs, uint32_t paths, uint32_t length, uint32_t block,
                               double sample_rate, float gain) {
    if (length == 0 || block == 0 || !(sample_rate >= 8000.0) || !isfinite(sample_rate) || !isfinite(gain)) return NULL;
    const uint32_t parts = (length + block - 1) / block;
    const uint32_t log2n = log2_ceil(2 * block);
    const size_t count = (size_t)paths * parts * block;
    tl_ir_set *set = calloc(1, sizeof(tl_ir_set));
    float *buf = calloc(2 * (size_t)block, sizeof(float));
    FFTSetup fft = vDSP_create_fftsetup(log2n, kFFTRadix2);
    if (set) {
        set->re = calloc(count, sizeof(float));
        set->im = calloc(count, sizeof(float));
    }
    if (!set || !set->re || !set->im || !buf || !fft) {
        if (set) { free(set->re); free(set->im); free(set); }
        free(buf);
        if (fft) vDSP_destroy_fftsetup(fft);
        return NULL;
    }
    set->sample_rate = sample_rate;
    set->block = block;
    set->parts = parts;
    set->paths = paths;
    // zrip's forward transform scales by 2 (here and for the input), its inverse by the
    // transform size: fold the undo into the IR.
    const float scale = gain / (4.0f * (float)(2 * block));
    for (uint32_t path = 0; path < paths; path++) {
        for (uint32_t part = 0; part < parts; part++) {
            memset(buf, 0, 2 * (size_t)block * sizeof(float));
            const uint32_t start = part * block;
            const uint32_t n = length - start < block ? length - start : block;
            for (uint32_t i = 0; i < n; i++) {
                const float v = irs[path][start + i];
                buf[i] = isfinite(v) ? v * scale : 0.0f;
            }
            const size_t offset = ((size_t)path * parts + part) * block;
            DSPSplitComplex split = { set->re + offset, set->im + offset };
            vDSP_ctoz((const DSPComplex *)buf, 2, &split, 1, block);
            vDSP_fft_zrip(fft, &split, 1, log2n, FFT_FORWARD);
        }
    }
    free(buf);
    vDSP_destroy_fftsetup(fft);
    return set;
}

tl_ir_set *tl_ir_set_create(const float *const *irs, uint32_t length, double sample_rate, float gain) {
    if (length > TL_CONV_MAX_TAPS) length = TL_CONV_MAX_TAPS;
    return ir_set_build(irs, 4, length, TL_CONV_BLOCK, sample_rate, gain);
}

void tl_ir_set_destroy(tl_ir_set *set) {
    if (!set || set == TL_NO_SET) return;
    free(set->re);
    free(set->im);
    free(set);
}

// MARK: - Linear-phase FIR sizes

static int32_t clamp_mode(int32_t mode) {
    return mode == TL_LIN_LOW_LATENCY ? TL_LIN_LOW_LATENCY : TL_LIN_HIGH_PRECISION;
}

uint32_t tl_linear_phase_taps(double sample_rate, int32_t mode) {
    if (!(sample_rate >= 8000.0) || !isfinite(sample_rate)) sample_rate = 48000.0;
    const double wanted = 1.25 * sample_rate;
    uint32_t taps = TL_LIN_MIN_TAPS * 4;
    while ((double)taps < wanted && taps < TL_LIN_MAX_TAPS) taps <<= 1;
    return clamp_mode(mode) == TL_LIN_LOW_LATENCY ? taps / 4 : taps;
}

uint32_t tl_linear_phase_block(double sample_rate, int32_t mode) {
    return tl_linear_phase_taps(sample_rate, mode) / TL_LIN_PARTS;
}

uint32_t tl_linear_phase_latency(double sample_rate, int32_t mode) {
    return tl_linear_phase_taps(sample_rate, mode) / 2 + tl_linear_phase_block(sample_rate, mode);
}

tl_ir_set *tl_fir_set_create(const float *left, const float *right, uint32_t length, double sample_rate) {
    if (!(sample_rate >= 8000.0) || !isfinite(sample_rate)) return NULL;
    if (length != tl_linear_phase_taps(sample_rate, TL_LIN_LOW_LATENCY) && length != tl_linear_phase_taps(sample_rate, TL_LIN_HIGH_PRECISION)) return NULL;
    const float *irs[2] = { left, right ? right : left };
    return ir_set_build(irs, right && right != left ? 2 : 1, length, length / TL_LIN_PARTS, sample_rate, 1.0f);
}

// MARK: - Convolution engine

// Allocates the engine (non-realtime). 0 when out of memory; the engine is then unusable.
static int conv_init(tl_conv *c, uint32_t block, uint32_t max_parts, int32_t matrix) {
    memset(c, 0, sizeof(*c));
    const uint32_t log2n = log2_ceil(2 * block);
    FFTSetup fft = vDSP_create_fftsetup(log2n, kFFTRadix2);
    // in: 2 x 2 blocks, out / out_next / acc_re / acc_im: 4 x 2 x 1, time: 2, delay line: 4 x max_parts.
    float *memory = calloc((size_t)(14 + 4 * (size_t)max_parts) * block, sizeof(float));
    if (!fft || !memory) {
        if (fft) vDSP_destroy_fftsetup(fft);
        free(memory);
        return 0;
    }
    c->block = block;
    c->log2n = log2n;
    c->max_parts = max_parts;
    c->matrix = matrix;
    c->fft = fft;
    c->memory = memory;
    float *p = memory;
    for (int ch = 0; ch < 2; ch++) { c->in[ch] = p; p += 2 * (size_t)block; }
    for (int ch = 0; ch < 2; ch++) { c->out[ch] = p; p += block; }
    for (int ch = 0; ch < 2; ch++) { c->out_next[ch] = p; p += block; }
    for (int ch = 0; ch < 2; ch++) { c->acc_re[ch] = p; p += block; }
    for (int ch = 0; ch < 2; ch++) { c->acc_im[ch] = p; p += block; }
    c->time = p; p += 2 * (size_t)block;
    for (int ch = 0; ch < 2; ch++) { c->fdl_re[ch] = p; p += (size_t)max_parts * block; }
    for (int ch = 0; ch < 2; ch++) { c->fdl_im[ch] = p; p += (size_t)max_parts * block; }
    return 1;
}

static void conv_collect(tl_conv *c) {
    tl_ir_set *retired = atomic_exchange_explicit(&c->retired, NULL, memory_order_acq_rel);
    if (retired) tl_ir_set_destroy(retired);
}

// Frees the engine and every set it holds (non-realtime, IO stopped).
static void conv_free(tl_conv *c) {
    conv_collect(c);
    tl_ir_set *pending = atomic_exchange(&c->pending, NULL);
    if (pending && pending != TL_NO_SET) tl_ir_set_destroy(pending);
    tl_ir_set_destroy(c->cur);
    tl_ir_set_destroy(c->next);
    if (c->fft) vDSP_destroy_fftsetup(c->fft);
    free(c->memory);
    memset(c, 0, sizeof(*c));
}

// Main thread: hands `set` (NULL: none) to the IO thread.
static void conv_hand_over(tl_conv *c, tl_ir_set *set) {
    conv_collect(c);
    // A set the IO thread has not picked up yet was never seen by it: free it here.
    tl_ir_set *previous = atomic_exchange_explicit(&c->pending, set ? set : TL_NO_SET, memory_order_acq_rel);
    if (previous && previous != TL_NO_SET) tl_ir_set_destroy(previous);
}

static void conv_reset(tl_conv *c) {
    c->running = 0;
    c->pos = 0;
    c->filled = 0;
    if (!c->block) return;
    for (int ch = 0; ch < 2; ch++) {
        memset(c->in[ch], 0, 2 * (size_t)c->block * sizeof(float));
        memset(c->out[ch], 0, c->block * sizeof(float));
        memset(c->out_next[ch], 0, c->block * sizeof(float));
    }
}

// Reconfigure (non-realtime, IO stopped): finishes any change in progress and drops a set
// made for another sample rate; the main thread sends a new one.
static void conv_configure(tl_conv *c, double sample_rate, float swap_seconds) {
    conv_collect(c);
    if (c->next) {
        tl_ir_set_destroy(c->cur);
        c->cur = c->next;
        c->next = NULL;
    }
    if (c->removing && c->cur) {
        tl_ir_set_destroy(c->cur);
        c->cur = NULL;
    }
    c->removing = 0;
    if (c->cur && c->cur->sample_rate != sample_rate) {
        tl_ir_set_destroy(c->cur);
        c->cur = NULL;
    }
    c->swap = 0.0f;
    c->swap_step = (float)(1.0 / (swap_seconds * sample_rate));
    conv_reset(c);
}

// Hands a set back to the main thread. Only one is ever outstanding: changes are picked up
// only while the slot is empty, and each change retires at most one set.
static inline void conv_retire(tl_conv *c, tl_ir_set *set) {
    if (set) atomic_store_explicit(&c->retired, set, memory_order_release);
}

// Silent: finishes a change that waited for the fade-out.
static void conv_settle(tl_conv *c) {
    if (c->next) {
        conv_retire(c, c->cur);
        c->cur = c->next;
        c->next = NULL;
    }
    if (c->removing) {
        conv_retire(c, c->cur);
        c->cur = NULL;
        c->removing = 0;
    }
    c->swap = 0.0f;
}

// Transforms the input block that just filled up into the newest delay-line slot.
static void conv_push_block(tl_conv *c) {
    const uint32_t block = c->block;
    c->head = (c->head + 1) & (c->max_parts - 1);
    for (int ch = 0; ch < 2; ch++) {
        DSPSplitComplex split = { c->fdl_re[ch] + (size_t)c->head * block, c->fdl_im[ch] + (size_t)c->head * block };
        vDSP_ctoz((const DSPComplex *)c->in[ch], 2, &split, 1, block);
        vDSP_fft_zrip(c->fft, &split, 1, c->log2n, FFT_FORWARD);
        memcpy(c->in[ch], c->in[ch] + block, block * sizeof(float));
    }
    if (c->filled < c->max_parts) c->filled++;
}

// acc += x * h for packed real-FFT spectra.
static inline void conv_mac(float *restrict acc_re, float *restrict acc_im,
                            const float *restrict x_re, const float *restrict x_im,
                            const float *restrict h_re, const float *restrict h_im, uint32_t bins) {
    acc_re[0] += x_re[0] * h_re[0];  // DC
    acc_im[0] += x_im[0] * h_im[0];  // Nyquist
    for (uint32_t b = 1; b < bins; b++) {
        acc_re[b] += x_re[b] * h_re[b] - x_im[b] * h_im[b];
        acc_im[b] += x_re[b] * h_im[b] + x_im[b] * h_re[b];
    }
}

// One output block of `set` from the delay line. Returns 0 (and leaves silence) when it
// is not finite, e.g. after an EQ filter blew up: the caller then restarts the delay line.
static int conv_render(tl_conv *c, const tl_ir_set *set, float *const out[2]) {
    const uint32_t block = c->block;
    const uint32_t parts = set->parts < c->filled ? set->parts : c->filled;
    for (int ch = 0; ch < 2; ch++) {
        memset(c->acc_re[ch], 0, block * sizeof(float));
        memset(c->acc_im[ch], 0, block * sizeof(float));
    }
    for (uint32_t part = 0; part < parts; part++) {
        const size_t slot = (size_t)((c->head - part) & (c->max_parts - 1)) * block;
        for (int in = 0; in < 2; in++) {
            const float *x_re = c->fdl_re[in] + slot;
            const float *x_im = c->fdl_im[in] + slot;
            if (set->paths == 4) {
                // Speaker `in` to both ears.
                for (int ear = 0; ear < 2; ear++) {
                    const size_t offset = ((size_t)(in * 2 + ear) * set->parts + part) * block;
                    conv_mac(c->acc_re[ear], c->acc_im[ear], x_re, x_im, set->re + offset, set->im + offset, block);
                }
            } else {
                const size_t offset = ((size_t)(set->paths == 1 ? 0 : in) * set->parts + part) * block;
                conv_mac(c->acc_re[in], c->acc_im[in], x_re, x_im, set->re + offset, set->im + offset, block);
            }
        }
    }
    int ok = 1;
    for (int ch = 0; ch < 2; ch++) {
        DSPSplitComplex split = { c->acc_re[ch], c->acc_im[ch] };
        vDSP_fft_zrip(c->fft, &split, 1, c->log2n, FFT_INVERSE);
        vDSP_ztoc(&split, 1, (DSPComplex *)c->time, 2, block);
        // Overlap-save: the first half wrapped around, the second half is the output.
        float sum = 0.0f;
        for (uint32_t i = 0; i < block; i++) {
            out[ch][i] = c->time[block + i];
            sum += out[ch][i];
        }
        if (!isfinite(sum)) ok = 0;
    }
    if (!ok) {
        memset(out[0], 0, block * sizeof(float));
        memset(out[1], 0, block * sizeof(float));
    }
    return ok;
}

// Clears the delay line and input after a non-finite block.
static void conv_restart(tl_conv *c) {
    c->filled = 0;
    memset(c->in[0], 0, 2 * (size_t)c->block * sizeof(float));
    memset(c->in[1], 0, 2 * (size_t)c->block * sizeof(float));
}

// Takes over a set from the main thread, one change at a time. `audible`: whether the
// owner's output of cur can be heard (else a new set replaces it right away).
static void conv_pick_up(tl_conv *c, double sample_rate, int audible) {
    if (c->next || c->removing) return;
    if (!atomic_load_explicit(&c->pending, memory_order_relaxed)) return;
    if (atomic_load_explicit(&c->retired, memory_order_acquire)) return;
    tl_ir_set *set = atomic_exchange_explicit(&c->pending, NULL, memory_order_acq_rel);
    if (!set) return;
    if (set == TL_NO_SET) {
        set = NULL;
    } else if (set->sample_rate != sample_rate || set->block != c->block || (set->paths == 4) != (c->matrix != 0)) {
        conv_retire(c, set);   // made for the rate before a reconfigure
        return;
    }
    if (!c->cur) {
        c->cur = set;
    } else if (!audible) {
        // Nothing of the old set is audible: switch right away.
        conv_retire(c, c->cur);
        c->cur = set;
        if (set && c->running && !conv_render(c, set, c->out)) conv_restart(c);
    } else if (!set) {
        c->removing = 1;
    } else {
        c->next = set;
        c->swap = 0.0f;
        // Its output for the block that is playing, from the same delay line.
        if (!conv_render(c, set, c->out_next)) conv_restart(c);
    }
}

// Feeds `frames` samples into the delay line and writes the convolved signal, `block` samples
// late, to `wet_l` / `wet_r`, crossfading from cur to next while there is one. Needs cur.
static void conv_process(tl_conv *c, const float *l, const float *r, float *wet_l, float *wet_r, uint32_t frames) {
    const uint32_t block = c->block;
    const float swap_step = c->swap_step;
    float swap = c->swap;
    uint32_t pos = c->pos;
    for (uint32_t i = 0; i < frames; i++) {
        float out_l = c->out[0][pos], out_r = c->out[1][pos];
        if (c->next) {
            out_l += swap * (c->out_next[0][pos] - out_l);
            out_r += swap * (c->out_next[1][pos] - out_r);
            swap += swap_step;
            if (swap >= 1.0f) {
                conv_retire(c, c->cur);
                c->cur = c->next;
                c->next = NULL;
                memcpy(c->out[0], c->out_next[0], block * sizeof(float));
                memcpy(c->out[1], c->out_next[1], block * sizeof(float));
                swap = 0.0f;
            }
        }
        c->in[0][block + pos] = l[i];
        c->in[1][block + pos] = r[i];
        if (++pos == block) {
            pos = 0;
            conv_push_block(c);
            int ok = conv_render(c, c->cur, c->out);
            if (c->next) ok &= conv_render(c, c->next, c->out_next);
            if (!ok) conv_restart(c);
        }
        wet_l[i] = out_l;
        wet_r[i] = out_r;
    }
    c->swap = swap;
    c->pos = pos;
}

// MARK: - Kernel lifetime

void tl_speakers_init(tl_speakers *sp) {
    memset(sp, 0, sizeof(*sp));
    for (int c = 0; c < TL_MAX_CHANNELS; c++) {
        for (int i = 0; i < TL_MAX_SPEAKER_STAGES; i++) sp->channel[c].stages[i].b0 = 1.0;
        sp->channel[c].gain = 1.0f;
    }
    sp->eq_all = 1;
}

tl_kernel *tl_kernel_create(void) {
    tl_kernel *k = calloc(1, sizeof(tl_kernel));
    if (!k) return NULL;
    if (!conv_init(&k->vs, TL_CONV_BLOCK, TL_CONV_MAX_PARTS, 1)) { free(k); return NULL; }
    for (int i = 0; i < 3; i++) tl_params_init(&k->slots[i]);
    k->front = 0;
    atomic_store(&k->middle, 1);
    k->back = 2;
    for (int i = 0; i < 3; i++) tl_speakers_init(&k->spk_slots[i]);
    k->spk_front = 0;
    atomic_store(&k->spk_middle, 1);
    k->spk_back = 2;
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
    conv_free(&k->vs);
    for (int m = 0; m < TL_LIN_MODES; m++) {
        conv_free(&k->lin[m].conv);
        free(k->lin[m].ring);
    }
    free(k);
}

// Linear-phase EQ: an engine and delay lines for each length, sized for the rate (allocates when
// it changed). A length without memory for them is never played.
static void configure_linear_phase(tl_kernel *k, double sample_rate) {
    const tl_params *p = &k->slots[k->front];
    const uint32_t channels = 2 + (k->layout.passthrough ? TL_LIN_MAX_PASSTHROUGH : 0);
    for (int m = 0; m < TL_LIN_MODES; m++) {
        tl_lin_path *path = &k->lin[m];
        const uint32_t block = tl_linear_phase_block(sample_rate, m + 1);
        if (path->conv.block != block) {
            conv_free(&path->conv);
            if (!conv_init(&path->conv, block, TL_LIN_PARTS, 0)) conv_free(&path->conv);
        }
        conv_configure(&path->conv, sample_rate, 0.050f);

        path->taps = tl_linear_phase_taps(sample_rate, m + 1);
        path->latency = tl_linear_phase_latency(sample_rate, m + 1);
        const uint32_t size = 1u << log2_ceil(path->latency + 1);
        if (size != path->ring_size || channels != path->ring_channels || !path->ring) {
            free(path->ring);
            path->ring = calloc((size_t)size * channels, sizeof(float));
            path->ring_size = path->ring ? size : 0;
            path->ring_channels = path->ring ? channels : 0;
        }
        if (!path->ring && path->conv.block) conv_free(&path->conv);
        path->ring_pos = 0;
        path->cycle_active = 0;
        path->warmup = 0;
        path->gain = 0.0f;
        path->bypass_current = p->bypass_gain;
        path->eq_on = p->eq_enabled ? 1 : 0;
    }
    k->eq_path = 0;
    k->min_gain = 1.0f;
    k->lin_fade_step = (float)(1.0 / (0.030 * sample_rate));
}

void tl_kernel_configure(tl_kernel *k, double sample_rate, const tl_layout *layout) {
    if (sample_rate < 8000.0) sample_rate = 48000.0;
    k->sample_rate = sample_rate;
    k->layout = *layout;
    if (k->layout.source_count > TL_MAX_SOURCES) k->layout.source_count = TL_MAX_SOURCES;
    if (k->layout.source_count < 0) k->layout.source_count = 0;
    if (k->layout.channel_count > TL_MAX_CHANNELS || k->layout.channel_count < 3) k->layout.channel_count = 0;

    memset(k->z, 0, sizeof(k->z));
    memset(k->was_active, 0, sizeof(k->was_active));

    const tl_params *p = &k->slots[k->front];
    k->preamp_current = p->preamp;
    k->bypass_current = p->bypass_gain;
    k->eq_on = p->eq_enabled ? 1 : 0;
    k->output_current = p->output_gain;
    k->balance_current = p->balance;
    k->width_current = p->width;
    k->xf_amount_current = p->crossfeed_enabled ? p->crossfeed : 0.0f;
    memset(k->sim_z, 0, sizeof(k->sim_z));
    memset(k->sim_was_active, 0, sizeof(k->sim_was_active));
    k->sim_gain_current = p->simulation.gain;
    k->sim_width_current = p->simulation.width;
    k->sim_on = p->simulation.enabled ? 1 : 0;
    for (int i = 0; i < TL_MAX_SOURCES; i++) {
        k->source_gain_current[i] = atomic_load(&k->source_gain_target[i]);
    }

    // Multichannel speakers start where their settings are, with empty delay lines.
    {
        const tl_speakers *sp = &k->spk_slots[k->spk_front];
        k->mc_active = 0;
        memset(k->mc_z, 0, sizeof(k->mc_z));
        memset(k->mc_was_active, 0, sizeof(k->mc_was_active));
        memset(k->spk_z, 0, sizeof(k->spk_z));
        memset(k->spk_was_active, 0, sizeof(k->spk_was_active));
        memset(k->dl_ring, 0, sizeof(k->dl_ring));
        memset(k->mc_lim_ring, 0, sizeof(k->mc_lim_ring));
        k->dl_pos = 0;
        k->dl_fade_step = (float)(1.0 / (0.020 * sample_rate));
        for (int c = 0; c < TL_MAX_CHANNELS; c++) {
            k->mc_preamp[c] = p->preamp;
            k->mc_dry[c] = sp->eq_all ? p->bypass_gain : 1.0f;
            k->mc_output[c] = p->output_gain;
            k->mc_wet[c] = p->eq_enabled && sp->eq_all ? 1 : 0;
            k->spk_on[c] = 0;
            k->spk_gain[c] = sp->channel[c].gain;
            double d = (double)sp->channel[c].delay * sample_rate;
            if (!(d > 0.0)) d = 0.0;
            if (d > TL_MAX_DELAY - 1) d = TL_MAX_DELAY - 1;
            k->dl_cur[c] = k->dl_next[c] = (uint32_t)lround(d);
            k->dl_fading[c] = 0;
            k->dl_t[c] = 0.0f;
        }
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

    // Virtual speakers: finish any change in progress and start dry, so starting IO fades
    // the effect in. A set made for another sample rate is dropped; the main thread sends a
    // new one. 30 ms to switch the effect on or off, 50 ms between two sets.
    conv_configure(&k->vs, sample_rate, 0.050f);
    k->vs_mix = 0.0f;
    k->vs_mix_step = (float)(1.0 / (0.030 * sample_rate));

    // Linear-phase EQ: the same for its FIR, and it starts on the stages. 30 ms to switch
    // between the two, 50 ms between two FIRs.
    configure_linear_phase(k, sample_rate);
}

// MARK: - Main-thread API

void tl_kernel_set_ir_set(tl_kernel *k, tl_ir_set *set) {
    conv_hand_over(&k->vs, set);
}

void tl_kernel_set_fir(tl_kernel *k, int32_t mode, tl_ir_set *set) {
    conv_hand_over(&k->lin[clamp_mode(mode) - 1].conv, set);
}

void tl_kernel_collect(tl_kernel *k) {
    conv_collect(&k->vs);
    for (int m = 0; m < TL_LIN_MODES; m++) conv_collect(&k->lin[m].conv);
}

// Non-finite parameters would latch the gain ramps and filter states: replace them with
// neutral values here, on the writer's thread, so the IO thread never has to check.
static void sanitize_stages(tl_biquad *stages, int count) {
    for (int i = 0; i < count; i++) {
        tl_biquad *c = &stages[i];
        if (!isfinite(c->b0) || !isfinite(c->b1) || !isfinite(c->b2) || !isfinite(c->a1) || !isfinite(c->a2)) {
            c->active = 0;
        }
    }
}

static void sanitize_params(tl_params *p) {
    sanitize_stages(p->stages, TL_MAX_STAGES);
    sanitize_stages(p->simulation.stages, TL_MAX_SIM_STAGES);
    if (!isfinite(p->preamp)) p->preamp = 0.0f;
    if (!isfinite(p->output_gain)) p->output_gain = 0.0f;
    if (!isfinite(p->balance)) p->balance = 0.0f;
    if (!isfinite(p->width)) p->width = 1.0f;
    if (!isfinite(p->crossfeed)) p->crossfeed = 0.0f;
    if (!(p->limiter_ceiling > 0.0f && p->limiter_ceiling <= 1.0f)) p->limiter_ceiling = 1.0f;
    // Bypass never boosts, so it cannot drive the limiter harder than the input does.
    if (!(p->bypass_gain >= 0.0f && p->bypass_gain <= 1.0f)) p->bypass_gain = 1.0f;
    // The simulation's make-up gain stays within +-12 dB; the width only narrows.
    if (!(p->simulation.gain >= 0.0f && p->simulation.gain <= 4.0f)) p->simulation.gain = 1.0f;
    if (!(p->simulation.width >= 0.0f && p->simulation.width <= 1.0f)) p->simulation.width = 1.0f;
}

void tl_kernel_set_params(tl_kernel *k, const tl_params *params) {
    k->slots[k->back] = *params;
    sanitize_params(&k->slots[k->back]);
    int previous = atomic_exchange_explicit(&k->middle, k->back | TL_DIRTY, memory_order_acq_rel);
    k->back = previous & 3;
}

static void sanitize_speakers(tl_speakers *sp) {
    for (int c = 0; c < TL_MAX_CHANNELS; c++) {
        tl_speaker *s = &sp->channel[c];
        for (int i = 0; i < TL_MAX_SPEAKER_STAGES; i++) {
            tl_biquad *b = &s->stages[i];
            if (!isfinite(b->b0) || !isfinite(b->b1) || !isfinite(b->b2) || !isfinite(b->a1) || !isfinite(b->a2)) b->active = 0;
        }
        if (!(s->gain >= 0.0f && s->gain <= 4.0f)) s->gain = 1.0f;
        if (!(s->delay >= 0.0f && s->delay <= 1.0f)) s->delay = 0.0f;
    }
}

void tl_kernel_set_speakers(tl_kernel *k, const tl_speakers *speakers) {
    k->spk_slots[k->spk_back] = *speakers;
    sanitize_speakers(&k->spk_slots[k->spk_back]);
    int previous = atomic_exchange_explicit(&k->spk_middle, k->spk_back | TL_DIRTY, memory_order_acq_rel);
    k->spk_back = previous & 3;
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

// Runs a block of biquads over `l` and `r`, with `z` and `was_active` holding their state.
static void run_stages(const tl_biquad *stages, int count, double (*z)[2][2], int32_t *was_active,
                       float *l, float *r, uint32_t frames) {
    for (int s = 0; s < count; s++) {
        const tl_biquad *c = &stages[s];
        if (!c->active) {
            was_active[s] = 0;
            continue;
        }
        if (!was_active[s]) {
            memset(z[s], 0, sizeof(z[s]));
            was_active[s] = 1;
        }
        const double b0 = c->b0, b1 = c->b1, b2 = c->b2, a1 = c->a1, a2 = c->a2;
        for (int ch = 0; ch < 2; ch++) {
            if ((c->channels == 1 && ch == 1) || (c->channels == 2 && ch == 0)) {
                z[s][ch][0] = z[s][ch][1] = 0;
                continue;
            }
            float *x = ch == 0 ? l : r;
            double z1 = z[s][ch][0];
            double z2 = z[s][ch][1];
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
            z[s][ch][0] = z1;
            z[s][ch][1] = z2;
        }
    }
}

static void run_biquads(tl_kernel *k, const tl_params *p, uint32_t frames) {
    run_stages(p->stages, TL_MAX_STAGES, k->z, k->was_active, k->buf_l, k->buf_r, frames);
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

// Mid/side width, ramped from `current` to `target`: 0 = mono, 1 = unchanged, 2 = extra wide.
static void apply_width(float *l, float *r, uint32_t frames, float *current, float target) {
    if (fabsf(target - 1.0f) > 1e-6f || fabsf(*current - 1.0f) > 1e-6f) {
        float w = *current;
        const float step = (target - w) / (float)frames;
        for (uint32_t i = 0; i < frames; i++) {
            w += step;
            const float m = 0.5f * (l[i] + r[i]);
            const float sd = 0.5f * (l[i] - r[i]) * w;
            l[i] = m + sd;
            r[i] = m - sd;
        }
    }
    *current = target;
}

// Fades over one buffer from `dry` to the processed signal in `l` / `r`, or back with `to_wet` == 0.
static void crossfade(float *l, float *r, const float *dry_l, const float *dry_r, uint32_t frames, int32_t to_wet) {
    const float step = 1.0f / (float)frames;
    for (uint32_t i = 0; i < frames; i++) {
        const float t = (float)(i + 1) * step;
        const float wet = to_wet ? t : 1.0f - t;
        l[i] = dry_l[i] + (l[i] - dry_l[i]) * wet;
        r[i] = dry_r[i] + (r[i] - dry_r[i]) * wet;
    }
}

static void run_simulation(tl_kernel *k, const tl_simulation *sim, float *l, float *r, uint32_t frames) {
    apply_gain_ramp(l, r, frames, &k->sim_gain_current, sim->gain);
    run_stages(sim->stages, TL_MAX_SIM_STAGES, k->sim_z, k->sim_was_active, l, r, frames);
    apply_width(l, r, frames, &k->sim_width_current, sim->width);
}

// MARK: - Multichannel speakers (IO thread)

static inline void ramp_mono(float *x, uint32_t frames, float *current, float target) {
    float g = *current;
    if (fabsf(target - g) < 1e-6f) {
        if (g != 1.0f) {
            for (uint32_t i = 0; i < frames; i++) x[i] *= g;
        }
        *current = target;
        return;
    }
    const float step = (target - g) / (float)frames;
    for (uint32_t i = 0; i < frames; i++) {
        g += step;
        x[i] *= g;
    }
    *current = target;
}

// Fades over one buffer from `dry` to the processed `x`, or back with `to_wet` == 0.
static inline void crossfade_mono(float *x, const float *dry, uint32_t frames, int32_t to_wet) {
    const float step = 1.0f / (float)frames;
    for (uint32_t i = 0; i < frames; i++) {
        const float t = (float)(i + 1) * step;
        const float wet = to_wet ? t : 1.0f - t;
        x[i] = dry[i] + (x[i] - dry[i]) * wet;
    }
}

// Biquads over one channel. Stages in `skip`, and with `shared_only` the stages for one side
// of the stereo pair, are left out.
static void run_mono_stages(const tl_biquad *stages, int count, double (*z)[2], int32_t *was_active,
                            float *x, uint32_t frames, uint32_t skip, int shared_only) {
    for (int s = 0; s < count; s++) {
        const tl_biquad *c = &stages[s];
        if (!c->active || (s < 32 && ((skip >> s) & 1u)) || (shared_only && c->channels != 0)) {
            was_active[s] = 0;
            continue;
        }
        if (!was_active[s]) {
            z[s][0] = z[s][1] = 0;
            was_active[s] = 1;
        }
        const double b0 = c->b0, b1 = c->b1, b2 = c->b2, a1 = c->a1, a2 = c->a2;
        double z1 = z[s][0], z2 = z[s][1];
        for (uint32_t i = 0; i < frames; i++) {
            const double in = x[i];
            const double out = b0 * in + z1;
            z1 = b1 * in - a1 * out + z2;
            z2 = b2 * in - a2 * out;
            x[i] = (float)out;
        }
        if (!isfinite(z1) || !isfinite(z2)) { z1 = 0; z2 = 0; }
        if (fabs(z1) < 1e-30) z1 = 0;
        if (fabs(z2) < 1e-30) z2 = 0;
        z[s][0] = z1;
        z[s][1] = z2;
    }
}

// Whether this buffer can be processed as multichannel: the layout has speakers, the sources
// copy the output stream, and the stereo pair lives in that same stream.
static int mc_usable(const tl_layout *L, const AudioBuffer *ob_l, const AudioBuffer *ob_r) {
    if (L->channel_count < 3 || !L->passthrough || ob_r != ob_l) return 0;
    if (ob_l->mNumberChannels != (UInt32)L->channel_count) return 0;
    return L->out_channel_l >= 0 && L->out_channel_l < L->channel_count
        && L->out_channel_r >= 0 && L->out_channel_r < L->channel_count && L->out_channel_l != L->out_channel_r;
}

static inline int mc_is_pair(const tl_layout *L, int c) {
    return c == L->out_channel_l || c == L->out_channel_r;
}

// The channels the multichannel path processes: known speakers and the front pair.
static inline int mc_is_speaker(const tl_layout *L, int c) {
    return L->channel_role[c] != TL_ROLE_PASS || mc_is_pair(L, c);
}

// Every channel of the stream except the stereo pair, mixed from the sources with their gain
// ramps (as step 1 does for the pair; call it first, since that step ends the ramps).
static void mc_mix_sources(tl_kernel *k, const tl_layout *L, const AudioBufferList *input, uint32_t frames) {
    const uint32_t stride = (uint32_t)L->channel_count;
    for (int c = 0; c < L->channel_count; c++) {
        if (!mc_is_pair(L, c)) memset(k->mc_buf[c], 0, frames * sizeof(float));
    }
    if (!input) return;
    for (int s = 0; s < L->source_count; s++) {
        const int idx = L->source_buffer_index[s];
        if (idx < 0 || (UInt32)idx >= input->mNumberBuffers) continue;
        const AudioBuffer *ib = &input->mBuffers[idx];
        if (!ib->mData || ib->mNumberChannels != stride) continue;
        const float *src = (const float *)ib->mData;
        uint32_t n = ib->mDataByteSize / (uint32_t)(sizeof(float) * stride);
        if (n > frames) n = frames;
        const float target = atomic_load_explicit(&k->source_gain_target[s], memory_order_relaxed);
        const float start = k->source_gain_current[s];
        const float step = (target - start) / (float)(n ? n : 1);
        float peak = 0.0f;
        for (int c = 0; c < L->channel_count; c++) {
            if (mc_is_pair(L, c)) continue;
            float *dst = k->mc_buf[c];
            float g = start;
            for (uint32_t i = 0; i < n; i++) {
                float v = src[i * stride + (uint32_t)c];
                if (!isfinite(v)) { v = 0.0f; peak = INFINITY; }
                const float a = fabsf(v);
                if (a > peak) peak = a;
                g += step;
                dst[i] += v * g;
            }
        }
        atomic_max_float(&k->m_source_peak[s], peak);
    }
}

// The EQ on the speakers other than the front pair, when it plays on every speaker: the same
// preamp, stages and bypass gain as the pair, each channel with its own filter state.
static void mc_run_eq(tl_kernel *k, const tl_params *p, const tl_speakers *sp, const tl_layout *L, uint32_t frames) {
    for (int c = 0; c < L->channel_count; c++) {
        const int32_t role = L->channel_role[c];
        if (mc_is_pair(L, c) || role == TL_ROLE_PASS) continue;
        float *x = k->mc_buf[c];
        const int32_t eq = sp->eq_all ? 1 : 0;
        const int32_t wet = eq && p->eq_enabled ? 1 : 0;
        const float dry_target = eq ? p->bypass_gain : 1.0f;
        const uint32_t skip = role == TL_ROLE_LFE ? sp->lfe_skip : 0;
        if (wet != k->mc_wet[c]) {
            memcpy(k->dry_l, x, frames * sizeof(float));
            ramp_mono(k->dry_l, frames, &k->mc_dry[c], dry_target);
            ramp_mono(x, frames, &k->mc_preamp[c], p->preamp);
            run_mono_stages(p->stages, TL_MAX_STAGES, k->mc_z[c], k->mc_was_active[c], x, frames, skip, 1);
            crossfade_mono(x, k->dry_l, frames, wet);
            k->mc_wet[c] = wet;
        } else if (wet) {
            ramp_mono(x, frames, &k->mc_preamp[c], p->preamp);
            run_mono_stages(p->stages, TL_MAX_STAGES, k->mc_z[c], k->mc_was_active[c], x, frames, skip, 1);
            k->mc_dry[c] = dry_target;
        } else {
            ramp_mono(x, frames, &k->mc_dry[c], dry_target);
            k->mc_preamp[c] = p->preamp;
            for (int s = 0; s < TL_MAX_STAGES; s++) k->mc_was_active[c][s] = 0;
        }
    }
}

// Delays one channel by its speaker's delay. A change crossfades from the old tap to the new
// one over 20 ms; the ring always records, so a new delay never reads stale sound.
static void run_delay(tl_kernel *k, int c, float *x, uint32_t frames, float seconds) {
    const uint32_t mask = TL_MAX_DELAY - 1;
    double d = (double)seconds * k->sample_rate;
    if (!(d > 0.0)) d = 0.0;
    if (d > TL_MAX_DELAY - 1) d = TL_MAX_DELAY - 1;
    const uint32_t target = (uint32_t)lround(d);
    if (!k->dl_fading[c] && target != k->dl_cur[c]) {
        k->dl_next[c] = target;
        k->dl_fading[c] = 1;
        k->dl_t[c] = 0.0f;
    }
    float *ring = k->dl_ring[c];
    uint32_t pos = k->dl_pos, cur = k->dl_cur[c];
    const uint32_t next = k->dl_next[c];
    int32_t fading = k->dl_fading[c];
    float t = k->dl_t[c];
    const float step = k->dl_fade_step;
    for (uint32_t i = 0; i < frames; i++) {
        ring[pos] = isfinite(x[i]) ? x[i] : 0.0f;
        float y = ring[(pos - cur) & mask];
        if (fading) {
            const float y2 = ring[(pos - next) & mask];
            t += step;
            if (t >= 1.0f) { y = y2; cur = next; fading = 0; t = 0.0f; }
            else y += t * (y2 - y);
        }
        x[i] = y;
        pos = (pos + 1) & mask;
    }
    k->dl_cur[c] = cur;
    k->dl_fading[c] = fading;
    k->dl_t[c] = t;
}

// Each speaker's own correction, trim and delay, the front pair included.
static void mc_run_speakers(tl_kernel *k, const tl_speakers *sp, const tl_layout *L, float *const *x, uint32_t frames) {
    for (int c = 0; c < L->channel_count; c++) {
        if (!mc_is_speaker(L, c)) continue;
        const tl_speaker *s = &sp->channel[c];
        int32_t on = 0;
        for (int i = 0; i < TL_MAX_SPEAKER_STAGES; i++) on |= s->stages[i].active;
        on = on ? 1 : 0;
        if (on != k->spk_on[c]) {
            memcpy(k->dry_l, x[c], frames * sizeof(float));
            run_mono_stages(s->stages, TL_MAX_SPEAKER_STAGES, k->spk_z[c], k->spk_was_active[c], x[c], frames, 0, 0);
            crossfade_mono(x[c], k->dry_l, frames, on);
            k->spk_on[c] = on;
        } else if (on) {
            run_mono_stages(s->stages, TL_MAX_SPEAKER_STAGES, k->spk_z[c], k->spk_was_active[c], x[c], frames, 0, 0);
        } else {
            for (int i = 0; i < TL_MAX_SPEAKER_STAGES; i++) k->spk_was_active[c][i] = 0;
        }
        ramp_mono(x[c], frames, &k->spk_gain[c], s->gain);
        run_delay(k, c, x[c], frames, s->delay);
    }
    k->dl_pos = (k->dl_pos + frames) & (TL_MAX_DELAY - 1);
}

// MARK: - EQ (IO thread)

// Preamp + stages in place on buf_l / buf_r. While the EQ is bypassed, `bypass_gain` takes
// their place so that switching compares the sound, not the level. A switch crossfades the
// two paths over one buffer: the filters shift the phase, and a hard cut would click.
static void run_stages_path(tl_kernel *k, const tl_params *p, uint32_t frames) {
    float *l = k->buf_l, *r = k->buf_r;
    const int32_t eq_on = p->eq_enabled ? 1 : 0;
    if (eq_on != k->eq_on) {
        memcpy(k->dry_l, l, frames * sizeof(float));
        memcpy(k->dry_r, r, frames * sizeof(float));
        apply_gain_ramp(k->dry_l, k->dry_r, frames, &k->bypass_current, p->bypass_gain);
        apply_gain_ramp(l, r, frames, &k->preamp_current, p->preamp);
        run_biquads(k, p, frames);
        crossfade(l, r, k->dry_l, k->dry_r, frames, eq_on);
        k->eq_on = eq_on;
    } else if (eq_on) {
        apply_gain_ramp(l, r, frames, &k->preamp_current, p->preamp);
        run_biquads(k, p, frames);
        k->bypass_current = p->bypass_gain;
    } else {
        apply_gain_ramp(l, r, frames, &k->bypass_current, p->bypass_gain);
        k->preamp_current = p->preamp;
        for (int s = 0; s < TL_MAX_STAGES; s++) k->was_active[s] = 0;
    }
}

// A linear-phase path from in_l / in_r into lin_l / lin_r: the FIR (which holds the preamp),
// or while bypassed the input delayed by the same latency, at `bypass_gain`. Both are lined
// up sample for sample and differ only in their magnitude, so the one-buffer crossfade between
// them neither comb-filters nor jumps in time. Leaves the delayed input in dly_l / dly_r.
static void run_linear_path(tl_kernel *k, tl_lin_path *path, const tl_params *p, uint32_t frames) {
    const uint32_t mask = path->ring_size - 1, latency = path->latency, start = path->ring_pos;
    for (int ch = 0; ch < 2; ch++) {
        float *ring = path->ring + (size_t)ch * path->ring_size;
        const float *x = ch == 0 ? k->in_l : k->in_r;
        float *d = ch == 0 ? k->dly_l : k->dly_r;
        for (uint32_t i = 0; i < frames; i++) {
            const uint32_t w = (start + i) & mask;
            ring[w] = x[i];
            d[i] = ring[(w - latency) & mask];
        }
    }
    conv_process(&path->conv, k->in_l, k->in_r, k->wet_l, k->wet_r, frames);
    path->cycle_pos = start;
    path->cycle_active = 1;
    path->ring_pos = (start + frames) & mask;

    float *l = k->lin_l, *r = k->lin_r;
    const int32_t eq_on = p->eq_enabled ? 1 : 0;
    if (eq_on != path->eq_on || !eq_on) {
        memcpy(l, k->dly_l, frames * sizeof(float));
        memcpy(r, k->dly_r, frames * sizeof(float));
        apply_gain_ramp(l, r, frames, &path->bypass_current, p->bypass_gain);
        if (eq_on != path->eq_on) {
            // l / r hold the bypassed signal: fade the FIR's output in over it, or out.
            for (uint32_t i = 0; i < frames; i++) {
                const float t = (float)(i + 1) / (float)frames;
                const float wet = eq_on ? t : 1.0f - t;
                l[i] += (k->wet_l[i] - l[i]) * wet;
                r[i] += (k->wet_r[i] - r[i]) * wet;
            }
            path->eq_on = eq_on;
        }
    } else {
        memcpy(l, k->wet_l, frames * sizeof(float));
        memcpy(r, k->wet_r, frames * sizeof(float));
        path->bypass_current = p->bypass_gain;
    }
}

// Whether the EQ can play path `eq_path` (0 = stages, always).
static int eq_path_ready(const tl_kernel *k, int32_t eq_path) {
    if (eq_path <= 0) return 1;
    const tl_conv *c = &k->lin[eq_path - 1].conv;
    return c->block && c->cur && !c->removing;
}

static inline float ramp_towards(float g, float target, float step) {
    return g < target ? fminf(target, g + step) : fmaxf(target, g - step);
}

// 3. Preamp + EQ, through the stages (minimum phase, no latency) or, on a stereo layout, a
//    linear-phase FIR (low latency or high precision, tl_linear_phase_latency samples late). The EQ moves to the
//    requested path once that can play; switching fades the old path out and the new one in,
//    30 ms each: their delays differ, so the music skips forward (to a shorter delay) or pauses
//    (to a FIR, whose output fades in once its delay line is full) rather than playing twice.
//    Also feeds the pre-EQ analyzer, lined up with the output.
static void run_eq(tl_kernel *k, const tl_params *p, uint32_t frames, uint64_t ana) {
    float *l = k->buf_l, *r = k->buf_r;
    for (int m = 0; m < TL_LIN_MODES; m++) {
        tl_conv *c = &k->lin[m].conv;
        if (c->block) conv_pick_up(c, k->sample_rate, k->lin[m].gain > 0.0f);
        // The FIR was removed before its output faded in: nothing of it is audible.
        if (c->running && !c->cur) conv_reset(c);
    }
    // The FIRs cover the stereo pair: a multichannel speaker layout keeps the stages, or the
    // front pair would lag the other speakers by the FIR's latency.
    const int32_t requested = !k->mc_active && (p->linear_phase == TL_LIN_LOW_LATENCY || p->linear_phase == TL_LIN_HIGH_PRECISION)
        ? p->linear_phase : 0;
    if (eq_path_ready(k, requested)) k->eq_path = requested;
    else if (!eq_path_ready(k, k->eq_path)) k->eq_path = 0;
    const int32_t target = k->eq_path;
    if (target > 0) {
        tl_lin_path *path = &k->lin[target - 1];
        if (!path->conv.running) {
            conv_reset(&path->conv);
            path->conv.running = 1;
            path->warmup = path->latency;
            path->eq_on = p->eq_enabled ? 1 : 0;
            path->bypass_current = p->bypass_gain;
        }
    }

    // The shares of the paths, sample by sample.
    const float step = k->lin_fade_step;
    const float min_before = k->min_gain;
    float mg = min_before;
    const float min_target = target == 0 ? 1.0f : 0.0f;
    for (uint32_t i = 0; i < frames; i++) {
        mg = ramp_towards(mg, min_target, step);
        k->min_g[i] = mg;
    }
    tl_lin_path *loudest = NULL;
    for (int m = 0; m < TL_LIN_MODES; m++) {
        tl_lin_path *path = &k->lin[m];
        path->cycle_active = 0;
        if (!path->conv.running) continue;
        if (!loudest || path->gain > loudest->gain) loudest = path;
        float g = path->gain;
        for (uint32_t i = 0; i < frames; i++) {
            float lin_target = 0.0f;
            if (target == m + 1) {
                if (path->warmup > 0) path->warmup--;
                else lin_target = 1.0f;
            }
            g = ramp_towards(g, lin_target, step);
            path->g[i] = g;
        }
    }
    const int run_min = min_before > 0.0f || mg > 0.0f;
    // The analyzer shows the input as it lines up with what plays.
    tl_lin_path *delayed_pre = loudest && loudest->gain >= 0.5f ? loudest : NULL;

    if (loudest) {
        memcpy(k->in_l, l, frames * sizeof(float));
        memcpy(k->in_r, r, frames * sizeof(float));
    }
    if (!delayed_pre) {
        for (uint32_t i = 0; i < frames; i++) {
            k->ana_pre[(ana + i) & (TL_ANALYZER_SIZE - 1)] = 0.5f * (l[i] + r[i]);
        }
    }

    if (run_min) {
        run_stages_path(k, p, frames);
        if (min_before < 1.0f || mg < 1.0f) {
            for (uint32_t i = 0; i < frames; i++) {
                l[i] *= k->min_g[i];
                r[i] *= k->min_g[i];
            }
        }
    } else {
        // Idle: start fresh, without ramps, when it is needed again.
        k->preamp_current = p->preamp;
        k->bypass_current = p->bypass_gain;
        k->eq_on = p->eq_enabled ? 1 : 0;
        for (int s = 0; s < TL_MAX_STAGES; s++) k->was_active[s] = 0;
        memset(l, 0, frames * sizeof(float));
        memset(r, 0, frames * sizeof(float));
    }

    for (int m = 0; m < TL_LIN_MODES; m++) {
        tl_lin_path *path = &k->lin[m];
        if (!path->conv.running) continue;
        run_linear_path(k, path, p, frames);
        if (path == delayed_pre) {
            for (uint32_t i = 0; i < frames; i++) {
                k->ana_pre[(ana + i) & (TL_ANALYZER_SIZE - 1)] = 0.5f * (k->dly_l[i] + k->dly_r[i]);
            }
        }
        for (uint32_t i = 0; i < frames; i++) {
            l[i] += k->lin_l[i] * path->g[i];
            r[i] += k->lin_r[i] * path->g[i];
        }
        path->gain = path->g[frames - 1];
    }
    k->min_gain = mg;

    for (int m = 0; m < TL_LIN_MODES; m++) {
        tl_lin_path *path = &k->lin[m];
        if (target == m + 1 || path->gain > 0.0f) continue;
        // Silent: let go of the FIR's delay line, and finish a change that waited.
        if (path->conv.block) conv_settle(&path->conv);
        if (path->conv.running) conv_reset(&path->conv);
        path->gain = 0.0f;
        path->warmup = 0;
    }
}

// MARK: - Virtual speakers (IO thread)

// Crossfades between the dry signal and the convolved one: the convolved signal lags by
// TL_CONV_BLOCK samples, so it fades in only once its first block is out.
static void run_virtual_speakers(tl_kernel *k, const tl_params *p, float *l, float *r, uint32_t frames) {
    tl_conv *c = &k->vs;
    conv_pick_up(c, k->sample_rate, k->vs_mix > 0.0f);
    // A headphone effect: never on a multichannel speaker layout.
    const int wanted = p->virtual_speakers_enabled && !k->mc_active && c->cur && !c->removing;
    if (!c->cur || (!wanted && k->vs_mix <= 0.0f)) {
        // Dry: finish a change that waited for the fade-out and stop convolving.
        conv_settle(c);
        k->vs_mix = 0.0f;
        if (c->running) conv_reset(c);
        return;
    }
    if (!c->running) {
        c->running = 1;
        k->vs_warmup = c->block;
    }
    conv_process(c, l, r, k->wet_l, k->wet_r, frames);
    const float target = wanted ? 1.0f : 0.0f;
    const float mix_step = k->vs_mix_step;
    float mix = k->vs_mix;
    for (uint32_t i = 0; i < frames; i++) {
        if (k->vs_warmup > 0) {
            k->vs_warmup--;
        } else if (mix < target) {
            mix = fminf(target, mix + mix_step);
        } else if (mix > target) {
            mix = fmaxf(target, mix - mix_step);
        }
        l[i] += mix * (k->wet_l[i] - l[i]);
        r[i] += mix * (k->wet_r[i] - r[i]);
    }
    k->vs_mix = mix;
}

// Clamps to +-limit; NaN and infinities become silence.
static inline float guard_sample(float x, float limit) {
    if (fabsf(x) <= limit) return x;
    return isfinite(x) ? copysignf(limit, x) : 0.0f;
}

// MARK: - Processing core (IO thread)

// Picks up the newest parameter block and speaker settings, if any.
static void pick_up_params(tl_kernel *k) {
    if (atomic_load_explicit(&k->middle, memory_order_acquire) & TL_DIRTY) {
        int previous = atomic_exchange_explicit(&k->middle, k->front, memory_order_acq_rel);
        k->front = previous & 3;
    }
    if (atomic_load_explicit(&k->spk_middle, memory_order_acquire) & TL_DIRTY) {
        int previous = atomic_exchange_explicit(&k->spk_middle, k->spk_front, memory_order_acq_rel);
        k->spk_front = previous & 3;
    }
}

// Steps 2 to 9 on the stereo pair in buf_l / buf_r, from the EQ to the meters: what tl_ioproc
// and tl_kernel_process share. With `mc`, the other speakers of a multichannel layout (`chan`,
// by channel of the stream, the pair being buf_l / buf_r) go along.
static void process_pair(tl_kernel *k, const tl_params *p, const tl_speakers *sp, int mc,
                         float *const *chan, uint32_t frames) {
    const tl_layout *L = &k->layout;
    float *l = k->buf_l;
    float *r = k->buf_r;

    // 2 + 3. Pre analyzer, preamp + EQ (minimum or linear phase, see run_eq).
    uint64_t pos = atomic_load_explicit(&k->ana_pos, memory_order_relaxed);
    run_eq(k, p, frames, pos);

    // 3b. Device simulation ("Listen as"): make-up gain, the device's filters and its stereo
    //     width, on top of the EQ whether or not that is bypassed. Switching crossfades like the EQ.
    //     It imitates a stereo device, so never on a multichannel speaker layout.
    {
        const tl_simulation *sim = &p->simulation;
        const int32_t sim_on = sim->enabled && !mc ? 1 : 0;
        if (sim_on != k->sim_on) {
            memcpy(k->dry_l, l, frames * sizeof(float));
            memcpy(k->dry_r, r, frames * sizeof(float));
            run_simulation(k, sim, l, r, frames);
            crossfade(l, r, k->dry_l, k->dry_r, frames, sim_on);
            k->sim_on = sim_on;
        } else if (sim_on) {
            run_simulation(k, sim, l, r, frames);
        } else {
            k->sim_gain_current = sim->gain;
            k->sim_width_current = sim->width;
            for (int s = 0; s < TL_MAX_SIM_STAGES; s++) k->sim_was_active[s] = 0;
        }
    }

    // 3c. Multichannel: the EQ on the other speakers too.
    if (mc) mc_run_eq(k, p, sp, L, frames);

    // 4. Channel utilities (the front pair only on a multichannel layout)
    if (p->swap_channels) {
        for (uint32_t i = 0; i < frames; i++) { float t = l[i]; l[i] = r[i]; r[i] = t; }
    }
    apply_width(l, r, frames, &k->width_current, p->mono ? 0.0f : p->width);

    // 5. Crossfeed (keeps running while fading out so toggling never clicks)
    // A headphone effect: never on a multichannel speaker layout.
    float xf_target = p->crossfeed_enabled && !mc ? p->crossfeed : 0.0f;
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

    // 5b. Virtual speakers (crossfeed's replacement; the settings never enable both)
    run_virtual_speakers(k, p, l, r, frames);

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

    // 7b. Multichannel: the output gain on the other speakers, then every speaker's own
    //     correction, trim and delay.
    if (mc) {
        for (int c = 0; c < L->channel_count; c++) {
            if (mc_is_pair(L, c) || L->channel_role[c] == TL_ROLE_PASS) continue;
            ramp_mono(k->mc_buf[c], frames, &k->mc_output[c], p->output_gain);
        }
        mc_run_speakers(k, sp, L, chan, frames);
    }

    // 8. Look-ahead limiter, then the output guard: nothing non-finite reaches the device.
    //    On a multichannel layout one gain for every speaker keeps the balance between them.
    if (mc) {
        const float ceiling = p->limiter_enabled ? p->limiter_ceiling : FLT_MAX;
        const uint32_t n = k->lim_lookahead;
        float held = k->lim_held, gain = k->lim_gain, min_gain = 1.0f;
        uint32_t hold = k->lim_hold, lp = k->lim_pos;
        const float att = k->lim_attack, rel = k->lim_release;
        for (uint32_t i = 0; i < frames; i++) {
            float target = 1.0f;
            if (p->limiter_enabled) {
                float pk = 0.0f;
                for (int c = 0; c < L->channel_count; c++) {
                    if (mc_is_speaker(L, c)) pk = fmaxf(pk, fabsf(chan[c][i]));
                }
                if (pk > ceiling) target = ceiling / pk;
            }
            if (target <= held) { held = target; hold = n; }
            else if (hold > 0) { hold--; }
            else { held = target; }
            gain = held < gain ? held + (gain - held) * att : held + (gain - held) * rel;
            for (int c = 0; c < L->channel_count; c++) {
                if (!mc_is_speaker(L, c)) continue;
                const float delayed = k->mc_lim_ring[c][lp];
                k->mc_lim_ring[c][lp] = chan[c][i];
                chan[c][i] = guard_sample(delayed * gain, ceiling);
            }
            lp = (lp + 1 >= n) ? 0 : lp + 1;
            if (gain < min_gain) min_gain = gain;
        }
        k->lim_held = held; k->lim_gain = gain; k->lim_hold = hold; k->lim_pos = lp;
        if (min_gain < 1.0f) atomic_max_float(&k->m_gr, -20.0f * log10f(fmaxf(min_gain, 1e-6f)));
    } else {
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

    // 9. Meters + post analyzer (the front pair on a multichannel layout)
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

    pick_up_params(k);
    const tl_params *p = &k->slots[k->front];
    const tl_layout *L = &k->layout;
    const tl_speakers *sp = &k->spk_slots[k->spk_front];

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

    // Multichannel speakers: every channel of the stream, by channel (the pair is l / r).
    const int mc = mc_usable(L, ob_l, ob_r);
    k->mc_active = mc;
    float *chan[TL_MAX_CHANNELS];
    if (mc) {
        for (int c = 0; c < L->channel_count; c++) chan[c] = k->mc_buf[c];
        chan[L->out_channel_l] = l;
        chan[L->out_channel_r] = r;
        mc_mix_sources(k, L, input, frames);
    }

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

    // 2 ... 9. EQ, effects, limiter, meters.
    process_pair(k, p, sp, mc, chan, frames);

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
        // Multichannel: the other channels of the stream; unknown ones as the sources had them.
        if (mc) {
            for (int ch = 0; ch < L->channel_count; ch++) {
                if (mc_is_pair(L, ch)) continue;
                const float *x = k->mc_buf[ch];
                for (uint32_t i = 0; i < frames; i++) dst[i * stride + (uint32_t)ch] = x[i];
            }
        }
    }

    // 11. Pass the other channels of the stream straight through (multichannel interfaces).
    if (!mc && L->passthrough && input && ob_l->mNumberChannels > 2) {
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
        // Linear phase (stereo layouts only, so never here on a multichannel speaker layout):
        // they follow the processed pair through delay lines of its latency, and through its
        // switches between the paths.
        int any = 0;
        for (int m = 0; m < TL_LIN_MODES; m++) any |= k->lin[m].cycle_active;
        if (any) {
            uint32_t line = 2;
            for (uint32_t c = 0; c < stride; c++) {
                if (c == skip_l || c == skip_r) continue;
                for (uint32_t i = 0; i < frames; i++) {
                    float v = dst[i * stride + c];
                    if (!isfinite(v)) v = 0.0f;
                    float out = v * k->min_g[i];
                    for (int m = 0; m < TL_LIN_MODES; m++) {
                        tl_lin_path *path = &k->lin[m];
                        if (!path->cycle_active || line >= path->ring_channels) continue;
                        const uint32_t mask = path->ring_size - 1;
                        float *ring = path->ring + (size_t)line * path->ring_size;
                        const uint32_t w = (path->cycle_pos + i) & mask;
                        ring[w] = v;
                        out += ring[(w - path->latency) & mask] * path->g[i];
                    }
                    dst[i * stride + c] = out;
                }
                line++;
            }
        }
    }
    return noErr;
}

// MARK: - Host buffers (Audio Unit)

void tl_kernel_process(tl_kernel *k, float *const *channels, uint32_t channel_count, uint32_t frames) {
    if (!k || !channels || channel_count == 0 || !channels[0]) return;
    float *in_l = channels[0];
    float *in_r = channel_count > 1 && channels[1] ? channels[1] : NULL;

    pick_up_params(k);
    const tl_params *p = &k->slots[k->front];
    const tl_speakers *sp = &k->spk_slots[k->spk_front];
    // One stereo pair, never a speaker layout: the headphone effects and linear phase play.
    k->mc_active = 0;
    atomic_fetch_add_explicit(&k->m_cycles, 1, memory_order_relaxed);

    for (uint32_t done = 0; done < frames;) {
        const uint32_t n = frames - done < TL_MAX_FRAMES ? frames - done : TL_MAX_FRAMES;
        memcpy(k->buf_l, in_l + done, n * sizeof(float));
        memcpy(k->buf_r, (in_r ? in_r : in_l) + done, n * sizeof(float));
        process_pair(k, p, sp, 0, NULL, n);
        if (in_r) {
            memcpy(in_l + done, k->buf_l, n * sizeof(float));
            memcpy(in_r + done, k->buf_r, n * sizeof(float));
        } else {
            for (uint32_t i = 0; i < n; i++) in_l[done + i] = 0.5f * (k->buf_l[i] + k->buf_r[i]);
        }
        done += n;
    }
}

uint32_t tl_kernel_latency(const tl_kernel *k, const tl_params *params) {
    uint32_t latency = k->lim_lookahead;
    if (params->virtual_speakers_enabled) latency += k->vs.block;
    if (params->linear_phase == TL_LIN_LOW_LATENCY || params->linear_phase == TL_LIN_HIGH_PRECISION) {
        const tl_lin_path *path = &k->lin[params->linear_phase - 1];
        // A length the kernel has no memory for keeps playing the stages.
        if (path->conv.block) latency += path->latency;
    }
    return latency;
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
