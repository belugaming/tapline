# Tapline intro video

A 66-second intro video, built from code: the scenes are an HTML page that recreates
Tapline's UI (EQ curves are computed with the same RBJ filters the app uses), rendered
frame by frame in headless Chrome and encoded with ffmpeg. The soundtrack is synthesized
by `music.cjs`, so it is original and free to publish. Its demo moments (bypass, headphone
and room correction) really change the sound, and the on-screen spectrum and meters are
computed from the soundtrack itself.

```bash
npm install
./build.sh            # everything into dist/ (about 4 minutes)
./build.sh videos     # or a single stage: music, videos, covers, github, dist
```

Requirements: Node 22+, ffmpeg, and the Chrome headless shell used by Puppeteer
(`npx @puppeteer/browsers install chrome-headless-shell@148.0.7778.97 --path ~/.cache/puppeteer`,
or set `CHROME` to another Chromium binary).

| File | What |
|---|---|
| `src/timeline.js` | Scene and event times, shared by the page and the music |
| `src/strings.js` | All on-screen text, Chinese and English |
| `src/scenes.js` | One builder per scene; everything is a pure function of time |
| `music.cjs` | Soundtrack synthesis and the spectrum/level analysis for the visuals |
| `render.cjs` | Parallel frame rendering (`--stills 5,12.4` renders single frames for checking) |
| `PUBLISHING.md` | Upload notes and post copy for Bilibili, Douyin, WeChat and GitHub |

Preview in a browser: `node serve.cjs`, then open
`http://127.0.0.1:8123/promo/src/index.html?lang=zh&fmt=h&play` (`fmt=v` for portrait,
`?t=12.5` for a single frame) and click to start the sound.
