// Cube Radar web UI: renders the state pushed by `cuberadar radar` over /ws. Coordinates are game units (cm).
"use strict";

function loadI18n() {
  if (globalThis.I18N) return globalThis.I18N;
  try {
    const xhr = new XMLHttpRequest();
    xhr.open("GET", "i18n.json", false);
    xhr.send();
    if (xhr.responseText) return JSON.parse(xhr.responseText);
  } catch { /* the page is served over HTTP; tests inject I18N instead */ }
  return { ru: {}, en: {} };
}
const I18N = loadI18n();
let lang = localStorage.getItem("lang") || "ru";
if (!I18N[lang]) lang = "ru";

function t(key, ...args) {
  const pack = I18N[lang] || {};
  const fallback = I18N.en || {};
  let s = pack[key] ?? fallback[key] ?? key;
  args.forEach((a, i) => { s = s.split("{" + i + "}").join(String(a)); });
  return s;
}

const SEND_RADIUS = 10000; // measured send radius for cubes (docs/FINDINGS.md B8), planar units
const LEVEL_M = 3;         // |height difference| up to this counts as "same height"
const STRIP = 64;          // width of the height strip on the right of the radar (px)
const STATUS_KEY = {
  Visible: "status.visible",
  OutOfView: "status.out",
  Opening: "status.opening",
  Opened: "status.opened",
  Gone: "status.gone",
};
const COLORS = {};
for (const k of ["visible", "out", "opening", "gone", "me", "spot", "hist", "text", "muted", "line", "enemy"])
  COLORS[k] = getComputedStyle(document.documentElement).getPropertyValue("--" + k).trim();

const ui = {
  mode: localStorage.getItem("mode") || "radar",
  range: Number(localStorage.getItem("range") || 200),
  basemap: localStorage.getItem("basemap") || "auto",
  align: false,
  sound: false,
  enemies: localStorage.getItem("enemies") !== "0",
  notify: localStorage.getItem("notify") === "1" && "Notification" in window && Notification.permission === "granted",
};
let ZONE_MAPS = [];
const tileCache = new Map();
let msg = null;            // last pushed state
let frame = null;          // last radar projection, so a click can be turned back into world centimeters
let receivedAt = 0;
let spots = [];            // possible spots of the current zone
let history = [];          // cube positions seen in earlier sessions
let spotsZone = null;
const known = new Set();   // cubes already announced
let firstUpdate = true;
let audio = null;

// ------------------------------------------------------------------ connection

function connect() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = (e) => {
    msg = JSON.parse(e.data);
    receivedAt = Date.now();
    onState();
  };
  ws.onclose = () => setTimeout(connect, 2000);
}

function onState() {
  if (msg.lang && I18N[msg.lang] && msg.lang !== lang) {
    lang = msg.lang;
    localStorage.setItem("lang", lang);
    applyStatic();
  }
  const r = msg.radar;
  const fresh = [];
  for (const c of r.cubes) {
    const key = `${c.objectId}@${c.firstSeenMs}`;
    if (!known.has(key)) {
      known.add(key);
      if (!firstUpdate && c.status === "Visible") fresh.push(c);
    }
  }
  firstUpdate = false;
  if (fresh.length) announce(fresh);
  if (r.zone && r.zone !== spotsZone) loadZone(r.zone);
  const sel = document.getElementById("north");
  if (document.activeElement !== sel) sel.value = r.orientation.north;
  const base = document.getElementById("basemap");
  if (base && document.activeElement !== base) base.value = ui.basemap;
  const faction = document.getElementById("faction");
  if (document.activeElement !== faction) faction.value = r.factionSetting || "Auto";
  renderHeader();
  renderList(fresh);
  renderEnemies();
  notePosition(ownSample());
  draw();
}

async function loadZone(zone) {
  spotsZone = zone;
  try {
    spots = await (await fetch(`/api/spots?zone=${encodeURIComponent(zone)}`)).json();
    history = await (await fetch(`/api/history?zone=${encodeURIComponent(zone)}`)).json();
  } catch { /* optional data */ }
}
setInterval(() => spotsZone && loadZone(spotsZone), 60000);

// ------------------------------------------------------------------ geometry

/** North comes from the server (shared with the overlay), changed through the "Norden" select. */
function orientation() {
  const o = msg.radar.orientation;
  return { nx: o.northX, ny: o.northY, ex: o.eastX, ey: o.eastY };
}

/** Own position: the latest of real fix and ring estimate. */
function me() {
  const r = msg.radar;
  const fix = r.self, ring = r.ring;
  // The ring estimate has no height: the last real fix's z stands in (its age is shown).
  const z = fix ? fix.z : null, zAge = fix ? fix.ageMs : null;
  if (ring && (!fix || ring.timeMs > fix.timeMs)) return { x: ring.x, y: ring.y, z, zAge, age: ring.ageMs, ring: ring.uncertainty, src: t("web.estimate") };
  if (fix) return { x: fix.x, y: fix.y, z, zAge, age: fix.ageMs, ring: 0, src: fix.source };
  return null;
}

/** Height of a cube relative to us in metres, or null when our height is unknown. */
function dzOf(c, m) {
  return m && m.z != null ? (c.z - m.z) / 100 : null;
}

function heightInfo(dz) {
  if (dz == null) return { cls: "unknown", text: t("height.unknown"), short: "" };
  const a = Math.abs(Math.round(dz));
  if (Math.abs(dz) <= LEVEL_M) return { cls: "level", text: t("height.same"), short: "≈" };
  return dz > 0
    ? { cls: "up", text: t("height.above", a), short: `▲${a}` }
    : { cls: "down", text: t("height.below", a), short: `▼${a}` };
}

/** World centimeters to pixels. Image +x is world +x and image +y is world +y; the fit is not centred on 0. */
function mapPixel(map, x, y) {
  return [map.offsetX + map.scale * x, map.offsetY + map.scale * y];
}

function mapBounds(map) {
  const w = map.tilesX * map.tile, h = map.tilesY * map.tile;
  return {
    minX: -map.offsetX / map.scale,
    minY: -map.offsetY / map.scale,
    maxX: (w - map.offsetX) / map.scale,
    maxY: (h - map.offsetY) / map.scale,
  };
}

function loadNudges() {
  try { return JSON.parse(localStorage.getItem("mapNudge") || "{}") || {}; }
  catch { return {}; }
}

function shiftOf(id) {
  const s = loadNudges()[id];
  return s && Number.isFinite(s.x) && Number.isFinite(s.y) ? s : { x: 0, y: 0 };
}

