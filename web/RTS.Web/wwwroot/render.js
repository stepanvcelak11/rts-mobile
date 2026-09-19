// Isometric canvas renderer with procedurally generated sprites (no art assets needed).
// World coordinates are map cells (x east, y north). Screen: x east → right-up, y north → left-up.

export const PLAYER = ["#3b82f6", "#dc2626", "#f59e0b", "#22c55e"];
const PLAYER_DARK = ["#1e40af", "#7f1d1d", "#92400e", "#14532d"];
const PLAYER_LIGHT = ["#93c5fd", "#fca5a5", "#fde68a", "#86efac"];
const WILD = "#6b6f78";
export function playerColor(p) { return p < 0 ? WILD : PLAYER[p % 4]; }
export function setCivColors(civColors) { civColors.forEach((c, i) => { if (c) { PLAYER[i] = c; PLAYER_DARK[i] = shade(c, 0.6); PLAYER_LIGHT[i] = mix(c, "#ffffff", 0.45); } }); }

// terrain: 0 grass 1 dirt 2 sand 3 water 4 cliff
const TERRAIN = [
  { base: "#6ea555", alt: "#67a04f", dark: "#4f8a3c" },
  { base: "#a0784e", alt: "#9a7249", dark: "#7a5a38" },
  { base: "#dcc48e", alt: "#d6bd86", dark: "#b7a06a" },
  { base: "#3d7fc4", alt: "#3a78bb", dark: "#2f6299" },
  { base: "#8a8380", alt: "#83807c", dark: "#5b5754" },
];

const BASE = 32;               // pixels per cell (half tile width) at which sprites are baked
const cache = new Map();

