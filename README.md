<p align="center">
  <img src="docs/icon.png" width="128" alt="Tapline icon">
</p>

<h1 align="center">Tapline</h1>

<p align="center">
  <a href="https://github.com/belugaming/tapline/actions/workflows/ci.yml"><img src="https://github.com/belugaming/tapline/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="https://github.com/belugaming/tapline/releases/latest"><img src="https://img.shields.io/github/v/release/belugaming/tapline?include_prereleases&sort=semver" alt="Release"></a>
  <img src="https://img.shields.io/badge/macOS-26%2B-black?logo=apple" alt="macOS 26+">
  <img src="https://img.shields.io/badge/Apple%20Silicon-arm64-black" alt="Apple Silicon">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/belugaming/tapline" alt="License: GPL-3.0"></a>
</p>

<p align="center">
  <b>A system-wide parametric EQ, per-app mixer and headphone correction tool for macOS.</b><br>
  No audio driver, no virtual device: Tapline is built on Core Audio process taps.<br>
  <a href="README.zh-CN.md">简体中文</a>
</p>

https://github.com/user-attachments/assets/63696f9b-a6cf-421d-9467-1525775c0ca1

![Tapline equalizer](docs/images/en/equalizer.png)

## What is Tapline?

Tapline sits between your apps and your speakers or headphones and lets you shape everything you hear: a precise parametric EQ with a live spectrum analyzer, per-app volume, headphone correction curves computed from published measurements, and a handful of listening effects. It lives in the menu bar, follows whatever output macOS is using, and keeps a separate setup for every device.

What makes it different:

- **No driver to install.** Tapline uses the process tap API that macOS 14.2+ provides. Nothing is added to the system, and if Tapline quits, audio simply goes back to normal.
- **An EQ you can see.** Drag bands on the frequency response, watch the spectrum before and after the EQ, compare A/B, undo anything.
- **Headphone correction that keeps up.** Apply one of 5,000+ AutoEQ presets, or let Tapline fit an EQ from 40,000+ raw measurements on squig.link, including headphones released last week.
- **Room correction for speakers.** Measure your speakers with a microphone where you sit: Tapline checks balance, distance, polarity and placement, tames the room's bass resonances, and can write the correction into an RME ADI-2 DAC.
- **Made for listening, safe for production.** Bypass a DAW in the mixer and it plays untouched.
- **Free and open source** (GPL-3.0), written in Swift with a realtime core in C.

## Contents

