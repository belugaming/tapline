# Changelog

All notable changes to Tapline are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

The release workflow publishes the section of the tagged version as the
GitHub release notes, so write entries for users.

## [Unreleased]

### Added
- **Listen As** on the Assistant page: hear how your music would sound on other playback devices, through your own headphones, like Sonarworks SoundID's Translation Check. Pick headphones or earbuds from squig.link (AirPods, EarPods, Galaxy Buds…) and Tapline fits an EQ from the target your headphone correction aims at to that device: the raw difference when both were measured in the same database, else through each database's own target so the measurement rigs cancel. Or pick one of five built-in speakers: phone, laptop, TV, small Bluetooth speaker and car, approximations with their channel handling (mono, narrow stereo). It plays on top of your EQ without changing it, switching is level-matched, and it is never saved in presets. Switch it on the card, in the menu bar panel or with ⌃⌥⌘L; Tapline remembers the last device and starts with it off.

- **Surround and Dolby Atmos speakers** (5.1, 7.1 and up to 9.1.6, for example 7.1.4): Tapline now processes every channel of a multichannel speaker layout, like Sonarworks SoundID Reference's multichannel calibration. On such an output the Room page shows the speaker layout, checks the wiring (pink noise on each speaker in turn), measures every speaker at several positions, reports each one's level, distance and polarity, and fits a correction for each speaker with the trims and delays that line them up at your seat. Surrounds and heights get their own **Correct below** (300 Hz); the LFE channel is corrected below 120 Hz and never boosted above it. **Copy Filters as Text** gives Equalizer APO sections per channel (`Channel:`, `Preamp:`, `Delay:`). Tapline reads the layout from macOS (Audio MIDI Setup › Configure Speakers, or the receiver over HDMI), and Apple Music's Dolby Atmos, which macOS renders to that layout, is processed like any other sound.
- **Effects › Speakers** on a multichannel output: play the EQ on every speaker or on the front pair only, and switch the speaker correction off and on. Balance, width, mono and swap act on the front left and right; crossfeed, Virtual Speakers and Listen As are for headphones and stay off there.

## [0.4.0] - 2026-10-13

### Added
- **Virtual Speakers** on the Effects page: on headphones, the music plays from a pair of speakers in front of you instead of inside your head, like Sonarworks SoundID Virtual Monitoring. Two built-in sets from the SADIE II database (Neumann KU100 dummy head): a treated studio room, and anechoic. Import your own: HeSuVi files, Impulcifer's measurements of your own ears, 4-channel files or two stereo files. Saved per device and in presets with **Include effects**. It replaces crossfeed and adds 5 ms of latency while it is on.
- **Strength** in Assistant → Headphone Correction, 0–100 %, like a Dry/Wet control: apply only part of a correction. For squig.link measurements and your own measurement, the EQ is fitted to a curve that far between the headphones and the target, so it stays accurate; the green line and *Deviation* show the result. AutoEQ presets scale each band's gain.
- **Settings → EQ Bypass → Match loudness when bypassing** (on by default).

### Changed
- Switching the EQ off (EQ switch, ⌘B, ⌃⌥⌘B, menu bar) is now level-matched: the original plays at the loudness of the EQ instead of a few dB louder, because a correction's preamp makes it quieter and louder tends to sound better. The gain is the EQ's loudness change on pink noise, weighted like a LUFS meter, and is never a boost. It shows next to the EQ switch while the EQ is off.
- Switching the EQ on or off crossfades over a few milliseconds instead of cutting, so it no longer clicks.

### Fixed
- The controls at the top right of the Equalizer page (input spectrum, range, more) keep their size while you edit the EQ. On a narrow window they were squeezed whenever the preset name changed, for example from a preset's name to "Custom".

## [0.3.2] - 2026-10-12

### Changed
- Releases are signed with Tapline's own certificate instead of a new ad-hoc signature for every build, so macOS keeps Tapline's audio and microphone permissions across updates. Updating to the first signed release asks for them once more. It is still not notarized: the first launch still needs **Open Anyway**.
- Tapline uses less energy while only its menu bar icon is showing, or its window is minimized or covered. It then checks the audio 4 times a second instead of 30, and the spectrum analyzer pauses.

## [0.3.1] - 2026-10-12

### Fixed
- Room correction's **Check Level** and **Measure** no longer crash Tapline as they start (seen on macOS 27.2): the microphone is recorded in the format it actually runs at.
- When the audio devices for a room measurement cannot start, Room correction shows why instead of crashing Tapline.

## [0.3.0] - 2026-10-12

### Added
- Room correction guides the measurement: a map shows where to put the microphone next (your listening spot, then about 20 cm to each side, in front and behind) and how many positions are done, with a note once there are enough to correct.
- While measuring, Room correction shows which speaker is playing, the seconds left and the microphone level.
- Each room measurement is marked **Good**, **Fair** or **Measure again**: clipped, noisy, or clearly unlike the other positions.
- The room chart shows how much the positions disagree (a band of one standard deviation), and each position's curve with **Positions**.
- A warning when the output or the input of a room measurement is a virtual device: software behind it, such as SoundID Reference, would end up in the measurement.

### Changed
- Room correction's **Correct below** now goes up to 20 kHz, on a log scale, so the EQ can also even out a crossover or tweeter problem. The default stays 500 Hz, which corrects only the room's bass.

