// Browser presentation for the RTS: canvas renderer, gestures and HTML HUD.
// All game logic lives in the .NET simulation (GameApi exports); this file only draws and
// translates input into map coordinates.

const S = 64; // fixed-point scale used by GameApi.Frame

const canvas = document.getElementById("game");
const ctx = canvas.getContext("2d");
const mini = document.getElementById("minimap");
const mctx = mini.getContext("2d");
const $ = (id) => document.getElementById(id);

let exports = null;
let running = false;
let mapW = 0, mapH = 0;
let ground = null;                 // offscreen canvas with the terrain
const cam = { x: 32, y: 32, zoom: 26, minZoom: 9, maxZoom: 60 };
let dpr = 1;
let lastTime = 0;
let effects = [];                   // {kind, x, y, t}
let hudActionsKey = "";
let lastFrame = null;
let radialShown = false;

const PLAYER = ["#3b82f6", "#dc2626", "#f59e0b", "#22c55e"];
const TERRAIN = ["#6aa050", "#8c6e46", "#d7c38c", "#3c78be", "#6e6964"];
const TERRAIN_ALT = ["#70a655", "#92744c", "#dcc892", "#3f7cc2", "#736e69"];

// ------------------------------------------------------------------ boot
async function boot() {
  resize();
  window.addEventListener("resize", resize);
  await Blazor.start();
  const runtime = await globalThis.getDotnetRuntime(0);
  exports = (await runtime.getAssemblyExports("RTS.Web.dll")).RTS.Web.GameApi;
  buildMenu();
}

function buildMenu() {
  const civs = JSON.parse(exports.Civs());
  const box = $("civs");
  let selected = civs[0]?.id;
  civs.forEach((c, i) => {
    const el = document.createElement("div");
    el.className = "civ" + (i === 0 ? " selected" : "");
    el.innerHTML = `<b><i style="background:${c.color}"></i>${c.name}</b><span>${c.tagline}</span>`;
    el.onclick = () => { selected = c.id; [...box.children].forEach(x => x.classList.remove("selected")); el.classList.add("selected"); };
    box.appendChild(el);
  });
  const play = $("playBtn");
  play.disabled = false;
  play.textContent = "Play";
  play.onclick = () => {
    const diff = parseInt(document.querySelector('input[name="diff"]:checked').value, 10);
    const others = civs.filter(c => c.id !== selected);
    const enemy = others.length ? others[Math.floor(Math.random() * others.length)].id : selected;
    startMatch(selected, enemy, diff);
  };
  $("againBtn").onclick = () => { $("end").classList.add("hidden"); $("hud").classList.add("hidden"); $("menu").classList.remove("hidden"); running = false; };
}

function startMatch(civ, enemy, diff) {
  exports.StartMatch(civ, enemy, diff, 0);
  mapW = exports.MapWidth(); mapH = exports.MapHeight();
  buildGround(exports.Terrain());
  const home = exports.Home();
  cam.x = home[0]; cam.y = home[1] - 3; cam.zoom = Math.max(cam.minZoom, Math.min(cam.maxZoom, Math.min(canvas.width, canvas.height) / dpr / 26));
  $("menu").classList.add("hidden");
  $("hud").classList.remove("hidden");
  $("end").classList.add("hidden");
  effects = [];
  hudActionsKey = "";
  running = true;
  lastTime = performance.now();
  requestAnimationFrame(frame);
  setInterval(pollHud, 150);
}

function buildGround(terrain) {
  ground = document.createElement("canvas");
  ground.width = mapW; ground.height = mapH;
  const g = ground.getContext("2d");
  const img = g.createImageData(mapW, mapH);
  for (let y = 0; y < mapH; y++) for (let x = 0; x < mapW; x++) {
    const t = terrain[y * mapW + x];
    const hex = ((x + y) & 1) ? TERRAIN[t] : TERRAIN_ALT[t];
    const i = ((mapH - 1 - y) * mapW + x) * 4;   // flip: world y up, image y down
    img.data[i] = parseInt(hex.slice(1, 3), 16); img.data[i + 1] = parseInt(hex.slice(3, 5), 16); img.data[i + 2] = parseInt(hex.slice(5, 7), 16); img.data[i + 3] = 255;
  }
  g.putImageData(img, 0, 0);
}