function withShift(map) {
  const s = shiftOf(map.id);
  if (!s.x && !s.y) return map;
  return { ...map, offsetX: map.offsetX + s.x, offsetY: map.offsetY + s.y };
}

/** Pixels to add so the player lands on the map point currently drawn at `at`. */
function mapNudgeDelta(map, player, at) {
  return { x: map.scale * (at.x - player.x), y: map.scale * (at.y - player.y) };
}

/** Possible respawns in packet centimeters. Present whether or not a cube is there now. */
let SPAWNS = {};

function zoneSpawns(id) {
  return (id && SPAWNS[id]) || [];
}

/** Background wins until a zone packet arrives; after that the packet wins in auto mode. */
function currentZoneId() {
  if (!msg) return null;
  const r = msg.radar;
  const picked = ui.basemap && ui.basemap !== "auto" ? ui.basemap : null;
  if (r.mapId == null) return picked;
  if (picked) return picked;
  const raw = ZONE_MAPS.find((m) => m.mapIds.includes(r.mapId) || (r.zone && m.zone === r.zone));
  return raw ? raw.id : null;
}

/** Marker pixel on the zone image back into world centimeters, using the current fit. */
function spawnWorld(map, px, py) {
  return { x: (px - map.offsetX) / map.scale, y: (py - map.offsetY) / map.scale };
}

function frameAround(points, w, h) {
  const o = orientation();
  const ns = points.map((p) => p.x * o.nx + p.y * o.ny);
  const es = points.map((p) => p.x * o.ex + p.y * o.ey);
  const pad = 6000;
  const nMin = Math.min(...ns) - pad, nMax = Math.max(...ns) + pad;
  const eMin = Math.min(...es) - pad, eMax = Math.max(...es) + pad;
  const scale = Math.min((w - 80) / Math.max(1, eMax - eMin), (h - 80) / Math.max(1, nMax - nMin));
  const cn = (nMin + nMax) / 2, ce = (eMin + eMax) / 2;
  return { scale, center: { x: cn * o.nx + ce * o.ex, y: cn * o.ny + ce * o.ey } };
}

function currentMap() {
  if (!ZONE_MAPS.length || !msg) return null;
  const r = msg.radar;
  const picked = ui.basemap && ui.basemap !== "auto" ? ZONE_MAPS.find((m) => m.id === ui.basemap) : null;
  // The zone packet arrives once, on entry. Until it does, the Background choice is the zone.
  if (r.mapId == null) return picked ? withShift(picked) : null;
  if (picked) return withShift(picked);
  const raw = ZONE_MAPS.find((m) => m.mapIds.includes(r.mapId) || (r.zone && m.zone === r.zone));
  return raw ? withShift(raw) : null;
}

function tileImage(map, col, row) {
  const key = `${map.id}:${col}:${row}`;
  if (tileCache.has(key)) return tileCache.get(key);
  const img = new Image();
  const rec = { img, ready: false };
  img.onload = () => { rec.ready = true; if (msg) draw(); };
  img.src = `${map.path}/${map.file}_${String(col).padStart(2, "0")}_${String(row).padStart(2, "0")}.webp`;
  tileCache.set(key, rec);
  return rec;
}

function drawZoneMap(map, toScreen, w, h) {
  if (!map || typeof Image !== "function") return;
  ctx.save();
  ctx.beginPath();
  ctx.rect(0, 0, w, h);
  ctx.clip();
  for (let row = 0; row < map.tilesY; row++) {
    for (let col = 0; col < map.tilesX; col++) {
      const x0 = (col * map.tile - map.offsetX) / map.scale;
      const y0 = (row * map.tile - map.offsetY) / map.scale;
      const x1 = ((col + 1) * map.tile - map.offsetX) / map.scale;
      const y1 = ((row + 1) * map.tile - map.offsetY) / map.scale;
      const o = toScreen({ x: x0, y: y0 });
      const ox = toScreen({ x: x1, y: y0 });
      const oy = toScreen({ x: x0, y: y1 });
      const xs = [o[0], ox[0], oy[0], ox[0] + oy[0] - o[0]];
      const ys = [o[1], ox[1], oy[1], ox[1] + oy[1] - o[1]];
      if (Math.max(...xs) < 0 || Math.min(...xs) > w || Math.max(...ys) < 0 || Math.min(...ys) > h) continue;
      const rec = tileImage(map, col, row);
      if (!rec.ready) continue;
      ctx.save();
      ctx.transform((ox[0] - o[0]) / map.tile, (ox[1] - o[1]) / map.tile, (oy[0] - o[0]) / map.tile, (oy[1] - o[1]) / map.tile, o[0], o[1]);
      ctx.drawImage(rec.img, 0, 0);
      ctx.restore();
    }
  }
  ctx.restore();
}

function rel(p, from) {
  const o = orientation();
  const dx = p.x - from.x, dy = p.y - from.y;
  const n = dx * o.nx + dy * o.ny, e = dx * o.ex + dy * o.ey;
  let bearing = Math.atan2(e, n) * 180 / Math.PI;
  if (bearing < 0) bearing += 360;
  return { n, e, dist: Math.hypot(dx, dy), bearing };
}

const COMPASS_KEYS = ["compass.n", "compass.ne", "compass.e", "compass.se", "compass.s", "compass.sw", "compass.w", "compass.nw"];
const WIRE_COMPASS = { N: "compass.n", NO: "compass.ne", O: "compass.e", SO: "compass.se", S: "compass.s", SW: "compass.sw", W: "compass.w", NW: "compass.nw" };
const compass = (b) => t(COMPASS_KEYS[Math.round(b / 45) % 8]);
const compassWire = (code) => (code && WIRE_COMPASS[code] ? t(WIRE_COMPASS[code]) : (code || ""));

function fmtAge(ms) {
  if (ms == null || ms < 0) return "–";
  const s = Math.floor(ms / 1000);
  if (s < 60) return t("age.s", s);
  const m = Math.floor(s / 60);
  if (m < 60) return t("age.m", `${m}:${String(s % 60).padStart(2, "0")}`);
  return t("age.h", `${Math.floor(m / 60)}:${String(m % 60).padStart(2, "0")}`);
}
const fmtNum = (v) => Math.round(v).toLocaleString(lang === "ru" ? "ru-RU" : "en-US");
const esc = (v) => String(v).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const factionName = (v) => ({ Elyos: t("faction.elyos"), Asmodian: t("faction.asmodian") })[v] || t("faction.unknown");
const enemies = () => ui.enemies ? (msg.radar.players || []).filter((p) => p.isEnemy === true) : [];

