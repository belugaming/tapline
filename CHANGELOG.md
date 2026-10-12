# Changelog

All notable changes to Tapline are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

The release workflow publishes the section of the tagged version as the
GitHub release notes, so write entries for users.

## [Unreleased]

### Fixed
- Tapline no longer crashes when Room correction's **Check Level** or **Measure** cannot start the speakers or the microphone. It shows the reason instead.

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

[Unreleased]: https://github.com/belugaming/tapline/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/belugaming/tapline/compare/v0.2.3...v0.3.0
[0.2.3]: https://github.com/belugaming/tapline/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/belugaming/tapline/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/belugaming/tapline/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/belugaming/tapline/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/belugaming/tapline/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/belugaming/tapline/releases/tag/v0.1.0
