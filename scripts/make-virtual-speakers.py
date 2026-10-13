#!/usr/bin/env python3
"""Builds the bundled Virtual Speakers impulse responses from the SADIE II database.

    python3 scripts/make-virtual-speakers.py path/to/D1 Tapline/Resources/VirtualSpeakers

`D1` is the Neumann KU100 folder of SADIE II (D1.zip from https://zenodo.org/records/12092466,
Apache License 2.0, University of York). Only the Python standard library is needed.

Writes two 4-channel 32-bit float WAV files at 48 kHz, channels FL->left ear, FL->right ear,
FR->left ear, FR->right ear, for a pair of speakers at +-30 degrees, 0 degrees elevation:

- "SADIE-II-KU100.wav": the KU100's head-related impulse responses, unchanged apart from
  the format. SADIE II's HRIRs are already diffuse-field equalised (`--check` shows the
  average over all directions), so they suit headphones tuned to a diffuse-field or
  Harman-like target.
- "SADIE-II-KU100-Room.wav": the same direct sound plus the reflections of the room the
  SADIE II BRIRs were measured in (The Audio Lab, University of York). The BRIRs only
  exist for speakers at +-45 degrees, so their direct sound is cut out and replaced by the
  +-30 degree HRIRs. The reflections keep their measured delays and level relative to the
  direct sound, and are high-passed at 500 Hz: the room's bass modes (+11 dB at 125 Hz
  in the raw BRIRs) stay out, and the bass and lower mids sound as clean as the anechoic set.
"""

import argparse
import math
import os
import struct
import sys

RATE = 48000


def read_wav(path):
    data = open(path, "rb").read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        raise ValueError(f"{path}: not a WAV file")
    pos, fmt = 12, None
    while pos + 8 <= len(data):
        cid = data[pos:pos + 4]
        size = struct.unpack("<I", data[pos + 4:pos + 8])[0]
        body = data[pos + 8:pos + 8 + size]
        if cid == b"fmt ":
            fmt = struct.unpack("<HHIIHH", body[:16])
        elif cid == b"data":
            tag, channels, rate, _, align, bits = fmt
            if tag != 1 or bits not in (16, 24):
                raise ValueError(f"{path}: expected 16 or 24-bit PCM")
            width = bits // 8
            scale = float(1 << (bits - 1))
            frames = len(body) // align
            out = [[0.0] * frames for _ in range(channels)]
            for i in range(frames):
                for c in range(channels):
                    o = i * align + c * width
                    out[c][i] = int.from_bytes(body[o:o + width], "little", signed=True) / scale
            return rate, out
        pos += 8 + size + (size & 1)
    raise ValueError(f"{path}: no data chunk")


def write_wav(path, channels, rate):
    frames = len(channels[0])
    body = bytearray()
    for i in range(frames):
        for ch in channels:
            body += struct.pack("<f", ch[i])
    fmt = struct.pack("<HHIIHH", 3, len(channels), rate, rate * 4 * len(channels), 4 * len(channels), 32)
    with open(path, "wb") as f:
        f.write(b"RIFF" + struct.pack("<I", 4 + 8 + len(fmt) + 8 + len(body)) + b"WAVE")
        f.write(b"fmt " + struct.pack("<I", len(fmt)) + fmt)
        f.write(b"data" + struct.pack("<I", len(body)) + body)


def fft(x):
    """Radix-2 FFT of a list whose length is a power of two."""
    n = len(x)
    a = [complex(v) for v in x]
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j:
            a[i], a[j] = a[j], a[i]
    size = 2
    while size <= n:
        half = size // 2
        tw = [complex(math.cos(-2 * math.pi * k / size), math.sin(-2 * math.pi * k / size)) for k in range(half)]
        for start in range(0, n, size):
            for k in range(half):
                u = a[start + k]
                t = a[start + k + half] * tw[k]
                a[start + k] = u + t
                a[start + k + half] = u - t
        size *= 2
    return a