function rankText(p) {
  const text = p.rankName && !String(p.rankName).startsWith("PvP-Rang") ? p.rankName
    : (p.rankCode != null ? t("rank.code", p.rankCode) : t("rank.unknown"));
  return text + (p.rankCode != null && !p.rankVerified ? " *" : "");
}

function rankSymbol(p) {
  if (typeof p.rankSymbol === "string" && /^[1-9T?]$/.test(p.rankSymbol)) return p.rankSymbol;
  // Compatibility with older snapshots; never put a wire code (e.g. 14) into the class badge (5).
  if (p.rankCode === 19) return "T";
  return Number.isInteger(p.rankCode) && p.rankCode >= 10 && p.rankCode <= 18 ? String(p.rankCode - 9) : "?";
}

function enemyMarker(x, y, p) {
  const radius = 11;
  ctx.save();
  ctx.fillStyle = COLORS.enemy;
  ctx.beginPath();
  ctx.moveTo(x, y - radius); ctx.lineTo(x + radius, y); ctx.lineTo(x, y + radius); ctx.lineTo(x - radius, y); ctx.closePath();
  ctx.fill();
  ctx.font = "bold 12px Segoe UI, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillStyle = "#0e1217";
  ctx.fillText(rankSymbol(p), x, y + 0.5);
  ctx.restore();
}

function renderEnemies() {
  const r = msg.radar, list = enemies();
  const unknown = (r.players || []).filter((p) => p.isEnemy == null).length;
  document.getElementById("enemy-count").textContent = `(${list.length})`;
  const empty = document.getElementById("enemy-empty");
  empty.hidden = list.length > 0;
  empty.textContent = !ui.enemies ? t("web.enemyOff")
    : r.ownFaction === "Unknown" ? t("web.factionPick")
    : t("web.noEnemies", unknown ? t("web.unknownPlayers", unknown) : "");
  document.getElementById("enemy-list").innerHTML = list.map((p) => {
    const distance = p.distanceM != null ? `${p.distanceEstimated ? "≈ " : ""}${fmtNum(p.distanceM)} m ${compassWire(p.compass)}` : t("web.posUnknown");
    const age = p.positionAgeMs != null ? t("web.posAge", fmtAge(p.positionAgeMs)) : "";
    return `<li class="enemy-card${p.positionStale ? " stale" : ""}"><strong>${esc(p.name)}</strong> · ${esc(factionName(p.faction))}
      <div>${esc(rankText(p))}</div><div class="enemy-meta">${esc(distance + age)}${p.positionStale ? esc(t("web.stale")) : ""}</div></li>`;
  }).join("");
}

// ------------------------------------------------------------------ header and list

function chip(cls, label, value, title = "") {
  return `<span class="chip ${cls}" title="${title}"><span class="dot"></span>${label} <b>${value}</b></span>`;
}

function renderHeader() {
  const r = msg.radar, c = msg.capture;
  // Live: packet clock and wall clock share a base, so their gap is the time without game data. Replay: only updates count.
  const idle = Date.now() - receivedAt + (c.mode === "live" ? msg.wallMs - r.nowMs : 0);
  const capCls = c.streams > 0 && idle < 5000 ? "ok" : c.streams > 0 ? "mid" : "bad";
  const m = me();
  const fresh = r.fixFreshSeconds * 1000;
  const posCls = !m ? "bad" : m.ring ? "mid" : m.age <= fresh ? "ok" : m.age <= 30000 ? "mid" : "bad";
  const posVal = !m ? t("web.unknown") : `${m.ring ? "± " + Math.round(m.ring / 100) + " m, " : ""}${t("web.before", fmtAge(m.age))}`;
  document.getElementById("chips").innerHTML = [
    chip(capCls, c.mode === "replay" ? t("web.replay") : t("web.capture"),
      c.mode === "replay" ? (c.status || t("web.running")) : c.streams > 0 ? (idle < 5000 ? t("web.running") : t("web.idle", fmtAge(idle))) : (c.status || t("web.waiting")),
      `${c.status || ""} ${c.device || ""} · ${t("web.packets", c.packets, c.npcapDropped ? t("web.dropped", c.npcapDropped) : "")}`),
    chip(r.zone ? "ok" : "mid", t("web.zone"), r.zone || "?", r.mapId ? t("web.mapId", r.mapId) : t("web.mapUnknown")),
    chip(r.selfId ? "ok" : "bad", t("web.me"), r.selfId ?? "?", t("web.selfId")),
    chip(r.ownFaction && r.ownFaction !== "Unknown" ? "ok" : "mid", t("web.faction"), factionName(r.ownFaction)),
    chip(posCls, t("web.position"), posVal, m ? t("web.source", m.src) : t("web.noFix")),
  ].join("");
  const w = document.getElementById("warnings");
  w.hidden = r.warnings.length === 0;
  w.innerHTML = r.warnings.map((t) => `<div>${t}</div>`).join("");
}

function arrowSvg(bearing, color) {
  return `<svg class="arrow" viewBox="-22 -22 44 44"><circle r="20" fill="none" stroke="${COLORS.line}"/>
    <text y="-12" text-anchor="middle" font-size="7" fill="${COLORS.muted}">${t("overlay.north")}</text>
    <g transform="rotate(${bearing})"><path d="M0 -15 L7 6 L0 2 L-7 6 Z" fill="${color}"/></g></svg>`;
}