export class Renderer {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext("2d");
    this.cam = { x: 0, y: 0, zoom: 30, minZoom: 12, maxZoom: 64 };
    this.mapW = 0; this.mapH = 0;
    this.terrain = null; this.elev = null; this.fog = null;
    this.dpr = 1;
    this.time = 0;
    this.effects = [];
    this.markers = [];
    this.defs = null;
    this.hoverCell = null;
  }

  // ---------------------------------------------------------------- setup
  setMap(w, h, terrainInfo, defs) {
    this.mapW = w; this.mapH = h; this.defs = defs;
    this.terrain = new Uint8Array(w * h); this.elev = new Uint8Array(w * h);
    for (let i = 0; i < w * h; i++) { this.terrain[i] = terrainInfo[i] & 15; this.elev[i] = terrainInfo[i] >> 4; }
    this.fog = new Uint8Array(w * h);
    this.variant = new Uint8Array(w * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) this.variant[y * w + x] = hash2(x, y) % 6;
    this.ground = bakeGround(this.terrain, this.elev, w, h);
    this.fogCanvas = offscreen(w, h);
    this.fogImage = this.fogCanvas.getContext("2d").createImageData(w, h);
    this.hasElevation = this.elev.some(v => v > 0);
  }
  setFog(f) {
    for (let i = 0; i < f.length; i++) this.fog[i] = f[i];
    const d = this.fogImage.data, W = this.mapW, H = this.mapH;
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const v = this.fog[y * W + x];
      const o = ((H - 1 - y) * W + x) * 4;      // image rows top→bottom = world north→south
      d[o] = 7; d[o + 1] = 9; d[o + 2] = 13; d[o + 3] = v === 2 ? 0 : v === 1 ? 128 : 255;
    }
    this.fogCanvas.getContext("2d").putImageData(this.fogImage, 0, 0);
  }

  /// Canvas transform that maps map-image pixels (x right, y down = north→south) onto the iso diamond.
  applyMapTransform(ctx) {
    const hw = this.hw, hh = this.hh, H = this.mapH, dpr = this.dpr;
    const [ox, oy] = this.toScreen(0, 0);
    ctx.setTransform(dpr * hw, -dpr * hh, dpr * hw, dpr * hh, dpr * (ox - H * hw), dpr * (oy - H * hh));
  }

  resize() {
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.canvas.width = Math.floor(innerWidth * this.dpr);
    this.canvas.height = Math.floor(innerHeight * this.dpr);
    this.canvas.style.width = innerWidth + "px";
    this.canvas.style.height = innerHeight + "px";
  }

  // ---------------------------------------------------------------- camera
  get hw() { return this.cam.zoom; }
  get hh() { return this.cam.zoom * 0.5; }
  toScreen(x, y) {
    const dx = x - this.cam.x, dy = y - this.cam.y;
    return [(dx - dy) * this.hw + innerWidth / 2, -(dx + dy) * this.hh + innerHeight / 2];
  }
  toWorld(sx, sy) {
    const u = (sx - innerWidth / 2) / this.hw, v = -(sy - innerHeight / 2) / this.hh;
    return [this.cam.x + (u + v) / 2, this.cam.y + (v - u) / 2];
  }
  clampCam() {
    const m = 4;
    this.cam.x = Math.max(-m, Math.min(this.mapW + m, this.cam.x));
    this.cam.y = Math.max(-m, Math.min(this.mapH + m, this.cam.y));
  }
  zoomAt(factor, sx, sy) {
    const [wx, wy] = this.toWorld(sx, sy);
    this.cam.zoom = Math.max(this.cam.minZoom, Math.min(this.cam.maxZoom, this.cam.zoom * factor));
    const [nx, ny] = this.toWorld(sx, sy);
    this.cam.x += wx - nx; this.cam.y += wy - ny;
    this.clampCam();
  }
  panScreen(fromSx, fromSy, toSx, toSy) {
    const [ax, ay] = this.toWorld(fromSx, fromSy), [bx, by] = this.toWorld(toSx, toSy);
    this.cam.x += ax - bx; this.cam.y += ay - by;
    this.clampCam();
  }
  centerOn(x, y) { this.cam.x = x; this.cam.y = y; this.clampCam(); }

  // ---------------------------------------------------------------- frame
  addEffect(kind, x, y) { this.effects.push({ kind, x, y, t: 0 }); }
  addMarker(kind, x, y) { this.markers.push({ kind, x, y, t: 0 }); }

  draw(buf, dt) {
    const ctx = this.ctx;
    this.time += dt;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.fillStyle = "#0b0e14";
    ctx.fillRect(0, 0, innerWidth, innerHeight);
    if (!buf || buf.length < 16 || !this.terrain) return;

    const ents = parseEntities(buf);
    this.drawGround();
    this.drawGhost(buf);
    this.drawRally(buf, ents);
    this.drawMarkers(dt);
    this.drawEntities(ents);
    this.drawEffects(buf, ents, dt);
    this.drawFog();
    this.drawDayNight();
    this.drawBirds(dt);
    this.drawPing(buf);
    this.lastEnts = ents;
  }

  /// Slow day/night cycle (8 minutes): a cool tint at night, warm at dusk. Purely cosmetic.
  drawDayNight() {
    const t = (this.time % 480) / 480;                 // 0 = noon, 0.5 = midnight
    const night = Math.max(0, Math.cos(t * Math.PI * 2) * -1);   // 0 by day, 1 at midnight
    const dusk = Math.max(0, 1 - Math.abs(t - 0.28) / 0.08) + Math.max(0, 1 - Math.abs(t - 0.72) / 0.08);
    const ctx = this.ctx;
    if (night > 0.02) { ctx.fillStyle = `rgba(10,20,60,${0.38 * night})`; ctx.fillRect(0, 0, innerWidth, innerHeight); }
    if (dusk > 0.02) { ctx.fillStyle = `rgba(255,140,60,${0.12 * dusk})`; ctx.fillRect(0, 0, innerWidth, innerHeight); }
  }

  drawBirds(dt) {
    if (!this.birds) this.birds = [];
    if (this.birds.length < 2 && Math.random() < dt * 0.15) {
      const dir = Math.random() < 0.5 ? 1 : -1;
      this.birds.push({ x: dir > 0 ? -60 : innerWidth + 60, y: 40 + Math.random() * innerHeight * 0.4, dir, n: 3 + Math.floor(Math.random() * 4), t: 0 });
    }
    const ctx = this.ctx;
    ctx.strokeStyle = "rgba(20,20,30,0.7)"; ctx.lineWidth = 1.5;
    for (const b of this.birds) {
      b.x += b.dir * dt * 45; b.t += dt;
      for (let k = 0; k < b.n; k++) {
        const bx = b.x - b.dir * k * 14, by = b.y + (k % 2) * 8 + Math.sin(b.t * 3 + k) * 2;
        const f = Math.sin(b.t * 12 + k) * 3;
        ctx.beginPath(); ctx.moveTo(bx - 5, by + f); ctx.lineTo(bx, by - 2); ctx.lineTo(bx + 5, by + f); ctx.stroke();
      }
    }
    this.birds = this.birds.filter(b => b.x > -200 && b.x < innerWidth + 200);
  }

  // ---------------------------------------------------------------- ground
  visibleRange() {
    const corners = [[0, 0], [innerWidth, 0], [0, innerHeight], [innerWidth, innerHeight]].map(([sx, sy]) => this.toWorld(sx, sy));
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const [x, y] of corners) { minX = Math.min(minX, x); maxX = Math.max(maxX, x); minY = Math.min(minY, y); maxY = Math.max(maxY, y); }
    return {
      x0: Math.max(0, Math.floor(minX) - 2), x1: Math.min(this.mapW - 1, Math.ceil(maxX) + 2),
      y0: Math.max(0, Math.floor(minY) - 2), y1: Math.min(this.mapH - 1, Math.ceil(maxY) + 3),
    };
  }

  drawGround() {
    const ctx = this.ctx, hw = this.hw, hh = this.hh, W = this.mapW;
    // Whole static terrain in one call: the baked texture warped onto the iso diamond.
    ctx.save();
    this.applyMapTransform(ctx);
    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(this.ground, 0, 0, this.mapW, this.mapH);
    ctx.restore();
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    // Animated water shimmer: a few drifting highlights over water cells in view.
    this.waterShimmer();
    if (!this.hasElevation) return;
    const r = this.visibleRange();
    const waterFrame = 0;
    // Elevated cells are drawn as lifted tiles with cliff faces, far to near (decreasing x+y).
    for (let s = r.x1 + r.y1; s >= r.x0 + r.y0; s--) {
      for (let x = r.x0; x <= r.x1; x++) {
        const y = s - x;
        if (y < r.y0 || y > r.y1) continue;
        const i = y * W + x;
        const t = this.terrain[i], e = this.elev[i];
        if (e === 0) continue;
        const [sx, sy] = this.toScreen(x + 0.5, y + 0.5);
        if (sx < -hw * 2 || sx > innerWidth + hw * 2 || sy < -hh * 6 || sy > innerHeight + hh * 3) continue;
        const lift = e * hh * 1.6;
        // Cliff faces where a lower neighbour lies to the south (y-1) or west (x-1) — the sides facing the viewer.
        if (e > 0) {
          const eS = y > 0 ? this.elev[i - W] : 0, eW = x > 0 ? this.elev[i - 1] : 0;
          if (eS < e) this.cliffFace(sx, sy - lift, hw, hh, (e - eS) * hh * 1.6, "south");
          if (eW < e) this.cliffFace(sx, sy - lift, hw, hh, (e - eW) * hh * 1.6, "west");
        }
        const sprite = tileSprite(t, this.variant[i], 0, hw);
        ctx.drawImage(sprite, sx - hw - 1, sy - lift - hh - 1, hw * 2 + 2, hh * 2 + 2);
      }
    }
  }

  waterShimmer() {
    const ctx = this.ctx, hw = this.hw, hh = this.hh, W = this.mapW;
    const r = this.visibleRange();
    ctx.strokeStyle = "rgba(255,255,255,0.22)"; ctx.lineWidth = Math.max(1, hw * 0.05);
    ctx.beginPath();
    const t = this.time;
    for (let y = r.y0; y <= r.y1; y++) for (let x = r.x0; x <= r.x1; x++) {
      const i = y * W + x;
      if (this.terrain[i] !== 3 || this.elev[i] > 0) continue;
      const ph = (t * 0.6 + hash2(x, y) % 100 / 100) % 1;
      const [sx, sy] = this.toScreen(x + 0.2 + ph * 0.6, y + 0.5);
      ctx.moveTo(sx - hw * 0.18, sy); ctx.quadraticCurveTo(sx, sy - hh * 0.12, sx + hw * 0.18, sy);
    }
    ctx.stroke();
  }

  cliffFace(sx, sy, hw, hh, height, side) {
    const ctx = this.ctx;
    ctx.beginPath();
    if (side === "south") { ctx.moveTo(sx - hw, sy); ctx.lineTo(sx, sy + hh); ctx.lineTo(sx, sy + hh + height); ctx.lineTo(sx - hw, sy + height); }
    else { ctx.moveTo(sx, sy + hh); ctx.lineTo(sx + hw, sy); ctx.lineTo(sx + hw, sy + height); ctx.lineTo(sx, sy + hh + height); }
    ctx.closePath();
    ctx.fillStyle = side === "south" ? "#6b5b4c" : "#5a4c40";
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,0.35)"; ctx.lineWidth = 1; ctx.stroke();
  }

  shore(x, y, sx, sy, hw, hh) {
    const W = this.mapW, H = this.mapH, T = this.terrain;
    const land = (xx, yy) => xx >= 0 && yy >= 0 && xx < W && yy < H && T[yy * W + xx] !== 3;
    const ctx = this.ctx;
    ctx.strokeStyle = "rgba(255,255,255,0.35)"; ctx.lineWidth = Math.max(1, hw * 0.08);
    ctx.beginPath();
    if (land(x, y + 1)) { ctx.moveTo(sx - hw, sy); ctx.lineTo(sx, sy - hh); }        // north-west edge
    if (land(x + 1, y)) { ctx.moveTo(sx, sy - hh); ctx.lineTo(sx + hw, sy); }        // north-east edge
    if (land(x, y - 1)) { ctx.moveTo(sx - hw, sy); ctx.lineTo(sx, sy + hh); }        // south-west edge
    if (land(x - 1, y)) { ctx.moveTo(sx, sy + hh); ctx.lineTo(sx + hw, sy); }        // south-east edge
    ctx.stroke();
  }

  // ---------------------------------------------------------------- entities
  drawEntities(ents) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    // depth: far (large x+y) first
    ents.sort((a, b) => depthKey(b) - depthKey(a));
    this.hitRects = [];
    const margin = hw * 4;
    for (const e of ents) {
      if (e.kind !== 4) {   // cull off-screen sprites (big maps)
        const [cx, cy] = this.toScreen(e.kind === 1 ? e.x : e.x + e.a / 2, e.kind === 1 ? e.y : e.y + e.b / 2);
        if (cx < -margin || cx > innerWidth + margin || cy < -margin || cy > innerHeight + margin * 1.5) continue;
      }
      if (e.kind === 2) this.drawBuilding(e);
      else if (e.kind === 3) this.drawNode(e);
      else if (e.kind === 1) this.drawUnit(e);
      else if (e.kind === 4) this.drawProjectile(e);
    }
  }

  elevAt(x, y) {
    const cx = Math.max(0, Math.min(this.mapW - 1, Math.floor(x))), cy = Math.max(0, Math.min(this.mapH - 1, Math.floor(y)));
    return this.elev[cy * this.mapW + cx];
  }

  drawBuilding(e) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    const lift = this.elevAt(e.x + 0.5, e.y + 0.5) * hh * 1.6;
    const def = this.defs.buildings[e.def];
    const site = (e.flags & 2) !== 0;
    const remembered = (e.flags & 4096) !== 0;
    const sprite = buildingSprite(def.id, e.a, e.b, e.player, hw);
    const [sx, sy] = this.toScreen(e.x, e.y);       // near corner of the diamond
    // soft ground shadow toward the south-east
    ctx.fillStyle = "rgba(0,0,0,0.16)";
    const shc = this.toScreen(e.x + e.a / 2 + 0.25, e.y + e.b / 2 - 0.35);
    ctx.beginPath(); ctx.ellipse(shc[0], shc[1] - lift, (e.a + e.b) * hw * 0.3, (e.a + e.b) * hh * 0.3, 0, 0, Math.PI * 2); ctx.fill();
    const scale = hw / BASE;
    const dw = sprite.width * scale, dh = sprite.height * scale;
    const left = sx - (e.b * BASE) * scale;          // sprite origin: west corner is at (x, y+h)
    const top = sy - lift - dh + (0);
    this.hitRects.push({ id: e.id, x0: left, y0: top + dh * 0.15, x1: left + dw, y1: top + dh, kind: 2, depth: depthKey(e) });
    if (site) {
      const k = Math.max(0.08, e.state / 100);
      ctx.save();
      ctx.globalAlpha = 0.9;
      // scaffold outline of the footprint
      this.footprintOutline(e.x, e.y, e.a, e.b, lift, "#8b6a3e", 2);
      ctx.beginPath(); ctx.rect(left, top + dh * (1 - k), dw, dh * k); ctx.clip();
      ctx.drawImage(sprite, left, top, dw, dh);
      ctx.restore();
      ctx.fillStyle = "#fff"; ctx.font = `bold ${Math.max(10, hw * 0.4)}px system-ui`; ctx.textAlign = "center";
      ctx.fillText(e.state + "%", sx - (e.b - e.a) * hw / 2, top + dh * 0.5);
    } else {
      if (remembered) ctx.globalAlpha = 0.6;
      ctx.drawImage(sprite, left, top, dw, dh);
      ctx.globalAlpha = 1;
      if (e.flags & 2048) this.farmPlots(e, lift);
      if (e.flags & 16) this.productionDot(sx - (e.b - e.a) * hw / 2, top + dh * 0.15);
      if (e.flags & 1024) this.productionDot(sx - (e.b - e.a) * hw / 2 + hw * 0.4, top + dh * 0.15, "#c084fc");
      if (def.id === "bld.mill") this.millBlades(sx - (e.b - e.a) * hw / 2, top + dh * 0.28, hw);
      if (def.id === "bld.foundry" || def.id === "bld.towncenter") this.smoke(sx - (e.b - e.a) * hw / 2 + hw * 0.5, top + dh * 0.1, e.id);
    }
    if (e.flags & 1) this.footprintOutline(e.x, e.y, e.a, e.b, lift, "#ffffff", 2);
    if (e.hp >= 0 && (e.hp < 100 || (e.flags & 1))) this.bar(sx - (e.b - e.a) * hw / 2, top - 6, Math.max(30, (e.a + e.b) * hw * 0.5), e.hp);
  }

  farmPlots(e, lift) {
    // Furrows drawn around the mill on the ground (a ring one cell wide).
    const ctx = this.ctx;
    ctx.strokeStyle = "rgba(90,60,30,0.35)"; ctx.lineWidth = 1;
    for (let dy = -1; dy <= e.b; dy++) for (let dx = -1; dx <= e.a; dx++) {
      if (dx >= 0 && dy >= 0 && dx < e.a && dy < e.b) continue;
      const [cx, cy] = this.toScreen(e.x + dx + 0.5, e.y + dy + 0.5);
      for (let k = -1; k <= 1; k++) {
        ctx.beginPath(); ctx.moveTo(cx - this.hw * 0.5, cy - lift + k * this.hh * 0.3); ctx.lineTo(cx + this.hw * 0.5, cy - lift + k * this.hh * 0.3); ctx.stroke();
      }
    }
  }

  productionDot(x, y, color = "#ffd766") {
    const ctx = this.ctx; const k = 0.7 + 0.3 * Math.sin(this.time * 6);
    ctx.fillStyle = color; ctx.globalAlpha = k; ctx.beginPath(); ctx.arc(x, y, Math.max(3, this.hw * 0.12), 0, Math.PI * 2); ctx.fill(); ctx.globalAlpha = 1;
  }

  millBlades(x, y, hw) {
    const ctx = this.ctx; const a = this.time * 1.5;
    ctx.strokeStyle = "#f1e5c6"; ctx.lineWidth = Math.max(1.5, hw * 0.08);
    for (let k = 0; k < 4; k++) {
      const ang = a + k * Math.PI / 2;
      ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + Math.cos(ang) * hw * 0.9, y + Math.sin(ang) * hw * 0.55); ctx.stroke();
    }
  }

  smoke(x, y, seed) {
    const ctx = this.ctx;
    for (let k = 0; k < 3; k++) {
      const t = ((this.time * 0.5 + k / 3 + (seed % 7) / 7) % 1);
      ctx.fillStyle = `rgba(200,200,210,${0.35 * (1 - t)})`;
      ctx.beginPath(); ctx.arc(x + Math.sin(t * 6 + k) * this.hw * 0.15, y - t * this.hh * 2.5, this.hw * (0.08 + t * 0.22), 0, Math.PI * 2); ctx.fill();
    }
  }

  footprintOutline(x, y, w, h, lift, color, width) {
    const ctx = this.ctx;
    const p = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]].map(([px, py]) => this.toScreen(px, py));
    ctx.strokeStyle = color; ctx.lineWidth = width;
    ctx.beginPath(); ctx.moveTo(p[0][0], p[0][1] - lift);
    for (let i = 1; i < 4; i++) ctx.lineTo(p[i][0], p[i][1] - lift);
    ctx.closePath(); ctx.stroke();
  }

  drawNode(e) {
    const ctx = this.ctx, hw = this.hw;
    const lift = this.elevAt(e.x + 0.5, e.y + 0.5) * this.hh * 1.6;
    const sprite = nodeSprite(e.state, e.id % 3, e.a, hw, (e.flags & 8192) !== 0);
    const [sx, sy] = this.toScreen(e.x + e.a / 2, e.y + e.b / 2);
    const scale = hw / BASE;
    const dw = sprite.width * scale, dh = sprite.height * scale;
    const top = sy - lift - dh + (e.a + e.b) * this.hh * 0.5;
    ctx.drawImage(sprite, sx - dw / 2, top, dw, dh);
    this.hitRects.push({ id: e.id, x0: sx - dw * 0.4, y0: top + dh * 0.1, x1: sx + dw * 0.4, y1: top + dh, kind: 3, depth: depthKey(e) });
  }

  drawUnit(e) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    if (e.player < 0 && e.flags & 1) e.flags &= ~1;
    const lift = this.elevAt(e.x, e.y) * hh * 1.6;
    const [sx, syRaw] = this.toScreen(e.x, e.y);
    const sy = syRaw - lift;
    const def = this.defs.units[e.def];
    const moving = (e.flags & (1 << 18)) !== 0;
    const frame = moving ? Math.floor((this.time * 8 + e.id) % 2) : 0;
    const dir = facingBucket(e.facing);
    if (def.tags.includes("tag.animal")) { const s2 = wolfSprite(dir, frame); return this.blitUnit(e, s2, sx, sy, moving); }
    const sprite = unitSprite(def.id, def.tags, e.player, dir, frame, e.state);
    const scale = hw / BASE;
    const dw = sprite.width * scale, dh = sprite.height * scale;
    // shadow
    ctx.fillStyle = "rgba(0,0,0,0.28)";
    ctx.beginPath(); ctx.ellipse(sx, sy, e.a * hw * 1.6, e.a * hh * 1.6, 0, 0, Math.PI * 2); ctx.fill();
    if (e.flags & 1) { ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.beginPath(); ctx.ellipse(sx, sy, e.a * hw * 2.2, e.a * hh * 2.2, 0, 0, Math.PI * 2); ctx.stroke(); }
    const bob = moving ? Math.abs(Math.sin(this.time * 10 + e.id)) * hh * 0.15 : 0;
    ctx.drawImage(sprite, sx - dw / 2, sy - dh + hh * 0.35 - bob, dw, dh);
    this.hitRects.push({ id: e.id, x0: sx - Math.max(dw * 0.45, 14), y0: sy - dh + hh * 0.35, x1: sx + Math.max(dw * 0.45, 14), y1: sy + hh * 0.5, kind: 1, depth: depthKey(e) });
    if (e.flags & 512) {   // cargo
      const res = (e.flags >> 14) & 3;
      ctx.fillStyle = ["#8fe07f", "#a3722f", "#f7d66a"][res] || "#ddd";
      ctx.beginPath(); ctx.arc(sx + dw * 0.35, sy - dh * 0.55, Math.max(2, hw * 0.12), 0, Math.PI * 2); ctx.fill();
    }
    if (e.flags & (1 << 20)) {   // muzzle flash / swing
      const a = e.facing * Math.PI / 180;
      const fx = sx + Math.cos(a) * hw * 0.9, fy = sy - dh * 0.5 - Math.sin(a) * hh * 0.9;
      ctx.fillStyle = "rgba(255,220,120,0.9)"; ctx.beginPath(); ctx.arc(fx, fy, hw * 0.14, 0, Math.PI * 2); ctx.fill();
    }
    if (e.hp >= 0 && (e.hp < 100 || (e.flags & 1))) this.bar(sx, sy - dh - 4, Math.max(18, hw * 0.9), e.hp);
    if (e.state === 0 && (e.flags & 4) && e.player === 0) {   // idle villager marker
      ctx.fillStyle = "rgba(255,255,255,0.8)"; ctx.font = `${Math.max(9, hw * 0.35)}px system-ui`; ctx.textAlign = "center";
      ctx.fillText("z", sx + dw * 0.4, sy - dh - 2 + Math.sin(this.time * 3) * 2);
    }
  }

  blitUnit(e, sprite, sx, sy, moving) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    const scale = hw / BASE, dw = sprite.width * scale, dh = sprite.height * scale;
    ctx.fillStyle = "rgba(0,0,0,0.28)"; ctx.beginPath(); ctx.ellipse(sx, sy, e.a * hw * 1.8, e.a * hh * 1.8, 0, 0, Math.PI * 2); ctx.fill();
    const bob = moving ? Math.abs(Math.sin(this.time * 12 + e.id)) * hh * 0.1 : 0;
    ctx.drawImage(sprite, sx - dw / 2, sy - dh + hh * 0.35 - bob, dw, dh);
    this.hitRects.push({ id: e.id, x0: sx - dw / 2, y0: sy - dh + hh * 0.35, x1: sx + dw / 2, y1: sy + hh * 0.5, kind: 1, depth: depthKey(e) });
    if (e.hp >= 0 && e.hp < 100) this.bar(sx, sy - dh - 4, Math.max(18, hw * 0.9), e.hp);
  }

  drawProjectile(e) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    const lift = this.elevAt(e.x, e.y) * hh * 1.6;
    const [sx, sy] = this.toScreen(e.x, e.y);
    const t = e.state / 100;
    const arc = (e.flags & 128 ? 3.0 : 1.0) * Math.sin(t * Math.PI) * hh;
    const r = (e.flags & 128 ? 0.11 : 0.05) * hw;
    ctx.fillStyle = "rgba(0,0,0,0.25)"; ctx.beginPath(); ctx.ellipse(sx, sy - lift, r * 1.5, r * 0.7, 0, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = e.flags & 128 ? "#222" : "#3a2e22";
    ctx.beginPath(); ctx.arc(sx, sy - lift - hh * 0.9 - arc, Math.max(1.5, r), 0, Math.PI * 2); ctx.fill();
  }

  bar(x, y, w, hp) {
    const ctx = this.ctx;
    ctx.fillStyle = "rgba(0,0,0,0.7)"; ctx.fillRect(x - w / 2, y, w, 4);
    ctx.fillStyle = hp > 50 ? "#4ad66a" : hp > 25 ? "#f2c14e" : "#e5453a"; ctx.fillRect(x - w / 2, y, w * hp / 100, 4);
  }

  // ---------------------------------------------------------------- overlays
  drawGhost(buf) {
    if (!buf[3]) return;
    const x = buf[4] / 64, y = buf[5] / 64, w = buf[6] / 64, h = buf[7] / 64, ok = buf[8] === 1;
    const lift = this.elevAt(x + 0.5, y + 0.5) * this.hh * 1.6;
    const ctx = this.ctx;
    const p = [[x, y], [x + w, y], [x + w, y + h], [x, y + h]].map(([px, py]) => this.toScreen(px, py));
    ctx.beginPath(); ctx.moveTo(p[0][0], p[0][1] - lift); for (let i = 1; i < 4; i++) ctx.lineTo(p[i][0], p[i][1] - lift); ctx.closePath();
    ctx.fillStyle = ok ? "rgba(60,220,90,0.35)" : "rgba(230,70,60,0.35)"; ctx.fill();
    ctx.strokeStyle = ok ? "#6ee38a" : "#ff6a5a"; ctx.lineWidth = 2; ctx.stroke();
    // grid cells inside
    ctx.strokeStyle = ok ? "rgba(110,227,138,0.4)" : "rgba(255,106,90,0.4)"; ctx.lineWidth = 1;
    for (let i = 1; i < w; i++) { const a = this.toScreen(x + i, y), b = this.toScreen(x + i, y + h); ctx.beginPath(); ctx.moveTo(a[0], a[1] - lift); ctx.lineTo(b[0], b[1] - lift); ctx.stroke(); }
    for (let j = 1; j < h; j++) { const a = this.toScreen(x, y + j), b = this.toScreen(x + w, y + j); ctx.beginPath(); ctx.moveTo(a[0], a[1] - lift); ctx.lineTo(b[0], b[1] - lift); ctx.stroke(); }
  }

  drawRally(buf, ents) {
    if (buf[13] < 0) return;
    const rx = buf[13] / 64, ry = buf[14] / 64;
    const [sx, sy] = this.toScreen(rx, ry);
    const ctx = this.ctx, hw = this.hw;
    const sel = ents.find(e => e.kind === 2 && (e.flags & 1));
    if (sel) {
      const [bx, by] = this.toScreen(sel.x + sel.a / 2, sel.y + sel.b / 2);
      ctx.setLineDash([6, 6]); ctx.strokeStyle = "rgba(255,255,255,0.6)"; ctx.lineWidth = 1.5;
      ctx.beginPath(); ctx.moveTo(bx, by); ctx.lineTo(sx, sy); ctx.stroke(); ctx.setLineDash([]);
    }
    // flag
    ctx.strokeStyle = "#f5f5f5"; ctx.lineWidth = 2; ctx.beginPath(); ctx.moveTo(sx, sy); ctx.lineTo(sx, sy - hw * 0.9); ctx.stroke();
    ctx.fillStyle = PLAYER[0]; ctx.beginPath(); ctx.moveTo(sx, sy - hw * 0.9); ctx.lineTo(sx + hw * 0.5, sy - hw * 0.75); ctx.lineTo(sx, sy - hw * 0.6); ctx.closePath(); ctx.fill();
  }

  drawMarkers(dt) {
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    this.markers = this.markers.filter(m => (m.t += dt) < 0.8);
    for (const m of this.markers) {
      const [sx, syRaw] = this.toScreen(m.x, m.y);
      const sy = syRaw - this.elevAt(m.x, m.y) * hh * 1.6;
      const k = m.t / 0.8;
      const color = m.kind === "attack" ? "255,80,60" : m.kind === "gather" ? "250,210,90" : m.kind === "build" ? "110,227,138" : "255,255,255";
      ctx.strokeStyle = `rgba(${color},${1 - k})`; ctx.lineWidth = 2;
      ctx.beginPath(); ctx.ellipse(sx, sy, hw * (1.2 - k * 0.9), hh * (1.2 - k * 0.9), 0, 0, Math.PI * 2); ctx.stroke();
      if (m.kind === "attack") { ctx.beginPath(); ctx.moveTo(sx - hw * 0.3, sy - hh * 0.3); ctx.lineTo(sx + hw * 0.3, sy + hh * 0.3); ctx.moveTo(sx + hw * 0.3, sy - hh * 0.3); ctx.lineTo(sx - hw * 0.3, sy + hh * 0.3); ctx.stroke(); }
    }
  }

  drawEffects(buf, ents, dt) {
    const n = buf[9];
    let o = 16 + buf[0] * 12;
    for (let i = 0; i < n; i++, o += 3) this.effects.push({ kind: buf[o], x: buf[o + 1] / 64, y: buf[o + 2] / 64, t: 0, seed: i * 7 + n });
    this.effects = this.effects.filter(f => (f.t += dt) < 0.7);
    const ctx = this.ctx, hw = this.hw, hh = this.hh;
    for (const f of this.effects) {
      const [sx, syRaw] = this.toScreen(f.x, f.y);
      const sy = syRaw - this.elevAt(f.x, f.y) * hh * 1.6;
      const k = f.t / 0.7;
      if (f.kind === 1) {   // death: dust puff + ring
        for (let p = 0; p < 5; p++) {
          const a = p * 1.3 + f.seed;
          ctx.fillStyle = `rgba(210,190,160,${0.6 * (1 - k)})`;
          ctx.beginPath(); ctx.arc(sx + Math.cos(a) * hw * 0.6 * k, sy - hh * 0.4 - Math.sin(a) * hh * 0.6 * k - k * hh, hw * 0.12 * (1 + k), 0, Math.PI * 2); ctx.fill();
        }
        ctx.strokeStyle = `rgba(255,255,255,${0.7 * (1 - k)})`; ctx.lineWidth = 2;
        ctx.beginPath(); ctx.ellipse(sx, sy, hw * 0.8 * (0.3 + k), hh * 0.8 * (0.3 + k), 0, 0, Math.PI * 2); ctx.stroke();
      } else {              // hit spark
        ctx.strokeStyle = `rgba(255,210,90,${1 - k})`; ctx.lineWidth = 2;
        for (let p = 0; p < 4; p++) {
          const a = p * Math.PI / 2 + f.seed;
          ctx.beginPath(); ctx.moveTo(sx, sy - hh); ctx.lineTo(sx + Math.cos(a) * hw * 0.35 * (0.3 + k), sy - hh + Math.sin(a) * hh * 0.5 * (0.3 + k)); ctx.stroke();
        }
      }
    }
  }

  drawFog() {
    // The fog mask is a W×H image (alpha per cell) warped onto the diamond with bilinear
    // smoothing, which gives soft edges for free. Slightly enlarged so map borders stay dark.
    const ctx = this.ctx;
    ctx.save();
    this.applyMapTransform(ctx);
    ctx.imageSmoothingEnabled = true;
    ctx.drawImage(this.fogCanvas, -0.5, -0.5, this.mapW + 1, this.mapH + 1);
    ctx.restore();
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
  }

  drawPing(buf) {
    if (buf[10] < 0) return;
    const [sx, sy] = this.toScreen(buf[10] / 64, buf[11] / 64);
    const k = (this.time % 1);
    const ctx = this.ctx;
    ctx.strokeStyle = `rgba(255,70,50,${1 - k})`; ctx.lineWidth = 3;
    ctx.beginPath(); ctx.ellipse(sx, sy, (0.8 + k * 2) * this.hw, (0.8 + k * 2) * this.hh, 0, 0, Math.PI * 2); ctx.stroke();
  }

  // ---------------------------------------------------------------- minimap
  drawMinimap(mini, ents, localPlayer) {
    const mctx = mini.getContext("2d");
    const W = this.mapW, H = this.mapH, s = mini.width / W;
    const img = mctx.createImageData(mini.width, mini.height);
    const d = img.data;
    for (let py = 0; py < mini.height; py++) for (let px = 0; px < mini.width; px++) {
      const x = Math.floor(px / s), y = H - 1 - Math.floor(py / s);
      const i = y * W + x;
      const f = this.fog[i];
      let c = f === 0 ? [10, 12, 16] : hexToRgb(TERRAIN[this.terrain[i]].base);
      if (f === 1) c = c.map(v => v * 0.55);
      const o = (py * mini.width + px) * 4;
      d[o] = c[0]; d[o + 1] = c[1]; d[o + 2] = c[2]; d[o + 3] = 255;
    }
    mctx.putImageData(img, 0, 0);
    for (const e of ents) {
      if (e.kind === 4) continue;
      const px = e.x * s, py = (H - e.y) * s;
      if (e.kind === 1) { mctx.fillStyle = e.player === localPlayer ? "#fff" : playerColor(e.player); mctx.fillRect(px - 1, py - 1, 2.5, 2.5); }
      else if (e.kind === 2) { mctx.fillStyle = playerColor(e.player); mctx.fillRect(px, py - e.b * s, Math.max(2, e.a * s), Math.max(2, e.b * s)); }
      else { mctx.fillStyle = e.state === 0 ? "#1e5a28" : e.state === 3 ? "#dcb432" : e.state === 1 ? "#96285a" : e.state === 5 ? "#ffffff" : "#8c5a32"; mctx.fillRect(px, py - e.b * s, Math.max(1, e.a * s), Math.max(1, e.b * s)); }
    }
    // camera outline (the visible diamond)
    const corners = [[0, 0], [innerWidth, 0], [innerWidth, innerHeight], [0, innerHeight]].map(([sx, sy]) => this.toWorld(sx, sy));
    mctx.strokeStyle = "rgba(255,255,255,0.85)"; mctx.lineWidth = 1;
    mctx.beginPath();
    corners.forEach(([x, y], i) => { const px = x * s, py = (H - y) * s; if (i === 0) mctx.moveTo(px, py); else mctx.lineTo(px, py); });
    mctx.closePath(); mctx.stroke();
  }

  /// The entity whose sprite is under a screen point (nearest to the viewer wins; units before buildings).
  hitTest(sx, sy) {
    if (!this.hitRects) return 0;
    let best = null;
    for (const r of this.hitRects) {
      if (sx < r.x0 || sx > r.x1 || sy < r.y0 || sy > r.y1) continue;
      if (!best || r.kind === 1 && best.kind !== 1 || (r.kind === best.kind || best.kind !== 1) && r.depth < best.depth) best = r;
    }
    return best ? best.id : 0;
  }

  /// Entities whose screen position lies inside a screen rect (box selection).
  entitiesInScreenRect(x0, y0, x1, y1) {
    const ids = [];
    if (!this.lastEnts) return ids;
    for (const e of this.lastEnts) {
      if (e.kind !== 1) continue;
      const [sx, sy] = this.toScreen(e.x, e.y);
      const syl = sy - this.elevAt(e.x, e.y) * this.hh * 1.6 - this.hh * 0.6;
      if (sx >= x0 && sx <= x1 && syl >= y0 && syl <= y1) ids.push(e.id);
    }
    return ids;
  }
}

