// Synthesizes the soundtrack (original, so it is free to publish anywhere) and an
// analysis file the page uses to draw the real spectrum and meters of what you hear.
//
//   node music.cjs            -> out/music_raw.wav, out/analysis.json
//
// Chords: Fmaj9 – G6/9 – Em7 – Am9 (IV–V–iii–vi), 120 BPM, one bar = 2 s.

const fs = require('node:fs');
const path = require('node:path');
const TL = require('./src/timeline.js');

const SR = 48000;
const DUR = TL.duration;
const N = Math.ceil(DUR * SR);
const BEAT = 60 / TL.bpm;
const BAR = 4 * BEAT;
const STEP = BEAT / 4;
const OUT = path.join(__dirname, 'out');
fs.mkdirSync(OUT, { recursive: true });

// ---------- helpers ----------
function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rnd = mulberry32(7);
const noise = () => rnd() * 2 - 1;
const mtof = (m) => 440 * Math.pow(2, (m - 69) / 12);
const db = (x) => Math.pow(10, x / 20);
const clamp = (x, a, b) => Math.min(b, Math.max(a, x));

function polyblep(t, dt) {
  if (t < dt) { t /= dt; return t + t - t * t - 1; }
  if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
  return 0;
}

class SVF {
  constructor() { this.ic1 = 0; this.ic2 = 0; }
  run(v0, fc, Q) {
    const g = Math.tan(Math.PI * clamp(fc, 10, SR * 0.45) / SR), k = 1 / Q;
    const a1 = 1 / (1 + g * (g + k)), a2 = g * a1, a3 = g * a2;
    const v3 = v0 - this.ic2;
    const v1 = a1 * this.ic1 + a2 * v3;
    const v2 = this.ic2 + a2 * this.ic1 + a3 * v3;
    this.ic1 = 2 * v1 - this.ic1; this.ic2 = 2 * v2 - this.ic2;
    this.lp = v2; this.bp = v1; this.hp = v0 - k * v1 - v2;
    return v2;
  }
}

// RBJ biquad, the same filter family Tapline uses.
function rbj(type, f, gainDb, Q) {
  const A = Math.pow(10, gainDb / 40), w = 2 * Math.PI * f / SR;
  const cw = Math.cos(w), sw = Math.sin(w), al = sw / (2 * Q);
  let b0, b1, b2, a0, a1, a2;
  if (type === 'peak') {
    b0 = 1 + al * A; b1 = -2 * cw; b2 = 1 - al * A; a0 = 1 + al / A; a1 = -2 * cw; a2 = 1 - al / A;
  } else if (type === 'lowshelf') {
    const s = 2 * Math.sqrt(A) * al;
    b0 = A * ((A + 1) - (A - 1) * cw + s); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - s);
    a0 = (A + 1) + (A - 1) * cw + s; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - s;
  } else if (type === 'highshelf') {
    const s = 2 * Math.sqrt(A) * al;
    b0 = A * ((A + 1) + (A - 1) * cw + s); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - s);
    a0 = (A + 1) - (A - 1) * cw + s; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - s;
  } else if (type === 'lowpass') {
    b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; a0 = 1 + al; a1 = -2 * cw; a2 = 1 - al;
  }
  return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
}
class Biquad {
  constructor() { this.z = [0, 0, 0, 0]; this.c = [1, 0, 0, 0, 0]; }
  run(x, ch) {
    const c = this.c, o = ch * 2, z = this.z;
    const y = c[0] * x + z[o];
    z[o] = c[1] * x - c[3] * y + z[o + 1];
    z[o + 1] = c[2] * x - c[4] * y;
    return y;
  }
}

// ---------- buses ----------
const bus = () => [new Float32Array(N), new Float32Array(N)];
const B = { drums: bus(), bass: bus(), pad: bus(), pluck: bus(), bell: bus(), fx: bus(), verb: bus(), dly: bus(), dry: bus() };
function add(b, i, l, r) { if (i >= 0 && i < N) { b[0][i] += l; b[1][i] += r; } }

