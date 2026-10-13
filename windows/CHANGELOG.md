# Tapline for Windows changelog

## Unreleased

## 0.4.0

Tapline for Windows is out of beta. From this release on it comes out together with Tapline for macOS and has the same version number, so it jumps from 0.1.0-beta.3 to 0.4.0. It brings over the correction strength, level-matched bypass and Virtual Speakers of Tapline 0.4.0 for macOS.

- **Virtual Speakers** on the Effects page: on headphones, the music plays from a pair of speakers in front of you instead of inside your head. Two built-in sets from the SADIE II database (Neumann KU100 dummy head): a treated studio room, and anechoic. Import your own: HeSuVi files, Impulcifer's measurements of your own ears, 4-channel files or two stereo files. It uses Equalizer APO's own convolver, like HeSuVi, and replaces crossfeed. Saved per device and in presets with **Include effects**.

- **Strength** in Assistant → Headphone Correction, 0–100 %, like a Dry/Wet control: apply only part of a correction. squig.link and your own measurements are fitted to a curve that far between the headphones and the target; AutoEQ presets scale each band's gain.
- Switching the EQ off is level-matched: the original plays at the loudness of the EQ (a `Preamp` line in Equalizer APO's configuration) instead of a few dB louder. Turn this off in **Settings → EQ Bypass**.

## 0.1.0-beta.3

Brings over the room-correction changes of Tapline 0.3.0 for macOS.

- Room correction guides the measurement: a map shows where to put the microphone next (your listening spot, then about 20 cm to each side, in front and behind) and how many positions are done, with a note once there are enough to correct.
- While measuring, Room correction shows which speaker is playing, the seconds left and the microphone level.
- Each room measurement is marked **Good**, **Fair** or **Measure again**: clipped, noisy, or clearly unlike the other positions.
- The room chart shows how much the positions disagree (a band of one standard deviation), and each position's curve with **Positions**.
- A warning when the output or the input of a room measurement is a virtual device (VB-Audio, Voicemeeter, SoundID Reference…): its software would end up in the measurement.
- **Correct below** now goes up to 20 kHz, on a log scale, so the EQ can also even out a crossover or tweeter problem. The default stays 500 Hz, which corrects only the room's bass.
- Releases come as an installer only. The portable `.zip` is gone: it had no uninstaller, so deleting it left Tapline's EQ in Equalizer APO's configuration.

## 0.1.0-beta.2

Brings over the fixes from Tapline 0.2.2 for macOS.

- Your EQ profiles and presets are no longer reset when part of Tapline's settings can't be read, for example after going back to an older version. Whatever can't be read is kept as it is, and changes made just before signing out or shutting down are saved.
- Invalid numbers (NaN, infinity) can no longer reach Equalizer APO's configuration. Imported EQ files with such values are skipped, and preamp values are limited to the range of the Preamp knob.
- Opening a different version of Tapline (for example after an update, or a copy run from another folder) replaces the running one instead of only showing its window.
- Room measurements are correct when the speakers and the microphone run at different sample rates (for example a 44.1 kHz interface and a 48 kHz measurement microphone).
- Searching the squig.link measurements is faster.

## 0.1.0-beta.1

First Windows build: a port of Tapline 0.2.1 for macOS, built on Equalizer APO.

- **Equalizer**: parametric EQ with up to 24 bands on a draggable graph, live output spectrum, A/B compare, undo, presets, automatic preamp. Each output device keeps its own EQ, and the EQ follows the default device even while Tapline is closed.
- **Headphone correction**: AutoEQ presets for 5,000+ headphones, fits from 40,000+ squig.link measurements, or your own measurement file.
- **Room correction**: measure speakers with a microphone, setup advice, room-mode correction, RME ADI-2 export.
- **Mixer**: per-app volume and mute (Windows' own app volumes) with level meters.
- **Effects**: balance, stereo width, mono, channel swap, headphone crossfeed, output gain.
- **Notification area**: a quick panel for power, output device, volume, EQ and the apps playing; global shortcuts Ctrl+Alt+Shift+P / B / K.
- English and Simplified Chinese.

Differences from macOS: Windows needs Equalizer APO installed on each output device; there is no limiter (keep the preamp on Auto); per-app bypass is not available (exclusive-mode and ASIO apps are never processed); the spectrum shows the output only.