function renderList(fresh) {
  const r = msg.radar, m = me();
  const freshKeys = new Set(fresh.map((c) => `${c.objectId}@${c.firstSeenMs}`));
  const rows = r.cubes.map((c) => {
    const rp = m ? rel(c, m) : null;
    return { c, rp };
  });
  const order = { Visible: 0, Opening: 1, OutOfView: 2, Opened: 3, Gone: 4 };
  rows.sort((a, b) => order[a.c.status] - order[b.c.status] || (a.rp?.dist ?? 1e12) - (b.rp?.dist ?? 1e12));
  document.getElementById("count").textContent = r.cubes.length ? t("web.visibleCount", r.cubes.filter((c) => c.status === "Visible").length) : "";
  document.getElementById("empty").hidden = r.cubes.length > 0;
  document.getElementById("list").innerHTML = rows.map(({ c, rp }) => {
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status] || COLORS.gone;
    const hi = heightInfo(dzOf(c, m));
    const status = t(STATUS_KEY[c.status] || "status.gone") + (c.openedBy ? (c.openedBySelf ? t("status.byYou") : t("status.byPlayer", c.openedBy)) : "");
    const dist = rp ? `${fmtNum(rp.dist / 100)} m<small>${compass(rp.bearing)} ${Math.round(rp.bearing)}°</small>` : `? m<small>${t("web.ownPosUnknown")}</small>`;
    const heightAge = m && m.zAge > msg.radar.fixFreshSeconds * 1000 ? ` <span class="age">${t("height.age", fmtAge(m.zAge))}</span>` : "";
    const created = c.createdMs ? t("web.exists", fmtAge(r.nowMs - c.createdMs)) : "";
    const since = c.status !== "Visible" ? t("web.since", fmtAge(r.nowMs - c.statusSinceMs)) : "";
    return `<li class="cube ${c.status}${freshKeys.has(`${c.objectId}@${c.firstSeenMs}`) ? " new" : ""}">
      <div class="dist">${dist}</div>
      ${rp ? arrowSvg(rp.bearing, color) : "<span></span>"}
      <div class="height ${hi.cls}">${hi.text}${heightAge}</div>
      <div class="status">${status}${c.learned ? t("web.learned", esc(c.learned)) : ""}</div>
      <div class="meta">${c.zone || "?"} · ${t("web.template")} ${c.template} · #${c.objectId} · ${t("web.seen", fmtAge(r.nowMs - c.firstSeenMs))}${created}${since}</div>
      <div class="coords">x ${fmtNum(c.x)} · y ${fmtNum(c.y)} · z ${fmtNum(c.z)}
        <button data-copy="${Math.round(c.x)} ${Math.round(c.y)} ${Math.round(c.z)}">${t("web.copy")}</button></div>
    </li>`;
  }).join("");
}

document.getElementById("list").addEventListener("click", (e) => {
  const t = e.target.closest("button[data-copy]");
  if (t) navigator.clipboard?.writeText(t.dataset.copy);
});

// ------------------------------------------------------------------ canvas

const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");

// Free look, like a GPS: the map follows the player until it is dragged. After the character
// actually moves, three new position packets bring it smoothly back.
const MOVE_CM = 100;
const RETURN_PACKETS = 3;
const RETURN_MS = 700;
let view = {
  zoom: 1, follow: true, center: null, returning: null,
  anchor: null, moves: 0, fixMs: null, fixX: null, fixY: null, ticking: false, suppressClick: false,
};

function mapView() { return view; }
function mapFrame() { return frame; }

function clampZoom(z) { return Math.min(8, Math.max(0.04, z)); }

function followScale(w, h) {
  return (Math.min(w, h) / 2 - 24) / (ui.range * 100) * view.zoom;
}

function playerAnchor() {
  const fix = msg && msg.radar && msg.radar.self;
  if (fix) return { x: fix.x, y: fix.y };
  const p = me();
  return p ? { x: p.x, y: p.y } : null;
}

function detachView() {
  if (view.follow && frame && frame.scale) {
    const nominal = (Math.min(frame.w, frame.h) / 2 - 24) / (ui.range * 100);
    if (nominal > 0) view.zoom = clampZoom(frame.scale / nominal);
  }
  view.follow = false;
  view.returning = null;
  view.moves = 0;
  view.anchor = playerAnchor();
  if (view.anchor) { view.fixX = view.anchor.x; view.fixY = view.anchor.y; }
}

function ownSample() {
  const radar = msg && msg.radar;
  if (!radar) return null;
  const fix = radar.self, ring = radar.ring;
  if (ring && (!fix || ring.timeMs > fix.timeMs)) return { x: ring.x, y: ring.y, timeMs: ring.timeMs };
  if (fix) return { x: fix.x, y: fix.y, timeMs: fix.timeMs };
  return null;
}

/** Shift the looked-at world point. dx/dy are pixels since the drag started, from `origin`. */
function panTo(origin, dx, dy) {
  if (!frame || !frame.scale || !origin) return;
  if (view.follow || view.returning) detachView();
  const o = orientation();
  const de = -dx / frame.scale, dn = dy / frame.scale;
  view.center = {
    x: origin.x + de * o.ex + dn * o.nx,
    y: origin.y + de * o.ey + dn * o.ny,
  };
}

/** Wheel zoom. While following, the player stays in the centre; while looking around, the point under the cursor stays. */
function zoomView(factor, sx, sy) {
  const next = clampZoom(view.zoom * factor);
  if (!frame || !frame.scale || next === view.zoom || view.follow || sx == null) {
    view.zoom = next;
    return next;
  }
  const before = worldAt(sx, sy);
  const ratio = next / view.zoom;
  view.zoom = next;
  if (!before) return next;
  const e = (sx - frame.w / 2) / (frame.scale * ratio);
  const n = (frame.h / 2 - sy) / (frame.scale * ratio);
  const o = orientation();
  view.center = {
    x: before.x - (n * o.nx + e * o.ex),
    y: before.y - (n * o.ny + e * o.ey),
  };
  return next;
}

function notePosition(fix) {
  if (!fix || fix.x == null || fix.y == null) return;
  const step = view.fixX == null ? 0 : Math.hypot(fix.x - view.fixX, fix.y - view.fixY);
  const sameTime = fix.timeMs != null && view.fixMs === fix.timeMs;
  // A repeated timestamp still counts when the coordinates moved: walking updates the point
  // even if the packet clock on that sample does not.
  if ((sameTime || fix.timeMs == null) && step < MOVE_CM && view.fixX != null) return;
  if (fix.timeMs != null) view.fixMs = fix.timeMs;
  view.fixX = fix.x;
  view.fixY = fix.y;
  if (view.follow || view.returning || !view.anchor) return;
  if (step < MOVE_CM) return;
  view.moves += 1;
  if (view.moves >= RETURN_PACKETS) beginReturn();
}

function beginReturn() {
  const from = !view.follow && view.center ? view.center : frame && frame.center;
  view.moves = 0;
  if (!from) {
    view.follow = true;
    view.center = null;
    view.anchor = null;
    return;
  }
  view.returning = { from: { x: from.x, y: from.y }, t0: Date.now() };
}