// ---------- arrangement ----------
const CH = {
  F: { pad: [53, 57, 60, 64, 67], root: 41 },
  G: { pad: [55, 59, 62, 64, 69], root: 43 },
  Em: { pad: [52, 55, 59, 62, 67], root: 40 },
  Am: { pad: [57, 60, 64, 67, 71], root: 45 },
  C: { pad: [48, 52, 55, 59, 62], root: 36 },
};
const PROG = ['F', 'G', 'Em', 'Am'];
function chordAt(b) {
  if (b === 29) return 'F';
  if (b === 30) return 'G';
  if (b >= 31) return 'C';
  return PROG[(((b - 2) % 4) + 4) % 4];
}
const BARS = Math.ceil(DUR / BAR);
const sceneBar = (name) => Math.round(TL.scenes[name][0] / BAR);
const S = {};
for (const k of Object.keys(TL.scenes)) S[k] = sceneBar(k);
const inBars = (b, a, z) => b >= a && b < z; // [a, z)
const fillBars = new Set([S.hp - 1, S.room - 1, S.mixer - 1, S.devices - 1, S.tech - 1, S.cta - 1]);

const kickTimes = [];

// ---------- instruments ----------
function kick(t0, vel = 1) {
  kickTimes.push(+t0.toFixed(4));
  const i0 = Math.round(t0 * SR), len = Math.round(0.45 * SR);
  let ph = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    const f = 46 + 120 * Math.exp(-t / 0.032);
    ph += 2 * Math.PI * f / SR;
    let s = Math.sin(ph) * Math.exp(-t / 0.3);
    s += (n < 120 ? noise() * (1 - n / 120) * 0.25 : 0);
    s = Math.tanh(s * 1.4) * 0.85 * vel;
    add(B.drums, i0 + n, s, s);
  }
}
const clapF = new SVF();
function clap(t0, vel = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round(0.3 * SR);
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    let env = 0;
    for (const o of [0, 0.011, 0.022]) if (t >= o) env = Math.max(env, Math.exp(-(t - o) / 0.007));
    env = Math.max(env, 0.55 * Math.exp(-Math.max(0, t - 0.022) / 0.11) * (t >= 0.022 ? 1 : 0));
    const nz = clapF.run(noise(), 1500, 1.1);
    const body = Math.sin(2 * Math.PI * 185 * t) * Math.exp(-t / 0.06) * 0.35;
    const s = (nz * env * 1.5 + body) * 0.42 * vel;
    add(B.drums, i0 + n, s * 0.95, s);
    add(B.verb, i0 + n, s * 0.35, s * 0.35);
  }
}
const hatF = new SVF();
function hat(t0, vel = 1, open = false, pan = 0.2) {
  const i0 = Math.round(t0 * SR), dec = open ? 0.16 : 0.022, len = Math.round(dec * 6 * SR);
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    hatF.run(noise(), 8500, 0.8);
    const s = hatF.hp * Math.exp(-t / dec) * 0.28 * vel;
    add(B.drums, i0 + n, s * (1 - pan), s * (1 + pan));
  }
}
const shkF = new SVF();
function shaker(t0, vel = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round(0.09 * SR);
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    const env = Math.min(1, t / 0.012) * Math.exp(-t / 0.03);
    const s = shkF.run(noise(), 6000, 1.5) * env * 0.12 * vel;
    add(B.drums, i0 + n, s * 1.3, s * 0.7);
  }
}
function bassNote(t0, dur, midi, vel = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round((dur + 0.05) * SR);
  const f = mtof(midi);
  let ph = 0, ph2 = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    ph += 2 * Math.PI * f / SR; ph2 += Math.PI * f / SR;
    const a = Math.min(1, t / 0.006) * (0.55 + 0.45 * Math.exp(-t / 0.12)) * (t > dur ? Math.exp(-(t - dur) / 0.015) : 1);
    let s = Math.sin(ph) + 0.35 * Math.sin(2 * ph) + 0.12 * Math.sin(3 * ph);
    s = Math.tanh(s * 1.3) * 0.5 + Math.sin(ph2) * 0.32; // sub an octave below for headphones
    s *= a * 0.48 * vel;
    add(B.bass, i0 + n, s, s);
  }
}
function padChord(t0, dur, notes, vel = 1) {
  const i0 = Math.round(t0 * SR), rel = 0.7, len = Math.round((dur + rel) * SR);
  for (let k = 0; k < notes.length; k++) {
    const f = mtof(notes[k]);
    const det = [-7, 0, 7].map((c) => f * Math.pow(2, c / 1200));
    const ph = [rnd(), rnd(), rnd()];
    const filt = new SVF();
    const pans = [-0.6, 0, 0.6];
    for (let n = 0; n < len; n++) {
      const t = n / SR, tt = t0 + t;
      const env = Math.min(1, t / 0.35) * (t > dur ? Math.exp(-(t - dur) / (rel / 3)) : 1);
      let l = 0, r = 0;
      for (let o = 0; o < 3; o++) {
        const dt = det[o] / SR;
        ph[o] += dt; if (ph[o] >= 1) ph[o] -= 1;
        const s = 2 * ph[o] - 1 - polyblep(ph[o], dt);
        l += s * (1 - pans[o]); r += s * (1 + pans[o]);
      }
      const fc = 2000 + 800 * Math.sin(2 * Math.PI * 0.11 * tt + k);
      // filter mono sum + keep width via side signal
      const m = filt.run((l + r) * 0.5, fc, 0.8);
      const side = (l - r) * 0.5 * 0.35;
      const g = env * 0.032 * vel;
      add(B.pad, i0 + n, (m + side) * g, (m - side) * g);
    }
  }
}
function pluck(t0, midi, vel = 1, pan = 0) {
  const i0 = Math.round(t0 * SR), len = Math.round(0.5 * SR);
  const f = mtof(midi), dt = f / SR;
  let ph = rnd();
  const filt = new SVF();
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    ph += dt; if (ph >= 1) ph -= 1;
    const saw = 2 * ph - 1 - polyblep(ph, dt);
    const sq = (ph < 0.5 ? 1 : -1);
    const x = saw * 0.7 + sq * 0.3;
    const y = filt.run(x, 600 + 6000 * Math.exp(-t / 0.06), 1.2);
    const s = y * Math.exp(-t / 0.16) * 0.11 * vel;
    add(B.pluck, i0 + n, s * (1 - pan), s * (1 + pan));
  }
}
function bell(t0, dur, midi, vel = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round((dur + 1.6) * SR);
  const f = mtof(midi);
  let pc = 0, pm = 0, pm2 = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    pm += 2 * Math.PI * f / SR; pm2 += 2 * Math.PI * f * 3.5 / SR; pc += 2 * Math.PI * f / SR;
    const idx = 1.6 * Math.exp(-t / 0.35), idx2 = 0.5 * Math.exp(-t / 0.08);
    const env = Math.min(1, t / 0.004) * Math.exp(-t / 0.9) * (t > dur ? Math.exp(-(t - dur) / 0.4) : 1);
    const s = Math.sin(pc + idx * Math.sin(pm) + idx2 * Math.sin(pm2)) * env * 0.13 * vel;
    add(B.bell, i0 + n, s * 0.9, s * 1.1);
  }
}
const riserF = new SVF();
function riser(t0, t1, level = 0.22) {
  const i0 = Math.round(t0 * SR), len = Math.round((t1 - t0) * SR);
  for (let n = 0; n < len; n++) {
    const p = n / len;
    const y = riserF.run(noise(), 300 * Math.pow(30, p), 2.5);
    const s = riserF.bp * Math.pow(p, 2.2) * level;
    add(B.dry, i0 + n, s, s); // not muffled by the intro filter
    add(B.verb, i0 + n, s * 0.5, s * 0.5);
  }
}
const impF = new SVF();
function impact(t0, level = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round(2.2 * SR);
  let ph = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    ph += 2 * Math.PI * (34 + 40 * Math.exp(-t / 0.25)) / SR;
    const sub = Math.sin(ph) * Math.exp(-t / 0.7) * 0.6;
    const nz = impF.run(noise(), 2400 * Math.exp(-t / 0.5) + 200, 0.7) * Math.exp(-t / 0.35) * 0.5;
    const s = (sub + nz) * level;
    add(B.fx, i0 + n, s, s);
    add(B.verb, i0 + n, nz * 0.6 * level, nz * 0.6 * level);
  }
}
const swF = new SVF();
function swish(tc, level = 1) {
  const d = 0.45, i0 = Math.round((tc - d * 0.7) * SR), len = Math.round(d * SR);
  for (let n = 0; n < len; n++) {
    const p = n / len;
    swF.run(noise(), 1200 * Math.pow(6, p), 1.6);
    const s = swF.bp * Math.sin(Math.PI * p) * 0.09 * level;
    add(B.fx, i0 + n, s * (1.2 - p), s * (0.2 + p));
  }
}
function click(t0, level = 1) {
  const i0 = Math.round(t0 * SR), len = Math.round(0.03 * SR);
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    const s = (Math.sin(2 * Math.PI * 2600 * t) * 0.6 + noise() * 0.4) * Math.exp(-t / 0.004) * 0.12 * level;
    add(B.fx, i0 + n, s, s);
  }
}
function sweep(t0, T, ch) {
  const i0 = Math.round(t0 * SR), len = Math.round(T * SR);
  const f0 = 40, f1 = 16000;
  let ph = 0;
  for (let n = 0; n < len; n++) {
    const p = n / len;
    ph += 2 * Math.PI * f0 * Math.pow(f1 / f0, p) / SR;
    const env = Math.min(1, n / (0.01 * SR), (len - n) / (0.02 * SR));
    const s = Math.sin(ph) * env * 0.1;
    add(B.dry, i0 + n, ch === 0 ? s : 0, ch === 1 ? s : 0);
  }
}