function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = Math.floor(innerWidth * dpr);
  canvas.height = Math.floor(innerHeight * dpr);
  canvas.style.width = innerWidth + "px";
  canvas.style.height = innerHeight + "px";
}

// ------------------------------------------------------------------ camera
const W = () => innerWidth, H = () => innerHeight;
function toScreen(wx, wy) { return [(wx - cam.x) * cam.zoom + W() / 2, (cam.y - wy) * cam.zoom + H() / 2]; }
function toWorld(sx, sy) { return [cam.x + (sx - W() / 2) / cam.zoom, cam.y - (sy - H() / 2) / cam.zoom]; }
function clampCam() {
  const halfW = W() / 2 / cam.zoom, halfH = H() / 2 / cam.zoom, m = 3;
  if (2 * halfW >= mapW + 2 * m) cam.x = mapW / 2; else cam.x = Math.max(halfW - m, Math.min(mapW + m - halfW, cam.x));
  if (2 * halfH >= mapH + 2 * m) cam.y = mapH / 2; else cam.y = Math.max(halfH - m, Math.min(mapH + m - halfH, cam.y));
}
function zoomAt(factor, sx, sy) {
  const [wx, wy] = toWorld(sx, sy);
  cam.zoom = Math.max(cam.minZoom, Math.min(cam.maxZoom, cam.zoom * factor));
  const [nx, ny] = toWorld(sx, sy);
  cam.x += wx - nx; cam.y += wy - ny;
  clampCam();
}

// ------------------------------------------------------------------ render
function frame(now) {
  if (!running) return;
  const dt = Math.min(0.1, (now - lastTime) / 1000);
  lastTime = now;
  const buf = exports.Frame(dt);
  lastFrame = buf;
  draw(buf, dt);
  requestAnimationFrame(frame);
}

function draw(buf, dt) {
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = "#0e1118";
  ctx.fillRect(0, 0, W(), H());
  if (!buf || buf.length < 12) return;

  // Ground
  const [gx, gy] = toScreen(0, mapH);
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(ground, gx, gy, mapW * cam.zoom, mapH * cam.zoom);
  ctx.imageSmoothingEnabled = true;
  if (cam.zoom >= 22) {
    ctx.strokeStyle = "rgba(0,0,0,0.06)"; ctx.lineWidth = 1;
    const x0 = Math.max(0, Math.floor(toWorld(0, 0)[0])), x1 = Math.min(mapW, Math.ceil(toWorld(W(), 0)[0]));
    const y1 = Math.min(mapH, Math.ceil(toWorld(0, 0)[1])), y0 = Math.max(0, Math.floor(toWorld(0, H())[1]));
    ctx.beginPath();
    for (let x = x0; x <= x1; x++) { const [sx] = toScreen(x, 0); ctx.moveTo(sx, gy); ctx.lineTo(sx, gy + mapH * cam.zoom); }
    for (let y = y0; y <= y1; y++) { const [, sy] = toScreen(0, y); ctx.moveTo(gx, sy); ctx.lineTo(gx + mapW * cam.zoom, sy); }
    ctx.stroke();
  }

  const count = buf[0];
  const ents = [];
  for (let i = 0; i < count; i++) {
    const o = 12 + i * 12;
    ents.push({ kind: buf[o], id: buf[o + 1], x: buf[o + 2] / S, y: buf[o + 3] / S, player: buf[o + 4], def: buf[o + 5],
      a: buf[o + 6] / S, b: buf[o + 7] / S, hp: buf[o + 8], flags: buf[o + 9], facing: buf[o + 10], state: buf[o + 11] });
  }
  // Painter's order: things further "north" (higher y) first so southern units overlap them.
  ents.sort((p, q) => (q.kind === 1 || q.kind === 4 ? q.y : q.y + q.b) - (p.kind === 1 || p.kind === 4 ? p.y : p.y + p.b));

  // Ghost
  if (buf[3]) {
    const [sx, sy] = toScreen(buf[4] / S, buf[5] / S + buf[7] / S);
    ctx.fillStyle = buf[8] ? "rgba(60,220,90,0.45)" : "rgba(230,70,60,0.45)";
    ctx.fillRect(sx, sy, buf[6] / S * cam.zoom, buf[7] / S * cam.zoom);
    ctx.strokeStyle = buf[8] ? "#6ee38a" : "#ff6a5a"; ctx.lineWidth = 2;
    ctx.strokeRect(sx, sy, buf[6] / S * cam.zoom, buf[7] / S * cam.zoom);
  }

  for (const e of ents) {
    if (e.kind === 2) drawBuilding(e);
    else if (e.kind === 3) drawNode(e);
  }
  for (const e of ents) {
    if (e.kind === 1) drawUnit(e);
    else if (e.kind === 4) drawProjectile(e);
  }

  // Effects
  const n = buf[9];
  let o = 12 + count * 12;
  for (let i = 0; i < n; i++, o += 3) effects.push({ kind: buf[o], x: buf[o + 1] / S, y: buf[o + 2] / S, t: 0 });
  effects = effects.filter(f => (f.t += dt) < 0.6);
  for (const f of effects) {
    const [sx, sy] = toScreen(f.x, f.y);
    const k = f.t / 0.6;
    ctx.beginPath();
    ctx.arc(sx, sy, (f.kind === 1 ? 0.9 : 0.35) * cam.zoom * (0.3 + k), 0, Math.PI * 2);
    ctx.strokeStyle = f.kind === 1 ? `rgba(255,255,255,${1 - k})` : `rgba(255,200,80,${1 - k})`;
    ctx.lineWidth = 2; ctx.stroke();
  }

  // Attack ping
  if (buf[10] >= 0) {
    const [sx, sy] = toScreen(buf[10] / S, buf[11] / S);
    const k = (performance.now() % 1000) / 1000;
    ctx.beginPath(); ctx.arc(sx, sy, (0.8 + k * 1.6) * cam.zoom, 0, Math.PI * 2);
    ctx.strokeStyle = `rgba(255,70,50,${1 - k})`; ctx.lineWidth = 3; ctx.stroke();
  }

  drawMinimap(ents);
}

