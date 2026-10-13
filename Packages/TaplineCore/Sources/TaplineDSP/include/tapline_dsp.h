// Tapline realtime DSP kernel.
//
// Everything that runs on the CoreAudio IO thread lives here. The kernel mixes
// the tapped process streams, runs the EQ / effects chain and writes the result
// into the output device's preferred stereo channels.
//
// Threading model:
//   - tl_kernel_set_params / tl_kernel_set_source_gain / tl_kernel_set_ir_set /
//     tl_kernel_collect: any single non-realtime thread (the main thread in practice).
//     Lock-free.
//   - tl_ir_set_create / tl_ir_set_destroy: any non-realtime thread.
//   - tl_kernel_configure / tl_kernel_reset: only while IO is stopped.
//   - tl_kernel_read_meters / tl_kernel_copy_analyzer: any thread. Lock-free.
//   - tl_ioproc: the IO thread.

#ifndef TAPLINE_DSP_H
#define TAPLINE_DSP_H

#include <stdint.h>
#include <CoreAudio/CoreAudio.h>

#ifdef __cplusplus
extern "C" {
#endif

#pragma clang assume_nonnull begin

#define TL_MAX_STAGES 32
#define TL_MAX_SOURCES 32
#define TL_MAX_FRAMES 8192
#define TL_ANALYZER_SIZE 32768

/// Virtual speakers: partition size of the convolution, which is also the latency it adds.
#define TL_CONV_BLOCK 256
/// Longest impulse response the convolution uses (at the device rate); longer ones are cut.
#define TL_CONV_MAX_TAPS 65536

typedef struct {
    double b0, b1, b2, a1, a2;
    int32_t active;
    int32_t channels;       // 0 = both, 1 = left only, 2 = right only
} tl_biquad;

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
} tl_layout;

typedef struct {
    float peak_l, peak_r;
    float rms_l, rms_r;
    float gain_reduction_db;   // limiter, positive number
    uint32_t clipped_samples;
    uint64_t io_cycles;
    float source_peak[TL_MAX_SOURCES];
} tl_meters;

typedef struct tl_kernel tl_kernel;

/// Impulse responses of a pair of virtual speakers, transformed for the realtime kernel.
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

/// Neutral parameter block: EQ stages inactive, unity gains, no effects.
void tl_params_init(tl_params *params);

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

/// Frees the sets the IO thread has finished with. Called by tl_kernel_set_ir_set; call it
/// now and then (e.g. with the meters) so a replaced set does not linger.
void tl_kernel_collect(tl_kernel *kernel);

/// The IOProc. Pass the kernel as client data. Signature matches AudioDeviceIOProc.
OSStatus tl_ioproc(AudioObjectID device,
                   const AudioTimeStamp *now,
                   const AudioBufferList *input,
                   const AudioTimeStamp *input_time,
                   AudioBufferList *output,
                   const AudioTimeStamp *output_time,
                   void *_Nullable client_data);

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
