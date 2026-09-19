// Browser client for the RTS: boot, lobby, input gestures, HUD and the frame loop.
// All rules live in the .NET simulation (GameApi exports); render.js draws, sfx.js beeps.
import { Renderer, iconFor, PLAYER, setCivColors } from "./render.js?v=dev";
import { sfx, unlock, setMuted, isMuted } from "./sfx.js?v=dev";
const BUILD = "dev";

const S = 64;
const $ = (id) => document.getElementById(id);
const canvas = $("game");
const R = new Renderer(canvas);
window.__R = R;   // for automated tests

let api = null;
let running = false, paused = false, speed = 1;
let defs = null;
let lastTime = 0;
let hudKey = "";
let lastFrame = null;
let lastHud = null;
let hudTimer = null;
let objectives = null;
let matchStart = 0;

// ------------------------------------------------------------------ boot
async function boot() {
  R.resize();
  window.addEventListener("resize", () => R.resize());
  await Blazor.start();
  const runtime = await globalThis.getDotnetRuntime(0);
  api = (await runtime.getAssemblyExports("RTS.Web.dll")).RTS.Web.GameApi;
  defs = JSON.parse(api.Defs());
  $("version").textContent = "build " + BUILD;
  buildMenu();
}

function buildMenu() {
  const civs = JSON.parse(api.Civs());
  const box = $("civs");
  let selected = civs[0]?.id;
  civs.forEach((c, i) => {
    const el = document.createElement("div");
    el.className = "civ" + (i === 0 ? " selected" : "");
    el.innerHTML = `<b><i style="background:${c.color}"></i>${c.name}</b><span>${c.tagline}</span><em>Unique: ${c.unique}</em>`;
    el.onclick = () => { selected = c.id; [...box.children].forEach(x => x.classList.remove("selected")); el.classList.add("selected"); sfx.click(); };
    box.appendChild(el);
  });
  const enemySel = $("enemyCiv");
  civs.forEach(c => { const o = document.createElement("option"); o.value = c.id; o.textContent = c.name; enemySel.appendChild(o); });
  const maps = JSON.parse(api.MapTypes());
  const names = { twoRivers: "Two Rivers", greatPlains: "Great Plains", highlands: "Highlands", lakeland: "Lakeland" };
  const sel = $("mapType");
  maps.forEach(m => { const o = document.createElement("option"); o.value = m; o.textContent = names[m] || m; sel.appendChild(o); });
  const play = $("playBtn");
  play.disabled = false;
  play.textContent = "Play";
  play.onclick = () => {
    unlock();
    const diff = parseInt(document.querySelector('input[name="diff"]:checked').value, 10);
    const size = parseInt(document.querySelector('input[name="size"]:checked').value, 10);
    const others = civs.filter(c => c.id !== selected);
    const enemySel = $("enemyCiv").value;
    const enemy = enemySel && enemySel !== "random" ? enemySel : (others.length ? others[Math.floor(Math.random() * others.length)].id : selected);
    startMatch(selected, enemy, diff, sel.value, size);
  };
  $("againBtn").onclick = () => { $("end").classList.add("hidden"); $("hud").classList.add("hidden"); $("menu").classList.remove("hidden"); running = false; };
  $("helpBtn").onclick = () => $("help").classList.toggle("hidden");
  $("helpClose").onclick = () => $("help").classList.add("hidden");
}

function layoutHud() {
  const top = $("top").offsetHeight;
  $("minimap").style.top = (top + 6) + "px";
  $("objectives").style.top = (top + 6) + "px";
  $("drawer").style.top = (top + 6) + "px";
}
window.addEventListener("resize", () => setTimeout(layoutHud, 50));