- [Requirements](#requirements)
- [Install](#install)
- [Windows (beta)](#windows-beta)
- [Quick start](#quick-start)
- Tutorials
  - [1. Shape the sound with the EQ](#1-shape-the-sound-with-the-eq)
  - [2. Correct your headphones](#2-correct-your-headphones)
  - [3. Correct your speakers and room](#3-correct-your-speakers-and-room)
  - [4. Quick adjustments without EQ knowledge](#4-quick-adjustments-without-eq-knowledge)
  - [5. Set the volume of each app](#5-set-the-volume-of-each-app)
  - [6. Effects](#6-effects)
  - [7. Several outputs and setups](#7-several-outputs-and-setups)
  - [8. Menu bar and keyboard shortcuts](#8-menu-bar-and-keyboard-shortcuts)
  - [9. Reading the meters](#9-reading-the-meters)
- [FAQ and troubleshooting](#faq-and-troubleshooting)
- [How it works](#how-it-works)
- [Privacy and network use](#privacy-and-network-use)
- [Build from source](#build-from-source)
- [Versioning and releases](#versioning-and-releases)
- [Credits](#credits) · [License](#license)

## Requirements

- macOS 26 Tahoe or later
- A Mac with Apple Silicon

## Install

1. Download `Tapline-x.y.z-arm64.dmg` from the [latest release](https://github.com/belugaming/tapline/releases/latest).
2. Open it and drag **Tapline.app** onto the **Applications** shortcut next to it.
3. Open Tapline. Release builds are ad-hoc signed and not notarized yet, so macOS blocks the first launch. Go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to the message about Tapline. (Since macOS 15, right-clicking and choosing Open no longer works for this.)

To update, quit Tapline and replace the app with the new one. macOS asks for the audio permission again after each update, because ad-hoc signed builds have a new identity every time.

## Windows (beta)

Tapline also runs on 64-bit Windows 10 and 11. The Windows version has the same pages (EQ, headphone and room correction, mixer, effects, devices) and keeps a separate EQ for every output device. It is new and has not been tested on many PCs yet: please report what works and what doesn't in [Issues](https://github.com/belugaming/tapline/issues).

Windows has no equivalent of the macOS process taps, so Tapline uses [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) (free, open source) as its audio engine and writes its EQ into Equalizer APO's configuration.

1. Install [Equalizer APO](https://sourceforge.net/projects/equalizerapo/). At the end its Configurator opens: tick the output devices you use (speakers, headphones…), then restart Windows.
2. Download `Tapline-x.y.z-win-x64-setup.exe` from the [releases](https://github.com/belugaming/tapline/releases) whose title starts with "Tapline for Windows", and run it. It is not code-signed yet, so SmartScreen may warn: click **More info → Run anyway**.
3. The first time, Tapline asks before it replaces Equalizer APO's configuration; the old one is kept as `config-before-tapline.txt` and can be restored under **Devices → Audio Engine**.

Differences from macOS:

- The EQ keeps working while Tapline is closed, because Equalizer APO applies it. Switch processing off (power button, tray panel or Ctrl+Alt+Shift+P) to hear the original sound.
- There is no limiter: keep the preamp on **Auto** so boosts can't clip.
- The mixer sets Windows' own per-app volumes (up to 100 %). Per-app bypass isn't possible; apps that use exclusive mode or ASIO are never processed.
- The spectrum shows the output after the EQ only.
- Tapline lives in the notification area instead of the menu bar. Global shortcuts are Ctrl+Alt+Shift+P / B / K.
- Windows on ARM isn't supported, because Equalizer APO only exists for x64.

## Quick start

1. **Quit other audio tools** such as SoundSource or eqMac. Two tools processing the same audio interfere with each other; Tapline shows a warning while one is running.
2. **Turn Tapline on** with the power button at the bottom left of the window (or press ⌃⌥⌘P).
3. **Allow System Audio Recording** when macOS asks. This is what lets Tapline process other apps' sound. Audio never leaves your Mac.
4. **Play something.** The spectrum starts moving and the meters on the right show the output level.
5. **Pick a preset** from the menu at the top of the Equalizer page, or continue with the tutorials below.

Tapline keeps running in the menu bar when you close the window or press ⌘Q; the Dock icon goes away until you open the window again. To quit completely, use the power button in the menu bar panel or ⌥⌘Q. Turn on **Settings → Open Tapline at login** to have it start with your Mac.

## Tutorials

### 1. Shape the sound with the EQ

The **Equalizer** page is the heart of Tapline. The large graph shows the EQ curve (white) on top of the live spectrum of what is playing (blue). The faint grey line is the spectrum before the EQ.

| To | Do this |
|---|---|
| Move a band | Drag its numbered point: left/right for frequency, up/down for gain |
| Change the width (Q) | Scroll or pinch over a point |
| Add a band | Double-click an empty spot of the graph, or click **Add Band** |
| Delete a band | ⌥-click it, press Delete, or use the trash button |
| Fine adjustments | Hold ⌥ while dragging, or use the arrow keys on the selected band |
| Change the filter type | Right-click a point, or use the shape buttons below the graph |

Below the graph:

- **Band chips** list every band. Click one to select it.
- **Inspector**: filter type (bell, low/high shelf, low/high pass, notch, band pass), on/off switch and three knobs for **Frequency**, **Gain** and **Q**. Drag a knob up or down, double-click it to reset, or click its value to type a number (`1.2k` works).
- **Preamp** with **Auto**: lowers the level by exactly the largest boost of your curve, so the EQ cannot clip. This is why boosting the bass makes everything a little quieter; turn the volume up to compensate.

At the top of the page:

- **EQ** switch: turn the EQ off for this device. Press **⌃⌥⌘B** anywhere to compare with the original sound.
- **Preset menu**: built-in presets (Bass Boost, Vocal Clarity, Loudness and more) and your own. **Delete Preset** is at the bottom of the menu.
- **Save** (download icon): save the current curve as a preset. Tick **Include effects** to store crossfeed, limiter and the other effects with it.
- **A / B**: two independent settings to switch between. Make a change on B, then toggle to hear the difference.
- **Range** (±6 to ±24 dB) and the **input spectrum** toggle.
- **⋯**: reset to flat, copy A to B, or copy the curve as Equalizer APO text.

Undo and redo (⌘Z / ⇧⌘Z) cover every change.

### 2. Correct your headphones

Every headphone colours the sound. Correction curves bring it closer to a neutral target. Open **Assistant → Headphone Correction** and pick a source:

![Headphone correction](docs/images/en/headphone-correction.png)

**AutoEQ presets**: ready-made curves for 5,000+ headphones. Search your model, pick a result and click **Apply Correction**. Results from oratory1990 and crinacle are listed first. The AutoEQ database was last updated in July 2025, so newer headphones may be missing.

**squig.link measurements**: raw measurements from 130+ reviewer databases, updated continuously. Tapline computes the EQ itself:

1. Search your model. Each result shows who measured it and with which rig. The name usually says in which mode it was measured (ANC on/off, wired, passive, a volume step…). **Pick the mode you actually use.**
2. Choose a **Target**. Each database offers targets that match its rig; a Harman target is a good start.
3. Read the chart: **grey** is the headphone as measured, **dashed** is the target, **green** is the result with Tapline's EQ. The closer green follows dashed, the better. *Deviation* shows the average difference before → after.
4. Adjust to taste: **Bass** adds a low shelf on top of the target, **Tilt** makes the balance brighter (right) or warmer (left), **Filters** sets how many bands are used (10 is plenty).
5. Click **Apply Correction**. The curve is applied to the current output and saved as a preset you can keep editing.

**My measurement**: import a frequency response you measured yourself (REW, miniDSP EARS, CSV) and fit it to a target from any database.

Tips:

- **Keep my bass** leaves everything below 250 Hz alone. Use it when you listen differently from how the headphones were measured, for example noise-cancelling headphones used passively (powered off) on a cable: their bass changes a lot when the electronics are off, so the measured bass correction would be wrong.
- Treble above 8 kHz is only corrected broadly, because measurement rigs disagree a lot up there.
- AirPods and other adaptive headphones change their own EQ with volume and fit. Corrections for them are approximate; compare with ⌃⌥⌘B and keep what sounds better.

### 3. Correct your speakers and room

In a room, the bass of any speaker is shaped by the walls: some notes boom, others nearly vanish. The **Room** page measures your speakers with a microphone at your listening position, tells you what to fix at the speakers first, and computes an EQ, either for Tapline or for the EQ built into an RME ADI-2 DAC.

![Room correction](docs/images/en/room.png)

**What you need**: a microphone on an input your Mac can see. A measurement microphone (miniDSP UMIK-1/UMIK-2, or an XLR measurement mic on an audio interface) is best; a good studio condenser microphone works well for the bass. Condenser microphones need phantom power (48 V) on the interface. The Mac's built-in microphone only gives a rough idea. The microphone and the speakers can be on different interfaces.

1. **Prepare**: pause all music, keep the room quiet and turn the volume down to a comfortable level. Measurements play at the current output volume. Switch off any EQ, Bass/Treble or Loudness in your DAC (an RME ADI-2 included) so Tapline measures the speakers as they are. The same goes for software behind a virtual output device, such as SoundID Reference: Tapline warns when the output or the input is a virtual device.
2. **Microphone**: choose the **Input** and **Channel** your microphone is on. Load its **Calibration** file if it came with one. Place it at ear height where you sit, pointing between the speakers.
3. **Check Level**: Tapline plays three seconds of pink noise. Adjust the volume or the preamp gain until the microphone peaks between −30 and −10 dB.
4. **Measure**: choose **Left & right**. One run plays an 8-second sweep on each speaker, then three short clicks for timing (about 22 seconds in all). Measure three to five positions around your head; Tapline averages them. A map below the button shows where to put the microphone next (your listening spot, then about 20 cm to the left, right, front and back) and how many positions are done. While a measurement runs you see which speaker is playing, the seconds left and the microphone level. Each measurement is marked **Good**, **Fair** or **Measure again** (clipped, noisy, or clearly unlike the other positions); delete it with the bin and measure that spot again. **Together** measures both speakers at once, **Left** and **Right** one side.
5. **Check the setup**: Tapline lists what is better fixed at the speakers than with EQ:
   - **Polarity**: one speaker wired the other way round.
   - **Balance**: one side louder. Fix it on the speaker or in the DAC, or let Tapline set its balance.
   - **Distance**: one speaker farther away, in centimetres. The timing clicks cancel the clock drift between two interfaces, so this works with the microphone on one interface and the speakers on another.
   - **Subwoofer level**, **boomy lower bass** and **desk bounce**, with the speaker switch that fixes it (on Genelec monitors: Bass Tilt, Desktop Control, Treble Tilt).
   - **Room resonances**, with the room dimension each one fits, and **deep dips** that EQ cannot fill, with what to try instead (the subwoofer's phase switch, moving the seat or the speakers).
6. **Correct**: choose where the correction runs.
   - **Tapline** corrects what this Mac plays. Click **Apply to …**; the correction is saved as a preset.
   - **RME ADI-2 DAC** fits the DAC's own parametric EQ: five bands per channel, bands 4 and 5 from 200 Hz, 0.5 dB steps. Click **Export for ADI-2 Remote…**, then in RME's ADI-2 Remote app open **EQ Presets**, click **File – Load**, pick the file and **Store** it to the unit. The DAC then corrects every source connected to it. **Preview in Tapline** lets you listen first. Use one or the other, not both, or the room is corrected twice.
   - **Correct left and right separately** (on after a Left & right measurement) fits each speaker on its own; on the ADI-2 this uses Dual EQ.
   - The chart shows the measured response (grey), the target (dashed) and the result (green), and the filters are listed below it. With several positions, a grey band shows how much they disagree (one standard deviation): where it is wide the average is less certain. **Positions** draws each one. **Correct below** (default 500 Hz) leaves your speakers' own tuning above it alone. It goes up to 20 kHz: above about 2 kHz the EQ also corrects the speakers themselves (a crossover dip, a bright tweeter), which needs several positions and preferably a calibrated microphone. **Max boost** (default 3 dB) keeps dips from being boosted much: they come from sound cancelling itself at your seat, and boosting them mostly costs amplifier headroom. Tapline also never boosts below your speakers' low-frequency limit. Choose **Flat** or **Room curve** (a gentle bass lift and treble roll-off) and fine-tune with **Bass** and **Tilt**.

**Copy Filters as Text** gives the filters in Equalizer APO format (with `Channel: L` / `Channel: R` sections for separate corrections) for any other hardware EQ.

Tips: measurements bypass Tapline's own EQ and balance, so measuring again always shows the room as it is. To check a correction loaded into the ADI-2, measure again with its EQ on. Subwoofer crossover settings and time alignment are left to the subwoofer's own controls; if your speakers have a calibration system of their own (such as Genelec GLM for SAM models), use that first.

### 4. Quick adjustments without EQ knowledge

Further down the **Assistant** page:

- **Quick Tone**: four knobs, **Bass** (105 Hz shelf), **Warmth** (250 Hz), **Clarity** (3 kHz) and **Air** (10 kHz shelf), layered on top of the EQ.
- **Sound Assistant**: describe what you hear ("More bass", "Less boomy", "Less muddy", "Clearer vocals", "Less harsh", "Less sibilance", "More air"…). Each click makes a small change you can undo.
- **Presets**: every preset with a preview of its curve. Your own presets have rename and delete buttons.
- **Import & Export**: paste or import Equalizer APO / AutoEQ `ParametricEQ.txt` files or RME ADI-2 presets (`.adieqpr`), or export yours. **Export for RME ADI-2…** turns the current EQ into an ADI-2 preset: bells go to its five bands, one low shelf to Bass and one high shelf to Treble.

### 5. Set the volume of each app

![Mixer](docs/images/en/mixer.png)

The **Mixer** page lists the apps that use audio, with the ones playing right now at the top.

- Drag an app's slider to change its volume from 0 to 200%, on top of the app's own volume. It snaps to 100%.
- Click the speaker icon to mute an app.
- **⋯ → Bypass Tapline** lets an app play directly to the output, without EQ or effects. Use this for music production software such as Logic Pro, for calls, or for anything you want to hear untouched.
- **⋯ → Reset** returns an app to the system mix.

The first time you change an app's volume, Tapline rebuilds its audio path, which causes a short gap. Helper processes (browser tabs, Electron apps) are grouped under their app.

### 6. Effects

![Effects](docs/images/en/effects.png)

The **Effects** page works on everything that plays through the current device:

- **Limiter**: catches peaks so boosts never clip. Leave it on; the **GR** meter shows when it acts.
- **Output Gain**: overall level after the EQ.
- **Balance**, **Stereo Width** (0% is mono), **Mono** and **Swap left and right**.
- **Headphone Crossfeed**: blends a little of each channel into the other, like listening to speakers. It makes hard-panned recordings less tiring on headphones.

### 7. Several outputs and setups

![Devices](docs/images/en/devices.png)

Tapline follows the output selected in macOS and keeps **a separate EQ and effects setup for every device**. Plug in your headphones and their curve is applied; switch back to your speakers and theirs returns.

- The **Devices** page lists outputs and inputs. Click **Use** to switch, or change a device's sample rate.
- The output menu in the toolbar switches devices from any page.
- If one device feeds both headphones and speakers (an audio interface, for example), save two presets with **Include effects** ("Headphones" with crossfeed, "Speakers" without) and switch between them from the preset menu or the menu bar.

### 8. Menu bar and keyboard shortcuts

<img src="docs/images/en/menubar.png" width="340" align="right" alt="Menu bar panel">

Click the Tapline icon in the menu bar for a compact panel: power switch, output device, system volume, EQ on/off with the current curve, preset menu and the apps playing right now. Click the curve to open the main window.

Global shortcuts work in every app (turn them off in **Settings**):

| Shortcut | Action |
|---|---|
| ⌃⌥⌘P | Turn processing on or off |
| ⌃⌥⌘B | Bypass the EQ (compare with the original) |
| ⌃⌥⌘K | Switch between A and B |

Inside the app: ⌘Z / ⇧⌘Z undo and redo, ⌘B bypasses the EQ, ⌘K compares A/B, ⌘Q hides Tapline to the menu bar, ⌥⌘Q quits.

<br clear="right">

### 9. Reading the meters

The column next to the graph shows the output level of the left and right channel:

- **Solid green** is the average level, the **lighter part above it** the short peaks, and the **white tick** the highest peak of the last moment, also shown as a number in dB. 0 dB is the digital maximum.
- The **dot at the top** turns red when the signal clips.
- **GR** (gain reduction) shows how much the limiter is turning peaks down. Empty is ideal. If it is often busy, lower the preamp or turn on **Auto**.

## FAQ and troubleshooting

**I hear nothing after turning Tapline on.**
Check **Settings → Permission**. If System Audio Recording is denied, click **Open Privacy Settings** and allow Tapline, then turn it on again. Without the permission macOS delivers silence while the apps are muted.

**Everything got quieter after I applied an EQ.**
That is the automatic preamp making room for your boosts so nothing clips. Turn up the volume. You can switch **Auto** off under the preamp knob and rely on the limiter instead.

**Does Tapline change the sound when the EQ is flat?**
Practically no. Audio is processed in 32-bit float at the device's sample rate, and inactive filters are skipped. Three things can touch it: the limiter on peaks above −0.3 dBFS (turn it off in **Effects** if you want), the HAL's sample-rate drift compensation on USB and built-in outputs (high-quality, very small ratio), and about 12 ms of added latency.

**What about Dolby Atmos and Spatial Audio?**
This has not been tested thoroughly. If an Atmos track or head-tracked Spatial Audio sounds different with Tapline on, set the app (for example Music) to **Bypass Tapline** in the mixer. It then plays directly with full spatial rendering, without Tapline's EQ.

**Bluetooth headphones crackle or wobble.**
Update to 0.1.1 or later, which taps the device's own stream and handles Bluetooth clocks correctly.

**Can I use a studio microphone for room correction?**
Yes. A good condenser microphone is accurate enough in the bass, which is what room correction is about. Point a cardioid microphone between the speakers at ear height, switch on phantom power, and measure a few positions. Tapline uses the microphone only while measuring; macOS asks for microphone access the first time.

**SoundSource or another tool is running.**
Quit it. Its audio daemon may stay in the background; Tapline only warns while it is actually processing audio.

**macOS asks for the permission again after an update.**
Release builds are ad-hoc signed, so every build is a new app to macOS. Allow it again. Old entries can be removed in **System Settings → Privacy & Security → Screen & System Audio Recording**.

**How do I uninstall Tapline?**
Quit it from the menu bar panel, delete Tapline.app, and optionally remove its settings and caches:

```bash
defaults delete io.github.belugaming.Tapline
rm -rf ~/Library/Caches/io.github.belugaming.Tapline
```

## How it works

```
apps ──► process taps of the output device's stream (apps are muted at the source while Tapline reads them)
     ──► private aggregate device = [taps + the output device as clock]
     ──► C IOProc: mix apps → preamp → EQ → tone → effects → limiter
     ──► output device
```

- One tap captures everything headed for the current output except Tapline itself, bypassed apps and apps with their own mixer settings; each of those gets its own tap. Taps of the device's own stream follow its clock.
- Drift compensation is on for devices with their own crystal (USB, built-in) and off for Bluetooth and virtual devices, where it would make the HAL add and drop samples.
- The realtime path is plain C (`Packages/TaplineCore/Sources/TaplineDSP`): no allocation, locks or Swift runtime on the audio thread. Parameters reach it through a lock-free triple buffer. Filters are RBJ biquads computed in double precision.
- The EQ fitter (`EQFitter.swift`) follows AutoEQ's approach: level-match, smooth the required correction (two-octave smoothing above 8 kHz), add shelves and bells where the error is largest and refine all parameters with a pattern search.
- Only one copy of Tapline runs at a time, a watchdog restarts the engine if the device stops calling it, and a guard switches processing off if it detects a feedback loop. If Tapline quits or crashes, its private taps and aggregate device disappear with it.

## Privacy and network use

Audio is processed on your Mac and is never recorded or sent anywhere. Tapline only goes online when you use headphone correction:

- **AutoEQ**: the index and profiles are downloaded from GitHub and cached for a week.
- **squig.link**: the site directory, each database's list of headphones, target curves and the measurements you open are downloaded from the reviewers' squig.link sites and cached. Sites on squig.link's own opt-out list are never contacted.

## Build from source

Requires Xcode 26 or later and [XcodeGen](https://github.com/yonaskolb/XcodeGen).

```bash
brew install xcodegen
git clone https://github.com/belugaming/tapline.git
cd tapline
xcodegen generate
xcodebuild -project Tapline.xcodeproj -scheme Tapline -configuration Release -derivedDataPath build build
open build/Build/Products/Release/Tapline.app
```

Run the tests of the DSP core:

```bash
cd Packages/TaplineCore && swift test
```

Local builds are ad-hoc signed too. Set your own team in Xcode to keep the audio permission across rebuilds.

### Windows

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The Windows app is in `windows/`: `Tapline.Core` (the EQ, fitting and room-correction code, ported from Swift) and `Tapline` (the WPF app).

```bash
cd windows
dotnet test tests/Tapline.Core.Tests
dotnet publish src/Tapline/Tapline.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/publish
```

The core tests also run on macOS and Linux, and the app builds there too (`EnableWindowsTargeting`), but it only runs on Windows. `TAPLINE_SNAPSHOT_DIR=<folder>` makes the app render every page into PNG files and quit, which CI uses to check the UI on a real Windows machine.

## Versioning and releases

Tapline follows [Semantic Versioning](https://semver.org/). The version is defined once, as `MARKETING_VERSION` in `project.yml`; the build number is the commit count and is filled in by CI. Changes are collected under **Unreleased** in [CHANGELOG.md](CHANGELOG.md).

```bash
scripts/release.sh 0.2.0 --push
```

The script checks that `main` is clean, bumps the version, turns the Unreleased section into `0.2.0`, commits, tags `v0.2.0` and pushes. The [Release workflow](.github/workflows/release.yml) then tests and builds Tapline on GitHub's macOS runners and publishes the disk image with that changelog section as release notes. Versions with a suffix such as `0.3.0-beta.1` become pre-releases. Every push to `main` is built with Xcode 26 and Xcode 27 by the [CI workflow](.github/workflows/ci.yml).

## Known limitations

- Apps that take exclusive (hog) mode on a device cannot be tapped.
- Room correction does not set subwoofer crossovers or delays.
- The ADI-2 export writes files for the ADI-2 DAC (FS); the ADI-2 Pro and 2/4 Pro use other file types.
- The first volume change for an app causes a short gap while its audio path is rebuilt.
- Release builds are not notarized yet (see [Install](#install)).

## Credits

- Headphone correction curves: [AutoEQ](https://github.com/jaakkopasanen/AutoEq) by Jaakko Pasanen and contributors (oratory1990, crinacle, Rtings and others)
- Frequency response measurements: the reviewers publishing on [squig.link](https://squig.link). The data belongs to them; please visit and support their sites.
- Filter designs: Robert Bristow-Johnson's *Audio EQ Cookbook*

## License

Tapline is free software: you can redistribute it and/or modify it under the terms of the [GNU General Public License v3.0](LICENSE) as published by the Free Software Foundation. It comes with no warranty. If you distribute a modified version, it must also be released under GPL-3.0 with its source code.
