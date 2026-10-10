// Boot: build every scene once, then renderFrame(t) poses the whole stage at time t.
// ?t=12.5 shows one frame, ?play plays in real time with the soundtrack (for previews).
(async function boot() {
  document.body.classList.add('lang-' + LANG, V ? 'v' : 'h');
  const stage = document.getElementById('stage');
  S(stage, { width: W + 'px', height: H + 'px' });
  await loadAnalysis('/promo/out/analysis.json');
  const bg = h('div', 'layer', stage);
  const bgUpd = buildBackground(bg);
  const list = [];
  for (const [name, [s, e]] of Object.entries(TL.scenes)) {
    const root = h('div', 'scene', stage);
    root.dataset.name = name;
    list.push({ s, e, root, upd: SCENES[name](root, s, e) });
  }
  const pending = [...document.images].filter((i) => !i.complete).map((i) => new Promise((r) => { i.onload = i.onerror = r; }));
  pending.push(new Promise((r) => { const i = new Image(); i.onload = i.onerror = r; i.src = IMG + 'zh/mixer.png'; }));
  await Promise.all(pending);
  await document.fonts.ready;

  window.renderFrame = (t) => {
    bgUpd(t);
    for (const sc of list) {
      const on = t >= sc.s && t < sc.e + (sc.e >= TL.duration ? 1 : 0);
      sc.root.style.display = on ? '' : 'none';
      if (on) sc.upd(t);
    }
  };
  window.READY = true;

  if (P.has('cover')) {
    // Cover / poster: the EQ demo frame with a big title in place of the caption.
    const base = window.renderFrame, t = 12.45;
    const eq = list.find((x) => x.root.dataset.name === 'eq').root;
    const panel = eq.querySelector('.card');
    const box = h('div', 'abs', eq);
    const zh = LANG === 'zh';
    box.innerHTML = `<div style="display:flex;align-items:center;gap:22px"><img src="${IMG}icon.png" style="width:${V ? 120 : 104}px;height:${V ? 120 : 104}px;margin:-10px"><span style="font:700 ${V ? 64 : 54}px -apple-system,sans-serif;letter-spacing:-1.5px">Tapline</span></div>
      <div style="white-space:nowrap;font-size:${zh ? (V ? 132 : 90) : (V ? 104 : 74)}px;font-weight:700;line-height:1.12;letter-spacing:${zh ? -1 : -2.5}px;margin-top:${V ? 50 : 40}px">${T('coverTitle')}</div>
      <div style="font-size:${V ? 40 : 30}px;color:var(--text2);margin-top:${V ? 36 : 28}px">${T('coverSub')}</div>
      <div class="chip" style="margin-top:${V ? 40 : 30}px;color:var(--mint);border-color:rgba(79,220,184,0.45);background:rgba(79,220,184,0.12)">${T('coverChip')}</div>`;
    if (V) { pos(box, 80, 280, 920); S(box, { textAlign: 'center' }); box.firstChild.style.justifyContent = 'center'; }
    else pos(box, 110, zh ? 200 : 170, 600);
    window.renderFrame = () => {
      base(t);
      for (const c of eq.querySelectorAll('.cap')) c.style.display = 'none';
      for (const c of document.querySelectorAll('.cursor')) c.style.display = 'none';
      if (V) panel.style.transform = `translateY(${box.getBoundingClientRect().bottom + 50 - 600}px)`;
    };
    renderFrame(0);
  }
  if (P.has('t')) renderFrame(parseFloat(P.get('t')));
  if (P.has('play')) {
    const audio = new Audio('/promo/out/music.wav');
    const start = parseFloat(P.get('play')) || 0;
    audio.currentTime = start;
    document.addEventListener('click', () => audio.play(), { once: true });
    const t0 = performance.now();
    const loop = () => {
      const t = audio.paused ? start + (performance.now() - t0) / 1000 : audio.currentTime;
      renderFrame(t % TL.duration);
      requestAnimationFrame(loop);
    };
    loop();
  }
})();
