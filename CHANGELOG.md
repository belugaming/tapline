# Changelog

All notable changes to Tapline are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and versions follow
[Semantic Versioning](https://semver.org/).

The release workflow publishes the section of the tagged version as the
GitHub release notes, so write entries for users.

## [Unreleased]

### Added
- Builds and releases are produced by GitHub Actions; every push is built with Xcode 26 and Xcode 27.
- `scripts/release.sh` bumps the version, updates this changelog and tags the release.
- The About section shows the build number.

### Changed
- Tap updates use the Core Audio C API, so the project builds with the Xcode 26 SDK.

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

[Unreleased]: https://github.com/belugaming/tapline/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/belugaming/tapline/releases/tag/v0.1.0