// ---------- sequence ----------
const ARP = [0, 1, 2, 3, 4, 3, 2, 1, 0, 2, 1, 3, 2, 4, 3, 1];
const BELL = {
  [S.hp]: [[0, 81, 2], [4, 79, 2], [8, 76, 6]],
  [S.hp + 1]: [[0, 74, 2], [4, 76, 2], [8, 79, 6]],
  [S.hp + 2]: [[0, 71, 2], [4, 74, 2], [8, 76, 2], [12, 79, 4]],
  [S.hp + 3]: [[0, 76, 8], [8, 72, 4], [12, 74, 4]],
  [S.hp + 4]: [[0, 77, 4], [4, 76, 4], [8, 72, 8]],
  [S.cta]: [[0, 81, 2], [4, 79, 2], [8, 76, 6]],
  [S.cta + 1]: [[0, 74, 2], [4, 76, 2], [8, 79, 8]],
  [S.cta + 2]: [[0, 79, 16]],
};
const END = S.cta + 2; // final chord bar (62 s)

for (let b = 0; b < BARS; b++) {
  const t = b * BAR;
  const name = chordAt(b), C = CH[name];
  const full = b >= 2 && b < END;

  // pad
  if (b < END) padChord(t, BAR, C.pad, b < 2 ? 0.9 : 1);
  if (b === END) padChord(t, 3.6, C.pad, 1.15);

  // bass
  if (full) {
    const pat = [[0, 3, 0], [3, 1, 0], [6, 2, 0], [8, 3, 0], [11, 1, 0], [14, 2, 12]];
    for (const [st, ln, oc] of pat) bassNote(t + st * STEP, ln * STEP * 0.9, C.root + oc, st === 0 ? 1 : 0.85);
  }
  if (b === END) bassNote(t, 3.2, C.root, 1);

  // drums
  if (b === 1) { kick(t, 0.6); kick(t + 8 * STEP, 0.6); for (let s = 8; s < 16; s++) clap(t + s * STEP, 0.15 + 0.6 * (s - 8) / 8); }
  if (full) {
    const light = inBars(b, S.room, S.mixer);
    kick(t); kick(t + 8 * STEP);
    if (b % 2 === 1) kick(t + 11 * STEP, 0.8);
    clap(t + 4 * STEP); clap(t + 12 * STEP);
    for (let s = 0; s < 16; s++) {
      if (s % 4 === 2) hat(t + s * STEP, 1);
      else if (!light && s % 2 === 1) hat(t + s * STEP, 0.35);
      else if (inBars(b, S.tech, S.cta) && s % 4 === 0) hat(t + s * STEP, 0.4);
    }
    if (b % 2 === 1 && !light) hat(t + 14 * STEP, 0.6, true);
    if (inBars(b, S.devices, S.tech)) for (let s = 0; s < 16; s++) shaker(t + s * STEP, s % 2 ? 0.6 : 1);
    if (fillBars.has(b)) for (let s = 12; s < 16; s++) clap(t + s * STEP, 0.3 + 0.15 * (s - 12));
  }
  if (b === END) kick(t, 1);

  // pluck arp
  if (full) {
    const tones = C.pad.slice(0, 4).map((m) => m + 12);
    tones.push(C.pad[0] + 24);
    for (let s = 0; s < 16; s++) {
      if (inBars(b, S.room, S.mixer) && s % 2 === 1) continue;
      const m = tones[ARP[(s + b * 3) % 16] % tones.length];
      pluck(t + s * STEP, m, s % 4 === 0 ? 1 : 0.7, ((s % 2) ? 0.35 : -0.35));
    }
  }

  // bell melody
  if (BELL[b]) for (const [st, m, ln] of BELL[b]) bell(t + st * STEP, ln * STEP, m, b === END ? 0.9 : 1);
}

