// Renders the page frame by frame in headless Chrome and encodes with ffmpeg.
//
//   node render.cjs --lang zh --fmt h                 -> out/tapline-zh-16x9.mp4
//   node render.cjs --lang zh --fmt v --fps 60        -> out/tapline-zh-9x16.mp4
//   node render.cjs --lang zh --fmt h --stills 5,12.4 -> out/stills/zh-h-5.00.png ...
//
// Options: --workers N (default 8), --from/--to seconds (partial renders for checking).

const fs = require('node:fs');
const path = require('node:path');
const { spawn, execFileSync } = require('node:child_process');
const puppeteer = require('puppeteer-core');
const { start } = require('./serve.cjs');
const TL = require('./src/timeline.js');

const args = Object.fromEntries(process.argv.slice(2).reduce((a, x, i, all) => {
  if (x.startsWith('--')) a.push([x.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
  return a;
}, []));
const LANG = args.lang || 'zh', FMT = args.fmt || 'h';
const W = FMT === 'v' ? 1080 : 1920, H = FMT === 'v' ? 1920 : 1080;
const FPS = +(args.fps || TL.fps);
const WORKERS = +(args.workers || 8);
const OUT = path.join(__dirname, 'out');
const CHROME = process.env.CHROME || path.join(process.env.HOME,
  '.cache/puppeteer/chrome-headless-shell/mac_arm-148.0.7778.97/chrome-headless-shell-mac-arm64/chrome-headless-shell');

async function openPage(port) {
  const browser = await puppeteer.launch({
    executablePath: CHROME,
    headless: 'shell',
    args: ['--force-color-profile=srgb', '--hide-scrollbars', '--disable-lcd-text', '--autoplay-policy=no-user-gesture-required'],
  });
  const page = await browser.newPage();
  page.on('pageerror', (e) => console.error('[page]', e.message));
  page.on('console', (m) => { if (m.type() === 'error') console.error('[console]', m.text()); });
  await page.setViewport({ width: W, height: H, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/promo/src/index.html?lang=${LANG}&fmt=${FMT}${args.cover ? '&cover=1' : ''}`);
  await page.waitForFunction('window.READY === true', { timeout: 60000 });
  const cdp = await page.createCDPSession();
  const shot = async (t) => {
    await page.evaluate((x) => window.renderFrame(x), t);
    const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', optimizeForSpeed: true, clip: { x: 0, y: 0, width: W, height: H, scale: 1 } });
    return Buffer.from(data, 'base64');
  };
  return { browser, shot };
}

function encoder(file, fps) {
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y',
    '-f', 'image2pipe', '-framerate', String(fps), '-c:v', 'png', '-i', '-',
    '-vf', 'scale=out_color_matrix=bt709:out_range=tv,format=yuv420p',
    '-c:v', 'libx264', '-preset', 'medium', '-crf', '12', '-tune', 'animation',
    '-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709',
    file], { stdio: ['pipe', 'inherit', 'inherit'] });
  const done = new Promise((res, rej) => ff.on('close', (c) => (c ? rej(new Error('ffmpeg ' + c)) : res())));
  return { write: (b) => new Promise((r) => (ff.stdin.write(b) ? r() : ff.stdin.once('drain', r))), end: () => { ff.stdin.end(); return done; } };
}

async function main() {
  const srv = await start(0);
  const port = srv.address().port;

  if (args.cover) {
    fs.mkdirSync(path.join(OUT, 'covers'), { recursive: true });
    const p = await openPage(port);
    const f = path.join(OUT, 'covers', `cover-${LANG}-${FMT === 'v' ? '9x16' : '16x9'}.png`);
    fs.writeFileSync(f, await p.shot(0));
    console.log(f);
    await p.browser.close(); srv.close();
    return;
  }
  if (args.stills) {
    fs.mkdirSync(path.join(OUT, 'stills'), { recursive: true });
    const p = await openPage(port);
    for (const t of String(args.stills).split(',').map(Number)) {
      const f = path.join(OUT, 'stills', `${LANG}-${FMT}-${t.toFixed(2)}.png`);
      fs.writeFileSync(f, await p.shot(t));
      console.log(f);
    }
    await p.browser.close(); srv.close();
    return;
  }

  const from = +(args.from || 0), to = +(args.to || TL.duration);
  const f0 = Math.round(from * FPS), f1 = Math.round(to * FPS), total = f1 - f0;
  const segDir = path.join(OUT, `seg-${LANG}-${FMT}`);
  fs.rmSync(segDir, { recursive: true, force: true });
  fs.mkdirSync(segDir, { recursive: true });
  const per = Math.ceil(total / WORKERS);
  let doneFrames = 0;
  const t0 = Date.now();
  const tick = setInterval(() => {
    const el = (Date.now() - t0) / 1000;
    process.stdout.write(`\r${doneFrames}/${total} frames  ${(doneFrames / el).toFixed(1)} fps  ${el.toFixed(0)} s`);
  }, 1000);
  const segs = [];
  await Promise.all(Array.from({ length: WORKERS }, async (_, w) => {
    const a = f0 + w * per, b = Math.min(f1, a + per);
    if (a >= b) return;
    const file = path.join(segDir, `seg${String(w).padStart(2, '0')}.mp4`);
    segs[w] = file;
    const p = await openPage(port);
    const enc = encoder(file, FPS);
    for (let f = a; f < b; f++) { await enc.write(await p.shot(f / FPS)); doneFrames++; }
    await enc.end();
    await p.browser.close();
  }));
  clearInterval(tick);
  console.log(`\nrendered ${total} frames in ${((Date.now() - t0) / 1000).toFixed(0)} s`);
  srv.close();

  const list = path.join(segDir, 'list.txt');
  fs.writeFileSync(list, segs.filter(Boolean).map((s) => `file '${s}'`).join('\n'));
  const video = path.join(segDir, 'video.mp4');
  execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', video]);

  const music = path.join(OUT, 'music.wav');
  const name = args.out || path.join(OUT, `tapline-${LANG}-${FMT === 'v' ? '9x16' : '16x9'}${from || to !== TL.duration ? `-${from}-${to}` : ''}.mp4`);
  execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', '-i', video,
    '-ss', String(from), '-t', String(to - from), '-i', music,
    '-map', '0:v', '-map', '1:a', '-c:v', 'copy', '-c:a', 'aac', '-b:a', '256k', '-ar', '48000',
    '-movflags', '+faststart', '-shortest', name]);
  console.log(name);
}
main().catch((e) => { console.error(e); process.exit(1); });