function easedCenter(player) {
  if (!view.returning || !player) return null;
  const t = Math.min(1, (Date.now() - view.returning.t0) / RETURN_MS);
  const k = 1 - (1 - t) * (1 - t) * (1 - t);
  const center = {
    x: view.returning.from.x + (player.x - view.returning.from.x) * k,
    y: view.returning.from.y + (player.y - view.returning.from.y) * k,
  };
  if (t >= 1) {
    view.follow = true;
    view.returning = null;
    view.center = null;
    view.anchor = null;
  }
  return center;
}

function scheduleMapReturn() {
  if (view.ticking || typeof requestAnimationFrame !== "function") return;
  view.ticking = true;
  requestAnimationFrame(() => {
    view.ticking = false;
    if (view.returning && msg) draw();
  });
}

function draw() {
  if (!msg) return;
  const dpr = window.devicePixelRatio || 1;
  let w = canvas.clientWidth;
  const h = canvas.clientHeight;
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) {
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const r = msg.radar, m = me(), o = orientation();
  const zoneMap = currentMap();
  const zoneSpots = spots.map((s) => ({ x: s[0], y: s[1] }));
  const full = w;
  w -= STRIP; // the height strip takes the right edge
  let center, scale;
  const positionedEnemies = enemies().filter((p) => p.x != null && p.y != null && !p.positionStale);
  if (m && view.returning) {
    center = easedCenter(m);
    if (view.returning) scheduleMapReturn();
  }
  if (!center && !view.follow && view.center) center = view.center;
  if (!center && m) center = { x: m.x, y: m.y };
  if (!center && ui.mode === "radar" && (r.cubes.length || positionedEnemies.length))
    center = avg([...r.cubes, ...positionedEnemies]);
  if (center) scale = followScale(w, h);
  else {
    const pts = [...r.cubes, ...positionedEnemies, ...r.trail.filter((p) => p.length === 2).map((p) => ({ x: p[0], y: p[1] })), ...(m ? [m] : [])];
    const bounds = zoneMap ? mapBounds(zoneMap) : null;
    const fit = pts.length ? pts : (bounds
      ? [{ x: bounds.minX, y: bounds.minY }, { x: bounds.maxX, y: bounds.maxY }]
      : zoneSpots);
    if (!fit.length) return drawText(w / 2, h / 2, t("web.noData"), COLORS.muted, "center");
    const ns = fit.map((p) => p.x * o.nx + p.y * o.ny), es = fit.map((p) => p.x * o.ex + p.y * o.ey);
    const pad = 4000;
    const nMin = Math.min(...ns) - pad, nMax = Math.max(...ns) + pad, eMin = Math.min(...es) - pad, eMax = Math.max(...es) + pad;
    scale = Math.min((w - 40) / (eMax - eMin), (h - 40) / (nMax - nMin)) * view.zoom;
    const cn = (nMin + nMax) / 2, ce = (eMin + eMax) / 2;
    center = { x: cn * o.nx + ce * o.ex, y: cn * o.ny + ce * o.ey };
  }
  const zoneId = currentZoneId();
  const respawns = zoneSpawns(zoneId);
  if (ui.align && ui.mode === "radar" && m && respawns.length && center) {
    let best = Infinity, near = null;
    for (const [x, y] of respawns) {
      const d = Math.hypot(x - m.x, y - m.y);
      if (d < best) { best = d; near = { x, y }; }
    }
    if (near && best > ui.range * 100) {
      const fitted = frameAround([m, near], w, h);
      if (Number.isFinite(fitted.scale) && fitted.scale > 0) ({ center, scale } = fitted);
    }
  }
  const toScreen = (p) => {
    const q = rel(p, center);
    return [w / 2 + q.e * scale, h / 2 - q.n * scale];
  };
  frame = { center, scale, w, h, spawns: [] };

  drawZoneMap(zoneMap, toScreen, w, h);

  // The cross stays on the character. Panning moves the map under it; it is not glued to the screen centre.
  const [hx, hy] = m ? toScreen(m) : [w / 2, h / 2];
  ctx.lineWidth = 1;
  if (ui.mode === "radar") {
    const step = niceStep(ui.range / 4);
    ctx.font = "11px Segoe UI, sans-serif";
    for (let d = step; d <= ui.range * 1.5; d += step) {
      if (d === 100) continue; // the 100 m send ring is drawn brighter, on the player
      ring(hx, hy, d * 100 * scale, COLORS.line, []);
      drawText(hx + 4, hy - d * 100 * scale - 3, `${d} m`, COLORS.muted);
    }
  }
  ctx.strokeStyle = COLORS.line;
  line(hx, 8, hx, h - 8);
  line(8, hy, w - 8, hy);
  drawText(12, 20, "N ↑", COLORS.text);
  if (!zoneId && ui.basemap === "auto") drawText(w / 2, 36, t("web.spawnNeedMap"), COLORS.text, "center");
  else if (zoneId) drawText(12, 38, t("web.spawnCount", respawns.length), COLORS.text);
  if (ui.align) {
    const hint = !zoneId ? t("web.alignNeedMap") : !m ? t("web.alignNeedPos") : t("web.alignHint");
    drawText(w / 2, 36, hint, COLORS.text, "center");
  } else if (!view.follow) drawText(w / 2, h - 18, t("web.viewFree"), COLORS.muted, "center");

  // possible spots and history
  for (const s of zoneSpots) dot(toScreen(s), 1.6, COLORS.spot);
  ctx.globalAlpha = 0.55;
  for (const s of history) dot(toScreen(s), 2.4, COLORS.hist);
  ctx.globalAlpha = 1;

  // own trail and position
  if (r.trail.length > 1) {
    ctx.strokeStyle = COLORS.me;
    ctx.globalAlpha = 0.35;
    ctx.beginPath();
    let pen = false;
    for (const p of r.trail) {
      if (p.length < 2) { pen = false; continue; } // teleport: break the line
      const [x, y] = toScreen({ x: p[0], y: p[1] });
      pen ? ctx.lineTo(x, y) : ctx.moveTo(x, y);
      pen = true;
    }
    ctx.stroke();
    ctx.globalAlpha = 1;
  }
  if (m) {
    const [x, y] = toScreen(m);
    ctx.lineWidth = 2.5;
    ring(x, y, SEND_RADIUS * scale, COLORS.me, [8, 5], 1);
    ctx.lineWidth = 1;
    drawText(x + 6, y - SEND_RADIUS * scale - 3, "100 m", COLORS.me);
    if (m.ring) ring(x, y, Math.max(6, m.ring * scale), COLORS.me, [3, 4], 0.8);
    const stale = m.age > r.fixFreshSeconds * 1000;
    ctx.globalAlpha = stale ? 0.5 : 1;
    dot([x, y], 6, COLORS.me);
    ctx.globalAlpha = 1;
    drawText(x - 10, y + 16, stale || m.ring ? t("web.youAgo", fmtAge(m.age)) : t("web.you"), COLORS.me, "right");
  }

  // Possible respawns, known ahead of time. A collected cube leaves the point in place.
  // Drawn after the player dot so the spot underfoot stays visible, and before live cubes.
  if (respawns.length) {
    const marks = [];
    const live = r.cubes;
    const placed = [];
    for (const [x, y] of respawns) {
      if (live.some((c) => Math.hypot(c.x - x, c.y - y) < 300)) continue;
      const world = { x, y };
      const [sx, sy] = toScreen(world);
      placed.push({ sx, sy, world, dist: m ? Math.hypot(x - m.x, y - m.y) : 0 });
    }
    const inside = [];
    const outside = [];
    for (const s of placed) {
      (s.sx > 14 && s.sx < w - 14 && s.sy > 14 && s.sy < h - 14 ? inside : outside).push(s);
    }
    outside.sort((a, b) => a.dist - b.dist);
    for (const s of inside) {
      drawSpawn(s.sx, s.sy, s.dist < 400);
      marks.push({ sx: s.sx, sy: s.sy, world: s.world });
    }
    for (const s of outside.slice(0, 8)) {
      const a = Math.atan2(s.sy - h / 2, s.sx - w / 2);
      const rx = w / 2 - 16, ry = h / 2 - 16;
      const k = 1 / Math.max(Math.abs(Math.cos(a)) / rx, Math.abs(Math.sin(a)) / ry);
      const x = w / 2 + Math.cos(a) * k, y = h / 2 + Math.sin(a) * k;
      drawSpawn(x, y, false);
      if (m) drawText(x + 10, y - 8, `${Math.max(1, Math.round(s.dist / 100))} m`, COLORS.muted);
      marks.push({ sx: x, sy: y, world: s.world });
    }
    frame.spawns = marks;
  }

  // cubes
  const now = Date.now();
  for (const c of r.cubes) {
    let [x, y] = toScreen(c);
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status] || COLORS.gone;
    const rp = m ? rel(c, m) : null;
    const inside = x > 12 && x < w - 12 && y > 12 && y < h - 12;
    if (!inside) { // arrow on the rim
      const a = Math.atan2(y - h / 2, x - w / 2);
      const rx = w / 2 - 18, ry = h / 2 - 18;
      const k = 1 / Math.max(Math.abs(Math.cos(a)) / rx, Math.abs(Math.sin(a)) / ry);
      x = w / 2 + Math.cos(a) * k;
      y = h / 2 + Math.sin(a) * k;
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(a);
      ctx.fillStyle = color;
      ctx.beginPath();
      ctx.moveTo(10, 0); ctx.lineTo(-6, 7); ctx.lineTo(-6, -7); ctx.closePath();
      ctx.fill();
      ctx.restore();
    } else if (c.status === "Visible" || c.status === "Opening" || c.status === "OutOfView") {
      const pulse = c.status === "Opening" ? 3 + 2 * Math.sin(now / 150) : 0;
      const filled = c.status !== "OutOfView";
      ctx.shadowColor = color;
      ctx.shadowBlur = filled ? 12 : 0;
      marker(x, y, 7 + pulse / 2, heightInfo(dzOf(c, m)).cls, color, filled);
      ctx.shadowBlur = 0;
    } else {
      ctx.strokeStyle = color;
      ctx.lineWidth = 2;
      line(x - 5, y - 5, x + 5, y + 5);
      line(x - 5, y + 5, x + 5, y - 5);
      ctx.lineWidth = 1;
    }
    const hs = heightInfo(dzOf(c, m)).short;
    if (rp) drawText(x + 11, y - 8, `${fmtNum(rp.dist / 100)} m${hs ? " · " + hs : ""}`, color);
  }

  const enemyLabels = [];
  const enemyMarkers = [];
  for (const p of positionedEnemies) {
    let [x, y] = toScreen(p);
    if (ui.mode === "radar") {
      const dx = x - w / 2, dy = y - h / 2;
      const k = Math.max(1, Math.abs(dx) / (w / 2 - 18), Math.abs(dy) / (h / 2 - 18));
      x = w / 2 + dx / k; y = h / 2 + dy / k;
    }
    enemyMarkers.push({ x, y, p });
    // Avoid unreadable stacks for crowded contacts; their complete details remain in the list.
    if (!enemyLabels.some(([lx, ly]) => Math.abs(lx - x) < 240 && Math.abs(ly - y) < 16)) {
      drawText(x + 14, y - 16, `${p.name}${p.distanceM != null ? " · " + (p.distanceEstimated ? "≈" : "") + fmtNum(p.distanceM) + " m" : ""}`, COLORS.enemy);
      enemyLabels.push([x, y]);
    }
  }
  for (const { x, y, p } of enemyMarkers.reverse()) enemyMarker(x, y, p);
  drawHeightStrip(full, h, r, m);
}