function drawBuilding(e) {
  const [sx, sy] = toScreen(e.x, e.y + e.b);
  const w = e.a * cam.zoom, h = e.b * cam.zoom;
  const color = PLAYER[e.player % PLAYER.length];
  const site = (e.flags & 2) !== 0;
  const lift = Math.min(h * 0.35, 0.9 * cam.zoom) * (site ? Math.max(0.15, e.state / 100) : 1);
  // wall (darker) below, roof lifted up for a boxy 3D feel
  ctx.fillStyle = shade(color, 0.5);
  ctx.fillRect(sx + 1, sy - lift + h - 2, w - 2, lift + 1);
  ctx.fillStyle = site ? shade(color, 0.8) : shade(color, 0.95);
  ctx.fillRect(sx + 1, sy - lift + 1, w - 2, h - 2);
  ctx.strokeStyle = "rgba(0,0,0,0.45)"; ctx.lineWidth = 1.5;
  ctx.strokeRect(sx + 1, sy - lift + 1, w - 2, h - 2);
  if (e.flags & 32) { // turret: small dark square in the middle
    ctx.fillStyle = "rgba(20,20,25,0.7)";
    ctx.fillRect(sx + w / 2 - w * 0.12, sy - lift + h / 2 - h * 0.12, w * 0.24, h * 0.24);
  }
  if (site) {
    ctx.fillStyle = "rgba(255,255,255,0.85)"; ctx.font = `${Math.max(10, cam.zoom * 0.45)}px system-ui`; ctx.textAlign = "center";
    ctx.fillText(e.state + "%", sx + w / 2, sy - lift + h / 2 + 4);
  }
  if (e.flags & 16) { // production in progress marker
    ctx.fillStyle = "#ffd766"; ctx.beginPath(); ctx.arc(sx + w - 6, sy - lift + 6, 3.5, 0, Math.PI * 2); ctx.fill();
  }
  if (e.flags & 1) { ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.strokeRect(sx - 2, sy - lift - 2, w + 4, h + 4); }
  if (e.hp >= 0 && (e.hp < 100 || (e.flags & 1))) bar(sx + w * 0.1, sy - lift - 8, w * 0.8, e.hp);
}

