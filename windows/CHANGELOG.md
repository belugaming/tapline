# Tapline for Windows changelog

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
