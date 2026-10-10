# Changelog

All notable changes to Tapline are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

The release workflow publishes the section of the tagged version as the
GitHub release notes, so write entries for users.

## [Unreleased]

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

[Unreleased]: https://github.com/belugaming/tapline/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/belugaming/tapline/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/belugaming/tapline/releases/tag/v0.1.0