let spawnIcon = null;

function drawSpawn(x, y, here) {
  const s = here ? 16 : 12;
  if (spawnIcon && spawnIcon.complete && spawnIcon.naturalWidth) {
    ctx.drawImage(spawnIcon, x - s / 2, y - s / 2, s, s);
    return;
  }
  ctx.beginPath();
  ctx.rect(x - 3, y - 3, 6, 6);
  ctx.fillStyle = "#e7d7a2";
  ctx.fill();
}

function loadSpawnIcon() {
  if (typeof Image !== "function") return;
  const img = new Image();
  img.onload = () => { spawnIcon = img; if (msg) draw(); };
  img.src = "maps/hidden-cube.webp";
}

/** Square for "same height", triangle up / down for above / below us. */
function marker(x, y, s, cls, color, filled) {
  ctx.beginPath();
  if (cls === "up") { ctx.moveTo(x, y - s); ctx.lineTo(x + s, y + s * 0.8); ctx.lineTo(x - s, y + s * 0.8); }
  else if (cls === "down") { ctx.moveTo(x, y + s); ctx.lineTo(x + s, y - s * 0.8); ctx.lineTo(x - s, y - s * 0.8); }
  else ctx.rect(x - s * 0.85, y - s * 0.85, s * 1.7, s * 1.7);
  ctx.closePath();
  ctx.lineWidth = 2;
  if (filled) { ctx.fillStyle = color; ctx.fill(); }
  else { ctx.strokeStyle = color; ctx.stroke(); }
  ctx.lineWidth = 1;
}