// ==================================================================== parsing / helpers
function parseEntities(buf) {
  const count = buf[0], ents = new Array(count);
  for (let i = 0; i < count; i++) {
    const o = 16 + i * 12;
    ents[i] = { kind: buf[o], id: buf[o + 1], x: buf[o + 2] / 64, y: buf[o + 3] / 64, player: buf[o + 4], def: buf[o + 5],
      a: buf[o + 6] / 64, b: buf[o + 7] / 64, hp: buf[o + 8], flags: buf[o + 9], facing: buf[o + 10], state: buf[o + 11] };
  }
  return ents;
}
function depthKey(e) { return e.kind === 1 || e.kind === 4 ? e.x + e.y : e.x + e.a / 2 + e.y + e.b / 2; }
function hash2(x, y) { let h = (x * 374761393 + y * 668265263) | 0; h = (h ^ (h >> 13)) * 1274126177; return (h ^ (h >> 16)) >>> 0; }
function hexToRgb(hex) { return [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)]; }
function mix(a, b, t) { const A = hexToRgb(a), B = hexToRgb(b); return `rgb(${A[0] + (B[0] - A[0]) * t | 0},${A[1] + (B[1] - A[1]) * t | 0},${A[2] + (B[2] - A[2]) * t | 0})`; }
function shade(hex, k) { const [r, g, b] = hexToRgb(hex); return `rgb(${Math.min(255, r * k) | 0},${Math.min(255, g * k) | 0},${Math.min(255, b * k) | 0})`; }
function facingBucket(deg) { return ((Math.round(deg / 45) % 8) + 8) % 8; }   // 0 east, 2 north, 4 west, 6 south

