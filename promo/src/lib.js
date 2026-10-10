// Helpers: timing/easing, DOM, filter math (RBJ, like Tapline), chart drawing.

const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
const lerp = (a, b, t) => a + (b - a) * t;
const pr = (t, a, b) => clamp((t - a) / (b - a));
const ease = {
  outCubic: (t) => 1 - Math.pow(1 - t, 3),
  inCubic: (t) => t * t * t,
  inOutCubic: (t) => (t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2),
  outQuint: (t) => 1 - Math.pow(1 - t, 5),
  outExpo: (t) => (t >= 1 ? 1 : 1 - Math.pow(2, -10 * t)),
  outBack: (t) => { const c1 = 1.5, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
  spring: (t) => (t >= 1 ? 1 : 1 - Math.exp(-6.5 * t) * Math.cos(9 * t)),
};
const E = (name, t, a, b) => ease[name](pr(t, a, b));

function h(tag, cls, parent, html) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  if (parent) parent.appendChild(e);
  return e;
}
function S(e, o) { for (const k in o) e.style[k] = o[k]; return e; }
function pos(e, x, y, w, hh) {
  e.style.left = x + 'px'; e.style.top = y + 'px';
  if (w != null) e.style.width = w + 'px';
  if (hh != null) e.style.height = hh + 'px';
  return e;
}

// Standard element entrance/exit: fade + rise + unblur in, fade out.
function reveal(e, t, t0, t1, o = {}) {
  const din = o.din ?? 0.55, dout = o.dout ?? 0.3, dy = o.dy ?? 28, dx = o.dx ?? 0;
  const pi = ease.outCubic(pr(t, t0, t0 + din));
  const po = t1 == null ? 0 : ease.inCubic(pr(t, t1 - dout, t1));
  const a = pi * (1 - po);
  e.style.opacity = a;
  e.style.visibility = a <= 0.001 ? 'hidden' : 'visible';
  const sc = o.scale ? lerp(o.scale, 1, pi) : 1;
  e.style.transform = `translate(${(1 - pi) * dx}px, ${(1 - pi) * dy - po * (o.dyOut ?? 12)}px) scale(${sc})`;
  const blur = (1 - pi) * (o.blur ?? 10) + po * (o.blurOut ?? 6);
  e.style.filter = blur > 0.05 ? `blur(${blur.toFixed(2)}px)` : 'none';
  return a;
}
function vis(e, a) { e.style.opacity = a; e.style.visibility = a <= 0.001 ? 'hidden' : 'visible'; }

// ---------- filters ----------
const FS = 48000;
function coeffs(type, f, g, q) {
  const A = Math.pow(10, g / 40), w = 2 * Math.PI * f / FS;
  const cw = Math.cos(w), sw = Math.sin(w), al = sw / (2 * q);
  let b0, b1, b2, a0, a1, a2;
  switch (type) {
    case 'bell':
      b0 = 1 + al * A; b1 = -2 * cw; b2 = 1 - al * A; a0 = 1 + al / A; a1 = -2 * cw; a2 = 1 - al / A; break;
    case 'lowshelf': {
      const s = 2 * Math.sqrt(A) * al;
      b0 = A * ((A + 1) - (A - 1) * cw + s); b1 = 2 * A * ((A - 1) - (A + 1) * cw); b2 = A * ((A + 1) - (A - 1) * cw - s);
      a0 = (A + 1) + (A - 1) * cw + s; a1 = -2 * ((A - 1) + (A + 1) * cw); a2 = (A + 1) + (A - 1) * cw - s; break;
    }
    case 'highshelf': {
      const s = 2 * Math.sqrt(A) * al;
      b0 = A * ((A + 1) + (A - 1) * cw + s); b1 = -2 * A * ((A - 1) + (A + 1) * cw); b2 = A * ((A + 1) + (A - 1) * cw - s);
      a0 = (A + 1) - (A - 1) * cw + s; a1 = 2 * ((A - 1) - (A + 1) * cw); a2 = (A + 1) - (A - 1) * cw - s; break;
    }
    case 'hp':
      b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; a0 = 1 + al; a1 = -2 * cw; a2 = 1 - al; break;
    default:
      return [1, 0, 0, 0, 0];
  }
  return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
}
function respDb(c, f) {
  const w = 2 * Math.PI * f / FS;
  const c1 = Math.cos(w), s1 = Math.sin(w), c2 = Math.cos(2 * w), s2 = Math.sin(2 * w);
  const nr = c[0] + c[1] * c1 + c[2] * c2, ni = -(c[1] * s1 + c[2] * s2);
  const dr = 1 + c[3] * c1 + c[4] * c2, di = -(c[3] * s1 + c[4] * s2);
  return 10 * Math.log10((nr * nr + ni * ni) / (dr * dr + di * di));
}
const NPTS = 320;
const FREQS = Array.from({ length: NPTS }, (_, i) => 20 * Math.pow(1000, i / (NPTS - 1)));
// bands: [{type, f, g, q, w (0..1 weight)}] -> dB at FREQS
function curve(bands) {
  const out = new Float32Array(NPTS);
  for (const b of bands) {
    if (!b || (b.w ?? 1) <= 0) continue;
    const w = b.w ?? 1;
    const c = b.type === 'hp' ? coeffs('hp', lerp(10, b.f, w), 0, b.q) : coeffs(b.type, b.f, b.g * w, b.q);
    for (let i = 0; i < NPTS; i++) out[i] += respDb(c, FREQS[i]);
  }
  return out;
}
function bandDb(b) {
  const c = coeffs(b.type, b.f, b.g * (b.w ?? 1), b.q);
  return (f) => respDb(c, f);
}

