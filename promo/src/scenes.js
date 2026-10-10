// Scene builders. Each takes (root, start, end) and returns update(t) with the
// global time t in seconds. Everything is a pure function of t, so any frame can
// be rendered in any order (the renderer splits the video across workers).

const IC = {
  headphones: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 14v-2a8 8 0 0 1 16 0v2"/><rect x="3" y="14" width="4.2" height="7" rx="1.7"/><rect x="16.8" y="14" width="4.2" height="7" rx="1.7"/></svg>',
  speaker: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7"><rect x="5" y="2.5" width="14" height="19" rx="2.6"/><circle cx="12" cy="14.6" r="3.7"/><circle cx="12" cy="7" r="1.4"/></svg>',
  mic: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="9" y="2.5" width="6" height="12" rx="3"/><path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3.5"/></svg>',
  search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="10.5" cy="10.5" r="6.5"/><path d="M15.5 15.5 21 21"/></svg>',
  list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><rect x="3" y="4" width="18" height="16" rx="2.5"/><path d="M7 9h10M7 12.5h10M7 16h6"/></svg>',
  chevron: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m7 10 5 5 5-5"/></svg>',
  updown: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="m8 9 4-4 4 4M8 15l4 4 4-4"/></svg>',
  vol: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path d="M15.5 9a4 4 0 0 1 0 6M18 6.5a7.5 7.5 0 0 1 0 11"/></svg>',
  mute: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 9.5h3.5L12 5.5v13l-4.5-4H4z" fill="currentColor"/><path d="m16 9.5 5 5M21 9.5l-5 5"/></svg>',
  dots: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="1.9"/><circle cx="12" cy="12" r="1.9"/><circle cx="19" cy="12" r="1.9"/></svg>',
  wand: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M4 20 15 9M14 3.5v3M20.5 10h-3M18.5 5.5l-2 2M9 4.5l1 1M19.5 15.5l-1-1"/></svg>',
  sliders: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 3v18M12 3v18M18 3v18"/><circle cx="6" cy="14" r="2.5" fill="#000"/><circle cx="12" cy="8" r="2.5" fill="#000"/><circle cx="18" cy="15" r="2.5" fill="#000"/></svg>',
  export: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15V3.5M7.5 8 12 3.5 16.5 8M5 13.5V19a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-5.5"/></svg>',
  lr: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="18" height="18" rx="4"/><path d="m7.5 12.5 3 3 6-7"/></svg>',
  wifi: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M2.5 9a14 14 0 0 1 19 0M5.8 12.6a9 9 0 0 1 12.4 0M9.2 16.1a4 4 0 0 1 5.6 0"/><circle cx="12" cy="19.3" r="1.3" fill="currentColor"/></svg>',
  battery: '<svg viewBox="0 0 30 24" fill="none" stroke="currentColor" stroke-width="1.6"><rect x="1.5" y="6" width="24" height="12" rx="3.5"/><rect x="4" y="8.5" width="15" height="7" rx="1.5" fill="currentColor" stroke="none"/><path d="M27.5 10v4" stroke-linecap="round"/></svg>',
  arrow: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h15M14 6l6 6-6 6"/></svg>',
};
const CURSOR = '<svg viewBox="0 0 20 30" width="100%" height="100%"><path d="M1.6 1.6 L1.6 23.2 L7 18 L10.6 26.4 L14.1 24.9 L10.6 16.8 L17.6 16.8 Z" fill="#000" stroke="#fff" stroke-width="1.7" stroke-linejoin="round"/></svg>';
const icon = (name, size, color) => `<span style="display:inline-flex;width:${size}px;height:${size}px;color:${color || 'currentColor'};flex:none">${IC[name]}</span>`;

const BAND_COLORS = ['#ef6b5e', '#f5b14a', '#b5d55a', '#5fd4a7', '#5aaef0', '#7c86f0', '#b07cf0'];
const ICONS = { music: 359, safari: 464, podcasts: 637, logic: 747 }; // y in mixer.png, x = 219, 46 px
function appIcon(parent, key, x, y, size) {
  const e = pos(h('div', 'appicon', parent), x, y, size, size);
  const k = size / 46;
  S(e, {
    backgroundImage: `url(${IMG}zh/mixer.png)`,
    backgroundSize: `${1920 * k}px ${920 * k}px`,
    backgroundPosition: `${-219 * k}px ${-ICONS[key] * k}px`,
  });
  return e;
}

// Split HTML into per-character spans (keeps tags like <em> and <br>).
function splitChars(container, html) {
  container.innerHTML = html;
  const spans = [];
  const walk = (node) => {
    for (const n of [...node.childNodes]) {
      if (n.nodeType === 3) {
        const frag = document.createDocumentFragment();
        for (const ch of n.textContent) {
          const s = document.createElement('span');
          s.className = 'ch'; s.textContent = ch;
          frag.appendChild(s); spans.push(s);
        }
        n.replaceWith(frag);
      } else if (n.nodeType === 1) walk(n);
    }
  };
  walk(container);
  return spans;
}
function charReveal(spans, t, t0, step = 0.03, dur = 0.5) {
  spans.forEach((s, i) => {
    const p = ease.outCubic(pr(t, t0 + i * step, t0 + i * step + dur));
    s.style.opacity = p;
    s.style.transform = `translateY(${(1 - p) * 0.35}em)`;
    s.style.filter = p < 0.99 ? `blur(${(1 - p) * 8}px)` : 'none';
  });
}

function caption(parent, o) {
  const box = pos(h('div', 'abs cap', parent), o.x, o.y, o.w);
  if (o.align) box.style.textAlign = o.align;
  const ti = h('div', 'title', box, o.title);
  const su = o.sub ? h('div', 'sub', box, o.sub) : null;
  return (t, t0, t1) => { reveal(ti, t, t0, t1); if (su) reveal(su, t, t0 + 0.14, t1); };
}

function cursorEl(parent) {
  const c = h('div', 'cursor', parent, CURSOR);
  c.style.zIndex = 50;
  return (x, y, a, down = 0) => {
    vis(c, a);
    c.style.left = x + 'px'; c.style.top = y + 'px';
    c.style.transform = `scale(${(V ? 2.0 : 1.6) * (1 - 0.12 * down)})`;
  };
}

// piecewise path through keyframes [[t, x, y], ...] with easing between them
function track(kf, t) {
  if (t <= kf[0][0]) return kf[0].slice(1);
  for (let i = 1; i < kf.length; i++) if (t <= kf[i][0]) {
    const a = kf[i - 1], b = kf[i], u = ease.inOutCubic((t - a[0]) / (b[0] - a[0]));
    return a.slice(1).map((v, k) => lerp(v, b[k + 1], u));
  }
  return kf[kf.length - 1].slice(1);
}

function fmtF(f) { return f >= 1000 ? (f / 1000).toFixed(f >= 10000 ? 1 : 2).replace(/\.?0+$/, (m) => (m.includes('.') && f < 10000 ? m : '')) + 'k' : f.toFixed(f < 100 ? 1 : 0); }
function fmtHz(f) { return f >= 1000 ? (f / 1000).toFixed(f >= 9995 ? 1 : 2) + 'k Hz' : f.toFixed(f < 100 ? 1 : 0) + ' Hz'; }
function fmtDb(g) { return (g >= 0 ? '+' : '') + g.toFixed(1) + ' dB'; }

// ============================================================ background
function buildBackground(root) {
  const blobs = [
    { c: 'rgba(79,220,184,0.13)', r: 900, x: 0.2, y: 0.25 },
    { c: 'rgba(90,174,240,0.12)', r: 1000, x: 0.85, y: 0.7 },
    { c: 'rgba(255,122,107,0.07)', r: 800, x: 0.75, y: 0.1 },
  ].map((b) => {
    const e = h('div', 'blob', root);
    S(e, { width: b.r * 2 + 'px', height: b.r * 2 + 'px', background: `radial-gradient(circle, ${b.c} 0%, transparent 65%)` });
    return [e, b];
  });
  h('div', 'grid', root);
  const ribbon = h('canvas', 'layer', root);
  ribbon.width = W; ribbon.height = H;
  const rc = ribbon.getContext('2d');
  h('div', 'vignette', root);
  const flash = h('div', 'flash', root);
  return (t) => {
    blobs.forEach(([e, b], i) => {
      const x = b.x * W + Math.sin(t * 0.13 + i * 2) * 120 - b.r;
      const y = b.y * H + Math.cos(t * 0.11 + i) * 90 - b.r;
      e.style.transform = `translate(${x}px, ${y}px)`;
    });
    // glowing EQ ribbon behind the logo and the ending
    const ra = Math.max(
      E('outCubic', t, 4.0, 4.8) * (1 - E('inCubic', t, 7.4, 8.0)),
      E('outCubic', t, 58.2, 59.4) * (1 - E('inCubic', t, 65.3, 66)),
    );
    rc.clearRect(0, 0, W, H);
    if (ra > 0.001) {
      const kp = kickPulse(t);
      const base = (V ? H * 0.82 : H * 0.8) + (t > 50 ? H * 0.07 : 0);
      for (let layer = 0; layer < 3; layer++) {
        rc.beginPath();
        for (let i = 0; i <= 200; i++) {
          const u = i / 200, x = u * W;
          const y = base
            - Math.sin(u * 5.2 + t * 0.6 + layer * 0.7) * (60 + layer * 25)
            - Math.sin(u * 11 - t * 0.9 + layer) * 22
            - kp * 18 * Math.sin(u * Math.PI);
          if (i) rc.lineTo(x, y); else rc.moveTo(x, y);
        }
        rc.strokeStyle = ['rgba(79,220,184,', 'rgba(90,174,240,', 'rgba(176,124,240,'][layer] + (0.5 - layer * 0.12) * ra + ')';
        rc.lineWidth = 3 - layer * 0.6;
        rc.shadowColor = rc.strokeStyle; rc.shadowBlur = 24;
        rc.stroke();
      }
    }
    flash.style.opacity = 0.22 * Math.exp(-Math.max(0, t - TL.ev.drop) / 0.12) * (t >= TL.ev.drop ? 1 : 0)
      + 0.14 * Math.exp(-Math.max(0, t - 58) / 0.12) * (t >= 58 ? 1 : 0);
  };
}