function startMatch(civ, enemy, diff, mapType, size) {
  api.StartMatch(civ, enemy, diff, 0, mapType, size);
  const civList = JSON.parse(api.Civs());
  const col = (id) => (civList.find(c => c.id === id) || {}).color;
  setCivColors([col(civ) === col(enemy) ? null : col(civ), col(enemy)]);
  const w = api.MapWidth(), h = api.MapHeight();
  R.setMap(w, h, api.Terrain(), defs);
  R.setFog(api.Fog());
  const home = api.Home();
  R.cam.zoom = Math.max(R.cam.minZoom, Math.min(R.cam.maxZoom, Math.min(innerWidth, innerHeight) / 18));
  R.centerOn(home[0] - 1, home[1] - 4);
  R.effects = []; R.markers = [];
  $("menu").classList.add("hidden");
  $("hud").classList.remove("hidden");
  $("end").classList.add("hidden");
  $("drawer").classList.add("hidden");
  setTimeout(layoutHud, 30);
  hudKey = "";
  paused = false; speed = 1; updateSpeedButtons();
  objectives = makeObjectives();
  renderObjectives();
  running = true;
  matchStart = performance.now();
  lastTime = performance.now();
  requestAnimationFrame(frame);
  clearInterval(hudTimer);
  hudTimer = setInterval(pollHud, 150);
}

// ------------------------------------------------------------------ loop
let miniAt = 0;
function frame(now) {
  if (!running) return;
  const dt = Math.min(0.1, (now - lastTime) / 1000);
  lastTime = now;
  const buf = api.Frame(paused ? 0 : dt * speed);
  lastFrame = buf;
  if (buf.length >= 16 && buf[12]) R.setFog(api.Fog());
  R.draw(buf, dt);
  if (now - miniAt > 250) { miniAt = now; R.drawMinimap(mini, R.lastEnts || [], 0); }
  // sounds for effects
  const n = buf[9]; let o = 16 + buf[0] * 12;
  for (let i = 0; i < n; i++, o += 5) { if (buf[o] === 1) sfx.death(); else if (buf[o] === 2 && i < 2) sfx.hit(); }
  requestAnimationFrame(frame);
}

// ------------------------------------------------------------------ input
const pointers = new Map();
let gesture = null;
let pinchDist = 0, pinchCenter = null;
const TAP_MOVE = 12, LONG_MS = 380;

canvas.addEventListener("pointerdown", (ev) => {
  if (!running) return;
  unlock();
  canvas.setPointerCapture(ev.pointerId);
  pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
  hideRadial(); hideTip();
  if (pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    pinchDist = Math.hypot(a.x - b.x, a.y - b.y); pinchCenter = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    if (gesture) { clearTimeout(gesture.timer); gesture = null; $("box").classList.add("hidden"); }
    return;
  }
  gesture = { id: ev.pointerId, sx: ev.clientX, sy: ev.clientY, lx: ev.clientX, ly: ev.clientY, t0: performance.now(), mode: "touch" };
  gesture.timer = setTimeout(() => {
    if (gesture && gesture.mode === "touch") {
      gesture.mode = "long";
      const [wx, wy] = R.toWorld(gesture.sx, gesture.sy);
      if (api.LongPress(wx, wy)) { showRadial(gesture.sx, gesture.sy, wx, wy); if (navigator.vibrate) navigator.vibrate(15); }
    }
  }, LONG_MS);
});