// ---------- analysis (real spectrum + levels of the soundtrack) ----------
let AN = null, SPEC = null;
async function loadAnalysis(url) {
  AN = await (await fetch(url)).json();
  SPEC = Uint8Array.from(atob(AN.spec), (c) => c.charCodeAt(0));
}
function frameOf(t) { return clamp(Math.round(t * AN.fps), 0, AN.frames - 1); }
// spectrum resampled to FREQS, in display dB (roughly -12..+6)
function spectrumAt(t, scale = 0.42, offset = 22) {
  const f = frameOf(t), nb = AN.bands, out = new Float32Array(NPTS);
  for (let i = 0; i < NPTS; i++) {
    const k = i / (NPTS - 1) * (nb - 1), k0 = Math.floor(k), k1 = Math.min(nb - 1, k0 + 1), fr = k - k0;
    const v = lerp(SPEC[f * nb + k0], SPEC[f * nb + k1], fr) / 2 + AN.specOffset;
    out[i] = (v - offset) * scale;
  }
  return out;
}
function levelsAt(t) { return AN.levels[frameOf(t)]; } // [rmsL, rmsR, peakL, peakR] dBFS
function kickPulse(t, tau = 0.18) {
  let last = -10;
  for (const k of AN.kicks) { if (k <= t) last = k; else break; }
  return Math.exp(-(t - last) / tau);
}

// deterministic pseudo-noise
function hash(n) { const s = Math.sin(n * 127.1 + 311.7) * 43758.5453; return s - Math.floor(s); }
function vnoise(x) { const i = Math.floor(x), f = x - i, u = f * f * (3 - 2 * f); return lerp(hash(i), hash(i + 1), u) * 2 - 1; }