// fx
riser(2.0, TL.ev.drop - 0.02);
impact(TL.ev.drop, 1);
riser(TL.scenes.cta[0] - 2, TL.scenes.cta[0] - 0.02, 0.18);
impact(TL.scenes.cta[0], 0.85);
impact(END * BAR, 0.6);
for (const k of ['eq', 'hp', 'room', 'mixer', 'devices', 'effects', 'menubar', 'tech']) swish(TL.scenes[k][0], 0.9);
for (const k of ['eqBypassOn', 'eqBypassOff', 'abToB', 'hpRow', 'hpApply', 'mixMute', 'mixBypass']) click(TL.ev[k]);
sweep(TL.ev.roomSweepL, 0.7, 0);
sweep(TL.ev.roomSweepR, 0.7, 1);

// ---------- sidechain (duck pad, bass, pluck under the kick) ----------
const duck = new Float32Array(N).fill(1);
for (const kt of kickTimes) {
  const i0 = Math.round(kt * SR), len = Math.round(0.35 * SR);
  for (let n = 0; n < len && i0 + n < N; n++) {
    const t = n / SR;
    const g = 1 - 0.5 * Math.exp(-t / 0.09) * Math.min(1, t / 0.004 + 0.3);
    duck[i0 + n] = Math.min(duck[i0 + n], g);
  }
}
for (const b of [B.pad, B.bass, B.pluck]) for (let i = 0; i < N; i++) { b[0][i] *= duck[i]; b[1][i] *= duck[i]; }