canvas.addEventListener("pointermove", (ev) => {
  if (!running) return;
  if (pointers.has(ev.pointerId)) pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
  const [wx, wy] = R.toWorld(ev.clientX, ev.clientY);
  api.Pointer(wx, wy);
  if (pointers.size === 2) {
    const [a, b] = [...pointers.values()];
    const d = Math.hypot(a.x - b.x, a.y - b.y), c = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    if (pinchDist > 0) R.zoomAt(d / pinchDist, c.x, c.y);
    R.panScreen(pinchCenter.x, pinchCenter.y, c.x, c.y);
    pinchDist = d; pinchCenter = c;
    return;
  }
  if (!gesture || gesture.id !== ev.pointerId) return;
  const moved = Math.hypot(ev.clientX - gesture.sx, ev.clientY - gesture.sy);
  if (gesture.mode === "touch" && moved > TAP_MOVE) { gesture.mode = "pan"; clearTimeout(gesture.timer); }
  if (gesture.mode === "long" && moved > TAP_MOVE) { gesture.mode = "box"; hideRadial(); }
  if (gesture.mode === "pan") R.panScreen(gesture.lx, gesture.ly, ev.clientX, ev.clientY);
  else if (gesture.mode === "box") {
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
  if (gesture.mode === "touch" && ev.type !== "pointercancel") {
    const [wx, wy] = R.toWorld(ev.clientX, ev.clientY);
    const hit = R.hitTest(ev.clientX, ev.clientY);
    const code = api.Tap(wx, wy, 26 / R.cam.zoom, hit);
    feedback(code, wx, wy);
  } else if (gesture.mode === "box") {
    const x0 = Math.min(gesture.sx, ev.clientX), x1 = Math.max(gesture.sx, ev.clientX), y0 = Math.min(gesture.sy, ev.clientY), y1 = Math.max(gesture.sy, ev.clientY);
    if (x1 - x0 > 6 || y1 - y0 > 6) { const ids = R.entitiesInScreenRect(x0, y0, x1, y1); if (ids.length) { api.SelectEntities(ids); R.everSelected = true; sfx.select(); pollHud(); } }
  }
  gesture = null;
  $("box").classList.add("hidden");
}
canvas.addEventListener("pointerup", endPointer);
canvas.addEventListener("pointercancel", endPointer);
canvas.addEventListener("wheel", (ev) => { if (running) { R.zoomAt(ev.deltaY < 0 ? 1.15 : 1 / 1.15, ev.clientX, ev.clientY); ev.preventDefault(); } }, { passive: false });
window.addEventListener("keydown", (ev) => {
  if (!running) return;
  const step = 2;
  if (ev.key === "ArrowLeft" || ev.key === "a") { R.cam.x -= step; R.cam.y += step; }
  if (ev.key === "ArrowRight" || ev.key === "d") { R.cam.x += step; R.cam.y -= step; }
  if (ev.key === "ArrowUp" || ev.key === "w") { R.cam.x += step; R.cam.y += step; }
  if (ev.key === "ArrowDown" || ev.key === "s") { R.cam.x -= step; R.cam.y -= step; }
  if (ev.key === "Escape") { api.Action("cancelmode", 0); hideRadial(); pollHud(); }
  if (ev.key === "h") { const home = api.Home(); R.centerOn(home[0], home[1]); }
  if (ev.key === " ") { togglePause(); ev.preventDefault(); }
  if (ev.key === ".") { const r = JSON.parse(api.Action("idle", 0)); if (r.x !== undefined) R.centerOn(r.x, r.y); pollHud(); }
  R.clampCam();
});

function feedback(code, wx, wy) {
  if (code === 4) R.everSelected = true;
  switch (code) {
    case 1: R.addMarker("move", wx, wy); sfx.move(); break;
    case 2: R.addMarker("attack", wx, wy); sfx.attack(); break;
    case 3: R.addMarker("gather", wx, wy); sfx.gather(); break;
    case 4: sfx.select(); break;
    case 5: sfx.build(); break;
    case 6: R.addMarker("build", wx, wy); sfx.build(); break;
    case 7: R.addMarker("attack", wx, wy); sfx.attack(); break;
    case 9: R.addMarker("move", wx, wy); sfx.click(); break;
    case 8: toast("Tap a villager first, then tap the resource"); break;
    case 10: R.everSelected = true; R.addMarker("gather", wx, wy); sfx.gather(); toast("Nearest idle villager sent to gather"); break;
    default: break;
  }
  pollHud();
}

// minimap
const mini = $("minimap");
mini.addEventListener("pointerdown", (ev) => { miniJump(ev); ev.stopPropagation(); });
mini.addEventListener("pointermove", (ev) => { if (ev.buttons) { miniJump(ev); ev.stopPropagation(); } });
function miniJump(ev) {
  const r = mini.getBoundingClientRect();
  R.centerOn((ev.clientX - r.left) / r.width * R.mapW, R.mapH - (ev.clientY - r.top) / r.height * R.mapH);
}

// ------------------------------------------------------------------ radial
let radialPoint = null;
function showRadial(sx, sy, wx, wy) {
  radialPoint = [wx, wy];
  const r = $("radial"); r.innerHTML = ""; r.style.left = sx + "px"; r.style.top = sy + "px";
  const add = (dx, dy, text, cls, action) => {
    const b = document.createElement("button"); b.className = "btn round " + cls; b.textContent = text;
    b.style.left = dx + "px"; b.style.top = dy + "px";
    b.onpointerdown = (e) => e.stopPropagation();
    b.onclick = () => { if (action) { api.PointAction(action, radialPoint[0], radialPoint[1]); R.addMarker(action === "attackmove" ? "attack" : "move", radialPoint[0], radialPoint[1]); (action === "attackmove" ? sfx.attack : sfx.move)(); } hideRadial(); pollHud(); };
    r.appendChild(b);
  };
  add(0, -70, "Move", "", "move");
  add(70, 0, "Attack", "attack", "attackmove");
  add(0, 70, "Stop", "neutral", null);
  r.querySelectorAll("button")[2].onclick = () => { api.Action("stop", 0); hideRadial(); pollHud(); };
  add(-70, 0, "×", "neutral", null);
  r.classList.remove("hidden");
  setTimeout(() => hideRadial(), 4000);
}
function hideRadial() { $("radial").classList.add("hidden"); }

// ------------------------------------------------------------------ HUD
let toastTimer = null;
function pollHud() {
  if (!running || !api) return;
  const h = JSON.parse(api.HudJson());
  lastHud = h;
  $("food").textContent = h.food; $("wood").textContent = h.wood; $("gold").textContent = h.gold;
  $("pop").textContent = h.pop + "/" + h.popCap;
  $("pop").classList.toggle("warn", h.popCap - h.pop <= 2);
  $("age").textContent = ["I", "II", "III", "IV"][h.age] + " · " + h.ageName + (h.ageUp >= 0 ? " · " + h.ageUp + " s" : "");
  $("clock").textContent = h.minutes + ":" + String(h.seconds).padStart(2, "0");
  const sb = $("shipBtn"); sb.textContent = h.shipAvail > 0 ? "Shipments (" + h.shipAvail + ")" : "XP " + h.xp + "/" + h.shipCost; sb.classList.toggle("ready", h.shipAvail > 0);
  const ib = $("idleBtn"); ib.textContent = h.idle > 0 ? "Idle " + h.idle : "Idle"; ib.style.opacity = h.idle > 0 ? 1 : 0.45;
  for (const t of h.toasts) { toast(t); if (/Welcome/.test(t)) sfx.age(); else if (/completed|researched/.test(t)) sfx.complete(); else if (/Shipment/.test(t)) sfx.ship(); else if (/^Not|^Can|^Advance|^Limit|^Queue|^Build more|^Already/.test(t)) sfx.error(); }
  $("label").textContent = h.label || (h.villagers + " villagers · " + h.army + " soldiers");

  const key = JSON.stringify([h.cards, h.actions, h.deck, h.market, h.stance, h.label === ""]);
  if (key !== hudKey) {
    hudKey = key;
    const cards = $("cards"); cards.innerHTML = "";
    for (const c of h.cards) {
      const b = document.createElement("button"); b.className = "btn card";
      b.innerHTML = `<img src="${iconFor("unit:" + c.id, defs)}" alt=""><span>${c.name} ×${c.count}</span>`;
      b.onclick = () => { api.Action("sub", c.def); sfx.click(); pollHud(); }; cards.appendChild(b);
    }
    const acts = $("actions"); acts.innerHTML = "";
    if (h.actions.length === 0 && h.mode === 0) {
      addAction(acts, { id: "selectvillagers", label: "All villagers", enabled: true, kind: "neutral", icon: "unit:unit.villager", tip: "Select every villager" });
      addAction(acts, { id: "selectarmy", label: "All soldiers", enabled: true, kind: "attack", icon: "attack", tip: "Select every soldier" });
    }
    for (const a of h.actions) addAction(acts, a);
    if (!$("drawer").classList.contains("hidden")) fillDrawer(h);
  }
  const ap = $("attackPing");
  if (lastFrame && lastFrame[10] >= 0) { ap.classList.remove("hidden"); ap.onclick = () => { R.centerOn(lastFrame[10] / S, lastFrame[11] / S); }; }
  else ap.classList.add("hidden");
  if (h.winner !== -1 && $("end").classList.contains("hidden")) showEnd(h);
  updateObjectives(h);
}

function addAction(parent, a) {
  const b = document.createElement("button"); b.className = "btn act " + a.kind; b.disabled = !a.enabled;
  const [name, cost] = a.label.split("|");
  b.innerHTML = (a.icon ? `<img src="${iconFor(a.icon, defs)}" alt="">` : "") + `<span class="name">${name}</span>` + (cost ? `<span class="cost">${cost}</span>` : "");
  const [act, arg] = a.id.split(":");
  b.onclick = () => {
    if (tipShown) { hideTip(); return; }
    const res = api.Action(act, arg ? parseInt(arg, 10) : 0);
    if (res && res[0] === "{") { const r = JSON.parse(res); if (r.gather) { R.addMarker("gather", r.x, r.y); sfx.gather(); pollHud(); return; } }
    sfx.click(); pollHud();
  };
  attachTip(b, a.tip ? (name + " — " + a.tip) : "");
  parent.appendChild(b);
}

let tipTimer = null, tipShown = false;
function attachTip(el, text) {
  if (!text) return;
  el.addEventListener("pointerdown", () => { tipTimer = setTimeout(() => showTip(el, text), 450); });
  el.addEventListener("pointerup", () => { clearTimeout(tipTimer); setTimeout(hideTip, 1500); });
  el.addEventListener("pointerleave", () => { clearTimeout(tipTimer); });
  el.addEventListener("mouseenter", () => { tipTimer = setTimeout(() => showTip(el, text), 500); });
  el.addEventListener("mouseleave", () => { clearTimeout(tipTimer); hideTip(); });
}
function showTip(el, text) {
  const t = $("tip"); t.textContent = text; t.classList.remove("hidden"); tipShown = true;
  const r = el.getBoundingClientRect();
  t.style.left = Math.max(8, Math.min(innerWidth - 8 - 280, r.left)) + "px"; t.style.top = (r.top - 8) + "px";
  setTimeout(() => { tipShown = false; }, 0);
}
function hideTip() { $("tip").classList.add("hidden"); tipShown = false; }

function fillDrawer(h) {
  const d = $("drawer"); d.innerHTML = `<h3>Home City — ${h.xp} XP · next shipment ${h.shipCost}</h3>`;
  for (const s of h.deck) {
    const b = document.createElement("button"); b.className = "btn " + (s.enabled ? "gold" : "neutral"); b.disabled = !s.enabled;
    b.innerHTML = s.name + (s.note ? `<span class="note">${s.note}</span>` : "");
    b.onclick = () => { api.Action("ship", s.tech); sfx.click(); d.classList.add("hidden"); pollHud(); };
    d.appendChild(b);
  }
  if (h.market) {
    const t = document.createElement("h3"); t.textContent = "Market — 100 units for gold"; d.appendChild(t);
    for (const m of h.market) {
      const row = document.createElement("div"); row.className = "trade";
      row.innerHTML = `<span>${m.name}</span>`;
      const buy = document.createElement("button"); buy.className = "btn small"; buy.textContent = `Buy ${m.buy}g`; buy.disabled = !m.canBuy; buy.onclick = () => { api.Action("buy", m.res); sfx.click(); pollHud(); fillDrawer(JSON.parse(api.HudJson())); };
      const sell = document.createElement("button"); sell.className = "btn small neutral"; sell.textContent = `Sell ${m.sell}g`; sell.disabled = !m.canSell; sell.onclick = () => { api.Action("sell", m.res); sfx.click(); pollHud(); fillDrawer(JSON.parse(api.HudJson())); };
      row.appendChild(buy); row.appendChild(sell); d.appendChild(row);
    }
  }
  const c = document.createElement("button"); c.className = "btn neutral"; c.textContent = "Close"; c.onclick = () => d.classList.add("hidden"); d.appendChild(c);
}
$("shipBtn").onclick = () => { const d = $("drawer"); d.classList.toggle("hidden"); if (!d.classList.contains("hidden")) fillDrawer(JSON.parse(api.HudJson())); };
$("idleBtn").onclick = () => { const r = JSON.parse(api.Action("idle", 0)); if (r.x !== undefined) { R.centerOn(r.x, r.y); sfx.select(); } pollHud(); };
$("pauseBtn").onclick = togglePause;
$("speedBtn").onclick = () => { speed = speed === 1 ? 2 : 1; updateSpeedButtons(); };
$("muteBtn").onclick = () => { setMuted(!isMuted()); $("muteBtn").textContent = isMuted() ? "🔇 Muted" : "🔊 Sound"; };
$("objToggle").onclick = () => $("objList").classList.toggle("hidden");
$("menuBtn").onclick = () => { paused = true; updateSpeedButtons(); $("pauseMenu").classList.remove("hidden"); };
$("resumeBtn").onclick = () => { paused = false; updateSpeedButtons(); $("pauseMenu").classList.add("hidden"); };
$("quitBtn").onclick = () => { $("pauseMenu").classList.add("hidden"); $("hud").classList.add("hidden"); $("menu").classList.remove("hidden"); running = false; };

function togglePause() { paused = !paused; updateSpeedButtons(); }
function updateSpeedButtons() { $("pauseBtn").textContent = paused ? "▶" : "⏸"; $("speedBtn").textContent = speed + "×"; $("speedBtn").classList.toggle("ready", speed > 1); }

function toast(text) { const t = $("toast"); t.textContent = text; t.classList.remove("hidden"); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add("hidden"), 2400); }

function showEnd(h) {
  $("endTitle").textContent = h.winner === 0 ? "Victory" : h.winner === -2 ? "Draw" : "Defeat";
  $("endStats").innerHTML = `<b>${h.minutes} min</b> · Age ${h.age + 1}<br>Units killed ${h.stats.killed} · lost ${h.stats.lost}<br>Buildings razed ${h.stats.razed} · shipments ${h.stats.ships}`;
  $("end").classList.remove("hidden");
  (h.winner === 0 ? sfx.victory : sfx.defeat)();
}

// ------------------------------------------------------------------ objectives (guided first minutes)
function makeObjectives() {
  return [
    { text: "Select a villager and tap berries or a tree", done: (h) => h.food > 200 + 20 || h.wood > 250 + 20 || h.gold > 100 + 20 },
    { text: "Build a House (select villagers → House)", done: (h) => h.popCap >= 30 },
    { text: "Train villagers at the Town Center (10+)", done: (h) => h.villagers >= 10 },
    { text: "Advance to the Colonial Age (Town Center)", done: (h) => h.age >= 1 },
    { text: "Build a Barracks and train 8 soldiers", done: (h) => h.army >= 8 },
    { text: "Send a Home City shipment", done: (h) => h.stats.ships >= 1 },
    { text: "Find and destroy the enemy Town Center", done: (h) => h.winner === 0 },
  ];
}
function renderObjectives() {
  const list = $("objList"); list.innerHTML = "";
  objectives.forEach((o, i) => { const li = document.createElement("li"); li.textContent = o.text; li.className = o.complete ? "done" : ""; list.appendChild(li); });
}
function updateObjectives(h) {
  let changed = false;
  for (const o of objectives) if (!o.complete && o.done(h)) { o.complete = true; changed = true; toast("Objective complete: " + o.text); sfx.complete(); }
  if (changed) renderObjectives();
  const next = objectives.find(o => !o.complete);
  $("objToggle").textContent = next ? "▸ " + next.text : "All objectives complete";
}

boot().catch(err => { console.error(err); const p = $("playBtn"); p.textContent = "Failed to load: " + err; });
