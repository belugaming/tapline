// Tapline realtime DSP kernel.
//
// Everything that runs on the CoreAudio IO thread lives here. The kernel mixes
// the tapped process streams, runs the EQ / effects chain and writes the result
// into the output device's preferred stereo channels. On a multichannel speaker layout
// (5.1 ... 9.1.6) it processes every channel of that stream: the EQ plays on every speaker,
// the stereo effects on the front pair, and each speaker gets its own room correction,
// level trim and delay, under one limiter.
//
// Threading model:
//   - tl_kernel_set_params / tl_kernel_set_speakers / tl_kernel_set_source_gain /
//     tl_kernel_set_ir_set / tl_kernel_set_fir / tl_kernel_collect: any single non-realtime
//     thread (the main thread in practice). Lock-free.
//   - tl_ir_set_create / tl_fir_set_create / tl_ir_set_destroy: any non-realtime thread.
//   - tl_kernel_configure / tl_kernel_reset: only while IO is stopped.
//   - tl_kernel_read_meters / tl_kernel_copy_analyzer: any thread. Lock-free.
//   - tl_ioproc / tl_kernel_process: the IO thread (one of the two per kernel).
//   - tl_kernel_latency: any non-realtime thread.

#ifndef TAPLINE_DSP_H
#define TAPLINE_DSP_H

#include <stdint.h>
#include <CoreAudio/CoreAudio.h>