// ---------- delay (ping-pong, on pluck + bell) ----------
{
  const D = Math.round(3 * STEP * SR), fb = 0.32;
  const bufL = new Float32Array(D), bufR = new Float32Array(D);
  let lpL = 0, lpR = 0, w = 0;
  for (let i = 0; i < N; i++) {
    const inL = B.pluck[0][i] * 0.5 + B.bell[0][i] * 0.3, inR = B.pluck[1][i] * 0.5 + B.bell[1][i] * 0.3;
    const oL = bufL[w], oR = bufR[w];
    lpL += 0.35 * (oL - lpL); lpR += 0.35 * (oR - lpR);
    bufL[w] = inR + lpR * fb; bufR[w] = inL + lpL * fb; // cross feedback
    B.dly[0][i] = oL; B.dly[1][i] = oR;
    w = (w + 1) % D;
  }
}

// ---------- reverb (Freeverb) ----------
{
  const sc = SR / 44100;
  const combT = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], apT = [556, 441, 341, 225];
  const mk = (sp) => ({
    combs: combT.map((d) => ({ b: new Float32Array(Math.round((d + sp) * sc)), i: 0, f: 0 })),
    aps: apT.map((d) => ({ b: new Float32Array(Math.round((d + sp) * sc)), i: 0 })),
  });
  const ch = [mk(0), mk(23)];
  const room = 0.86, damp = 0.25;
  for (let i = 0; i < N; i++) {
    const send = (B.verb[0][i] + B.verb[1][i]) * 0.5
      + (B.pad[0][i] + B.pad[1][i]) * 0.5 * 0.35
      + (B.pluck[0][i] + B.pluck[1][i]) * 0.5 * 0.3
      + (B.bell[0][i] + B.bell[1][i]) * 0.5 * 0.45;
    const x = send * 0.015;
    for (let c = 0; c < 2; c++) {
      let out = 0;
      for (const cb of ch[c].combs) {
        const y = cb.b[cb.i];
        cb.f = y * (1 - damp) + cb.f * damp;
        cb.b[cb.i] = x + cb.f * room;
        cb.i = (cb.i + 1) % cb.b.length;
        out += y;
      }
      for (const ap of ch[c].aps) {
        const bo = ap.b[ap.i];
        ap.b[ap.i] = out + bo * 0.5;
        ap.i = (ap.i + 1) % ap.b.length;
        out = bo - out;
      }
      B.fx[c][i] += out * 3.2; // wet
    }
  }
}