function drawNode(e) {
  const [sx, sy] = toScreen(e.x + e.a / 2, e.y + e.b / 2);
  const r = Math.max(2, e.a * 0.42 * cam.zoom);
  ctx.beginPath();
  if (e.state === 0) { // tree
    ctx.fillStyle = "#3b2a1a"; ctx.fillRect(sx - r * 0.15, sy, r * 0.3, r * 0.7);
    ctx.fillStyle = "#2f7a33"; ctx.arc(sx, sy - r * 0.2, r, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = "#3f9a42"; ctx.beginPath(); ctx.arc(sx - r * 0.3, sy - r * 0.45, r * 0.55, 0, Math.PI * 2); ctx.fill();
  } else if (e.state === 1) { // berries
    ctx.fillStyle = "#4c7a2a"; ctx.arc(sx, sy, r, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = "#c2306b";
    for (let k = 0; k < 5; k++) { ctx.beginPath(); ctx.arc(sx + Math.cos(k * 1.3) * r * 0.5, sy + Math.sin(k * 1.3) * r * 0.5, r * 0.18, 0, Math.PI * 2); ctx.fill(); }
  } else if (e.state === 3) { // mine
    ctx.fillStyle = "#7b6a4e"; ctx.fillRect(sx - r, sy - r * 0.8, r * 2, r * 1.6);
    ctx.fillStyle = "#e5c04a"; ctx.fillRect(sx - r * 0.5, sy - r * 0.35, r * 0.4, r * 0.4); ctx.fillRect(sx + r * 0.1, sy, r * 0.35, r * 0.35);
  } else { // hunt
    ctx.fillStyle = "#8a5a32"; ctx.ellipse(sx, sy, r, r * 0.65, 0, 0, Math.PI * 2); ctx.fill();
  }
}

function drawUnit(e) {
  const [sx, sy] = toScreen(e.x, e.y);
  const r = Math.max(3, e.a * cam.zoom);
  const color = PLAYER[e.player % PLAYER.length];
  const villager = (e.flags & 4) !== 0;
  // shadow
  ctx.fillStyle = "rgba(0,0,0,0.25)"; ctx.beginPath(); ctx.ellipse(sx, sy + r * 0.5, r * 1.1, r * 0.5, 0, 0, Math.PI * 2); ctx.fill();
  // body
  ctx.fillStyle = villager ? mix(color, "#ffffff", 0.35) : color;
  ctx.beginPath();
  if (e.flags & 128) { ctx.rect(sx - r, sy - r * 1.6, r * 2, r * 1.6); }            // artillery: box
  else if (e.flags & 64) { ctx.ellipse(sx, sy - r * 0.6, r * 1.3, r * 0.9, 0, 0, Math.PI * 2); } // cavalry: wide
  else { ctx.arc(sx, sy - r * 0.7, r, 0, Math.PI * 2); }
  ctx.fill();
  ctx.strokeStyle = "rgba(0,0,0,0.5)"; ctx.lineWidth = 1.2; ctx.stroke();
  // facing tick / weapon
  const a = e.facing * Math.PI / 180;
  ctx.strokeStyle = (e.flags & 8) ? "#111" : "#3b2a1a"; ctx.lineWidth = Math.max(1.5, r * 0.25);
  ctx.beginPath(); ctx.moveTo(sx, sy - r * 0.7); ctx.lineTo(sx + Math.cos(a) * r * 1.5, sy - r * 0.7 - Math.sin(a) * r * 1.5); ctx.stroke();
  if (e.flags & 512) { ctx.fillStyle = "#d9b27a"; ctx.fillRect(sx - r * 0.35, sy - r * 1.9, r * 0.7, r * 0.5); }  // cargo
  if (e.flags & 1) { ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.beginPath(); ctx.ellipse(sx, sy + r * 0.4, r * 1.4, r * 0.7, 0, 0, Math.PI * 2); ctx.stroke(); }
  if (e.hp >= 0 && (e.hp < 100 || (e.flags & 1))) bar(sx - r, sy - r * 2.3, r * 2, e.hp);
}

function drawProjectile(e) {
  const [sx, sy] = toScreen(e.x, e.y);
  ctx.fillStyle = "#1a1612"; ctx.beginPath(); ctx.arc(sx, sy - cam.zoom * 0.6, Math.max(1.5, cam.zoom * 0.08), 0, Math.PI * 2); ctx.fill();
}

function bar(x, y, w, hp) {
  ctx.fillStyle = "rgba(0,0,0,0.7)"; ctx.fillRect(x, y, w, 4);
  ctx.fillStyle = hp > 50 ? "#4ad66a" : hp > 25 ? "#f2c14e" : "#e5453a"; ctx.fillRect(x, y, w * hp / 100, 4);
}

function shade(hex, k) { const [r, g, b] = rgb(hex); return `rgb(${r * k | 0},${g * k | 0},${b * k | 0})`; }
function mix(a, b, t) { const A = rgb(a), B = rgb(b); return `rgb(${A[0] + (B[0] - A[0]) * t | 0},${A[1] + (B[1] - A[1]) * t | 0},${A[2] + (B[2] - A[2]) * t | 0})`; }
function rgb(hex) { return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)]; }