def power_spectrum(x, n):
    return [abs(v) ** 2 for v in fft(list(x) + [0.0] * (n - len(x)))[:n // 2 + 1]]


def onset(x, fraction=0.1):
    peak = max(abs(v) for v in x)
    return next(i for i, v in enumerate(x) if abs(v) >= fraction * peak)


def energy(x):
    return sum(v * v for v in x)


def highpass(x, frequency, rate):
    """Second-order Butterworth high-pass (RBJ cookbook, Q = 0.7071)."""
    w = 2 * math.pi * frequency / rate
    alpha = math.sin(w) / (2 * 0.7071067811865476)
    cw = math.cos(w)
    a0 = 1 + alpha
    b0, b1, b2 = (1 + cw) / 2 / a0, -(1 + cw) / a0, (1 + cw) / 2 / a0
    a1, a2 = -2 * cw / a0, (1 - alpha) / a0
    y, z1, z2 = [], 0.0, 0.0
    for v in x:
        out = b0 * v + z1
        z1 = b1 * v - a1 * out + z2
        z2 = b2 * v - a2 * out
        y.append(out)
    return y


def trim_tail(channels, floor_db=-100):
    peak = max(max(abs(v) for v in ch) for ch in channels)
    limit = peak * 10 ** (floor_db / 20)
    end = max(max((i for i, v in enumerate(ch) if abs(v) > limit), default=0) for ch in channels) + 1
    return [ch[:end] for ch in channels]


def speaker_pair(folder, kind, left_azimuth, right_azimuth):
    """[FL->L, FL->R, FR->L, FR->R]. SADIE II azimuths run anticlockwise: 30 is front left."""
    def load(azimuth):
        rate, ch = read_wav(os.path.join(folder, f"D1_{kind}_WAV", "48K_24bit", f"azi_{azimuth},0_ele_0,0.wav"))
        if rate != RATE or len(ch) != 2:
            raise ValueError(f"unexpected format for azimuth {azimuth}")
        return ch
    left, right = load(left_azimuth), load(right_azimuth)
    return [left[0], left[1], right[0], right[1]]


def room_variant(direct, folder):
    brir = speaker_pair(folder, "BRIR", 45, 315)
    hrir45 = speaker_pair(folder, "HRIR", 45, 315)
    out = []
    for i, (d, b, h) in enumerate(zip(direct, brir, hrir45)):
        ipsi = 0 if i < 2 else 3                  # the ear on the speaker's side
        b_onset = onset(brir[ipsi])
        # The BRIR's direct sound: 2.5 ms from its onset. Level it like the anechoic HRIR at
        # the same angle, so the reflections keep the measured direct-to-reflected ratio.
        cut = b_onset + int(0.0025 * RATE)
        gain = math.sqrt(energy(hrir45[ipsi]) / energy(brir[ipsi][:cut]))
        fade = int(0.001 * RATE)
        reflections = []
        for n, v in enumerate(b):
            if n < cut:
                v = 0.0
            elif n < cut + fade:
                v *= 0.5 - 0.5 * math.cos(math.pi * (n - cut) / fade)
            reflections.append(v * gain)
        reflections = highpass(reflections, 500, RATE)
        # Line the BRIR's onset up with the HRIR's, so each reflection arrives as long after
        # the direct sound as it did in the room.
        shift = onset(direct[ipsi]) - b_onset
        length = max(len(d), len(b) + shift)
        y = [0.0] * length
        for n, v in enumerate(d):
            y[n] += v
        for n, v in enumerate(reflections):
            if 0 <= n + shift < length:
                y[n + shift] += v
        out.append(y)
    return trim_tail(out)


def third_octave_db(channels, n=16384):
    """Smoothed level at the left ear for a centre (mono) source: FL->L + FR->L."""
    centre = [a + b for a, b in zip(channels[0] + [0.0] * (len(channels[2]) - len(channels[0])), channels[2])]
    p = power_spectrum(centre, n)
    row = []
    for f in (63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000):
        lo, hi = int(f / 2 ** (1 / 6) * n / RATE), int(f * 2 ** (1 / 6) * n / RATE) + 1
        row.append((f, 10 * math.log10(sum(p[lo:hi]) / (hi - lo))))
    return row


def check_diffuse_field(folder, n=512):
    """Area-weighted power average over every HRIR direction, per ear."""
    hrirs = os.path.join(folder, "D1_HRIR_WAV", "48K_24bit")
    rings = {}
    for name in os.listdir(hrirs):
        if name.endswith(".wav"):
            rings.setdefault(float(name.split("_ele_")[1][:-4].replace(",", ".")), []).append(name)
    elevations = sorted(rings)
    total, acc = 0.0, [[0.0] * (n // 2 + 1) for _ in range(2)]
    for i, e in enumerate(elevations):
        lo = -90.0 if i == 0 else (elevations[i - 1] + e) / 2
        hi = 90.0 if i == len(elevations) - 1 else (elevations[i + 1] + e) / 2
        weight = (math.sin(math.radians(hi)) - math.sin(math.radians(lo))) / len(rings[e])
        for name in rings[e]:
            _, ch = read_wav(os.path.join(hrirs, name))
            total += weight
            for c in range(2):
                for k, v in enumerate(power_spectrum(ch[c], n)):
                    acc[c][k] += weight * v
    for c in range(2):
        levels = [10 * math.log10(acc[c][k] / total) for k in range(n // 2 + 1) if 50 <= k * RATE / n <= 20000]
        print(f"diffuse-field average, ear {c}: {min(levels):.2f} ... {max(levels):.2f} dB (50 Hz - 20 kHz)")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("d1", help="SADIE II D1 (KU100) folder")
    parser.add_argument("out", help="output folder")
    parser.add_argument("--check", action="store_true", help="also print the diffuse-field average of the HRIRs")
    args = parser.parse_args()

    if args.check:
        check_diffuse_field(args.d1)
    dry = trim_tail(speaker_pair(args.d1, "HRIR", 30, 330))
    room = room_variant(dry, args.d1)
    os.makedirs(args.out, exist_ok=True)
    for name, irs in (("SADIE-II-KU100.wav", dry), ("SADIE-II-KU100-Room.wav", room)):
        length = max(len(x) for x in irs)
        irs = [x + [0.0] * (length - len(x)) for x in irs]
        write_wav(os.path.join(args.out, name), irs, RATE)
        print(f"{name}: {length} samples ({1000 * length / RATE:.1f} ms)")
        print("   centre source, left ear:", "  ".join(f"{f}: {db:+.1f}" for f, db in third_octave_db(irs)))
        for i, label in enumerate(("FL->L", "FL->R", "FR->L", "FR->R")):
            print(f"   {label}: energy {energy(irs[i]):.3f}, onset {onset(irs[i])}")


if __name__ == "__main__":
    sys.exit(main())