// ---------- chart ----------
class Chart {
  constructor(canvas, o) {
    this.cv = canvas; this.ctx = canvas.getContext('2d');
    this.o = Object.assign({ fMin: 20, fMax: 20000, dbMin: -12, dbMax: 12, l: 56, r: 16, t: 18, b: 34, dbStep: 3, fontSize: 15 }, o);
  }
  get W() { return this.cv.width; }
  get H() { return this.cv.height; }
  x(f) { const o = this.o; return o.l + (this.W - o.l - o.r) * Math.log(f / o.fMin) / Math.log(o.fMax / o.fMin); }
  y(d) { const o = this.o; return o.t + (this.H - o.t - o.b) * (o.dbMax - d) / (o.dbMax - o.dbMin); }
  clear() { this.ctx.clearRect(0, 0, this.W, this.H); }
  grid(alpha = 1, labels = true) {
    const c = this.ctx, o = this.o;
    c.save();
    c.globalAlpha = alpha;
    c.lineWidth = 1;
    const minor = [30, 40, 60, 70, 80, 90, 300, 400, 600, 700, 800, 900, 3000, 4000, 6000, 7000, 8000, 9000];
    const major = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];
    c.strokeStyle = 'rgba(255,255,255,0.035)';
    for (const f of minor) { const x = Math.round(this.x(f)) + 0.5; c.beginPath(); c.moveTo(x, o.t); c.lineTo(x, this.H - o.b); c.stroke(); }
    c.strokeStyle = 'rgba(255,255,255,0.07)';
    for (const f of major) { const x = Math.round(this.x(f)) + 0.5; c.beginPath(); c.moveTo(x, o.t); c.lineTo(x, this.H - o.b); c.stroke(); }
    for (let d = o.dbMin; d <= o.dbMax + 1e-6; d += o.dbStep) {
      const y = Math.round(this.y(d)) + 0.5;
      c.strokeStyle = d === 0 && o.dbMin < 0 ? 'rgba(255,255,255,0.12)' : 'rgba(255,255,255,0.06)';
      c.beginPath(); c.moveTo(o.l, y); c.lineTo(this.W - o.r, y); c.stroke();
    }
    if (labels) {
      c.fillStyle = 'rgba(255,255,255,0.42)';
      c.font = `500 ${o.fontSize}px "SF Pro Text", -apple-system, sans-serif`;
      c.textAlign = 'right'; c.textBaseline = 'middle';
      for (let d = o.dbMin; d <= o.dbMax + 1e-6; d += o.dbStep) {
        const lab = o.dbLabel ? o.dbLabel(d) : (d > 0 ? '+' + d : '' + d);
        c.fillText(lab, o.l - 12, this.y(d));
      }
      c.textAlign = 'center'; c.textBaseline = 'top';
      for (const f of major) {
        const lab = f >= 1000 ? f / 1000 + 'k' : '' + f;
        c.fillText(lab, clamp(this.x(f), o.l + 12, this.W - o.r - 14), this.H - o.b + 10);
      }
    }
    c.restore();
  }
  // path for values at FREQS (or custom freq array)
  path(vals, freqs = FREQS, upto = 1) {
    const c = this.ctx, n = Math.max(2, Math.round(vals.length * upto));
    c.beginPath();
    for (let i = 0; i < n; i++) {
      const x = this.x(freqs[i]), y = this.y(clamp(vals[i], this.o.dbMin - 3, this.o.dbMax + 3));
      if (i) c.lineTo(x, y); else c.moveTo(x, y);
    }
    return n;
  }
  line(vals, style, width, o = {}) {
    const c = this.ctx;
    c.save();
    this.clipPlot();
    if (o.alpha != null) c.globalAlpha = o.alpha;
    const n = this.path(vals, o.freqs, o.upto ?? 1);
    c.lineJoin = 'round'; c.lineCap = 'round';
    if (o.dash) c.setLineDash(o.dash);
    if (o.glow) { c.shadowColor = o.glow; c.shadowBlur = o.glowBlur ?? 14; }
    c.strokeStyle = style; c.lineWidth = width;
    c.stroke();
    if (o.glow && o.doubleGlow) c.stroke();
    c.restore();
    return n;
  }
  fill(vals, top, bottom, o = {}) {
    const c = this.ctx;
    c.save();
    this.clipPlot();
    if (o.alpha != null) c.globalAlpha = o.alpha;
    const n = this.path(vals, o.freqs);
    const freqs = o.freqs || FREQS;
    const base = o.base != null ? this.y(o.base) : this.H - this.o.b;
    c.lineTo(this.x(freqs[n - 1]), base);
    c.lineTo(this.x(freqs[0]), base);
    c.closePath();
    const g = c.createLinearGradient(0, this.o.t, 0, this.H - this.o.b);
    g.addColorStop(0, top); g.addColorStop(1, bottom);
    c.fillStyle = g;
    c.fill();
    c.restore();
  }
  clipPlot() {
    const o = this.o, c = this.ctx;
    c.beginPath(); c.rect(o.l, o.t - 2, this.W - o.l - o.r, this.H - o.t - o.b + 4); c.clip();
  }
}

// Smooth measurement-like curves built from control points (log-f, dB) plus jaggedness.
function shapeCurve(points, freqs, jag = 0, seed = 1, jagFrom = 2000) {
  const out = new Float32Array(freqs.length);
  const lp = points.map(([f, d]) => [Math.log(f), d]);
  for (let i = 0; i < freqs.length; i++) {
    const lf = Math.log(freqs[i]);
    let d;
    if (lf <= lp[0][0]) d = lp[0][1];
    else if (lf >= lp[lp.length - 1][0]) d = lp[lp.length - 1][1];
    else {
      let k = 1; while (lp[k][0] < lf) k++;
      const [x0, y0] = lp[k - 1], [x1, y1] = lp[k];
      const u = (lf - x0) / (x1 - x0), s = u * u * (3 - 2 * u);
      d = y0 + (y1 - y0) * s;
    }
    if (jag) {
      const amt = jag * clamp(Math.log(freqs[i] / jagFrom) / Math.log(4) + 0.15, 0.08, 1);
      d += (vnoise(i * 0.55 + seed * 17) * 0.7 + vnoise(i * 1.7 + seed * 5) * 0.3) * amt;
    }
    out[i] = d;
  }
  return out;
}