// ============================================================ 1. hook
SCENES.hook = (root, s, e) => {
  const l1 = h('div', 'abs hook', root), l2 = h('div', 'abs hook hook2', root);
  const y1 = V ? 640 : 380, y2 = V ? 960 : 520;
  pos(l1, 0, y1, W); pos(l2, 0, y2, W);
  const c1 = splitChars(l1, T('hook1')), c2 = splitChars(l2, T('hook2'));
  const cv = h('canvas', 'layer', root); cv.width = W; cv.height = H;
  const ctx = cv.getContext('2d');
  return (t) => {
    charReveal(c1, t, 0.25, 0.035);
    charReveal(c2, t, 1.55, 0.03);
    const out = E('inCubic', t, 3.6, 4.0);
    for (const l of [l1, l2]) {
      l.style.opacity = 1 - out;
      l.style.transform = `scale(${1 + out * 0.12})`;
      l.style.filter = out > 0.01 ? `blur(${out * 14}px)` : 'none';
    }
    // a thin "flat EQ" line that comes alive with the (still muffled) music
    ctx.clearRect(0, 0, W, H);
    const ly = V ? 1300 : 760, a = E('outCubic', t, 0.2, 1.2) * (1 - out);
    const lev = levelsAt(t), amp = clamp((lev[0] + 40) / 30, 0, 1);
    const grow = E('inOutCubic', t, 2.6, 3.9);
    ctx.beginPath();
    for (let i = 0; i <= 240; i++) {
      const u = i / 240, x = W * 0.12 + u * W * 0.76;
      const env = Math.sin(u * Math.PI);
      const y = ly - env * (amp * 10 * Math.sin(u * 40 + t * 9) + grow * (70 * Math.sin(u * 6.3 - 0.6) + 26 * Math.sin(u * 15 + t * 2)));
      if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y);
    }
    ctx.strokeStyle = `rgba(255,255,255,${0.85 * a})`;
    ctx.lineWidth = 3;
    ctx.shadowColor = 'rgba(79,220,184,0.9)'; ctx.shadowBlur = 10 + grow * 20;
    ctx.stroke();
  };
};

// ============================================================ 2. logo
SCENES.logo = (root, s, e) => {
  const glow = h('div', 'abs', root);
  const ic = h('img', 'abs', root); ic.src = IMG + 'icon.png';
  const word = h('div', 'abs', root, 'Tapline');
  S(word, { fontFamily: '-apple-system, "SF Pro Display", sans-serif', fontWeight: 700, fontSize: (V ? 150 : 150) + 'px', letterSpacing: '-4px', lineHeight: 1 });
  const tag = h('div', 'abs center', root, T('tagline'));
  S(tag, { fontSize: (V ? 38 : 34) + 'px', color: 'var(--text2)', lineHeight: 1.5 });
  const chips = h('div', 'abs', root);
  S(chips, { display: 'flex', gap: '18px', justifyContent: 'center' });
  const chipEls = T('chips').map((c) => h('div', 'chip', chips, c));
  let icS, icX, icY, wX, wY;
  if (V) {
    icS = 340; icX = (W - icS) / 2; icY = 470; wX = 0; wY = 860;
    S(word, { width: W + 'px', textAlign: 'center' });
    pos(tag, 0, 1060, W); pos(chips, 0, 1230, W);
  } else {
    icS = 230; icX = 560; icY = 250; wX = 820; wY = 300;
    pos(tag, 0, 560, W); pos(chips, 0, 680, W);
  }
  pos(ic, icX, icY, icS, icS);
  pos(word, wX, wY);
  pos(glow, icX - icS, icY - icS, icS * 3, icS * 3);
  S(glow, { background: 'radial-gradient(circle, rgba(79,220,184,0.35) 0%, rgba(90,174,240,0.12) 35%, transparent 65%)' });
  return (t) => {
    const out = E('inCubic', t, e - 0.4, e);
    const sp = ease.spring(pr(t, s, s + 1.1));
    vis(ic, Math.min(1, pr(t, s, s + 0.15)) * (1 - out));
    ic.style.transform = `translateY(${-out * 40}px) scale(${lerp(0.35, 1, sp)}) rotate(${(1 - sp) * -10}deg)`;
    const kp = kickPulse(t);
    vis(glow, (0.6 + 0.4 * kp) * pr(t, s, s + 0.4) * (1 - out));
    glow.style.transform = `scale(${0.9 + 0.12 * kp})`;
    // wordmark wipes in
    const wp = E('outQuint', t, s + 0.3, s + 1.1);
    vis(word, (wp > 0 ? 1 : 0) * (1 - out));
    word.style.clipPath = `inset(-20% ${100 - wp * 100}% -20% 0)`;
    word.style.transform = `translate(${(1 - wp) * -30}px, ${-out * 40}px)`;
    reveal(tag, t, s + 0.8, e);
    chipEls.forEach((c, i) => reveal(c, t, s + 1.4 + i * 0.14, e, { dy: 20, scale: 0.9 }));
  };
};

