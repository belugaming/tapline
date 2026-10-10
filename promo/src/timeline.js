// Shared by the page (scenes) and music.mjs (soundtrack), so cuts land on the beat
// and the audible before/after moments match what is on screen.
var TL = {
  fps: 60,
  duration: 66,
  bpm: 120, // one bar = 2 s
  scenes: {
    hook: [0, 4],
    logo: [4, 8],
    eq: [8, 20],
    hp: [20, 30],
    room: [30, 40],
    mixer: [40, 46],
    devices: [46, 48],
    effects: [48, 50],
    menubar: [50, 52],
    tech: [52, 58],
    cta: [58, 66],
  },
  ev: {
    drop: 4.0,
    eqBypassOn: 16.2,
    eqBypassOff: 17.2,
    abToB: 18.0,
    hpRow: 22.0,
    hpApply: 24.6,
    roomSweepL: 30.5,
    roomSweepR: 31.3,
    roomFix: 35.8,
    roomFixEnd: 37.0,
    mixSafari: 40.8,
    mixMute: 41.8,
    mixMusic: 42.5,
    mixBypass: 43.8,
  },
};
if (typeof module !== 'undefined') module.exports = TL;