function offscreen(w, h) { const c = document.createElement("canvas"); c.width = Math.max(1, Math.ceil(w)); c.height = Math.max(1, Math.ceil(h)); return c; }

// ==================================================================== ground texture (square cell space, warped at draw time)
let CELL = 24;   // texture pixels per cell (reduced on big maps to keep the texture under ~7 MP)

function bakeGround(terrain, elev, W, H) {
  CELL = Math.max(12, Math.min(24, Math.floor(2600 / Math.max(W, H))));
  cache.forEach((v, k) => { if (k.startsWith("cell:")) cache.delete(k); });
  const c = offscreen(W * CELL, H * CELL);
  const g = c.getContext("2d");
  const land = (x, y) => x >= 0 && y >= 0 && x < W && y < H && terrain[y * W + x] !== 3;
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = y * W + x, t = terrain[i], v = hash2(x, y) % 6;
    const px = x * CELL, py = (H - 1 - y) * CELL;       // row 0 = north
    g.drawImage(cellTexture(t, v), px, py);
    if (t === 3) {   // shore foam along edges shared with land
      g.strokeStyle = "rgba(255,255,255,0.45)"; g.lineWidth = 2;
      g.beginPath();
      if (land(x, y + 1)) { g.moveTo(px, py + 1); g.lineTo(px + CELL, py + 1); }
      if (land(x, y - 1)) { g.moveTo(px, py + CELL - 1); g.lineTo(px + CELL, py + CELL - 1); }
      if (land(x - 1, y)) { g.moveTo(px + 1, py); g.lineTo(px + 1, py + CELL); }
      if (land(x + 1, y)) { g.moveTo(px + CELL - 1, py); g.lineTo(px + CELL - 1, py + CELL); }
      g.stroke();
      // wet sand halo on the land side
    } else if (t === 2 || t === 0) {
      const nearWater = !land(x + 1, y) || !land(x - 1, y) || !land(x, y + 1) || !land(x, y - 1);
      if (nearWater) { g.fillStyle = "rgba(60,90,140,0.12)"; g.fillRect(px, py, CELL, CELL); }
    }
  }
  return c;
}