// ============================================================ 3. equalizer
SCENES.eq = (root, s, e) => {
  const L = V
    ? { cap: [80, 230, 920], panel: [40, 600, 1000, 900], graph: [20, 92, 892, 560], chipsY: 676, chipRows: 2 }
    : { cap: [110, 330, 560], panel: [730, 90, 1090, 900], graph: [24, 92, 966, 690], chipsY: 806, chipRows: 1 };
  const cap1 = caption(root, { x: L.cap[0], y: L.cap[1], w: L.cap[2], title: T('eqTitle'), sub: T('eqSub') });
  const cap2 = caption(root, { x: L.cap[0], y: L.cap[1], w: L.cap[2], title: T('eqTitle2'), sub: T('eqSub2') });
  const [px, py, pw, ph] = L.panel;
  const panel = pos(h('div', 'card abs', root), px, py, pw, ph);

  // toolbar
  const eqLab = pos(h('div', 'abs', panel, 'EQ'), 32, 26);
  S(eqLab, { fontSize: '28px', fontWeight: 700, fontFamily: '-apple-system, sans-serif' });
  const tog = pos(h('div', 'toggle', panel), 86, 22); const knob = h('div', 'knob', tog);
  const preset = pos(h('div', 'pill abs', panel, `${icon('list', 24)}<span>${T('eqPreset')}</span>${icon('chevron', 20)}`), 196, 19);
  const ab = pos(h('div', 'pill abs', panel), V ? 420 : 430, 19);
  ab.innerHTML = '<span class="a" style="font-weight:700">A</span><span style="opacity:.35">/</span><span class="b" style="font-weight:700">B</span>';
  const abA = ab.querySelector('.a'), abB = ab.querySelector('.b');
  const range = h('div', 'seg', panel);
  const rangeLabs = V ? ['±12'] : ['±6', '±12', '±18', '±24'];
  rangeLabs.forEach((r) => { const d = h('div', r === '±12' ? 'on' : '', range, r); d.style.padding = '0 16px'; });
  pos(range, pw - (V ? 130 : 330) - 24, 19, V ? 130 : 330, 48);

  // graph
  const [gx, gy, gw, gh] = L.graph;
  const gbox = pos(h('div', 'graphbox abs', panel), gx, gy, gw, gh);
  const cv = h('canvas', 'abs', gbox); cv.width = gw; cv.height = gh; pos(cv, 0, 0);
  const ch = new Chart(cv, { l: 58, r: 18, t: 22, b: 40, fontSize: V ? 18 : 16 });
  const ctx = ch.ctx;
  const orig = pos(h('div', 'pill abs', gbox, T('eqBypassed')), gw / 2 - 60, 30);
  S(orig, { background: 'rgba(255,190,79,0.18)', color: 'var(--amber)', border: '1px solid rgba(255,190,79,0.4)' });
  const keys = h('div', 'abs', gbox);
  keys.innerHTML = `<div class="keycaps">${['⌃', '⌥', '⌘', 'B'].map((k) => `<div class="key">${k}</div>`).join('')}</div>`;
  pos(keys, gw / 2 - 160, gh / 2 - 50, 340);
  S(keys, { display: 'flex', justifyContent: 'center' });
  const keyEls = [...keys.querySelectorAll('.key')];

  // meters
  const mbox = pos(h('div', 'graphbox abs', panel), gx + gw + 14, gy, pw - gx - gw - 14 - 24, gh);
  const mw = pw - gx - gw - 14 - 24;
  const mcv = h('canvas', 'abs', mbox); mcv.width = mw; mcv.height = gh;
  const mctx = mcv.getContext('2d');

  // band chips
  const chips = [];
  const perRow = L.chipRows === 1 ? 7 : 4, cw = (pw - 48 - (perRow - 1) * 10) / perRow;
  for (let i = 0; i < 7; i++) {
    const r = Math.floor(i / perRow), c = i % perRow;
    const el = pos(h('div', 'bandchip', panel), 24 + c * (cw + 10), L.chipsY + r * 76, cw, 64);
    el.innerHTML = `<div class="dot" style="background:${BAND_COLORS[i]}">${i + 1}</div><div><div class="f"></div><div class="g"></div></div>`;
    chips.push({ el, f: el.querySelector('.f'), g: el.querySelector('.g') });
  }
  const setCursor = cursorEl(root);

  const A = [
    { type: 'hp', f: 24, g: 0, q: 0.71 },
    { type: 'lowshelf', f: 105, g: 4.2, q: 0.7 },
    { type: 'bell', f: 132, g: -3.5, q: 1.2 },
    { type: 'bell', f: 350, g: 2.1, q: 1.0 },
    { type: 'bell', f: 1880, g: 3.4, q: 1.8 },
    { type: 'bell', f: 5990, g: -4.8, q: 4.0 },
    { type: 'highshelf', f: 10000, g: 2.5, q: 0.7 },
  ];
  const Bc = [
    { type: 'hp', f: 32, g: 0, q: 0.71 },
    { type: 'lowshelf', f: 120, g: -1.5, q: 0.7 },
    { type: 'bell', f: 260, g: -2.6, q: 1.1 },
    { type: 'bell', f: 700, g: 0.8, q: 1.0 },
    { type: 'bell', f: 3200, g: 4.2, q: 1.3 },
    { type: 'bell', f: 7200, g: -2.0, q: 3.0 },
    { type: 'highshelf', f: 11000, g: 3.2, q: 0.7 },
  ];
  const ev = TL.ev;
  const dragT0 = 11.45, dragT1 = 12.45, qT0 = 12.7, qT1 = 13.3;

  function bandsAt(t) {
    const out = A.map((b, i) => {
      const w = ease.outBack(pr(t, s + 0.5 + i * 0.2, s + 1.0 + i * 0.2));
      return Object.assign({}, b, { w });
    });
    // drag band 5
    const d = ease.inOutCubic(pr(t, dragT0, dragT1));
    out[4].f = Math.exp(lerp(Math.log(1880), Math.log(2600), d));
    out[4].g = lerp(3.4, 6.0, d);
    out[4].q = lerp(1.8, 1.1, ease.inOutCubic(pr(t, qT0, qT1)));
    // A -> B
    const m = ease.inOutCubic(pr(t, ev.abToB, ev.abToB + 0.6));
    if (m > 0) out.forEach((b, i) => {
      b.f = Math.exp(lerp(Math.log(b.f), Math.log(Bc[i].f), m));
      b.g = lerp(b.g, Bc[i].g, m);
      b.q = Math.exp(lerp(Math.log(b.q), Math.log(Bc[i].q), m));
    });
    return out;
  }
  const nodeXY = (b) => [ch.x(b.f), ch.y(b.type === 'hp' ? 0 : b.g * (b.w ?? 1))];

  return (t) => {
    const out = E('inCubic', t, e - 0.35, e);
    cap1(t, s + 0.15, 14.0);
    cap2(t, 14.05, e);
    const pin = E('outCubic', t, s, s + 0.6);
    vis(panel, pin * (1 - out));
    panel.style.transform = `translateY(${(1 - pin) * 60 - out * 30}px) scale(${lerp(0.96, 1, pin)})`;
    if (pin * (1 - out) <= 0.001) return;

    const byp = pr(t, ev.eqBypassOn, ev.eqBypassOn + 0.12) * (1 - pr(t, ev.eqBypassOff, ev.eqBypassOff + 0.12));
    knob.style.left = lerp(28, 4, byp) + 'px';
    tog.style.background = byp > 0.5 ? '#2c2d33' : '#45464e';
    knob.style.background = byp > 0.5 ? '#9a9ba3' : '#fff';
    const isB = t >= ev.abToB;
    abA.style.color = isB ? 'var(--text3)' : 'var(--mint)';
    abB.style.color = isB ? 'var(--mint)' : 'var(--text3)';
    vis(orig, byp);
    // keycaps flash before the bypass
    const kp = pr(t, ev.eqBypassOn - 0.55, ev.eqBypassOn - 0.35) * (1 - pr(t, ev.eqBypassOn + 0.5, ev.eqBypassOn + 0.8));
    vis(keys, kp);
    keys.style.transform = `scale(${lerp(0.85, 1, ease.outBack(pr(t, ev.eqBypassOn - 0.55, ev.eqBypassOn - 0.25)))})`;
    keyEls.forEach((k, i) => {
      const down = pr(t, ev.eqBypassOn - 0.3 + i * 0.04, ev.eqBypassOn - 0.24 + i * 0.04) * (1 - pr(t, ev.eqBypassOn + 0.1, ev.eqBypassOn + 0.2));
      k.style.transform = `translateY(${down * 5}px)`;
      k.style.boxShadow = `0 ${6 - down * 5}px 0 #16171b, 0 12px 30px rgba(0,0,0,0.45)`;
    });

    const bands = bandsAt(t);
    const cur = curve(bands);
    const pre = spectrumAt(t);
    const post = new Float32Array(NPTS);
    for (let i = 0; i < NPTS; i++) post[i] = pre[i] + cur[i] * (1 - byp);

    ch.clear();
    ch.grid();
    ch.fill(post, 'rgba(78,150,214,0.55)', 'rgba(30,60,96,0.04)');
    ch.line(pre, 'rgba(255,255,255,0.28)', 1.4);
    ch.line(post, 'rgba(120,186,240,0.95)', 1.6);
    // selected band area (during the drag)
    const selA = pr(t, dragT0 - 0.25, dragT0) * (1 - pr(t, 13.9, 14.3));
    if (selA > 0) {
      const bd = bandDb(bands[4]);
      const vals = FREQS.map(bd);
      ch.fill(vals, 'rgba(90,174,240,0.38)', 'rgba(90,174,240,0.06)', { base: 0, alpha: selA });
      ch.line(vals, 'rgba(90,174,240,0.9)', 2, { alpha: selA });
    }
    // EQ curve
    const ca = lerp(1, 0.28, byp);
    ch.line(cur, `rgba(255,255,255,${ca})`, 4.5, { glow: `rgba(255,255,255,${0.55 * ca})`, glowBlur: 16 });
    // nodes
    bands.forEach((b, i) => {
      const w = pr(t, s + 0.5 + i * 0.2, s + 0.8 + i * 0.2);
      if (w <= 0) return;
      const [x, y] = nodeXY(b);
      const sel = i === 4 && selA > 0.5;
      const r = (V ? 16 : 15) * ease.outBack(w) * (sel ? 1.15 : 1);
      ctx.save();
      ctx.globalAlpha = ca * 0.6 + 0.4;
      ctx.shadowColor = BAND_COLORS[i]; ctx.shadowBlur = sel ? 22 : 10;
      ctx.beginPath(); ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.fillStyle = BAND_COLORS[i]; ctx.fill();
      ctx.shadowBlur = 0;
      ctx.lineWidth = sel ? 4 : 2.5; ctx.strokeStyle = sel ? '#fff' : 'rgba(255,255,255,0.75)'; ctx.stroke();
      ctx.fillStyle = '#121316'; ctx.font = `700 ${V ? 17 : 16}px -apple-system, sans-serif`;
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
      ctx.fillText(String(i + 1), x, y + 0.5);
      ctx.restore();
    });
    // tooltip
    if (selA > 0) {
      const b = bands[4], [x, y] = nodeXY(b);
      const txt = `${fmtHz(b.f)}  ${fmtDb(b.g)}  Q ${b.q.toFixed(2)}`;
      ctx.save();
      ctx.globalAlpha = selA;
      ctx.font = `600 ${V ? 21 : 19}px -apple-system, sans-serif`;
      const tw = ctx.measureText(txt).width + 30, th = V ? 42 : 38;
      const tx = clamp(x - tw / 2, 64, gw - tw - 20), ty = y - th - 30;
      ctx.fillStyle = '#4a8fd6';
      ctx.beginPath(); ctx.roundRect(tx, ty, tw, th, 9); ctx.fill();
      ctx.fillStyle = '#fff'; ctx.textAlign = 'left'; ctx.textBaseline = 'middle';
      ctx.fillText(txt, tx + 15, ty + th / 2 + 1);
      ctx.restore();
    }
    // chips
    bands.forEach((b, i) => {
      const c = chips[i];
      reveal(c.el, t, s + 0.5 + i * 0.2, null, { dy: 14, din: 0.4, blur: 4 });
      c.f.textContent = fmtHz(b.f).replace(' Hz', V ? '' : ' Hz');
      c.g.textContent = b.type === 'hp' ? 'Q ' + b.q.toFixed(2) : fmtDb(b.g * (b.w ?? 1));
      c.el.classList.toggle('sel', i === 4 && selA > 0.5);
    });
    // meters
    const lev = levelsAt(t);
    mctx.clearRect(0, 0, mw, gh);
    const mt = 30, mb = 40, mh = gh - mt - mb, bwid = Math.min(14, (mw - 26) / 2);
    for (let c = 0; c < 2; c++) {
      const x = mw / 2 - bwid - 3 + c * (bwid + 6);
      const toH = (d) => clamp((d + 60) / 60) * mh;
      mctx.fillStyle = 'rgba(255,255,255,0.06)'; mctx.fillRect(x, mt, bwid, mh);
      const ph = toH(lev[2 + c]), rh = toH(lev[c] + 3);
      mctx.fillStyle = 'rgba(95,212,167,0.35)'; mctx.fillRect(x, mt + mh - ph, bwid, ph);
      const g = mctx.createLinearGradient(0, mt, 0, mt + mh);
      g.addColorStop(0, '#d9c36a'); g.addColorStop(0.25, '#6fd3a0'); g.addColorStop(1, '#4fc59a');
      mctx.fillStyle = g; mctx.fillRect(x, mt + mh - rh, bwid, rh);
      mctx.fillStyle = '#fff'; mctx.fillRect(x, mt + mh - ph - 2, bwid, 2);
    }
    mctx.fillStyle = 'rgba(255,255,255,0.5)'; mctx.font = '500 15px -apple-system, sans-serif'; mctx.textAlign = 'center';
    mctx.fillText(Math.max(lev[2], lev[3]).toFixed(1), mw / 2, gh - 16);

    // cursor: approach band 5, drag it, scroll for Q, leave
    const b5 = bands[4], [nx, ny] = nodeXY(b5);
    const ox = px + gx, oy = py + gy; // graph origin on stage
    const kf = [[10.7, ox + gw * 0.9, oy + gh * 0.95], [dragT0 - 0.1, ox + ch.x(1880), oy + ch.y(3.4)]];
    let cx, cy;
    if (t < dragT0) [cx, cy] = track(kf, t); else { cx = ox + nx; cy = oy + ny; }
    if (t > 13.5) { const u = ease.inOutCubic(pr(t, 13.5, 14.2)); cx += u * 160; cy += u * 120; }
    const ca2 = pr(t, 10.7, 11.0) * (1 - pr(t, 13.9, 14.2));
    setCursor(cx, cy, ca2 * (1 - out), t >= dragT0 && t < 13.45 ? 1 : 0);
  };
};