// ---------- mix ----------
const G = { drums: db(-6), bass: db(2), pad: db(2), pluck: db(10), bell: db(2), fx: db(-2), dly: db(4) };
const mixL = new Float32Array(N), mixR = new Float32Array(N);
for (const [k, g] of Object.entries(G)) for (let i = 0; i < N; i++) { mixL[i] += B[k][0][i] * g; mixR[i] += B[k][1][i] * g; }

if (process.env.DEBUG) {
  for (const [k, g] of Object.entries(G)) {
    let pk = 0, ss = 0; const a = B[k][0];
    const i0 = Math.round(10 * SR), i1 = Math.round(18 * SR);
    for (let i = i0; i < i1; i++) { pk = Math.max(pk, Math.abs(a[i] * g)); ss += (a[i] * g) ** 2; }
    console.log(k.padEnd(6), 'peak', (20 * Math.log10(pk)).toFixed(1), 'rms', (10 * Math.log10(ss / (i1 - i0))).toFixed(1));
  }
  let pk = 0, ss = 0; for (let i = 10 * SR; i < 18 * SR; i++) { pk = Math.max(pk, Math.abs(mixL[i])); ss += mixL[i] ** 2; }
  console.log('MIX    peak', (20 * Math.log10(pk)).toFixed(1), 'rms', (10 * Math.log10(ss / (8 * SR))).toFixed(1));
}
// ---------- master tone: less sub, more presence and air (phones and laptops) ----------
{
  const tone = [rbj('lowshelf', 60, -3, 0.7), rbj('peak', 3000, 2.5, 0.8), rbj('highshelf', 6000, 4.5, 0.7)];
  const fl = tone.map((c) => { const b = new Biquad(); b.c = c; return b; });
  const hp = 1 - 2 * Math.PI * 22 / SR; // DC blocker
  const z = [[0, 0], [0, 0]];
  for (let i = 0; i < N; i++) for (let c = 0; c < 2; c++) {
    const a = c ? mixR : mixL;
    let x = a[i];
    const y = x - z[c][0] + hp * z[c][1]; z[c][0] = x; z[c][1] = y; x = y;
    for (const f of fl) x = f.run(x, c);
    a[i] = x;
  }
}

