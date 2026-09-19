// Tiny procedural sound effects on WebAudio (no audio files). Everything is short and quiet.
let ctx = null;
let muted = false;
let master = null;

export function unlock() {
  if (ctx) { if (ctx.state === "suspended") ctx.resume(); return; }
  try {
    ctx = new (window.AudioContext || window.webkitAudioContext)();
    master = ctx.createGain(); master.gain.value = 0.35; master.connect(ctx.destination);
  } catch (e) { ctx = null; }
}
export function setMuted(m) { muted = m; if (master) master.gain.value = m ? 0 : 0.35; }
export function isMuted() { return muted; }

function tone(freq, dur, type = "sine", gain = 0.4, slideTo = null, delay = 0) {
  if (!ctx || muted) return;
  const t0 = ctx.currentTime + delay;
  const o = ctx.createOscillator(), g = ctx.createGain();
  o.type = type; o.frequency.setValueAtTime(freq, t0);
  if (slideTo) o.frequency.exponentialRampToValueAtTime(slideTo, t0 + dur);
  g.gain.setValueAtTime(0.0001, t0); g.gain.exponentialRampToValueAtTime(gain, t0 + 0.01); g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
  o.connect(g); g.connect(master); o.start(t0); o.stop(t0 + dur + 0.02);
}
function noise(dur, gain = 0.3, lowpass = 1200, delay = 0) {
  if (!ctx || muted) return;
  const t0 = ctx.currentTime + delay;
  const n = Math.floor(ctx.sampleRate * dur), buf = ctx.createBuffer(1, n, ctx.sampleRate), d = buf.getChannelData(0);
  for (let i = 0; i < n; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / n);
  const src = ctx.createBufferSource(); src.buffer = buf;
  const f = ctx.createBiquadFilter(); f.type = "lowpass"; f.frequency.value = lowpass;
  const g = ctx.createGain(); g.gain.value = gain;
  src.connect(f); f.connect(g); g.connect(master); src.start(t0);
}

export const sfx = {
  click: () => tone(900, 0.04, "square", 0.12),
  select: () => { tone(660, 0.05, "triangle", 0.2); tone(880, 0.06, "triangle", 0.15, null, 0.05); },
  move: () => tone(520, 0.08, "triangle", 0.2, 640),
  attack: () => { tone(220, 0.12, "sawtooth", 0.25, 160); noise(0.08, 0.15, 800); },
  gather: () => tone(700, 0.06, "triangle", 0.15, 900),
  build: () => { noise(0.05, 0.2, 500); tone(320, 0.08, "square", 0.12); },
  hit: () => noise(0.05, 0.18, 2500),
  shot: () => { noise(0.09, 0.28, 1800); tone(120, 0.08, "square", 0.12, 60); },
  death: () => { tone(180, 0.25, "sawtooth", 0.2, 60); noise(0.2, 0.15, 400); },
  complete: () => { tone(523, 0.12, "triangle", 0.25); tone(659, 0.12, "triangle", 0.25, null, 0.1); tone(784, 0.2, "triangle", 0.25, null, 0.2); },
  age: () => { [523, 659, 784, 1046].forEach((f, i) => tone(f, 0.25, "triangle", 0.25, null, i * 0.12)); },
  alarm: () => { tone(880, 0.12, "square", 0.15, 660); tone(880, 0.12, "square", 0.15, 660, 0.18); },
  error: () => tone(200, 0.15, "square", 0.12, 150),
  ship: () => { tone(440, 0.1, "triangle", 0.2); tone(554, 0.1, "triangle", 0.2, null, 0.1); tone(659, 0.18, "triangle", 0.2, null, 0.2); },
  victory: () => { [523, 659, 784, 1046, 1318].forEach((f, i) => tone(f, 0.35, "triangle", 0.3, null, i * 0.15)); },
  defeat: () => { [440, 415, 392, 349].forEach((f, i) => tone(f, 0.4, "sawtooth", 0.2, null, i * 0.25)); },
};