// ============================================================ 4. headphones
SCENES.hp = (root, s, e) => {
  const L = V
    ? { cap: [80, 230, 920], card: [40, 610, 1000, 880] }
    : { cap: [110, 250, 560], card: [730, 90, 1090, 900] };
  const cap = caption(root, { x: L.cap[0], y: L.cap[1], w: L.cap[2], title: T('hpTitle'), sub: T('hpSub') });
  const stats = h('div', 'abs', root);
  if (V) { pos(stats, 80, 380, 920); S(stats, { display: 'flex', gap: '60px' }); }
  else pos(stats, 110, 560, 560);
  const statEls = [T('stat1'), T('stat2')].map(([n, l]) => {
    const d = h('div', 'stat', stats); if (!V) d.style.marginBottom = '34px';
    const num = h('div', 'num', d, n); h('div', 'lab', d, l);
    return { d, num, n };
  });
  const [cx0, cy0, cw, chh] = L.card;
  const card = pos(h('div', 'card abs', root), cx0, cy0, cw, chh);
  const head = pos(h('div', 'abs', card, `${icon('headphones', 34)}<span>${T('hpCard')}</span>`), 36, 30);
  S(head, { display: 'flex', alignItems: 'center', gap: '14px', fontSize: '28px', fontWeight: 600 });
  const tabs = pos(h('div', 'seg', card), 36, 92, V ? 720 : 620, 52);
  T('hpTabs').forEach((x, i) => h('div', i === 1 ? 'on' : '', tabs, x));
  const search = pos(h('div', 'inner abs', card), 36, 166, cw - 72, 60);
  S(search, { display: 'flex', alignItems: 'center', gap: '14px', padding: '0 20px', fontSize: '26px' });
  search.innerHTML = `${icon('search', 26, 'var(--text3)')}<span class="q"></span><span class="caret" style="width:2px;height:30px;background:var(--blue)"></span>`;
  const q = search.querySelector('.q'), caret = search.querySelector('.caret');

  // results list (landscape) / single row (portrait)
  const rows = [];
  const listW = V ? cw - 72 : 380, nRows = V ? 1 : 4;
  for (let i = 0; i < nRows; i++) {
    const r = pos(h('div', 'abs', card), 36, 246 + i * 88, listW, 80);
    S(r, { borderRadius: '14px', padding: '12px 18px', display: 'flex', alignItems: 'center', gap: '16px' });
    r.innerHTML = `${icon('headphones', 28, 'var(--text3)')}<div><div style="font-size:23px;font-weight:600">${T('hpRows')[i][0]}</div><div style="font-size:18px;color:var(--text3);margin-top:4px">${T('hpRows')[i][1]}</div></div>`;
    rows.push(r);
  }
  const rx = V ? 36 : 440, rw = V ? cw - 72 : cw - 440 - 36;
  const ry = V ? 350 : 246;
  const model = pos(h('div', 'abs', card), rx, ry, rw);
  model.innerHTML = `<div style="display:flex;align-items:center;justify-content:space-between"><div style="font-size:27px;font-weight:700">Sennheiser HD 600</div><div style="display:flex;align-items:center;gap:12px;font-size:21px;color:var(--text2)">${T('hpTargetLabel')}<span class="pill" style="height:42px;font-size:20px">Harman 2018 ${icon('updown', 18)}</span></div></div>`;
  const chartY = ry + 66, chartH = V ? 330 : 380;
  const gbox = pos(h('div', 'graphbox abs', card), rx, chartY, rw, chartH);
  const cv = h('canvas', 'abs', gbox); cv.width = rw; cv.height = chartH;
  const ch = new Chart(cv, { dbMin: 70, dbMax: 110, dbStep: 10, l: 54, r: 16, t: 18, b: 38, fontSize: 16 });
  const pill = pos(h('div', 'status bad abs', gbox), rw - 170, 22);
  const legend = pos(h('div', 'legend abs', card), rx, chartY + chartH + 18, rw);
  const lg = T('hpLegend');
  legend.innerHTML = `<span><i style="border-color:rgba(255,255,255,0.55)"></i>${lg[0]}</span><span><i style="border-top-style:dashed;border-color:#fff"></i>${lg[1]}</span><span><i style="border-color:var(--mint)"></i>${lg[2]}</span><span class="dev" style="margin-left:auto"></span>`;
  const dev = legend.querySelector('.dev');
  const btn = pos(h('div', 'btn abs', card, `${icon('wand', 26)}<span>${T('hpApply')}</span>`), rx, chartY + chartH + 66);

  const target = shapeCurve([[20, 101], [60, 100.5], [150, 97.5], [300, 95.2], [1000, 95], [2000, 98], [3000, 102.5], [4500, 101], [6500, 97.5], [10000, 92], [16000, 86], [20000, 79]], FREQS);
  const meas = shapeCurve([[20, 85.5], [40, 89.5], [80, 92.8], [200, 94.8], [1000, 95.5], [2000, 98.2], [3000, 101], [4300, 102.8], [5300, 98.8], [6400, 101.2], [8000, 96.5], [10000, 97], [13000, 92.5], [16000, 90], [20000, 84]], FREQS, 2.2, 3, 2500);
  const after = new Float32Array(NPTS);
  for (let i = 0; i < NPTS; i++) {
    const f = FREQS[i], k = f < 6000 ? 0.12 : lerp(0.12, 0.55, clamp(Math.log(f / 6000) / Math.log(2)));
    after[i] = target[i] + (meas[i] - target[i]) * k + vnoise(i * 0.7 + 40) * 0.25;
  }
  const ev = TL.ev;
  const typed = 'HD 600';
  return (t) => {
    const out = E('inCubic', t, e - 0.35, e);
    cap(t, s + 0.15, e);
    const sub = root.querySelector('.sub');
    if (V && sub) { // in portrait the stats take the subtitle's place
      const so = E('inCubic', t, 25.0, 25.35);
      sub.style.opacity = Math.min(+sub.style.opacity, 1 - so);
    }
    statEls.forEach((st, i) => {
      reveal(st.d, t, 25.4 + i * 0.25, e, { dy: 30 });
      const p = E('outCubic', t, 25.4 + i * 0.25, 26.4 + i * 0.25);
      const target = parseInt(st.n.replace(/\D/g, ''), 10);
      st.num.textContent = Math.round(target * p).toLocaleString('en-US') + '+';
    });
    const pin = E('outCubic', t, s, s + 0.6);
    vis(card, pin * (1 - out));
    card.style.transform = `translateY(${(1 - pin) * 60 - out * 30}px) scale(${lerp(0.96, 1, pin)})`;
    if (pin * (1 - out) <= 0.001) return;
    const nChars = Math.floor(clamp((t - 20.55) / 0.11, 0, typed.length));
    q.textContent = typed.slice(0, nChars);
    caret.style.opacity = (Math.floor(t * 2.2) % 2 === 0 || (t > 20.5 && t < 21.3)) && t < 22 ? 1 : 0;
    rows.forEach((r, i) => {
      reveal(r, t, 21.35 + i * 0.08, null, { dy: 14, din: 0.35, blur: 4 });
      const sel = i === 0 && t >= ev.hpRow;
      r.style.background = sel ? 'rgba(255,255,255,0.09)' : 'transparent';
    });
    reveal(model, t, ev.hpRow + 0.05, null, { dy: 10, din: 0.35, blur: 3 });
    reveal(gbox, t, ev.hpRow, null, { dy: 16, din: 0.4, blur: 3 });
    reveal(legend, t, 22.6, null, { dy: 8, din: 0.4, blur: 2 });
    reveal(btn, t, 23.0, null, { dy: 8, din: 0.4, blur: 2 });
    const press = pr(t, ev.hpApply - 0.08, ev.hpApply) * (1 - pr(t, ev.hpApply + 0.08, ev.hpApply + 0.25));
    const lit = pr(t, ev.hpApply, ev.hpApply + 0.2);
    btn.style.transform += ` scale(${1 - press * 0.05})`;
    btn.style.background = lit ? `rgba(79,220,184,${0.12 + 0.2 * lit})` : 'rgba(255,255,255,0.1)';
    btn.style.color = lit > 0.5 ? 'var(--mint)' : '';
    btn.style.boxShadow = lit ? `0 0 ${40 * lit}px rgba(79,220,184,${0.35 * lit})` : 'none';
    const done = t >= ev.hpApply;
    pill.className = 'status abs ' + (done ? 'good' : 'bad');
    pill.innerHTML = done ? `✓ ${T('after')}` : T('before');
    reveal(pill, t, 22.2, null, { dy: 6, din: 0.3, blur: 2 });

    ch.clear();
    ch.grid();
    const dm = E('outCubic', t, 22.1, 22.9);
    if (dm > 0) ch.line(meas, 'rgba(255,255,255,0.55)', 2.4, { upto: dm });
    const ta = E('outCubic', t, 22.6, 23.2);
    if (ta > 0) ch.line(target, 'rgba(255,255,255,0.9)', 2, { dash: [7, 6], alpha: ta });
    const am = E('inOutCubic', t, 23.4, 24.4);
    if (am > 0) {
      const v = new Float32Array(NPTS);
      for (let i = 0; i < NPTS; i++) v[i] = lerp(meas[i], after[i], am);
      ch.line(v, '#4fdcb8', 3, { alpha: Math.min(1, am * 3), glow: 'rgba(79,220,184,0.6)', glowBlur: 12 });
    }
    const dv = lerp(4.5, 2.1, am);
    dev.innerHTML = t < 23.4 ? '' : `${T('hpDev')} 4.5 → <b style="color:var(--mint)">${dv.toFixed(1)} dB</b>`;
  };
};