let miniNext = 0;
function drawMinimap(ents) {
  const now = performance.now();
  if (now < miniNext) return;
  miniNext = now + 250;
  const s = mini.width / mapW;
  mctx.imageSmoothingEnabled = false;
  mctx.drawImage(ground, 0, 0, mini.width, mini.height);
  for (const e of ents) {
    if (e.kind === 4) continue;
    if (e.kind === 1) { mctx.fillStyle = e.player === 0 ? "#fff" : PLAYER[e.player % PLAYER.length]; mctx.fillRect(e.x * s - 1, (mapH - e.y) * s - 1, 2, 2); }
    else if (e.kind === 2) { mctx.fillStyle = PLAYER[e.player % PLAYER.length]; mctx.fillRect(e.x * s, (mapH - e.y - e.b) * s, Math.max(2, e.a * s), Math.max(2, e.b * s)); }
    else { mctx.fillStyle = e.state === 0 ? "#1e5a28" : e.state === 3 ? "#dcb432" : e.state === 1 ? "#96285a" : "#8c5a32"; mctx.fillRect(e.x * s, (mapH - e.y - e.b) * s, Math.max(1, e.a * s), Math.max(1, e.b * s)); }
  }
  const [x0, y1] = toWorld(0, 0), [x1, y0] = toWorld(W(), H());
  mctx.strokeStyle = "rgba(255,255,255,0.8)"; mctx.lineWidth = 1;
  mctx.strokeRect(x0 * s, (mapH - y1) * s, (x1 - x0) * s, (y1 - y0) * s);
}

// ------------------------------------------------------------------ input
const pointers = new Map();
let gesture = null;   // {id, sx, sy, lx, ly, t0, mode: 'touch'|'pan'|'long'|'box', timer}
let pinchDist = 0, pinchCenter = null;
const dp = () => Math.max(1, (window.devicePixelRatio || 1));
const TAP_MOVE = 12, LONG_MS = 350;

canvas.addEventListener("pointerdown", (ev) => {
  if (!running) return;
  canvas.setPointerCapture(ev.pointerId);
  pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
  hideRadial();
  if (pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    pinchDist = Math.hypot(a.x - b.x, a.y - b.y); pinchCenter = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    if (gesture) { clearTimeout(gesture.timer); if (gesture.mode === "box") endBox(gesture); gesture = null; $("box").classList.add("hidden"); }
    return;
  }
  gesture = { id: ev.pointerId, sx: ev.clientX, sy: ev.clientY, lx: ev.clientX, ly: ev.clientY, t0: performance.now(), mode: "touch" };
  gesture.timer = setTimeout(() => {
    if (gesture && gesture.mode === "touch") {
      gesture.mode = "long";
      const [wx, wy] = toWorld(gesture.sx, gesture.sy);
      if (exports.LongPress(wx, wy)) showRadial(gesture.sx, gesture.sy);
      if (navigator.vibrate) navigator.vibrate(15);
    }
  }, LONG_MS);
});