#ifdef __cplusplus
extern "C" {
#endif

#pragma clang assume_nonnull begin

#define TL_MAX_STAGES 32
#define TL_MAX_SIM_STAGES 16
#define TL_MAX_SOURCES 32
#define TL_MAX_FRAMES 8192
#define TL_ANALYZER_SIZE 32768

/// Virtual speakers: partition size of the convolution, which is also the latency it adds.
#define TL_CONV_BLOCK 256
/// Longest impulse response the convolution uses (at the device rate); longer ones are cut.
#define TL_CONV_MAX_TAPS 65536

/// Linear-phase EQ: its two FIR lengths (tl_params.linear_phase, tl_linear_phase_taps).
#define TL_LIN_LOW_LATENCY 1
#define TL_LIN_HIGH_PRECISION 2
#define TL_LIN_MODES 2
/// Linear-phase EQ: a FIR is cut into this many partitions, so its partition size (and the
/// latency the convolution adds on top of the FIR's own delay) is a 64th of its length.
#define TL_LIN_PARTS 64
/// Linear-phase EQ: bounds of the FIR length (see tl_linear_phase_taps).
#define TL_LIN_MIN_TAPS 1024
#define TL_LIN_MAX_TAPS 262144
/// Linear-phase EQ: the other channels of a stream that is not a speaker layout (an audio
/// interface's further outputs) are delayed along with the processed pair, up to this many.
#define TL_LIN_MAX_PASSTHROUGH 14

/// Multichannel speakers: channels of the output stream, room-correction stages per speaker,
/// and the longest speaker delay in samples (85 ms at 192 kHz, 341 ms at 48 kHz).
#define TL_MAX_CHANNELS 16
#define TL_MAX_SPEAKER_STAGES 16
#define TL_MAX_DELAY 16384

/// What one channel of a multichannel output is (tl_layout.channel_role).
enum {
    /// Not a known speaker: passed through untouched, as on a stereo output.
    TL_ROLE_PASS = 0,
    /// A speaker: the EQ (when it plays on every speaker), then its correction, trim and delay.
    TL_ROLE_SPEAKER = 1,
    /// An LFE channel: like a speaker, but without the EQ stages in tl_speakers.lfe_skip.
    TL_ROLE_LFE = 2,
};

typedef struct {
    double b0, b1, b2, a1, a2;
    int32_t active;
    int32_t channels;       // 0 = both, 1 = left only, 2 = right only
} tl_biquad;

/// "Listen as": colours the sound like another playback device. A block of its own after the
/// EQ, so the user's EQ keeps all of its stages, and it plays whether or not the EQ is bypassed.
typedef struct {
    tl_biquad stages[TL_MAX_SIM_STAGES];
    float gain;             // linear make-up gain before the stages, so switching keeps the loudness, 0 ... 4
    float width;            // the device's stereo width: 0 = mono, 1 = unchanged
    int32_t enabled;
} tl_simulation;

typedef struct {
    tl_biquad stages[TL_MAX_STAGES];
    float preamp;           // linear gain applied before the EQ stages
    float output_gain;      // linear gain applied after effects
    float balance;          // -1 (left) ... 0 ... +1 (right)
    float width;            // 0 = mono, 1 = unchanged, 2 = extra wide
    float crossfeed;        // 0 ... 1
    float limiter_ceiling;  // linear, e.g. 0.966 for -0.3 dBFS
    float bypass_gain;      // linear gain in place of preamp + EQ while eq_enabled == 0, 0 ... 1
    int32_t eq_enabled;
    int32_t mono;
    int32_t swap_channels;
    int32_t crossfeed_enabled;
    int32_t limiter_enabled;
    /// Convolves with the IR set from tl_kernel_set_ir_set. Takes crossfeed's place.
    int32_t virtual_speakers_enabled;
    /// 0, or TL_LIN_LOW_LATENCY / TL_LIN_HIGH_PRECISION: plays preamp + EQ through that
    /// length's FIR from tl_kernel_set_fir instead of the stages (which keep playing until
    /// there is a FIR). Stereo layouts only: a multichannel speaker layout keeps the stages. The FIR holds the preamp; the stages and `preamp` must describe the same
    /// curve, as the kernel switches between them.
    int32_t linear_phase;
    /// Filled in while a device is chosen, also when it is switched off, so that switching fades.
    tl_simulation simulation;
} tl_params;

typedef struct {
    int32_t source_count;
    int32_t source_buffer_index[TL_MAX_SOURCES];  // index into the IOProc input AudioBufferList
    int32_t source_channel_l, source_channel_r;   // stereo pair inside each source buffer
    int32_t out_buffer_l, out_channel_l;          // where the left channel goes
    int32_t out_buffer_r, out_channel_r;          // -1 when the device is mono
    /// Sources are copies of the output stream (device-stream taps): channels other
    /// than the processed stereo pair are passed through unchanged.
    int32_t passthrough;
    /// Multichannel speakers: the number of channels of the output stream (which must hold
    /// the stereo pair and match the sources, see `passthrough`) and what each one is.
    /// Below 3: stereo processing only.
    int32_t channel_count;
    int32_t channel_role[TL_MAX_CHANNELS];
} tl_layout;

/// One speaker of a multichannel output.
typedef struct {
    tl_biquad stages[TL_MAX_SPEAKER_STAGES];  // its room correction; `channels` is ignored
    float gain;             // linear: its level trim and the corrections' headroom, 0 ... 4
    float delay;            // seconds, until the farthest speaker arrives; 0 ... TL_MAX_DELAY samples
} tl_speaker;

/// How a multichannel output is processed. Ignored while the layout has no channel_count.
typedef struct {
    tl_speaker channel[TL_MAX_CHANNELS];   // by channel of the output stream
    int32_t eq_all;         // the EQ (preamp, stages) plays on every speaker; 0: the front pair only
    uint32_t lfe_skip;      // EQ stages left out on LFE channels, one bit per stage
} tl_speakers;

typedef struct {
    float peak_l, peak_r;
    float rms_l, rms_r;
    float gain_reduction_db;   // limiter, positive number
    uint32_t clipped_samples;
    uint64_t io_cycles;
    float source_peak[TL_MAX_SOURCES];
} tl_meters;

typedef struct tl_kernel tl_kernel;

/// Impulse responses transformed for the realtime kernel: a pair of virtual speakers
/// (tl_ir_set_create) or the linear-phase EQ's FIR (tl_fir_set_create).
typedef struct tl_ir_set tl_ir_set;

tl_kernel *_Nullable tl_kernel_create(void);
void tl_kernel_destroy(tl_kernel *kernel);

/// Sets sample rate and routing. Call only while IO is stopped. Resets all state.
void tl_kernel_configure(tl_kernel *kernel, double sample_rate, const tl_layout *layout);

/// Publishes a new parameter block to the IO thread.
void tl_kernel_set_params(tl_kernel *kernel, const tl_params *params);

/// Target gain of one source. The IO thread ramps towards it over one buffer.
void tl_kernel_set_source_gain(tl_kernel *kernel, int32_t source, float gain);

/// Peak/RMS are "max since last read"; reading resets them.
void tl_kernel_read_meters(tl_kernel *kernel, tl_meters *out);

/// Copies the most recent `count` mono samples (pre = 0, post = 1).
/// Returns the total number of samples written so far.
uint64_t tl_kernel_copy_analyzer(tl_kernel *kernel, int32_t which, float *dst, uint32_t count);

/// Neutral parameter block: EQ and simulation stages inactive, unity gains, no effects.
void tl_params_init(tl_params *params);

/// Neutral speakers: no corrections, unity gains, no delays, the EQ on every speaker.
void tl_speakers_init(tl_speakers *speakers);

/// Publishes new speaker settings to the IO thread. Gains ramp over one buffer, switching a
/// speaker's correction on or off crossfades over one buffer, delays crossfade over 20 ms.
void tl_kernel_set_speakers(tl_kernel *kernel, const tl_speakers *speakers);

/// Prepares the impulse responses of a pair of speakers for the kernel. `irs` holds four
/// pointers to `length` samples each: front left -> left ear, front left -> right ear,
/// front right -> left ear, front right -> right ear, at `sample_rate`, which must be the
/// rate the kernel is configured for (resample first). `gain` scales all four. Only the
/// first TL_CONV_MAX_TAPS samples are used. Allocates. NULL when the input is unusable.
tl_ir_set *_Nullable tl_ir_set_create(const float *_Nonnull const *_Nonnull irs, uint32_t length, double sample_rate, float gain);

/// Frees a set that was never handed to a kernel. NULL is ignored.
void tl_ir_set_destroy(tl_ir_set *_Nullable set);

/// Hands `set` (NULL: none) to the IO thread, which crossfades to it. The kernel owns the
/// set from now on. A set for another sample rate than the kernel's is ignored.
void tl_kernel_set_ir_set(tl_kernel *kernel, tl_ir_set *_Nullable set);

/// Frees the sets the IO thread has finished with. Called by tl_kernel_set_ir_set and
/// tl_kernel_set_fir; call it now and then (e.g. with the meters) so a replaced set does not
/// linger.
void tl_kernel_collect(tl_kernel *kernel);

/// Linear-phase EQ: length of the FIR at `sample_rate` for TL_LIN_LOW_LATENCY or
/// TL_LIN_HIGH_PRECISION, a power of two. High precision covers at least 1.25 seconds (the
/// length is what resolves narrow bass bands), up to TL_LIN_MAX_TAPS; low latency is a quarter
/// of it. The FIR is symmetric about tap `taps / 2`, and tap 0 is zero.
uint32_t tl_linear_phase_taps(double sample_rate, int32_t mode);

/// Linear-phase EQ: partition size of the convolution, taps / TL_LIN_PARTS.
uint32_t tl_linear_phase_block(double sample_rate, int32_t mode);

/// Linear-phase EQ: samples of delay it adds, the FIR's own (taps / 2) plus one partition.
uint32_t tl_linear_phase_latency(double sample_rate, int32_t mode);

/// Prepares a linear-phase FIR for the kernel: `left` and `right` (NULL: the same as `left`)
/// hold `length` samples each, the tl_linear_phase_taps of one of the modes at `sample_rate`,
/// which must be the kernel's. Allocates. NULL when the input is unusable.
tl_ir_set *_Nullable tl_fir_set_create(const float *left, const float *_Nullable right, uint32_t length, double sample_rate);

/// Hands the FIR of `mode` (NULL: none) to the IO thread, which crossfades to it. The kernel
/// owns the set from now on. A set of another length or sample rate is ignored.
void tl_kernel_set_fir(tl_kernel *kernel, int32_t mode, tl_ir_set *_Nullable set);

/// The IOProc. Pass the kernel as client data. Signature matches AudioDeviceIOProc.
OSStatus tl_ioproc(AudioObjectID device,
                   const AudioTimeStamp *now,
                   const AudioBufferList *input,
                   const AudioTimeStamp *input_time,
                   AudioBufferList *output,
                   const AudioTimeStamp *output_time,
                   void *_Nullable client_data);

/// For hosts that hand over their own buffers (the Audio Unit): runs the same chain as tl_ioproc
/// (EQ, Listen as, effects, Virtual Speakers, limiter, meters) in place on `frames` samples of
/// non-interleaved channels. `channels[0]` and `channels[1]` are the stereo pair; with one
/// channel it plays as both sides and gets their average back. Further channels are left alone.
/// Any number of frames (longer runs are processed TL_MAX_FRAMES at a time). Ignores the
/// layout's sources and routing, and never processes as a multichannel layout.
void tl_kernel_process(tl_kernel *kernel, float *_Nullable const *_Nonnull channels, uint32_t channel_count, uint32_t frames);

/// Samples of delay the kernel adds with `params` once they play (and their impulse responses
/// and FIR are in): the limiter's look-ahead, Virtual Speakers' partition and the linear-phase
/// EQ's latency, at the configured rate. For hosts that compensate it, e.g. an Audio Unit's latency.
uint32_t tl_kernel_latency(const tl_kernel *kernel, const tl_params *params);

/// Sets kAudioDevicePropertyIOProcStreamUsage for one direction.
/// `stream_on` has one entry per stream of that direction.
OSStatus tl_set_stream_usage(AudioObjectID device,
                             AudioDeviceIOProcID _Nullable proc,
                             int32_t is_input,
                             const uint32_t *stream_on,
                             uint32_t stream_count);

#pragma clang assume_nonnull end

#ifdef __cplusplus
}
#endif

#endif