/** Vertical strip: our height in the middle, every live cube above or below it. */
function drawHeightStrip(full, h, r, m) {
  const x0 = full - STRIP, mid = h / 2;
  ctx.fillStyle = "rgba(255,255,255,0.03)";
  ctx.fillRect(x0, 0, STRIP, h);
  ctx.strokeStyle = COLORS.line;
  line(x0, 0, x0, h);
  drawText(x0 + STRIP / 2, 20, t("web.height"), COLORS.muted, "center");
  const live = r.cubes.filter((c) => c.status === "Visible" || c.status === "Opening" || c.status === "OutOfView");
  if (!m || m.z == null) {
    drawText(x0 + STRIP / 2, mid, "?", COLORS.muted, "center");
    return;
  }
  const dzs = live.map((c) => dzOf(c, m));
  const range = Math.max(20, Math.ceil(Math.max(0, ...dzs.map(Math.abs)) / 10) * 10 + 10); // metres, middle to edge
  const px = (h / 2 - 34) / range;
  for (const v of [range, range / 2, -range / 2, -range]) {
    const y = mid - v * px;
    ctx.strokeStyle = COLORS.line;
    line(x0 + 6, y, x0 + 14, y);
    drawText(x0 + 17, y + 4, `${v > 0 ? "+" : ""}${Math.round(v)}`, COLORS.muted);
  }
  ctx.strokeStyle = COLORS.me;
  line(x0 + 4, mid, full - 4, mid);
  drawText(full - 4, mid - 4, t("web.you"), COLORS.me, "right");
  live.forEach((c, i) => {
    const dz = dzs[i];
    const y = mid - Math.max(-range, Math.min(range, dz)) * px;
    const color = { Visible: COLORS.visible, OutOfView: COLORS.out, Opening: COLORS.opening }[c.status];
    marker(x0 + 22, y, 5, heightInfo(dz).cls, color, c.status !== "OutOfView");
    drawText(x0 + 31, y + 4, `${dz > 0 ? "+" : ""}${Math.round(dz)}`, color);
  });
}

function avg(list) {
  return { x: list.reduce((s, p) => s + p.x, 0) / list.length, y: list.reduce((s, p) => s + p.y, 0) / list.length };
}

function niceStep(v) {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  return [1, 2, 2.5, 5, 10].map((f) => f * p).find((s) => s >= v) || v;
}

function ring(x, y, r, color, dash, alpha = 1) {
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = color;
  ctx.setLineDash(dash);
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.stroke();
  ctx.restore();
}

function dot([x, y], r, color) {
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.fill();
}

function line(x0, y0, x1, y1) {
  ctx.beginPath();
  ctx.moveTo(x0, y0);
  ctx.lineTo(x1, y1);
  ctx.stroke();
}

function drawText(x, y, text, color, align = "left") {
  ctx.font = "12px Segoe UI, sans-serif";
  ctx.textAlign = align;
  ctx.fillStyle = color;
  ctx.fillText(text, x, y);
  ctx.textAlign = "left";
}

// ------------------------------------------------------------------ alerts

function announce(cubes) {
  const m = me();
  const text = cubes.map((c) => {
    const rp = m ? rel(c, m) : null;
    return rp ? `${fmtNum(rp.dist / 100)} m ${compass(rp.bearing)}` : `${c.zone || ""} x ${fmtNum(c.x)} y ${fmtNum(c.y)}`;
  }).join(", ");
  if (ui.sound && audio) chime();
  if (ui.notify) new Notification(cubes.length > 1 ? t("web.cubeMany", cubes.length) : t("web.cubeOne"), { body: text, tag: "cube" });
  document.title = `● Cube: ${text}`;
  setTimeout(() => (document.title = "Cube Radar"), 15000);
}

function chime() {
  const now = audio.currentTime;
  [[880, 0], [1320, 0.16]].forEach(([f, d]) => {
    const osc = audio.createOscillator(), gain = audio.createGain();
    osc.frequency.value = f;
    gain.gain.setValueAtTime(0.0001, now + d);
    gain.gain.exponentialRampToValueAtTime(0.25, now + d + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + d + 0.3);
    osc.connect(gain).connect(audio.destination);
    osc.start(now + d);
    osc.stop(now + d + 0.32);
  });
}

// ------------------------------------------------------------------ controls

function syncControls() {
  document.querySelectorAll(".seg button").forEach((b) => b.classList.toggle("on", b.dataset.mode === ui.mode));
  document.getElementById("range").value = String(ui.range);
  const s = document.getElementById("sound");
  s.textContent = ui.sound ? t("web.soundOn") : t("web.soundOff");
  s.classList.toggle("on", ui.sound);
  const n = document.getElementById("notify");
  n.textContent = ui.notify ? t("web.notifyOn") : t("web.notifyOff");
  n.classList.toggle("on", ui.notify);
  const e = document.getElementById("enemies");
  e.textContent = ui.enemies ? t("web.enemiesOn") : t("web.enemiesOff");
  e.classList.toggle("on", ui.enemies);
  const align = document.getElementById("align");
  align.textContent = ui.align ? t("web.alignOn") : t("web.align");
  align.classList.toggle("on", ui.align);
  document.getElementById("canvas").classList.toggle("align", ui.align);
  const reset = document.getElementById("align-reset");
  reset.textContent = t("web.alignReset");
  const shown = currentMap();
  const shift = shown ? shiftOf(shown.id) : { x: 0, y: 0 };
  reset.hidden = !(shift.x || shift.y);
}

function worldAt(sx, sy) {
  if (!frame || !frame.scale) return null;
  const e = (sx - frame.w / 2) / frame.scale;
  const n = (frame.h / 2 - sy) / frame.scale;
  const o = orientation();
  return {
    x: frame.center.x + n * o.nx + e * o.ex,
    y: frame.center.y + n * o.ny + e * o.ey,
  };
}

function rememberNudge(id, extra) {
  const all = loadNudges();
  const cur = shiftOf(id);
  all[id] = { x: cur.x + extra.x, y: cur.y + extra.y };
  localStorage.setItem("mapNudge", JSON.stringify(all));
  publishNudge(id);
}

function publishNudge(id) {
  const s = shiftOf(id);
  fetch("/api/map-nudge", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ id, x: s.x, y: s.y }),
  }).catch(() => {});
}