canvas.addEventListener("pointermove", (ev) => {
  if (!running) return;
  if (pointers.has(ev.pointerId)) pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
  const [wx, wy] = toWorld(ev.clientX, ev.clientY);
  exports.Pointer(wx, wy);
  if (pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    const d = Math.hypot(a.x - b.x, a.y - b.y), c = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    if (pinchDist > 0) zoomAt(d / pinchDist, c.x, c.y);
    const [pwx, pwy] = toWorld(pinchCenter.x, pinchCenter.y), [nwx, nwy] = toWorld(c.x, c.y);
    cam.x += pwx - nwx; cam.y += pwy - nwy; clampCam();
    pinchDist = d; pinchCenter = c;
    return;
  }
  if (!gesture || gesture.id !== ev.pointerId) return;
  const moved = Math.hypot(ev.clientX - gesture.sx, ev.clientY - gesture.sy);
  if (gesture.mode === "touch" && moved > TAP_MOVE) { gesture.mode = "pan"; clearTimeout(gesture.timer); }
  if (gesture.mode === "long" && moved > TAP_MOVE) { gesture.mode = "box"; hideRadial(); }
  if (gesture.mode === "pan") {
    const [ax, ay] = toWorld(gesture.lx, gesture.ly), [bx, by] = toWorld(ev.clientX, ev.clientY);
    cam.x += ax - bx; cam.y += ay - by; clampCam();
  } else if (gesture.mode === "box") {
    const b = $("box"); b.classList.remove("hidden");
    b.style.left = Math.min(gesture.sx, ev.clientX) + "px"; b.style.top = Math.min(gesture.sy, ev.clientY) + "px";
    b.style.width = Math.abs(ev.clientX - gesture.sx) + "px"; b.style.height = Math.abs(ev.clientY - gesture.sy) + "px";
  }
  gesture.lx = ev.clientX; gesture.ly = ev.clientY;
});

function endPointer(ev) {
  pointers.delete(ev.pointerId);
  if (pointers.size < 2) pinchDist = 0;
  if (!gesture || gesture.id !== ev.pointerId) return;
  clearTimeout(gesture.timer);
  if (gesture.mode === "touch") {
    const [wx, wy] = toWorld(ev.clientX, ev.clientY);
    exports.Tap(wx, wy, 24 / cam.zoom);
  } else if (gesture.mode === "box") endBox(gesture, ev);
  gesture = null;
  $("box").classList.add("hidden");
}
function endBox(g, ev) {
  const ex = ev ? ev.clientX : g.lx, ey = ev ? ev.clientY : g.ly;
  if (Math.abs(ex - g.sx) < 6 && Math.abs(ey - g.sy) < 6) return;
  const [ax, ay] = toWorld(g.sx, g.sy), [bx, by] = toWorld(ex, ey);
  exports.BoxSelect(ax, ay, bx, by);
}
canvas.addEventListener("pointerup", endPointer);
canvas.addEventListener("pointercancel", endPointer);
canvas.addEventListener("wheel", (ev) => { if (running) { zoomAt(ev.deltaY < 0 ? 1.15 : 1 / 1.15, ev.clientX, ev.clientY); ev.preventDefault(); } }, { passive: false });
window.addEventListener("keydown", (ev) => {
  if (!running) return;
  const step = 2;
  if (ev.key === "ArrowLeft" || ev.key === "a") cam.x -= step; if (ev.key === "ArrowRight" || ev.key === "d") cam.x += step;
  if (ev.key === "ArrowUp" || ev.key === "w") cam.y += step; if (ev.key === "ArrowDown" || ev.key === "s") cam.y -= step;
  if (ev.key === "Escape") { exports.Action("cancelmode", 0); hideRadial(); }
  if (ev.key === "h") { const home = exports.Home(); cam.x = home[0]; cam.y = home[1]; }
  clampCam();
});
mini.addEventListener("pointerdown", (ev) => { miniJump(ev); ev.stopPropagation(); });
mini.addEventListener("pointermove", (ev) => { if (ev.buttons) { miniJump(ev); ev.stopPropagation(); } });
function miniJump(ev) {
  const r = mini.getBoundingClientRect();
  cam.x = (ev.clientX - r.left) / r.width * mapW; cam.y = mapH - (ev.clientY - r.top) / r.height * mapH; clampCam();
}

// ------------------------------------------------------------------ radial
function showRadial(sx, sy) {
  const r = $("radial"); r.innerHTML = ""; r.style.left = sx + "px"; r.style.top = sy + "px";
  const add = (dx, dy, text, cls, action) => {
    const b = document.createElement("button"); b.className = "btn " + cls; b.textContent = text;
    b.style.left = dx + "px"; b.style.top = dy + "px";
    b.onpointerdown = (e) => e.stopPropagation();
    b.onclick = () => { if (action) exports.Action(action, 0); hideRadial(); };
    r.appendChild(b);
  };
  add(0, -66, "Move", "", "move");
  add(66, 0, "Attack", "attack", "attackmove");
  add(0, 66, "Stop", "neutral", "stop");
  add(-66, 0, "×", "neutral", null);
  r.classList.remove("hidden"); radialShown = true;
  setTimeout(() => { if (radialShown) hideRadial(); }, 4000);
}
function hideRadial() { $("radial").classList.add("hidden"); radialShown = false; }