function cellTexture(t, v) {
  const key = `cell:${t}:${v}`;
  let c = cache.get(key);
  if (c) return c;
  c = offscreen(CELL, CELL);
  const g = c.getContext("2d");
  const T = TERRAIN[t];
  g.fillStyle = v % 2 ? T.base : T.alt; g.fillRect(0, 0, CELL, CELL);
  const rnd = mulberry(v * 131 + t * 17);
  if (t === 0) {
    g.fillStyle = "rgba(30,80,30,0.35)";
    for (let i = 0; i < 9; i++) { const x = rnd() * CELL, y = rnd() * CELL; g.fillRect(x, y, 2, 3); }
    g.fillStyle = "rgba(160,220,120,0.35)";
    for (let i = 0; i < 5; i++) { g.fillRect(rnd() * CELL, rnd() * CELL, 2, 2); }
    if (v === 3) { g.fillStyle = "#f5e04a"; g.fillRect(CELL * 0.6, CELL * 0.3, 2, 2); }
    if (v === 5) { g.fillStyle = "#f28fb0"; g.fillRect(CELL * 0.25, CELL * 0.65, 2, 2); }
  } else if (t === 1) {
    g.fillStyle = "rgba(60,40,20,0.35)";
    for (let i = 0; i < 6; i++) { g.beginPath(); g.ellipse(rnd() * CELL, rnd() * CELL, 2.2, 1.4, 0, 0, 7); g.fill(); }
    g.fillStyle = "rgba(255,230,190,0.15)";
    for (let i = 0; i < 4; i++) g.fillRect(rnd() * CELL, rnd() * CELL, 2, 1);
  } else if (t === 2) {
    g.fillStyle = "rgba(255,255,255,0.28)";
    for (let i = 0; i < 12; i++) g.fillRect(rnd() * CELL, rnd() * CELL, 1.5, 1.5);
    g.fillStyle = "rgba(120,90,50,0.12)";
    for (let i = 0; i < 3; i++) g.fillRect(rnd() * CELL, rnd() * CELL, 3, 1);
  } else if (t === 3) {
    g.fillStyle = "rgba(20,60,120,0.25)"; g.fillRect(0, 0, CELL, CELL * 0.5);
    g.strokeStyle = "rgba(255,255,255,0.18)"; g.lineWidth = 1;
    for (let i = 0; i < 3; i++) { const y = (i + 0.5) * CELL / 3, x = rnd() * CELL * 0.5; g.beginPath(); g.moveTo(x, y); g.quadraticCurveTo(x + 4, y - 2, x + 8, y); g.stroke(); }
  } else {
    g.strokeStyle = "rgba(0,0,0,0.35)"; g.lineWidth = 1;
    for (let i = 0; i < 5; i++) { const x = rnd() * CELL, y = rnd() * CELL; g.beginPath(); g.moveTo(x, y); g.lineTo(x + (rnd() - 0.5) * 10, y + (rnd() - 0.5) * 8); g.stroke(); }
    g.fillStyle = "rgba(255,255,255,0.08)"; g.fillRect(0, 0, CELL, 2);
  }
  cache.set(key, c);
  return c;
}

// ==================================================================== tile sprites
function tileSprite(t, variant, frame, hw) {
  // Bake per zoom bucket so texture density stays crisp.
  const bucket = Math.round(hw / 8) * 8;
  const key = `tile:${t}:${variant}:${frame}:${bucket}`;
  let c = cache.get(key);
  if (c) return c;
  const w = bucket * 2 + 2, h = bucket + 2;
  c = offscreen(w, h);
  const g = c.getContext("2d");
  const cx = w / 2, cy = h / 2, hw2 = bucket, hh2 = bucket / 2;
  const T = TERRAIN[t];
  g.beginPath(); g.moveTo(cx - hw2 - 1, cy); g.lineTo(cx, cy - hh2 - 1); g.lineTo(cx + hw2 + 1, cy); g.lineTo(cx, cy + hh2 + 1); g.closePath();
  g.fillStyle = variant % 2 ? T.base : T.alt; g.fill();
  g.save(); g.clip();
  const rnd = mulberry(variant * 97 + t * 13 + frame * 31);
  if (t === 0) {   // grass: tufts and the odd flower
    g.strokeStyle = "rgba(40,90,40,0.5)"; g.lineWidth = Math.max(1, bucket * 0.05);
    for (let i = 0; i < 10; i++) { const x = cx + (rnd() - 0.5) * hw2 * 1.4, y = cy + (rnd() - 0.5) * hh2 * 1.4; g.beginPath(); g.moveTo(x, y); g.lineTo(x + (rnd() - 0.5) * 3, y - bucket * 0.12); g.stroke(); }
    if (variant === 3) { g.fillStyle = "#f5e04a"; g.beginPath(); g.arc(cx + hw2 * 0.2, cy - hh2 * 0.1, bucket * 0.05, 0, 7); g.fill(); }
    if (variant === 5) { g.fillStyle = "#f28fb0"; g.beginPath(); g.arc(cx - hw2 * 0.3, cy + hh2 * 0.2, bucket * 0.05, 0, 7); g.fill(); }
  } else if (t === 1) {   // dirt: pebbles
    g.fillStyle = "rgba(60,40,20,0.35)";
    for (let i = 0; i < 6; i++) { g.beginPath(); g.ellipse(cx + (rnd() - 0.5) * hw2 * 1.3, cy + (rnd() - 0.5) * hh2 * 1.3, bucket * 0.06, bucket * 0.035, 0, 0, 7); g.fill(); }
  } else if (t === 2) {   // sand: speckles
    g.fillStyle = "rgba(255,255,255,0.25)";
    for (let i = 0; i < 12; i++) { g.fillRect(cx + (rnd() - 0.5) * hw2 * 1.5, cy + (rnd() - 0.5) * hh2 * 1.5, 1.5, 1.5); }
  } else if (t === 3) {   // water: ripples animated by frame
    g.strokeStyle = "rgba(255,255,255,0.28)"; g.lineWidth = Math.max(1, bucket * 0.04);
    for (let i = 0; i < 4; i++) {
      const y = cy + (i - 1.5) * hh2 * 0.45 + frame * hh2 * 0.15, x = cx + ((i * 37 + frame * 11) % 20) / 20 * hw2 - hw2 * 0.5;
      g.beginPath(); g.moveTo(x - bucket * 0.25, y); g.quadraticCurveTo(x, y - bucket * 0.06, x + bucket * 0.25, y); g.stroke();
    }
    g.fillStyle = "rgba(20,60,120,0.25)"; g.fillRect(0, cy + hh2 * 0.3, w, h);
  } else {                // cliff: rock cracks
    g.strokeStyle = "rgba(0,0,0,0.35)"; g.lineWidth = 1;
    for (let i = 0; i < 5; i++) { const x = cx + (rnd() - 0.5) * hw2, y = cy + (rnd() - 0.5) * hh2; g.beginPath(); g.moveTo(x, y); g.lineTo(x + (rnd() - 0.5) * bucket * 0.5, y + (rnd() - 0.5) * bucket * 0.25); g.stroke(); }
  }
  g.restore();
  // subtle edge
  g.strokeStyle = "rgba(0,0,0,0.08)"; g.lineWidth = 1; g.stroke();
  cache.set(key, c);
  return c;
}