// ============================================================ 5. room
SCENES.room = (root, s, e) => {
  const L = V
    ? { cap: [80, 230, 920], card: [40, 590, 1000, 760] }
    : { cap: [110, 200, 560], card: [730, 90, 1090, 900] };
  const cap = caption(root, { x: L.cap[0], y: L.cap[1], w: L.cap[2], title: T('roomTitle'), sub: T('roomSub') });
  const checks = h('div', 'abs', root);
  if (V) { pos(checks, 60, 1380, 960); S(checks, { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '18px 20px' }); }
  else { pos(checks, 110, 560, 580); S(checks, { display: 'flex', flexDirection: 'column', gap: '22px' }); }
  const checkEls = T('roomChecks').map(([k, txt]) => {
    const d = h('div', 'check', checks, `<div class="ic ${k}">${k === 'ok' ? '✓' : '!'}</div><span>${txt}</span>`);
    if (V) d.style.fontSize = '28px';
    return d;
  });
  const [cx0, cy0, cw, chh] = L.card;
  const card = pos(h('div', 'card abs', root), cx0, cy0, cw, chh);
  const head = pos(h('div', 'abs', card, `${icon('speaker', 32)}<span>${T('roomCard')}</span>`), 36, 30);
  S(head, { display: 'flex', alignItems: 'center', gap: '14px', fontSize: '28px', fontWeight: 600 });
  const stat = pos(h('div', 'abs', card), cw - 380, 34, 344);
  S(stat, { textAlign: 'right', fontSize: '21px', color: 'var(--text2)' });

  // speakers + mic
  const spY = 96, spH = V ? 150 : 170;
  const stageBox = pos(h('div', 'inner abs', card), 36, spY, cw - 72, spH);
  const spk = [0, 1].map((i) => {
    const d = pos(h('div', 'abs', stageBox), i ? (cw - 72) * 0.72 : (cw - 72) * 0.28 - 44, 26, 88, 110);
    S(d, { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px', color: 'var(--text)' });
    d.innerHTML = `${icon('speaker', 72)}<span style="font-size:20px;color:var(--text2)">${i ? T('roomR') : T('roomL')}</span>`;
    const rings = pos(h('canvas', 'abs', stageBox), 0, 0); rings.width = cw - 72; rings.height = spH;
    return { d, rings, ctx: rings.getContext('2d') };
  });
  const mic = pos(h('div', 'abs', stageBox, icon('mic', 50, 'var(--mint)')), (cw - 72) / 2 - 25, spH - 76);
  const dist = pos(h('div', 'abs', stageBox), (cw - 72) * 0.72 + 100, 50);
  S(dist, { fontSize: '22px', fontWeight: 600, color: 'var(--amber)' });
  dist.innerHTML = '← 4 cm';

  const chartY = spY + spH + 22, chartH = V ? 340 : 440;
  const gbox = pos(h('div', 'graphbox abs', card), 36, chartY, cw - 72, chartH);
  const cv = h('canvas', 'abs', gbox); cv.width = cw - 72; cv.height = chartH;
  const ch = new Chart(cv, { dbMin: -20, dbMax: 10, dbStep: 10, l: 56, r: 16, t: 18, b: 38, fontSize: 16 });
  const pill = pos(h('div', 'status bad abs', gbox), cw - 72 - 170, 22);
  const legend = pos(h('div', 'legend abs', card), 36, chartY + chartH + 18, cw - 72);
  const lg = T('roomLegend');
  legend.innerHTML = `<span><i style="border-color:rgba(255,255,255,0.55)"></i>${lg[0]}</span><span><i style="border-top-style:dashed;border-color:#fff"></i>${lg[1]}</span><span><i style="border-color:var(--mint)"></i>${lg[2]}</span>`;
  const exp = pos(h('div', 'abs', card), 36, chartY + chartH + 66, cw - 72);
  S(exp, { display: 'flex', gap: '16px' });
  const ex = T('roomExport');
  const exEls = [h('div', 'btn', exp, `${icon('export', 24)}<span>${ex[0]}</span>`), h('div', 'btn', exp, `${icon('lr', 24, 'var(--mint)')}<span>${ex[1]}</span>`)];

  const meas = shapeCurve([[20, -13], [28, -7], [36, -1], [47, 7], [56, 2.5], [68, 1.2], [85, -1], [100, -5.5], [112, -8.5], [128, -3], [150, 1], [177, 5.6], [205, 0.5], [260, -1.2], [350, -2.6], [480, -0.2], [1000, 0.5], [3000, 0.9], [8000, 0], [15000, -1.4], [20000, -3]], FREQS, 1.1, 9, 500);
  const fixed = new Float32Array(NPTS);
  for (let i = 0; i < NPTS; i++) {
    const f = FREQS[i];
    const w = f < 34 ? 0 : f < 40 ? (f - 34) / 6 : f > 600 ? 0 : f > 450 ? (600 - f) / 150 : 1;
    const corr = clamp(meas[i], -3, 99) * 0.93;
    fixed[i] = meas[i] - corr * w + vnoise(i * 0.9 + 70) * 0.25 * w;
  }
  const ev = TL.ev;
  return (t) => {
    const out = E('inCubic', t, e - 0.35, e);
    cap(t, s + 0.15, e);
    checkEls.forEach((c, i) => reveal(c, t, 32.9 + i * 0.55, e, { dx: -24, dy: 0 }));
    const pin = E('outCubic', t, s, s + 0.6);
    vis(card, pin * (1 - out));
    card.style.transform = `translateY(${(1 - pin) * 60 - out * 30}px) scale(${lerp(0.96, 1, pin)})`;
    if (pin * (1 - out) <= 0.001) return;
    // sweeps
    const sw = [[ev.roomSweepL, 0], [ev.roomSweepR, 1]];
    spk.forEach((sp, i) => {
      const t0 = sw[i][0], on = t >= t0 && t < t0 + 0.75;
      sp.ctx.clearRect(0, 0, cw - 72, spH);
      sp.d.style.color = on ? 'var(--mint)' : 'var(--text)';
      if (t >= t0 && t < t0 + 1.4) {
        const cx = (i ? (cw - 72) * 0.72 : (cw - 72) * 0.28 - 44) + 44, cy = 62;
        for (let k = 0; k < 4; k++) {
          const p = ((t - t0) * 1.6 - k * 0.25);
          if (p < 0 || p > 1) continue;
          sp.ctx.beginPath(); sp.ctx.arc(cx, cy, 50 + p * 160, -0.9 + (i ? Math.PI : 0) * 0, Math.PI * 2);
          sp.ctx.strokeStyle = `rgba(79,220,184,${(1 - p) * 0.5})`; sp.ctx.lineWidth = 2; sp.ctx.stroke();
        }
      }
    });
    const swp = t < ev.roomSweepL ? 0 : t < ev.roomSweepL + 0.7 ? (t - ev.roomSweepL) / 0.7 : t < ev.roomSweepR ? 1 : t < ev.roomSweepR + 0.7 ? (t - ev.roomSweepR) / 0.7 : 1;
    const hz = 40 * Math.pow(400, swp);
    const side = t < ev.roomSweepR ? T('roomL') : T('roomR');
    stat.innerHTML = t < 32.1 && t > ev.roomSweepL - 0.2 ? `${T('roomMeasuring')} · ${side} · <b style="color:var(--mint)">${hz >= 1000 ? (hz / 1000).toFixed(1) + ' kHz' : Math.round(hz) + ' Hz'}</b>` : (t >= 32.1 ? '3 / 3 · 2.1 m' : '');
    vis(mic, 1);
    reveal(dist, t, 32.9, 35.6, { dx: 20, dy: 0, din: 0.35 });
    reveal(gbox, t, s + 0.2, null, { dy: 12, din: 0.5, blur: 3 });
    reveal(legend, t, 32.4, null, { dy: 8, din: 0.4, blur: 2 });
    exEls.forEach((x, i) => reveal(x, t, 37.3 + i * 0.25, null, { dy: 16, scale: 0.94 }));
    const done = t >= ev.roomFix + 0.2;
    pill.className = 'status abs ' + (done ? 'good' : 'bad');
    pill.innerHTML = done ? `✓ ${T('after')}` : T('before');
    reveal(pill, t, 32.2, null, { dy: 6, din: 0.3, blur: 2 });

    ch.clear();
    ch.grid();
    ch.line(new Float32Array(NPTS), 'rgba(255,255,255,0.85)', 2, { dash: [7, 6], alpha: E('outCubic', t, 31.8, 32.4) });
    const dm = E('outCubic', t, 31.9, 32.8);
    const fm = E('inOutCubic', t, ev.roomFix, ev.roomFixEnd);
    if (dm > 0) ch.line(meas, 'rgba(255,255,255,0.55)', 2.4, { upto: dm });
    if (dm > 0) {
      const v = new Float32Array(NPTS);
      for (let i = 0; i < NPTS; i++) v[i] = lerp(meas[i], fixed[i], fm);
      ch.line(v, '#4fdcb8', 3, { upto: dm, alpha: lerp(0.0, 1, Math.max(fm, 0)), glow: 'rgba(79,220,184,0.6)', glowBlur: 12 });
    }
    // resonance markers
    const marks = [[47, 7, 33.95], [177, 5.6, 34.15]];
    for (const [f, d, t0] of marks) {
      const a = E('outBack', t, t0, t0 + 0.4) * (1 - E('inCubic', t, ev.roomFix, ev.roomFix + 0.6));
      if (a <= 0) continue;
      const c = ch.ctx, x = ch.x(f), y = ch.y(d);
      c.save();
      c.globalAlpha = clamp(a);
      c.strokeStyle = '#ffbe4f'; c.lineWidth = 3;
      c.beginPath(); c.arc(x, y, 24 * a + 4 * kickPulse(t), 0, Math.PI * 2); c.stroke();
      c.fillStyle = '#ffbe4f'; c.font = '600 18px -apple-system, sans-serif'; c.textAlign = 'center';
      c.fillText(`${f} Hz +${d} dB`, x + 4, y - 38);
      c.restore();
    }
  };
};

// ============================================================ 6. mixer
SCENES.mixer = (root, s, e) => {
  const L = V
    ? { cap: [80, 230, 920], card: [40, 600, 1000, 740], sl: [440, 300], pctX: 790 }
    : { cap: [110, 380, 560], card: [730, 165, 1090, 750], sl: [520, 380], pctX: 920 };
  const cap = caption(root, { x: L.cap[0], y: L.cap[1], w: L.cap[2], title: T('mixTitle'), sub: T('mixSub') });
  const [cx0, cy0, cw, chh] = L.card;
  const card = pos(h('div', 'card abs', root), cx0, cy0, cw, chh);
  const [slX, slW] = L.sl;
  function slider(parent, y) {
    const tr = pos(h('div', 'track', parent), slX, y - 4, slW);
    const fill = h('div', 'fill', tr);
    const tick = h('div', 'tick', tr); tick.style.left = slW / 2 - 1 + 'px';
    const kn = h('div', 'sknob', tr); kn.style.top = '-12px';
    const pct = pos(h('div', 'abs', parent), L.pctX - 30, y - 18, 120);
    S(pct, { fontSize: '26px', textAlign: 'right', color: 'var(--text2)', fontFamily: '-apple-system, sans-serif' });
    const vol = pos(h('div', 'abs', parent, IC.vol), slX - 52, y - 16, 32, 32); vol.style.color = 'var(--text2)';
    const dots = pos(h('div', 'abs', parent, IC.dots), L.pctX + 110, y - 15, 30, 30); dots.style.color = 'var(--text2)';
    return {
      set(v, dim = 0) {
        const x = clamp(v / 200) * slW;
        fill.style.width = x + 'px'; kn.style.left = x - 22 + 'px';
        kn.style.background = dim ? '#77787f' : '#fff';
        fill.style.background = dim ? 'rgba(255,255,255,0.12)' : 'rgba(255,255,255,0.42)';
        pct.textContent = Math.round(v) + '%';
        pct.style.opacity = dim ? 0.45 : 1;
      },
      knobX: (v) => cx0 + slX + clamp(v / 200) * slW, vol, pct, dots,
    };
  }
  const dev = pos(h('div', 'inner abs', card), 30, 28, cw - 60, 104);
  const devName = pos(h('div', 'abs', dev, `${icon('headphones', 40)}<div><div style="font-size:26px;font-weight:600">${T('mixDevice')[0]}</div><div style="font-size:19px;color:var(--text3);margin-top:3px">${T('mixDevice')[1]}</div></div>`), 26, 22);
  S(devName, { display: 'flex', alignItems: 'center', gap: '18px' });
  const devSl = slider(card, 80); devSl.dots.style.display = 'none';
  const sec1 = pos(h('div', 'abs', card, T('mixNow')), 34, 160); S(sec1, { fontSize: '23px', fontWeight: 700 });
  const sec2 = pos(h('div', 'abs', card, T('mixOther')), 34, 450); S(sec2, { fontSize: '23px', fontWeight: 700 });
  const apps = ['music', 'safari', 'podcasts', 'logic'];
  const ys = [205, 315, 495, 605];
  const rowEls = apps.map((k, i) => {
    const r = pos(h('div', 'row', card), 30, ys[i], cw - 60, 96);
    appIcon(r, k, 24, 22, 52);
    const nm = pos(h('div', 'abs', r), 94, i < 2 ? 18 : 16, 320);
    nm.innerHTML = `<div style="display:flex;align-items:center;gap:10px;font-size:25px;font-weight:600"><span>${T('mixApps')[i]}</span>${i < 2 ? '<span style="width:10px;height:10px;border-radius:50%;background:var(--mint)"></span>' : ''}<span class="bdg"></span></div><div class="st" style="font-size:18px;color:var(--text3);margin-top:6px">${i < 2 ? '' : T('mixInMix')}</div>`;
    const lv = i < 2 ? pos(h('div', 'abs', r), 94, 66, V ? 260 : 300, 6) : null;
    if (lv) S(lv, { background: 'rgba(255,255,255,0.08)', borderRadius: '3px', overflow: 'hidden' });
    const lvf = lv ? h('div', '', lv) : null;
    if (lvf) S(lvf, { height: '100%', background: 'linear-gradient(90deg,#4fc59a,#6fd3a0 80%,#d9c36a)', borderRadius: '3px' });
    const sl = slider(card, ys[i] + 48);
    return { r, nm, sl, lvf, bdg: nm.querySelector('.bdg'), st: nm.querySelector('.st') };
  });
  const setCursor = cursorEl(root);
  const ev = TL.ev;
  const vSafari = (t) => lerp(100, 40, ease.inOutCubic(pr(t, ev.mixSafari, ev.mixSafari + 0.6)));
  const vMusic = (t) => lerp(100, 150, ease.inOutCubic(pr(t, ev.mixMusic + 0.1, ev.mixMusic + 0.75)));
  return (t) => {
    const out = E('inCubic', t, e - 0.35, e);
    cap(t, s + 0.15, e);
    const pin = E('outCubic', t, s, s + 0.6);
    vis(card, pin * (1 - out));
    card.style.transform = `translateY(${(1 - pin) * 60 - out * 30}px) scale(${lerp(0.96, 1, pin)})`;
    if (pin * (1 - out) <= 0.001) { setCursor(0, 0, 0); return; }
    devSl.set(110);
    devSl.pct.textContent = '55%';
    const lev = levelsAt(t);
    const vs = [vMusic(t), vSafari(t), 100, 100];
    const muted = t >= ev.mixMute, byp = t >= ev.mixBypass;
    rowEls.forEach((r, i) => {
      reveal(r.r, t, s + 0.25 + i * 0.1, null, { dy: 16, din: 0.4, blur: 3 });
      const dim = (i === 2 && muted) || (i === 3 && byp);
      r.sl.set(vs[i], dim);
      if (r.lvf) r.lvf.style.width = clamp((lev[i === 0 ? 0 : 1] + 30 + 20 * Math.log10(vs[i] / 100)) / 26) * 100 + '%';
      if (i === 2) {
        r.sl.vol.innerHTML = muted ? IC.mute : IC.vol;
        r.sl.vol.style.color = muted ? 'var(--coral)' : 'var(--text2)';
        if (muted) r.sl.pct.textContent = '—';
      }
      if (i === 3) {
        const bp = E('outBack', t, ev.mixBypass, ev.mixBypass + 0.4);
        r.bdg.innerHTML = bp > 0 ? `<span class="badge" style="transform:scale(${bp});display:inline-flex">${T('mixBypass')}</span>` : '';
        r.st.textContent = byp ? T('mixBypassSub') : T('mixInMix');
        r.r.style.boxShadow = byp ? `inset 0 0 0 2px rgba(255,190,79,${0.5 * (1 - pr(t, ev.mixBypass + 0.6, ev.mixBypass + 1.6)) + 0.15})` : 'none';
      }
    });
    // cursor path (stage coordinates)
    const rowY = (i) => cy0 + ys[i] + 48;
    const kf = [
      [s + 0.6, cx0 + cw + 40, cy0 + chh * 0.9],
      [ev.mixSafari - 0.05, rowEls[1].sl.knobX(100), rowY(1)],
    ];
    let x, y, down = 0;
    if (t < ev.mixSafari) [x, y] = track(kf, t);
    else if (t < ev.mixSafari + 0.65) { x = rowEls[1].sl.knobX(vSafari(t)); y = rowY(1); down = 1; }
    else if (t < ev.mixMute) [x, y] = track([[ev.mixSafari + 0.65, rowEls[1].sl.knobX(40), rowY(1)], [ev.mixMute - 0.05, cx0 + slX - 36, rowY(2)]], t);
    else if (t < ev.mixMusic) { [x, y] = track([[ev.mixMute + 0.1, cx0 + slX - 36, rowY(2)], [ev.mixMusic + 0.05, rowEls[0].sl.knobX(100), rowY(0)]], t); down = t < ev.mixMute + 0.12 ? 1 : 0; }
    else if (t < ev.mixMusic + 0.8) { x = rowEls[0].sl.knobX(vMusic(t)); y = rowY(0); down = t > ev.mixMusic + 0.08 ? 1 : 0; }
    else if (t < ev.mixBypass) [x, y] = track([[ev.mixMusic + 0.8, rowEls[0].sl.knobX(150), rowY(0)], [ev.mixBypass - 0.05, cx0 + L.pctX + 125, rowY(3)]], t);
    else { [x, y] = track([[ev.mixBypass + 0.3, cx0 + L.pctX + 125, rowY(3)], [ev.mixBypass + 1.2, cx0 + cw + 60, rowY(3) + 120]], t); down = t < ev.mixBypass + 0.12 ? 1 : 0; }
    const ca = pr(t, s + 0.6, s + 0.9) * (1 - pr(t, ev.mixBypass + 0.8, ev.mixBypass + 1.2));
    setCursor(x, y, ca * (1 - out), down);
  };
};

// ============================================================ 7a. devices
function topCaption(root, title, sub) {
  return caption(root, { x: V ? 60 : 160, y: V ? 250 : 110, w: V ? 960 : 1600, title, sub, align: 'center' });
}
SCENES.devices = (root, s, e) => {
  const cap = topCaption(root, T('devTitle'), T('devSub'));
  const names = T('devices');
  const cards = [0, 1].map((i) => {
    const [x, y, w, hh] = V ? [90, 640 + i * 360, 900, 320] : [250 + i * 740, 400, 680, 480];
    const c = pos(h('div', 'card devcard abs', root), x, y, w, hh);
    const head = h('div', '', c);
    S(head, { display: 'flex', alignItems: 'center', gap: '20px' });
    head.innerHTML = `${icon(i ? 'speaker' : 'headphones', 56)}<div><div class="dn">${names[i]}</div><div class="ds">${i ? 'USB · 48 kHz' : 'Bluetooth · 48 kHz'}</div></div>`;
    const badge = h('div', 'status good abs', c, '●');
    pos(badge, w - 150, 34);
    const gb = pos(h('div', 'graphbox abs', c), 34, V ? 130 : 150, w - 68, hh - (V ? 160 : 184));
    const cv = h('canvas', 'abs', gb); cv.width = w - 68; cv.height = hh - (V ? 160 : 184);
    const chrt = new Chart(cv, { l: 10, r: 10, t: 12, b: 12, dbMin: -9, dbMax: 9, dbStep: 3 });
    return { c, badge, chrt };
  });
  const cA = curve([{ type: 'lowshelf', f: 105, g: 4.2, q: 0.7 }, { type: 'bell', f: 132, g: -3.5, q: 1.2 }, { type: 'bell', f: 1880, g: 3.4, q: 1.8 }, { type: 'bell', f: 5990, g: -4.8, q: 4 }, { type: 'highshelf', f: 10000, g: 2.5, q: 0.7 }]);
  const cB = curve([{ type: 'bell', f: 47, g: -6.5, q: 4.2 }, { type: 'bell', f: 112, g: 2.5, q: 5 }, { type: 'bell', f: 177, g: -5.4, q: 4.3 }, { type: 'bell', f: 300, g: 1.5, q: 3 }]);
  const sw = s + 0.95;
  return (t) => {
    cap(t, s + 0.05, e, {});
    const out = E('inCubic', t, e - 0.3, e);
    cards.forEach((c, i) => {
      reveal(c.c, t, s + 0.1 + i * 0.12, e, { dy: 40, scale: 0.95 });
      const active = i === 0 ? t < sw : t >= sw;
      const a = i === 0 ? 1 - pr(t, sw, sw + 0.2) : pr(t, sw, sw + 0.2);
      c.c.style.borderColor = `rgba(79,220,184,${0.1 + 0.7 * a})`;
      c.c.style.boxShadow = `0 40px 120px rgba(0,0,0,0.6), 0 0 ${50 * a}px rgba(79,220,184,${0.25 * a})`;
      vis(c.badge, a);
      c.badge.innerHTML = '● ' + (LANG === 'zh' ? '使用中' : 'In use');
      c.chrt.clear(); c.chrt.grid(1, false);
      c.chrt.line(i ? cB : cA, `rgba(255,255,255,${0.35 + 0.65 * a})`, 3.5, { glow: `rgba(255,255,255,${0.4 * a})`, upto: E('outCubic', t, s + 0.2 + i * 0.12, s + 0.9 + i * 0.12) });
    });
  };
};

// ============================================================ 7b. effects
SCENES.effects = (root, s, e) => {
  const cap = topCaption(root, T('fxTitle'), T('fxSub'));
  // crop of the screenshot (source pixels): the effect cards, without the window edge and toolbar
  const [sx, sy, sw, sh] = V ? [40, 190, 920, 990] : [40, 190, 1860, 990];
  const dw = V ? 880 : 1300, k = dw / sw, dh = Math.round(sh * k);
  const shot = pos(h('div', 'shot', root), (W - dw) / 2, V ? 590 : 300, dw, dh);
  const img = h('img', '', shot); img.src = `${IMG}${LANG}/effects.png`;
  S(img, { position: 'absolute', width: 1920 * k + 'px', left: -sx * k + 'px', top: -sy * k + 'px' });
  img.style.transformOrigin = `${(sx + sw / 2) * k}px ${(sy + sh / 2) * k}px`;
  return (t) => {
    cap(t, s + 0.05, e);
    reveal(shot, t, s + 0.1, e, { dy: 50, scale: 0.95 });
    img.style.transform = `scale(${lerp(1.0, 1.05, pr(t, s, e))})`;
  };
};

// ============================================================ 7c. menu bar
SCENES.menubar = (root, s, e) => {
  const cap = topCaption(root, T('mbTitle'), T('mbSub'));
  const [mx, my, mw] = V ? [60, 560, 960] : [300, 330, 1320];
  const bar = pos(h('div', 'menubar', root), mx, my, mw);
  const tapIcon = h('span', '', bar, IC.sliders);
  S(tapIcon, { width: '30px', height: '30px', display: 'inline-flex', color: '#fff', padding: '2px', borderRadius: '6px' });
  h('span', '', bar, IC.wifi).style.cssText = 'width:28px;height:28px;display:inline-flex';
  h('span', '', bar, IC.battery).style.cssText = 'width:36px;height:28px;display:inline-flex';
  h('span', '', bar, LANG === 'zh' ? '10月10日 周六 21:41' : 'Sat Oct 10  9:41 PM');
  const dw = V ? 560 : 500, dh = Math.round(dw * 730 / 680);
  const tapX = V ? mx + mw - 360 : mx + mw - 330;
  const drop = pos(h('div', 'shot', root), tapX - dw + 60, my + 62, dw, dh);
  S(drop, { borderRadius: '24px' });
  h('img', '', drop).src = `${IMG}${LANG}/menubar.png`;
  const keys = h('div', 'abs', root);
  if (V) { pos(keys, 0, my + 62 + dh + 50, W); S(keys, { display: 'flex', justifyContent: 'center', gap: '26px' }); }
  else { pos(keys, mx + 60, my + 160, 560); S(keys, { display: 'flex', flexDirection: 'column', gap: '34px' }); }
  const keyRows = T('keys').map(([k, l]) => {
    const d = h('div', '', keys);
    S(d, { display: 'flex', alignItems: 'center', gap: '20px', flexDirection: V ? 'column' : 'row' });
    d.innerHTML = `<div class="keycaps">${[...k].map((c) => `<div class="key" style="${V ? 'min-width:56px;height:56px;font-size:28px' : ''}">${c}</div>`).join('')}</div><span style="font-size:${V ? 28 : 28}px;color:var(--text2)">${l}</span>`;
    return d;
  });
  return (t) => {
    cap(t, s + 0.05, e);
    reveal(bar, t, s + 0.05, e, { dy: -20 });
    const hi = pr(t, s + 0.3, s + 0.4);
    tapIcon.style.background = `rgba(255,255,255,${0.25 * hi})`;
    const dp = ease.spring(pr(t, s + 0.4, s + 1.2));
    vis(drop, Math.min(1, pr(t, s + 0.4, s + 0.55)) * (1 - E('inCubic', t, e - 0.3, e)));
    drop.style.transformOrigin = '85% 0';
    drop.style.transform = `translateY(${(1 - dp) * -30}px) scale(${lerp(0.85, 1, dp)})`;
    keyRows.forEach((k, i) => reveal(k, t, s + 0.6 + i * 0.15, e, { dx: V ? 0 : 30, dy: V ? 20 : 0 }));
  };
};

// ============================================================ 8. tech
SCENES.tech = (root, s, e) => {
  const cap = caption(root, { x: V ? 60 : 160, y: V ? 250 : 120, w: V ? 960 : 1600, title: T('techTitle'), align: 'center' });
  const nodes = T('techNodes'), subs = T('techNodeSub');
  const nw = V ? 640 : 330, nh = V ? 150 : 200;
  const centers = V ? [[540, 760], [540, 960], [540, 1160], [540, 1360]] : [[300, 520], [740, 520], [1180, 520], [1620, 520]];
  const lineCv = h('canvas', 'layer', root); lineCv.width = W; lineCv.height = H;
  const lc = lineCv.getContext('2d');
  const nodeEls = centers.map(([x, y], i) => {
    const n = pos(h('div', 'node', root), x - nw / 2, y - nh / 2, nw, nh);
    if (V) S(n, { flexDirection: 'row', gap: '22px' });
    let art = '';
    if (i === 0) art = '<div class="apps" style="position:relative;width:150px;height:56px"></div>';
    if (i === 1) art = `<div style="width:56px;height:56px;border-radius:16px;background:rgba(90,174,240,0.18);color:var(--blue);display:flex;align-items:center;justify-content:center">${icon('sliders', 32)}</div>`;
    if (i === 2) art = `<img src="${IMG}icon.png" style="width:72px;height:72px">`;
    if (i === 3) art = `<div style="display:flex;gap:10px;color:var(--text)">${icon('headphones', 48)}${icon('speaker', 48)}</div>`;
    n.innerHTML = `${art}<div><div class="nt">${nodes[i]}</div>${subs[i] ? `<div class="ns">${subs[i]}</div>` : ''}</div>`;
    if (i === 0) {
      const a = n.querySelector('.apps');
      ['music', 'safari', 'podcasts'].forEach((k, j) => appIcon(a, k, j * 48, 0, 54));
    }
    if (i === 2) { n.style.borderColor = 'rgba(79,220,184,0.45)'; n.style.boxShadow = '0 30px 80px rgba(0,0,0,0.5), 0 0 60px rgba(79,220,184,0.18)'; }
    return n;
  });
  const checks = h('div', 'abs', root);
  if (V) { pos(checks, 100, 470, 880); S(checks, { display: 'flex', flexDirection: 'column', gap: '22px', alignItems: 'flex-start' }); }
  else { pos(checks, 0, 780, W); S(checks, { display: 'flex', gap: '56px', justifyContent: 'center' }); }
  const chEls = T('techChecks').map((c) => h('div', 'check', checks, `<div class="ic ok">✓</div><span>${c}</span>`));
  return (t) => {
    cap(t, s + 0.05, e);
    nodeEls.forEach((n, i) => reveal(n, t, s + 0.3 + i * 0.35, e, { dy: 30, scale: 0.9 }));
    chEls.forEach((c, i) => reveal(c, t, s + 2.0 + i * 0.25, e, { dy: 20 }));
    lc.clearRect(0, 0, W, H);
    const out = E('inCubic', t, e - 0.3, e);
    const kp = kickPulse(t);
    for (let i = 0; i < 3; i++) {
      const [x0, y0] = centers[i], [x1, y1] = centers[i + 1];
      const a = V ? [x0, y0 + nh / 2 + 6] : [x0 + nw / 2 + 8, y0];
      const b = V ? [x1, y1 - nh / 2 - 6] : [x1 - nw / 2 - 8, y1];
      const p = E('outCubic', t, s + 0.5 + i * 0.35, s + 0.9 + i * 0.35) * (1 - out);
      if (p <= 0) continue;
      lc.strokeStyle = 'rgba(255,255,255,0.18)'; lc.lineWidth = 3;
      lc.beginPath(); lc.moveTo(a[0], a[1]); lc.lineTo(lerp(a[0], b[0], p), lerp(a[1], b[1], p)); lc.stroke();
      // flowing audio
      for (let k = 0; k < 4; k++) {
        const u = ((t * 0.9 + k / 4 + i * 0.13) % 1);
        if (u > p) continue;
        const x = lerp(a[0], b[0], u), y = lerp(a[1], b[1], u);
        lc.beginPath(); lc.arc(x, y, 5 + kp * 3, 0, Math.PI * 2);
        lc.fillStyle = i === 2 ? 'rgba(79,220,184,0.95)' : 'rgba(120,186,240,0.95)';
        lc.shadowColor = lc.fillStyle; lc.shadowBlur = 14; lc.fill(); lc.shadowBlur = 0;
      }
    }
  };
};

// ============================================================ 9. call to action
SCENES.cta = (root, s, e) => {
  const ic = h('img', 'abs', root); ic.src = IMG + 'icon.png';
  const word = h('div', 'abs center', root, 'Tapline');
  S(word, { fontFamily: '-apple-system, "SF Pro Display", sans-serif', fontWeight: 700, fontSize: (V ? 150 : 140) + 'px', letterSpacing: '-4px', lineHeight: 1, width: W + 'px' });
  const line = h('div', 'abs center', root, T('ctaLine'));
  S(line, { fontSize: (V ? 42 : 36) + 'px', color: 'var(--text)', width: W + 'px', fontWeight: 500 });
  const urlWrap = h('div', 'abs', root);
  S(urlWrap, { width: W + 'px', display: 'flex', justifyContent: 'center' });
  const url = h('div', 'url', urlWrap, `${icon('arrow', V ? 40 : 34, 'var(--mint)')}<span>${T('ctaUrl')}</span>`);
  const req = h('div', 'abs center', root, T('ctaReq'));
  S(req, { fontSize: (V ? 30 : 26) + 'px', color: 'var(--text3)', width: W + 'px' });
  const icS = V ? 300 : 210;
  if (V) { pos(ic, (W - icS) / 2, 430, icS, icS); pos(word, 0, 790); pos(line, 0, 990); pos(urlWrap, 0, 1090); pos(req, 0, 1240); }
  else { pos(ic, (W - icS) / 2, 120, icS, icS); pos(word, 0, 370); pos(line, 0, 545); pos(urlWrap, 0, 630); pos(req, 0, 760); }
  const black = h('div', 'layer', root); black.style.background = '#000';
  return (t) => {
    const sp = ease.spring(pr(t, s, s + 1.1));
    vis(ic, pr(t, s, s + 0.12));
    ic.style.transform = `scale(${lerp(0.4, 1, sp) * (1 + 0.025 * kickPulse(t))})`;
    reveal(word, t, s + 0.25, null, { dy: 30 });
    reveal(line, t, s + 0.55, null);
    reveal(urlWrap, t, s + 0.8, null, { scale: 0.92 });
    reveal(req, t, s + 1.1, null);
    url.style.boxShadow = `0 0 ${30 + 20 * kickPulse(t)}px rgba(79,220,184,${0.12 + 0.1 * kickPulse(t)})`;
    vis(black, E('inOutCubic', t, e - 0.7, e));
  };
};