### Fixed
- Room correction's **Check Level** and **Measure** no longer crash on macOS 27.

## [0.2.3] - 2026-10-11

### Fixed
- Room correction's **Check Level** judges the loudest moment of the test noise. It used to read the silence after the noise and almost always said the level was too quiet.

## [0.2.2] - 2026-10-11

### Changed
- Releases are now a disk image (`.dmg`) instead of a zip: open it and drag Tapline onto the Applications shortcut.

### Fixed
- Your EQ profiles, presets and app volumes are no longer wiped when part of them cannot be read, for example after going back to an older version. Whatever can't be read is kept as it is, and changes made just before quitting are saved.
- The feedback guard no longer switches processing off because of loud settings (output gain, manual preamp, app volume above 100 %). It now reacts only to sound far above full scale, and processing comes back on at the next launch.
- Your Mac can idle-sleep again while Tapline is running.
- Invalid numbers (NaN, infinity) can no longer reach your speakers: Tapline silences them and recovers on its own. Imported EQ files with such values are skipped, and preamp values are limited to the range of the Preamp knob.
- Opening a different version of Tapline (for example after an update) replaces the running copy instead of quietly quitting. Opening the same version again shows its window, even when it was hidden in the menu bar.
- Typing in the squig.link search no longer makes the window stutter.
- Room measurements are correct when the speakers and the microphone run at different sample rates (for example a 44.1 kHz interface and a 48 kHz measurement microphone).
- `scripts/release.sh` accepts a final release after its pre-releases (0.3.0 after 0.3.0-beta.1).

## [0.2.1] - 2026-10-10

### Changed
- ⌘Q now hides Tapline to the menu bar: the window closes and the Dock icon goes away, while audio processing keeps running. Quit completely with ⌥⌘Q or the power button in the menu bar panel.

## [0.2.0] - 2026-10-10

### Added
- Room correction: measure your speakers with a microphone (sine sweeps, several positions averaged, microphone calibration files) and fit an EQ that cuts room resonances below a chosen frequency, with flat or room-curve targets. Boosts stop at the speakers' low-frequency limit.
- **Left & right** measurements: each speaker separately in one run, with timing clicks that cancel the clock drift between the playing and the recording interface.
- Setup check: polarity, left/right balance and distance, subwoofer level, boomy lower bass, desk bounce, treble trend, room resonances and cancellation dips, with the speaker switches to try (Genelec Bass Tilt, Desktop Control, Treble Tilt).
- Separate left and right corrections. EQ bands can now apply to the left or right channel only, on the EQ page too.
- RME ADI-2 DAC: fit the room correction to its five-band EQ (Dual EQ for separate channels) and export an `.adieqpr` preset for ADI-2 Remote. Import & Export also reads ADI-2 presets and exports any EQ to one (shelves become Bass and Treble).
- `Channel: L` / `Channel: R` lines in Equalizer APO text are read and written.

### Changed
- When drift compensation is needed (USB, built-in and other wired outputs), the HAL now uses its highest-quality converter.

### Fixed
- Install instructions: on macOS 15 and later, unnotarized apps are allowed with **Open Anyway** in System Settings → Privacy & Security, not by right-clicking.

## [0.1.1] - 2026-10-10

### Added
- Builds and releases are produced by GitHub Actions; every push is built with Xcode 26 and Xcode 27.
- `scripts/release.sh` bumps the version, updates this changelog and tags the release.
- The About section shows the build number.

### Changed
- Tapline now taps the output device's own stream instead of a system-wide stereo mixdown, so captured audio runs on the device's clock and only contains audio headed for that device.
- Drift compensation is chosen per device: off for Bluetooth and virtual outputs, on for USB, built-in and other wired outputs.
- Channels other than the stereo pair are passed through on multichannel interfaces.
- Tap updates use the Core Audio C API, so the project builds with the Xcode 26 SDK.

### Fixed
- Distorted sustained notes, occasional crackles, pitch jumps and silence after changing tracks on AirPods and other Bluetooth headphones.

## [0.1.0] - 2026-10-10

First public beta.

### Added
- System-wide parametric EQ (up to 24 bands) with a live spectrum analyzer, draggable bands, A/B comparison, undo/redo and automatic preamp.
- A separate EQ profile for every output device, switched automatically.
- Headphone correction from AutoEQ presets, squig.link measurements (EQ fitted on the fly) or your own measurement file, with bass, tilt and "keep my bass" options.
- Quick Tone controls and the Sound Assistant.
- Per-app volume, mute and bypass.
- Limiter, output gain, balance, stereo width, mono, channel swap and headphone crossfeed.
- Output/input device and sample-rate switching, menu bar panel, global shortcuts.
- Presets that can include effects.
- English and Simplified Chinese.

### Fixed
- Sustained notes no longer wobble on Bluetooth outputs such as AirPods.

[Unreleased]: https://github.com/belugaming/tapline/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/belugaming/tapline/compare/v0.3.2...v0.4.0
[0.3.2]: https://github.com/belugaming/tapline/compare/v0.3.1...v0.3.2
[0.3.1]: https://github.com/belugaming/tapline/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/belugaming/tapline/compare/v0.2.3...v0.3.0
[0.2.3]: https://github.com/belugaming/tapline/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/belugaming/tapline/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/belugaming/tapline/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/belugaming/tapline/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/belugaming/tapline/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/belugaming/tapline/releases/tag/v0.1.0