function mulberry(seed) { let a = seed >>> 0; return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

// ==================================================================== node sprites
function nodeSprite(kind, variant, size, hw, depleting) {
  const bucket = Math.round(hw / 8) * 8;
  const key = `node:${kind}:${variant}:${size}:${bucket}:${depleting ? 1 : 0}`;
  let c = cache.get(key);
  if (c) return c;
  const s = bucket / BASE;          // scale factor relative to BASE
  const w = Math.ceil((size + 1) * BASE * 2 * s), h = Math.ceil((size + 2) * BASE * s);
  c = offscreen(w, h);
  const g = c.getContext("2d");
  g.scale(s, s);
  const cx = w / 2 / s, base = h / s - size * BASE * 0.5;
  const rnd = mulberry(variant * 7 + kind);
  if (kind === 0) {           // tree: trunk + three foliage blobs
    const th = BASE * (1.1 + rnd() * 0.3);
    g.fillStyle = "#5a3b21"; g.fillRect(cx - BASE * 0.09, base - th * 0.55, BASE * 0.18, th * 0.55);
    const cols = depleting ? ["#7d8f3a", "#95a54a", "#aab95a"] : ["#2f7d3a", "#3f9a47", "#58b35a"];
    const blobs = [[0, -th * 0.55, 0.42], [-0.22, -th * 0.72, 0.34], [0.2, -th * 0.8, 0.36], [0, -th * 0.98, 0.3]];
    blobs.forEach(([dx, dy, r], i) => { g.fillStyle = cols[Math.min(2, i)]; g.beginPath(); g.arc(cx + dx * BASE, base + dy, r * BASE, 0, 7); g.fill(); });
    g.strokeStyle = "rgba(0,0,0,0.25)"; g.lineWidth = 1.2; g.beginPath(); g.arc(cx, base - th * 0.7, BASE * 0.55, 0, 7); g.stroke();
  } else if (kind === 1) {    // berry bush
    g.fillStyle = "#4c7a2a"; g.beginPath(); g.ellipse(cx, base - BASE * 0.35, BASE * 0.8, BASE * 0.5, 0, 0, 7); g.fill();
    g.fillStyle = "#5f9435"; g.beginPath(); g.ellipse(cx - BASE * 0.2, base - BASE * 0.5, BASE * 0.5, BASE * 0.35, 0, 0, 7); g.fill();
    g.fillStyle = depleting ? "#7a4a5a" : "#c2306b";
    for (let i = 0; i < 7; i++) { g.beginPath(); g.arc(cx + (rnd() - 0.5) * BASE * 1.2, base - BASE * 0.3 - rnd() * BASE * 0.5, BASE * 0.09, 0, 7); g.fill(); }
  } else if (kind === 3) {    // gold mine: rock pile with veins
    g.fillStyle = "#6f6558"; g.beginPath(); g.moveTo(cx - BASE * 1.3, base); g.lineTo(cx - BASE * 0.6, base - BASE * 0.9); g.lineTo(cx + BASE * 0.2, base - BASE * 1.1); g.lineTo(cx + BASE * 1.2, base - BASE * 0.5); g.lineTo(cx + BASE * 1.4, base); g.closePath(); g.fill();
    g.fillStyle = "#8a7f70"; g.beginPath(); g.moveTo(cx - BASE * 0.6, base - BASE * 0.9); g.lineTo(cx + BASE * 0.2, base - BASE * 1.1); g.lineTo(cx + BASE * 0.3, base - BASE * 0.5); g.lineTo(cx - BASE * 0.4, base - BASE * 0.4); g.closePath(); g.fill();
    g.fillStyle = depleting ? "#b8a04a" : "#f2c94c";
    for (let i = 0; i < 6; i++) { g.fillRect(cx + (rnd() - 0.5) * BASE * 1.6, base - rnd() * BASE * 0.8, BASE * 0.14, BASE * 0.1); }
    g.strokeStyle = "rgba(0,0,0,0.3)"; g.lineWidth = 1; g.stroke();
  } else if (kind === 2) {    // deer
    g.fillStyle = "#8a5a32"; g.beginPath(); g.ellipse(cx, base - BASE * 0.38, BASE * 0.42, BASE * 0.22, 0, 0, 7); g.fill();
    g.fillStyle = "#7a4c28"; g.beginPath(); g.ellipse(cx + BASE * 0.38, base - BASE * 0.6, BASE * 0.15, BASE * 0.12, 0, 0, 7); g.fill();
    g.strokeStyle = "#5a3b21"; g.lineWidth = 2;
    for (const dx of [-0.25, -0.1, 0.12, 0.27]) { g.beginPath(); g.moveTo(cx + dx * BASE, base - BASE * 0.2); g.lineTo(cx + dx * BASE, base); g.stroke(); }
    g.beginPath(); g.moveTo(cx + BASE * 0.42, base - BASE * 0.7); g.lineTo(cx + BASE * 0.5, base - BASE * 0.95); g.moveTo(cx + BASE * 0.42, base - BASE * 0.7); g.lineTo(cx + BASE * 0.3, base - BASE * 0.92); g.stroke();
  } else if (kind === 5) {    // treasure chest with a glint
    g.fillStyle = "#6b4426"; g.fillRect(cx - BASE * 0.45, base - BASE * 0.5, BASE * 0.9, BASE * 0.45);
    g.fillStyle = "#8a5a34"; g.beginPath(); g.moveTo(cx - BASE * 0.48, base - BASE * 0.5); g.lineTo(cx + BASE * 0.48, base - BASE * 0.5); g.lineTo(cx + BASE * 0.4, base - BASE * 0.7); g.lineTo(cx - BASE * 0.4, base - BASE * 0.7); g.closePath(); g.fill();
    g.fillStyle = "#f2c94c"; g.fillRect(cx - BASE * 0.06, base - BASE * 0.5, BASE * 0.12, BASE * 0.14); g.fillRect(cx - BASE * 0.45, base - BASE * 0.32, BASE * 0.9, BASE * 0.05);
    g.fillStyle = "#fff8c0"; g.beginPath(); g.arc(cx + BASE * 0.3, base - BASE * 0.75, BASE * 0.06, 0, 7); g.fill();
    g.strokeStyle = "rgba(0,0,0,0.4)"; g.lineWidth = 1; g.strokeRect(cx - BASE * 0.45, base - BASE * 0.5, BASE * 0.9, BASE * 0.45);
  } else {                    // farm plot
    g.fillStyle = "#9a7a3a"; g.fillRect(cx - BASE, base - BASE * 0.5, BASE * 2, BASE * 0.5);
  }
  cache.set(key, c);
  return c;
}

// ==================================================================== building sprites
function buildingSprite(id, w, h, player, hw) {
  const bucket = Math.round(hw / 8) * 8;
  const key = `bld:${id}:${w}:${h}:${player}:${bucket}`;
  let c = cache.get(key);
  if (c) return c;
  const s = bucket / BASE;
  const HW = BASE, HH = BASE / 2;
  const wall = wallHeight(id, w, h) * BASE;
  const roofExtra = BASE * 1.6;
  const cw = (w + h) * HW, ch = (w + h) * HH + wall + roofExtra;
  c = offscreen(cw * s, ch * s);
  const g = c.getContext("2d");
  g.scale(s, s);
  const pi = ((player % 4) + 4) % 4;
  const col = PLAYER[pi], dark = PLAYER_DARK[pi], light = PLAYER_LIGHT[pi];
  // Diamond corners in sprite space: west corner (x, y+h) at left middle-bottom; near corner (x, y) at bottom center-left...
  // Sprite origin: the footprint's four corners.
  const P = (x, y) => [(x - y) * HW + h * HW, ch - (x + y) * HH];   // (0,0) near corner bottom, x right-up, y left-up
  const near = P(0, 0), east = P(w, 0), far = P(w, h), west = P(0, h);
  const top = (p) => [p[0], p[1] - wall];
  // walls: west face (near→west) darker, south/east face (near→east) lighter
  const wallCol = { west: "#b9a58b", east: "#d6c4a8" };
  const styleFor = buildingStyle(id);
  g.fillStyle = styleFor.westWall; g.beginPath(); g.moveTo(...near); g.lineTo(...west); g.lineTo(...top(west)); g.lineTo(...top(near)); g.closePath(); g.fill();
  g.fillStyle = styleFor.eastWall; g.beginPath(); g.moveTo(...near); g.lineTo(...east); g.lineTo(...top(east)); g.lineTo(...top(near)); g.closePath(); g.fill();
  g.strokeStyle = "rgba(0,0,0,0.35)"; g.lineWidth = 1.2;
  g.beginPath(); g.moveTo(...near); g.lineTo(...top(near)); g.stroke();
  // roof
  drawRoof(g, id, [top(near), top(east), top(far), top(west)], w, h, HW, HH, col, dark, light, styleFor);
  // details: door on the east face, windows
  const doorX = (near[0] + east[0]) / 2, doorY = (near[1] + east[1]) / 2;
  g.fillStyle = "#3b2a1a"; g.beginPath(); g.moveTo(doorX - HW * 0.18, doorY - HH * 0.18); g.lineTo(doorX + HW * 0.18, doorY - HH * 0.18 - HH * 0.36); g.lineTo(doorX + HW * 0.18, doorY - HH * 0.18 - HH * 0.36 - wall * 0.45); g.lineTo(doorX - HW * 0.18, doorY - HH * 0.18 - wall * 0.45); g.closePath(); g.fill();
  if (w >= 3) {
    g.fillStyle = "#2b2f3a";
    for (let i = 1; i < w; i++) { const p = P(i, 0); g.fillRect(p[0] - 3, p[1] - wall * 0.75, 6, wall * 0.22); }
    for (let j = 1; j < h; j++) { const p = P(0, j); g.fillRect(p[0] - 3, p[1] - wall * 0.75, 6, wall * 0.22); }
  }
  // outline
  g.strokeStyle = "rgba(0,0,0,0.45)"; g.lineWidth = 1.2;
  g.beginPath(); g.moveTo(...west); g.lineTo(...near); g.lineTo(...east); g.stroke();
  cache.set(key, c);
  return c;
}

function wallHeight(id, w, h) {
  switch (id) {
    case "bld.lumbercamp": case "bld.miningcamp": return 0.7;
    case "bld.towncenter": return 1.6;
    case "bld.tower": return 2.6;
    case "bld.house": return 0.9;
    case "bld.mill": return 1.0;
    case "bld.market": return 0.9;
    case "bld.foundry": return 1.3;
    default: return 1.2;
  }
}

function buildingStyle(id) {
  switch (id) {
    case "bld.towncenter": return { westWall: "#b79b78", eastWall: "#d9bf9c", roof: "stone" };
    case "bld.house": return { westWall: "#c4a880", eastWall: "#e2c9a3", roof: "thatch" };
    case "bld.mill": return { westWall: "#a88f6f", eastWall: "#c9ae8a", roof: "thatch" };
    case "bld.tower": return { westWall: "#8b8a86", eastWall: "#aaa8a2", roof: "battlement" };
    case "bld.barracks": return { westWall: "#8f7a63", eastWall: "#ad967b", roof: "tile" };
    case "bld.stable": return { westWall: "#9c7b55", eastWall: "#bd9a70", roof: "thatch" };
    case "bld.foundry": return { westWall: "#7d7570", eastWall: "#9a928c", roof: "tile" };
    case "bld.market": return { westWall: "#b7a488", eastWall: "#d8c7a6", roof: "awning" };
    case "bld.lumbercamp": return { westWall: "#8a6a44", eastWall: "#a8865a", roof: "thatch" };
    case "bld.miningcamp": return { westWall: "#8a8073", eastWall: "#a89c8a", roof: "tile" };
    default: return { westWall: "#b0a08a", eastWall: "#cfbea4", roof: "tile" };
  }
}

function drawRoof(g, id, quad, w, h, HW, HH, col, dark, light, style) {
  const [n, e, f, wst] = quad;
  const cx = (n[0] + f[0]) / 2, cy = (n[1] + f[1]) / 2;
  if (style.roof === "battlement") {
    g.fillStyle = "#9d9b95"; poly(g, quad); g.fill();
    g.fillStyle = "#6c6a66";
    for (let i = 0; i < 4; i++) { const a = quad[i], b = quad[(i + 1) % 4]; for (let k = 0.1; k < 1; k += 0.3) { g.fillRect(a[0] + (b[0] - a[0]) * k - 2, a[1] + (b[1] - a[1]) * k - 6, 4, 6); } }
    g.fillStyle = col; g.fillRect(cx - 1, cy - HH * 2.2, 2, HH * 2.2); g.beginPath(); g.moveTo(cx + 1, cy - HH * 2.2); g.lineTo(cx + HW * 0.6, cy - HH * 1.9); g.lineTo(cx + 1, cy - HH * 1.6); g.fill();
    return;
  }
  if (style.roof === "awning") {
    g.fillStyle = "#e9dcc2"; poly(g, quad); g.fill();
    g.fillStyle = col;
    for (let k = 0; k < 6; k += 2) { g.beginPath(); g.moveTo(n[0] + (e[0] - n[0]) * k / 6, n[1] + (e[1] - n[1]) * k / 6); g.lineTo(n[0] + (e[0] - n[0]) * (k + 1) / 6, n[1] + (e[1] - n[1]) * (k + 1) / 6); g.lineTo(wst[0] + (f[0] - wst[0]) * (k + 1) / 6, wst[1] + (f[1] - wst[1]) * (k + 1) / 6); g.lineTo(wst[0] + (f[0] - wst[0]) * k / 6, wst[1] + (f[1] - wst[1]) * k / 6); g.closePath(); g.fill(); }
    g.strokeStyle = "rgba(0,0,0,0.3)"; poly(g, quad); g.stroke();
    return;
  }
  // pitched roof: ridge from west-mid to east-mid, raised
  const ridgeH = HH * Math.min(2.4, (w + h) * 0.45) * (style.roof === "stone" ? 0.55 : 1);
  const ridgeA = [(wst[0] + n[0]) / 2, (wst[1] + n[1]) / 2 - ridgeH];
  const ridgeB = [(e[0] + f[0]) / 2, (e[1] + f[1]) / 2 - ridgeH];
  const roofA = style.roof === "thatch" ? "#c9a25a" : style.roof === "stone" ? "#8e8ea6" : shade(col, 0.95);
  const roofB = style.roof === "thatch" ? "#a8843f" : style.roof === "stone" ? "#6f6f8c" : shade(col, 0.7);
  // near slope (facing viewer): n → e → ridgeB → ridgeA
  g.fillStyle = roofA; g.beginPath(); g.moveTo(...n); g.lineTo(...e); g.lineTo(...ridgeB); g.lineTo(...ridgeA); g.closePath(); g.fill();
  // far slope: wst → f → ridgeB → ridgeA
  g.fillStyle = roofB; g.beginPath(); g.moveTo(...wst); g.lineTo(...f); g.lineTo(...ridgeB); g.lineTo(...ridgeA); g.closePath(); g.fill();
  // gable at west end
  g.fillStyle = style.westWall; g.beginPath(); g.moveTo(...n); g.lineTo(...wst); g.lineTo(...ridgeA); g.closePath(); g.fill();
  g.strokeStyle = "rgba(0,0,0,0.35)"; g.lineWidth = 1.2;
  g.beginPath(); g.moveTo(...ridgeA); g.lineTo(...ridgeB); g.stroke();
  g.beginPath(); g.moveTo(...n); g.lineTo(...e); g.lineTo(...ridgeB); g.stroke();
  g.beginPath(); g.moveTo(...n); g.lineTo(...ridgeA); g.lineTo(...wst); g.stroke();
  if (style.roof === "thatch") { g.strokeStyle = "rgba(90,60,20,0.35)"; for (let k = 0.2; k < 1; k += 0.2) { g.beginPath(); g.moveTo(n[0] + (e[0] - n[0]) * k, n[1] + (e[1] - n[1]) * k); g.lineTo(ridgeA[0] + (ridgeB[0] - ridgeA[0]) * k, ridgeA[1] + (ridgeB[1] - ridgeA[1]) * k); g.stroke(); } }
  // banner with player colour on the near slope
  g.fillStyle = col; const bx = (n[0] + e[0]) / 2, by = (n[1] + e[1]) / 2 - ridgeH * 0.35;
  g.beginPath(); g.moveTo(bx - HW * 0.18, by - HH * 0.6); g.lineTo(bx + HW * 0.18, by - HH * 0.75); g.lineTo(bx + HW * 0.18, by + HH * 0.05); g.lineTo(bx, by + HH * 0.2); g.lineTo(bx - HW * 0.18, by + HH * 0.05); g.closePath(); g.fill();
  if (id === "bld.towncenter") {   // tower with flag on the ridge
    const tx = (ridgeA[0] + ridgeB[0]) / 2, ty = (ridgeA[1] + ridgeB[1]) / 2;
    g.fillStyle = "#a89c8a"; g.fillRect(tx - HW * 0.35, ty - HH * 2.4, HW * 0.7, HH * 2.4);
    g.fillStyle = "#6b6178"; g.beginPath(); g.moveTo(tx - HW * 0.45, ty - HH * 2.4); g.lineTo(tx, ty - HH * 3.4); g.lineTo(tx + HW * 0.45, ty - HH * 2.4); g.closePath(); g.fill();
    g.strokeStyle = "#333"; g.lineWidth = 1.5; g.beginPath(); g.moveTo(tx, ty - HH * 3.4); g.lineTo(tx, ty - HH * 4.6); g.stroke();
    g.fillStyle = col; g.beginPath(); g.moveTo(tx, ty - HH * 4.6); g.lineTo(tx + HW * 0.6, ty - HH * 4.35); g.lineTo(tx, ty - HH * 4.05); g.closePath(); g.fill();
  }
  if (id === "bld.foundry") {   // chimney
    const tx = ridgeB[0] - HW * 0.5, ty = ridgeB[1];
    g.fillStyle = "#4a4442"; g.fillRect(tx - HW * 0.18, ty - HH * 2.6, HW * 0.36, HH * 2.6);
  }
  if (id === "bld.stable") {   // hay
    g.fillStyle = "#e0c060"; g.beginPath(); g.arc(wst[0] + HW * 0.6, wst[1] - HH * 0.3, HW * 0.35, 0, 7); g.fill();
  }
}

function poly(g, pts) { g.beginPath(); g.moveTo(...pts[0]); for (let i = 1; i < pts.length; i++) g.lineTo(...pts[i]); g.closePath(); }

// ==================================================================== unit sprites
function unitSprite(id, tags, player, dir, frame, state) {
  const key = `unit:${id}:${player}:${dir}:${frame}:${state === 5 ? 1 : 0}`;
  let c = cache.get(key);
  if (c) return c;
  const cavalry = tags.includes("tag.cavalry"), artillery = tags.includes("tag.artillery");
  const W = artillery ? BASE * 2.2 : cavalry ? BASE * 2.0 : BASE * 1.3, H = artillery ? BASE * 1.6 : cavalry ? BASE * 1.9 : BASE * 1.9;
  c = offscreen(W, H);
  const g = c.getContext("2d");
  const pi = ((player % 4) + 4) % 4;
  const col = player < 0 ? WILD : PLAYER[pi], dark = player < 0 ? "#3d4048" : PLAYER_DARK[pi];
  const cx = W / 2, base = H - 2;
  const left = dir > 2 && dir < 6;                 // facing west-ish → mirror
  g.translate(cx, 0); if (left) g.scale(-1, 1); g.translate(-cx, 0);
  const walk = frame === 1 ? 1 : -1;
  const skin = "#f1c9a5";
  if (artillery) {
    drawCannon(g, cx, base, col, dark, id === "unit.mortar");
  } else if (cavalry) {
    drawHorse(g, cx, base, col, dark, walk);
    drawRider(g, cx + BASE * 0.05, base - BASE * 0.55, col, dark, skin, id, tags);
  } else {
    drawFigure(g, cx, base, col, dark, skin, id, tags, walk, state === 5);
  }
  cache.set(key, c);
  return c;
}

function drawFigure(g, cx, base, col, dark, skin, id, tags, walk, attacking) {
  const villager = id === "unit.villager";
  // legs
  g.strokeStyle = "#3a2e26"; g.lineWidth = 3.2; g.lineCap = "round";
  g.beginPath(); g.moveTo(cx - 3, base - BASE * 0.42); g.lineTo(cx - 3 - walk * 2.5, base); g.moveTo(cx + 3, base - BASE * 0.42); g.lineTo(cx + 3 + walk * 2.5, base); g.stroke();
  // body
  g.fillStyle = villager ? "#c8b49a" : col;
  roundRect(g, cx - BASE * 0.24, base - BASE * 0.95, BASE * 0.48, BASE * 0.56, 4); g.fill();
  g.fillStyle = villager ? col : dark; g.fillRect(cx - BASE * 0.24, base - BASE * 0.55, BASE * 0.48, BASE * 0.13);   // belt / sash
  // head
  g.fillStyle = skin; g.beginPath(); g.arc(cx, base - BASE * 1.12, BASE * 0.19, 0, 7); g.fill();
  // hat by type
  g.fillStyle = "#2b2b33";
  if (villager) { g.fillStyle = "#caa562"; g.beginPath(); g.ellipse(cx, base - BASE * 1.26, BASE * 0.34, BASE * 0.09, 0, 0, 7); g.fill(); g.fillRect(cx - BASE * 0.14, base - BASE * 1.4, BASE * 0.28, BASE * 0.14); }
  else if (id === "unit.pikeman" || id === "unit.jaguar") { g.fillStyle = id === "unit.jaguar" ? "#d9a441" : "#8f9498"; g.beginPath(); g.arc(cx, base - BASE * 1.16, BASE * 0.21, Math.PI, 0); g.fill(); }
  else if (id === "unit.crossbowman" || id === "unit.skirmisher") { g.fillStyle = "#4b5a3a"; g.beginPath(); g.arc(cx, base - BASE * 1.16, BASE * 0.21, Math.PI, 0); g.fill(); g.fillRect(cx - BASE * 0.3, base - BASE * 1.18, BASE * 0.6, BASE * 0.05); }
  else { g.fillStyle = id === "unit.redcoat" ? "#1f1f24" : "#2b2b33"; g.beginPath(); g.moveTo(cx - BASE * 0.34, base - BASE * 1.2); g.lineTo(cx + BASE * 0.34, base - BASE * 1.2); g.lineTo(cx + BASE * 0.2, base - BASE * 1.42); g.lineTo(cx - BASE * 0.2, base - BASE * 1.42); g.closePath(); g.fill(); }
  // weapon / tool
  g.strokeStyle = "#3a2e26"; g.lineWidth = 2.4;
  const lean = attacking ? 0.15 : 0;
  if (villager) { g.beginPath(); g.moveTo(cx + BASE * 0.28, base - BASE * 0.7); g.lineTo(cx + BASE * 0.5, base - BASE * 1.05); g.stroke(); g.fillStyle = "#8f8f96"; g.fillRect(cx + BASE * 0.42, base - BASE * 1.15, BASE * 0.16, BASE * 0.12); }
  else if (id === "unit.pikeman") { g.beginPath(); g.moveTo(cx + BASE * 0.3, base - BASE * 0.2); g.lineTo(cx + BASE * 0.45, base - BASE * 1.75); g.stroke(); g.fillStyle = "#c9ccd1"; g.beginPath(); g.moveTo(cx + BASE * 0.4, base - BASE * 1.72); g.lineTo(cx + BASE * 0.48, base - BASE * 1.92); g.lineTo(cx + BASE * 0.55, base - BASE * 1.72); g.fill(); }
  else if (id === "unit.jaguar") { g.beginPath(); g.moveTo(cx + BASE * 0.28, base - BASE * 0.75); g.lineTo(cx + BASE * 0.6, base - BASE * 1.15 - lean * BASE); g.stroke(); g.fillStyle = "#333"; g.fillRect(cx + BASE * 0.5, base - BASE * 1.3, BASE * 0.14, BASE * 0.3); }
  else if (id === "unit.crossbowman") { g.strokeStyle = "#5a3b21"; g.beginPath(); g.moveTo(cx + BASE * 0.28, base - BASE * 0.95); g.lineTo(cx + BASE * 0.7, base - BASE * 0.95); g.stroke(); g.strokeStyle = "#ddd"; g.lineWidth = 1.5; g.beginPath(); g.arc(cx + BASE * 0.6, base - BASE * 0.95, BASE * 0.22, -1.3, 1.3); g.stroke(); }
  else { g.strokeStyle = "#4a3a2c"; g.lineWidth = 3; g.beginPath(); g.moveTo(cx + BASE * 0.22, base - BASE * 0.78); g.lineTo(cx + BASE * 0.78, base - BASE * 1.0 - lean * BASE); g.stroke(); g.strokeStyle = "#8f8f96"; g.lineWidth = 2; g.beginPath(); g.moveTo(cx + BASE * 0.55, base - BASE * 0.91 - lean * BASE * 0.6); g.lineTo(cx + BASE * 0.82, base - BASE * 1.02 - lean * BASE); g.stroke(); }
  // outline
  g.strokeStyle = "rgba(0,0,0,0.4)"; g.lineWidth = 1; roundRect(g, cx - BASE * 0.24, base - BASE * 0.95, BASE * 0.48, BASE * 0.56, 4); g.stroke();
}

function drawHorse(g, cx, base, col, dark, walk) {
  g.fillStyle = "#6b4a2b";
  g.beginPath(); g.ellipse(cx, base - BASE * 0.55, BASE * 0.62, BASE * 0.3, 0, 0, 7); g.fill();
  // neck + head
  g.beginPath(); g.moveTo(cx + BASE * 0.45, base - BASE * 0.7); g.lineTo(cx + BASE * 0.8, base - BASE * 1.05); g.lineTo(cx + BASE * 0.95, base - BASE * 0.95); g.lineTo(cx + BASE * 0.6, base - BASE * 0.5); g.closePath(); g.fill();
  g.beginPath(); g.ellipse(cx + BASE * 0.92, base - BASE * 0.98, BASE * 0.17, BASE * 0.11, 0.3, 0, 7); g.fill();
  // legs
  g.strokeStyle = "#5a3b21"; g.lineWidth = 3; g.lineCap = "round";
  for (const [dx, ph] of [[-0.42, 1], [-0.25, -1], [0.25, -1], [0.42, 1]]) { g.beginPath(); g.moveTo(cx + dx * BASE, base - BASE * 0.4); g.lineTo(cx + dx * BASE + ph * walk * 3, base); g.stroke(); }
  // saddle in player colour, tail
  g.fillStyle = col; g.fillRect(cx - BASE * 0.2, base - BASE * 0.82, BASE * 0.4, BASE * 0.14);
  g.strokeStyle = "#3a2510"; g.lineWidth = 2.5; g.beginPath(); g.moveTo(cx - BASE * 0.6, base - BASE * 0.65); g.lineTo(cx - BASE * 0.85, base - BASE * 0.3); g.stroke();
}

function drawRider(g, cx, base, col, dark, skin, id, tags) {
  g.fillStyle = col; roundRect(g, cx - BASE * 0.18, base - BASE * 0.55, BASE * 0.36, BASE * 0.45, 3); g.fill();
  g.fillStyle = skin; g.beginPath(); g.arc(cx, base - BASE * 0.68, BASE * 0.15, 0, 7); g.fill();
  g.fillStyle = id === "unit.uhlan" ? "#1f1f24" : "#2b2b33"; g.beginPath(); g.arc(cx, base - BASE * 0.72, BASE * 0.17, Math.PI, 0); g.fill();
  g.strokeStyle = "#8f8f96"; g.lineWidth = 2.2; g.lineCap = "round";
  if (id === "unit.dragoon") { g.beginPath(); g.moveTo(cx + BASE * 0.15, base - BASE * 0.45); g.lineTo(cx + BASE * 0.6, base - BASE * 0.6); g.stroke(); }
  else { g.beginPath(); g.moveTo(cx + BASE * 0.15, base - BASE * 0.35); g.lineTo(cx + BASE * 0.55, base - BASE * 0.95); g.stroke(); }
}

function drawCannon(g, cx, base, col, dark, mortar) {
  g.fillStyle = "#5a3b21";
  for (const dx of [-0.45, 0.35]) { g.beginPath(); g.arc(cx + dx * BASE, base - BASE * 0.3, BASE * 0.3, 0, 7); g.fill(); g.strokeStyle = "#3a2510"; g.lineWidth = 2; g.stroke(); g.fillStyle = "#5a3b21"; }
  g.fillStyle = "#7a5a3a"; g.fillRect(cx - BASE * 0.5, base - BASE * 0.55, BASE, BASE * 0.2);
  g.fillStyle = "#2f3136";
  if (mortar) { g.beginPath(); g.moveTo(cx - BASE * 0.25, base - BASE * 0.55); g.lineTo(cx + BASE * 0.25, base - BASE * 0.55); g.lineTo(cx + BASE * 0.45, base - BASE * 1.15); g.lineTo(cx - BASE * 0.05, base - BASE * 1.15); g.closePath(); g.fill(); }
  else { g.beginPath(); g.moveTo(cx - BASE * 0.3, base - BASE * 0.55); g.lineTo(cx - BASE * 0.3, base - BASE * 0.8); g.lineTo(cx + BASE * 0.95, base - BASE * 1.0); g.lineTo(cx + BASE * 0.95, base - BASE * 0.78); g.closePath(); g.fill(); }
  g.fillStyle = col; g.fillRect(cx - BASE * 0.15, base - BASE * 0.7, BASE * 0.3, BASE * 0.12);
}

function wolfSprite(dir, frame) {
  const key = `wolf:${dir}:${frame}`;
  let c = cache.get(key);
  if (c) return c;
  const W = BASE * 1.6, H = BASE * 1.1;
  c = offscreen(W, H);
  const g = c.getContext("2d");
  const cx = W / 2, base = H - 2;
  const left = dir > 2 && dir < 6;
  g.translate(cx, 0); if (left) g.scale(-1, 1); g.translate(-cx, 0);
  const walk = frame === 1 ? 1 : -1;
  g.fillStyle = "#6b6f78";
  g.beginPath(); g.ellipse(cx, base - BASE * 0.4, BASE * 0.5, BASE * 0.22, 0, 0, 7); g.fill();
  g.beginPath(); g.ellipse(cx + BASE * 0.5, base - BASE * 0.55, BASE * 0.2, BASE * 0.14, 0.2, 0, 7); g.fill();
  g.fillStyle = "#4b4f57"; g.beginPath(); g.moveTo(cx + BASE * 0.45, base - BASE * 0.66); g.lineTo(cx + BASE * 0.52, base - BASE * 0.85); g.lineTo(cx + BASE * 0.6, base - BASE * 0.64); g.fill();
  g.strokeStyle = "#4b4f57"; g.lineWidth = 2.4; g.lineCap = "round";
  for (const [dx, ph] of [[-0.35, 1], [-0.2, -1], [0.2, -1], [0.35, 1]]) { g.beginPath(); g.moveTo(cx + dx * BASE, base - BASE * 0.3); g.lineTo(cx + dx * BASE + ph * walk * 2.5, base); g.stroke(); }
  g.beginPath(); g.moveTo(cx - BASE * 0.5, base - BASE * 0.45); g.lineTo(cx - BASE * 0.78, base - BASE * 0.3); g.stroke();
  g.fillStyle = "#f5e6c8"; g.beginPath(); g.arc(cx + BASE * 0.58, base - BASE * 0.56, 1.6, 0, 7); g.fill();
  cache.set(key, c);
  return c;
}

function roundRect(g, x, y, w, h, r) { g.beginPath(); g.moveTo(x + r, y); g.lineTo(x + w - r, y); g.quadraticCurveTo(x + w, y, x + w, y + r); g.lineTo(x + w, y + h - r); g.quadraticCurveTo(x + w, y + h, x + w - r, y + h); g.lineTo(x + r, y + h); g.quadraticCurveTo(x, y + h, x, y + h - r); g.lineTo(x, y + r); g.quadraticCurveTo(x, y, x + r, y); g.closePath(); }

// ==================================================================== icons for the HUD
export function iconFor(key, defs) {
  const k = "icon:" + key;
  if (cache.has(k)) return cache.get(k);
  const c = offscreen(40, 40);
  const g = c.getContext("2d");
  const [type, id] = key.split(":");
  if (type === "unit") {
    const u = defs.units.find(x => x.id === id);
    const s = unitSprite(id, u ? u.tags : [], 0, 0, 0, 0);
    const k2 = 36 / s.height; g.drawImage(s, 20 - s.width * k2 / 2, 2, s.width * k2, s.height * k2);
  } else if (type === "bld") {
    const b = defs.buildings.find(x => x.id === id);
    const s = buildingSprite(id, b ? b.w : 2, b ? b.h : 2, 0, BASE);
    const k2 = Math.min(38 / s.width, 38 / s.height); g.drawImage(s, 20 - s.width * k2 / 2, 20 - s.height * k2 / 2, s.width * k2, s.height * k2);
  } else {
    g.strokeStyle = "#fff"; g.fillStyle = "#fff"; g.lineWidth = 3; g.lineCap = "round";
    switch (type) {
      case "attack": g.beginPath(); g.moveTo(8, 32); g.lineTo(30, 10); g.moveTo(24, 10); g.lineTo(30, 10); g.lineTo(30, 16); g.stroke(); g.beginPath(); g.moveTo(10, 24); g.lineTo(16, 30); g.stroke(); break;
      case "stop": g.beginPath(); g.rect(11, 11, 18, 18); g.fill(); break;
      case "x": g.beginPath(); g.moveTo(11, 11); g.lineTo(29, 29); g.moveTo(29, 11); g.lineTo(11, 29); g.stroke(); break;
      case "rally": g.beginPath(); g.moveTo(12, 34); g.lineTo(12, 8); g.stroke(); g.beginPath(); g.moveTo(12, 8); g.lineTo(30, 13); g.lineTo(12, 18); g.fill(); break;
      case "tech": g.beginPath(); g.arc(20, 16, 8, 0, 7); g.stroke(); g.fillRect(16, 26, 8, 8); break;
      case "age": g.beginPath(); g.moveTo(8, 32); g.lineTo(20, 8); g.lineTo(32, 32); g.closePath(); g.stroke(); break;
      case "stance-a": g.beginPath(); g.moveTo(10, 28); g.lineTo(20, 10); g.lineTo(30, 28); g.stroke(); break;
      case "stance-d": g.beginPath(); g.moveTo(20, 8); g.lineTo(32, 13); g.lineTo(28, 30); g.lineTo(20, 34); g.lineTo(12, 30); g.lineTo(8, 13); g.closePath(); g.stroke(); break;
      case "stance-s": g.beginPath(); g.arc(20, 20, 9, 0, 7); g.stroke(); g.beginPath(); g.moveTo(20, 6); g.lineTo(20, 34); g.moveTo(6, 20); g.lineTo(34, 20); g.stroke(); break;
      case "stance-p": g.beginPath(); g.arc(20, 20, 10, 0, 7); g.stroke(); g.beginPath(); g.moveTo(12, 28); g.lineTo(28, 12); g.stroke(); break;
      default: g.beginPath(); g.arc(20, 20, 8, 0, 7); g.fill();
    }
  }
  const url = c.toDataURL();
  cache.set(k, url);
  return url;
}