// ------------------------------------------------------------------ HUD
let toastTimer = null;
function pollHud() {
  if (!running || !exports) return;
  const h = JSON.parse(exports.HudJson());
  $("food").textContent = "Food " + h.food; $("wood").textContent = "Wood " + h.wood; $("gold").textContent = "Gold " + h.gold;
  $("pop").textContent = h.pop + "/" + h.popCap;
  $("age").textContent = "Age " + ["I", "II", "III", "IV"][h.age] + " · " + h.ageName + (h.ageUp >= 0 ? " (" + h.ageUp + " s)" : "");
  const sb = $("shipBtn"); sb.textContent = h.shipAvail > 0 ? "Shipments (" + h.shipAvail + ")" : "Shipments " + h.xp + "/" + h.shipCost; sb.classList.toggle("ready", h.shipAvail > 0);
  const ib = $("idleBtn"); ib.textContent = h.idle > 0 ? "Idle " + h.idle : "Idle"; ib.style.opacity = h.idle > 0 ? 1 : 0.45;
  if (h.toasts.length) toast(h.toasts[h.toasts.length - 1]);
  $("label").textContent = h.label;
  const key = JSON.stringify([h.cards, h.actions, h.deck]);
  if (key !== hudActionsKey) {
    hudActionsKey = key;
    const cards = $("cards"); cards.innerHTML = "";
    for (const c of h.cards) { const b = document.createElement("button"); b.className = "btn"; b.textContent = c.name + " ×" + c.count; b.onclick = () => exports.Action("sub", c.def); cards.appendChild(b); }
    const acts = $("actions"); acts.innerHTML = "";
    for (const a of h.actions) {
      const b = document.createElement("button"); b.className = "btn " + a.kind; b.disabled = !a.enabled;
      const [name, cost] = a.label.split("|");
      b.innerHTML = name + (cost ? `<span class="cost">${cost}</span>` : "");
      const [act, arg] = a.id.split(":");
      b.onclick = () => { exports.Action(act, arg ? parseInt(arg, 10) : 0); pollHud(); };
      acts.appendChild(b);
    }
    if (!$("drawer").classList.contains("hidden")) fillDrawer(h);
  }
  const ap = $("attackPing");
  if (lastFrame && lastFrame[10] >= 0) { ap.classList.remove("hidden"); ap.onclick = () => { cam.x = lastFrame[10] / S; cam.y = lastFrame[11] / S; clampCam(); }; }
  else ap.classList.add("hidden");
  if (h.winner !== -1 && $("end").classList.contains("hidden")) {
    $("endTitle").textContent = h.winner === 0 ? "Victory" : h.winner === -2 ? "Draw" : "Defeat";
    $("endStats").textContent = `Units killed ${h.stats.killed} · lost ${h.stats.lost} · buildings razed ${h.stats.razed} · shipments ${h.stats.ships} · ${h.minutes} min`;
    $("end").classList.remove("hidden");
  }
}
function fillDrawer(h) {
  const d = $("drawer"); d.innerHTML = `<h3>Home City — ${h.xp} XP, next ${h.shipCost}</h3>`;
  for (const s of h.deck) {
    const b = document.createElement("button"); b.className = "btn " + (s.enabled ? "gold" : "neutral"); b.disabled = !s.enabled;
    b.innerHTML = s.name + (s.note ? `<span class="note">${s.note}</span>` : "");
    b.onclick = () => { exports.Action("ship", s.tech); d.classList.add("hidden"); };
    d.appendChild(b);
  }
  const c = document.createElement("button"); c.className = "btn neutral"; c.textContent = "Close"; c.onclick = () => d.classList.add("hidden"); d.appendChild(c);
}
$("shipBtn").onclick = () => { const d = $("drawer"); d.classList.toggle("hidden"); if (!d.classList.contains("hidden")) fillDrawer(JSON.parse(exports.HudJson())); };
$("idleBtn").onclick = () => { const r = JSON.parse(exports.Action("idle", 0)); if (r.x !== undefined) { cam.x = r.x; cam.y = r.y; clampCam(); } };
function toast(text) { const t = $("toast"); t.textContent = text; t.classList.remove("hidden"); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add("hidden"), 2200); }

boot().catch(err => { console.error(err); const p = $("playBtn"); p.textContent = "Failed to load: " + err; });