// ---------- demo processing: what the viewer hears change on screen ----------
// Each entry: [time, value]; linear interpolation between points.
const env = (pts) => (t) => {
  if (t <= pts[0][0]) return pts[0][1];
  for (let k = 1; k < pts.length; k++) if (t <= pts[k][0]) {
    const [t0, v0] = pts[k - 1], [t1, v1] = pts[k];
    return v0 + (v1 - v0) * (t - t0) / (t1 - t0);
  }
  return pts[pts.length - 1][1];
};
const e = TL.ev;
const lpHz = env([[0, 650], [3.9, 1500], [3.995, 1700], [4.0, 20000]]);
const ls = env([ // low shelf 120 Hz
  [e.eqBypassOn - 0.02, 0], [e.eqBypassOn + 0.04, -5], [e.eqBypassOff - 0.02, -5], [e.eqBypassOff + 0.04, 0],
  [TL.scenes.hp[0] - 0.05, 0], [TL.scenes.hp[0] + 0.1, -7], [e.hpApply, -7], [e.hpApply + 0.15, 0],
]);
const hs = env([ // high shelf 6 kHz
  [e.eqBypassOn - 0.02, 0], [e.eqBypassOn + 0.04, -5], [e.eqBypassOff - 0.02, -5], [e.eqBypassOff + 0.04, 0],
  [TL.scenes.hp[0] - 0.05, 0], [TL.scenes.hp[0] + 0.1, -6], [e.hpApply, -6], [e.hpApply + 0.15, 0],
]);
const pk1 = env([[TL.scenes.hp[0] - 0.05, 0], [TL.scenes.hp[0] + 0.1, 4], [e.hpApply, 4], [e.hpApply + 0.15, 0]]); // 1.2 kHz boxiness
const boom = env([[TL.scenes.room[0] - 0.05, 0], [TL.scenes.room[0] + 0.15, 1], [e.roomFix, 1], [e.roomFixEnd, 0]]);

const fLP = new Biquad(), fLS = new Biquad(), fHS = new Biquad(), fP1 = new Biquad(), fR1 = new Biquad(), fR2 = new Biquad(), fR3 = new Biquad();
const BLK = 16;
for (let i = 0; i < N; i += BLK) {
  const t = i / SR;
  const lp = lpHz(t), l = ls(t), h = hs(t), p = pk1(t), bm = boom(t);
  fLP.c = rbj('lowpass', Math.min(lp, 21000), 0, 0.9);
  fLS.c = rbj('lowshelf', 120, l, 0.7);
  fHS.c = rbj('highshelf', 6000, h, 0.7);
  fP1.c = rbj('peak', 1200, p, 1.0);
  fR1.c = rbj('peak', 52, 7 * bm, 4);
  fR2.c = rbj('peak', 175, 7 * bm, 3.5);
  fR3.c = rbj('peak', 110, -7 * bm, 4);
  const trim = db(-2.5 * bm); // keep "before" from simply sounding louder
  const useLP = lp < 19000, useEQ = l !== 0 || h !== 0 || p !== 0, useRoom = bm > 0;
  for (let j = i; j < Math.min(N, i + BLK); j++) {
    for (let c = 0; c < 2; c++) {
      const a = c ? mixR : mixL;
      let x = a[j];
      if (useLP) x = fLP.run(x, c);
      if (useEQ) { x = fLS.run(x, c); x = fHS.run(x, c); x = fP1.run(x, c); }
      if (useRoom) { x = fR1.run(x, c); x = fR2.run(x, c); x = fR3.run(x, c) * trim; }
      a[j] = x + B.dry[c][j];
    }
  }
}

// ending fade + soft clip + normalize
const fadeStart = DUR - 1.6;
let peak = 0;
for (let i = 0; i < N; i++) {
  const t = i / SR;
  const g = t > fadeStart ? Math.pow(Math.max(0, 1 - (t - fadeStart) / 1.6), 2) : 1;
  mixL[i] = Math.tanh(mixL[i] * g * 0.9) / 0.9; mixR[i] = Math.tanh(mixR[i] * g * 0.9) / 0.9;
  peak = Math.max(peak, Math.abs(mixL[i]), Math.abs(mixR[i]));
}
const norm = db(-1) / peak;
for (let i = 0; i < N; i++) { mixL[i] *= norm; mixR[i] *= norm; }

// ---------- write wav (24-bit) ----------
function writeWav(file, L, R) {
  const n = L.length, bytes = n * 2 * 3;
  const buf = Buffer.alloc(44 + bytes);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + bytes, 4); buf.write('WAVE', 8);
  buf.write('fmt ', 12); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(2, 22);
  buf.writeUInt32LE(SR, 24); buf.writeUInt32LE(SR * 6, 28); buf.writeUInt16LE(6, 32); buf.writeUInt16LE(24, 34);
  buf.write('data', 36); buf.writeUInt32LE(bytes, 40);
  let o = 44;
  for (let i = 0; i < n; i++) for (const a of [L, R]) {
    const v = Math.round(clamp(a[i], -1, 1) * 8388607);
    buf.writeIntLE(v, o, 3); o += 3;
  }
  fs.writeFileSync(file, buf);
}
writeWav(path.join(OUT, 'music_raw.wav'), mixL, mixR);