function publishNudges() {
  for (const id of Object.keys(loadNudges())) publishNudge(id);
}

document.querySelectorAll(".seg button").forEach((b) => b.addEventListener("click", () => {
  ui.mode = b.dataset.mode;
  localStorage.setItem("mode", ui.mode);
  syncControls();
  draw();
}));
document.getElementById("range").addEventListener("change", (e) => {
  ui.range = Number(e.target.value);
  localStorage.setItem("range", ui.range);
  draw();
});
document.getElementById("basemap").addEventListener("change", (e) => {
  ui.basemap = e.target.value;
  localStorage.setItem("basemap", ui.basemap);
  draw();
});
document.getElementById("align").addEventListener("click", () => {
  ui.align = !ui.align;
  syncControls();
  if (msg) draw();
});
document.getElementById("align-reset").addEventListener("click", () => {
  const map = currentMap();
  if (map) {
    const all = loadNudges();
    delete all[map.id];
    localStorage.setItem("mapNudge", JSON.stringify(all));
    publishNudge(map.id);
  }
  ui.align = false;
  syncControls();
  if (msg) draw();
});
document.getElementById("canvas").addEventListener("click", (e) => {
  if (view.suppressClick) { view.suppressClick = false; return; }
  if (!ui.align || !msg) return;
  const map = currentMap();
  const player = me();
  if (!map || !player || !frame || e.offsetX > frame.w || e.offsetY > frame.h) return;
  const marks = frame.spawns || [];
  let hit = null, best = 16;
  for (const s of marks) {
    const d = Math.hypot(s.sx - e.offsetX, s.sy - e.offsetY);
    if (d <= best) { best = d; hit = s; }
  }
  const at = hit ? hit.world : (marks.length ? null : worldAt(e.offsetX, e.offsetY));
  if (!at) return;
  rememberNudge(map.id, mapNudgeDelta(map, player, at));
  ui.align = false;
  syncControls();
  draw();
});
let drag = null;
canvas.addEventListener("pointerdown", (e) => {
  if (ui.align || e.button !== 0 || !frame || e.offsetX > frame.w) return;
  view.suppressClick = false;
  drag = { x: e.offsetX, y: e.offsetY, ox: frame.center.x, oy: frame.center.y, moved: false };
  if (canvas.setPointerCapture) canvas.setPointerCapture(e.pointerId);
});
canvas.addEventListener("pointermove", (e) => {
  if (!drag) return;
  const dx = e.offsetX - drag.x, dy = e.offsetY - drag.y;
  if (!drag.moved && Math.hypot(dx, dy) < 4) return;
  drag.moved = true;
  canvas.classList.add("panning");
  panTo({ x: drag.ox, y: drag.oy }, dx, dy);
  if (msg) draw();
});
canvas.addEventListener("pointerup", () => {
  if (!drag) return;
  view.suppressClick = drag.moved;
  drag = null;
  canvas.classList.remove("panning");
});
canvas.addEventListener("pointercancel", () => { drag = null; canvas.classList.remove("panning"); });
canvas.addEventListener("wheel", (e) => {
  if (!msg || !frame) return;
  e.preventDefault();
  const dy = e.deltaMode === 1 ? e.deltaY * 16 : e.deltaY;
  zoomView(Math.exp(-dy * 0.0015), e.offsetX, e.offsetY);
  draw();
}, { passive: false });

document.getElementById("north").addEventListener("change", async (e) => {
  await fetch("/api/north", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ north: e.target.value }) });
  e.target.blur();
});
localStorage.removeItem("north"); // older versions kept it per browser
document.getElementById("faction").addEventListener("change", async (e) => {
  const result = await fetch("/api/faction", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ faction: e.target.value }) });
  if (!result.ok) console.error(t("web.factionFailed"), await result.text());
  e.target.blur();
});
document.getElementById("enemies").addEventListener("click", () => {
  ui.enemies = !ui.enemies;
  localStorage.setItem("enemies", ui.enemies ? "1" : "0");
  syncControls();
  if (msg) { renderEnemies(); draw(); }
});
document.getElementById("sound").addEventListener("click", () => {
  ui.sound = !ui.sound;
  if (ui.sound && !audio) audio = new AudioContext();
  if (ui.sound) chime();
  syncControls();
});
document.getElementById("notify").addEventListener("click", async () => {
  if (!("Notification" in window)) return;
  if (!ui.notify && Notification.permission !== "granted") await Notification.requestPermission();
  ui.notify = !ui.notify && Notification.permission === "granted";
  localStorage.setItem("notify", ui.notify ? "1" : "0");
  syncControls();
});
window.addEventListener("resize", draw);
setInterval(() => msg && (renderHeader(), draw()), 1000);

function applyStatic() {
  document.documentElement.lang = lang;
  document.querySelectorAll("[data-i18n]").forEach((el) => { el.textContent = t(el.dataset.i18n); });
  const north = document.getElementById("north-label");
  if (north) north.title = t("web.northTitle");
  const langSel = document.getElementById("lang");
  if (langSel && document.activeElement !== langSel) langSel.value = lang;
  syncControls();
}

document.getElementById("lang").addEventListener("change", async (e) => {
  lang = e.target.value;
  if (!I18N[lang]) lang = "ru";
  localStorage.setItem("lang", lang);
  applyStatic();
  if (msg) onState();
  try {
    await fetch("/api/lang", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ lang }),
    });
  } catch { /* the radar is not serving this page, or this is the test harness */ }
  e.target.blur();
});

function loadMapCatalog() {
  if (typeof XMLHttpRequest === "undefined") return;
  try {
    const xhr = new XMLHttpRequest();
    xhr.open("GET", "maps/maps.json", false);
    xhr.send();
    if (xhr.status && xhr.status !== 200) return;
    ZONE_MAPS = JSON.parse(xhr.responseText).zones || [];
  } catch { /* tests, or the page is not served over HTTP */ }
}

function loadSpawns() {
  if (typeof XMLHttpRequest === "undefined") return;
  try {
    const xhr = new XMLHttpRequest();
    xhr.open("GET", "maps/spawns.json", false);
    xhr.send();
    if (xhr.status && xhr.status !== 200) return;
    SPAWNS = JSON.parse(xhr.responseText).zones || {};
  } catch { /* tests, or the page is not served over HTTP */ }
}

applyStatic();
loadMapCatalog();
loadSpawns();
loadSpawnIcon();
publishNudges();
connect();