// ---------- analysis for the visuals ----------
function fft(re, im) {
  const n = re.length;
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { [re[i], re[j]] = [re[j], re[i]]; [im[i], im[j]] = [im[j], im[i]]; }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len, wr = Math.cos(ang), wi = Math.sin(ang);
    for (let i = 0; i < n; i += len) {
      let cr = 1, ci = 0;
      for (let k = 0; k < len / 2; k++) {
        const ar = re[i + k], ai = im[i + k];
        const br = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
        const bi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
        re[i + k] = ar + br; im[i + k] = ai + bi;
        re[i + k + len / 2] = ar - br; im[i + k + len / 2] = ai - bi;
        const t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
      }
    }
  }
}
const FPS = TL.fps, FR = Math.round(DUR * FPS), NF = 8192, NB = 180;
const hann = new Float64Array(NF).map((_, i) => 0.5 - 0.5 * Math.cos(2 * Math.PI * i / (NF - 1)));
const bandF = Array.from({ length: NB }, (_, k) => 20 * Math.pow(1000, k / (NB - 1)));
const spec = new Uint8Array(FR * NB);
const lev = [];
let sm = new Float64Array(NB).fill(-100);
for (let f = 0; f < FR; f++) {
  const c = Math.round(f / FPS * SR);
  const re = new Float64Array(NF), im = new Float64Array(NF);
  for (let i = 0; i < NF; i++) {
    const j = c - NF / 2 + i;
    re[i] = j >= 0 && j < N ? (mixL[j] + mixR[j]) * 0.5 * hann[i] : 0;
  }
  fft(re, im);
  const mag = new Float64Array(NF / 2);
  for (let i = 0; i < NF / 2; i++) mag[i] = (re[i] * re[i] + im[i] * im[i]);
  for (let k = 0; k < NB; k++) {
    const fl = bandF[k] * Math.pow(2, -1 / 7), fh = bandF[k] * Math.pow(2, 1 / 7); // ~1/3.5-octave smoothing, like an analyzer
    const il = fl / SR * NF, ih = fh / SR * NF;
    let p = 0, cnt = 0;
    for (let i = Math.floor(il); i <= Math.ceil(ih); i++) { p += mag[i] || 0; cnt++; }
    p /= Math.max(1, cnt);
    let d = 10 * Math.log10(p + 1e-12) + 3 * Math.log2(bandF[k] / 1000); // pink tilt like an analyzer
    const prev = sm[k];
    sm[k] = d > prev ? prev + (d - prev) * 0.5 : prev + (d - prev) * 0.12;
    spec[f * NB + k] = clamp(Math.round((sm[k] + 40) * 2), 0, 255); // 0.5 dB steps from -40 dB
  }
  // levels over 1/60 s (peak) and 50 ms (rms)
  let pl = 0, pr = 0, sl = 0, sr = 0, cnt = 0;
  for (let j = c - 1200; j < c + 1200; j++) if (j >= 0 && j < N) { sl += mixL[j] ** 2; sr += mixR[j] ** 2; cnt++; }
  for (let j = c - 400; j < c + 400; j++) if (j >= 0 && j < N) { pl = Math.max(pl, Math.abs(mixL[j])); pr = Math.max(pr, Math.abs(mixR[j])); }
  const toDb = (x) => +(20 * Math.log10(x + 1e-9)).toFixed(1);
  lev.push([toDb(Math.sqrt(sl / Math.max(1, cnt))), toDb(Math.sqrt(sr / Math.max(1, cnt))), toDb(pl), toDb(pr)]);
}
fs.writeFileSync(path.join(OUT, 'analysis.json'), JSON.stringify({
  fps: FPS, frames: FR, bands: NB, fMin: 20, fMax: 20000, specOffset: -40,
  spec: Buffer.from(spec).toString('base64'), levels: lev, kicks: kickTimes,
}));
console.log('done', { peakBeforeNorm: peak.toFixed(3), kicks: kickTimes.length, frames: FR });
