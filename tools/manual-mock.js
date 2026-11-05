#!/usr/bin/env node
// The mock website of the v0.18 Field manual (spec section 2.4): one HTML page with all its sections, for the owner to
// approve before any of the manual is built into the game. Every number comes from GameConfig.xml, the words from
// tools/manual-text.json, the sprites from the built atlases (Node, no dependencies, no Unity). Strong / Weak against
// come from equal-cost fights (balance-sheet.js), so a matchup can only read one way; see counterTable.
// It refuses to write the page when the manual contradicts itself (see check).
//
// Usage: node tools/manual-mock.js [--config Assets/Resources/GameConfig.xml] --out <file.html>
//        node tools/manual-mock.js --write-text   (copies manual-text.json into Assets/Scripts/UI/Models/ManualText.Data.cs for the game)
'use strict';
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const xml = require('./lib/config-xml');
const sheet = require('./balance-sheet');

const ROOT = path.resolve(__dirname, '..');
const CLASSES = ['LightUnit', 'HeavyUnit', 'Building', 'Wall'];
const CLASS_NAMES = { LightUnit: 'Light units', HeavyUnit: 'Heavy units', Building: 'Buildings', Wall: 'Walls' };
// Mirrors SpawnerRules.Produces (code, not config), as balance-sheet.js does.
const PRODUCERS = { Base: ['Builder'], Barracks: ['Scout', 'Tank', 'Flamethrower'], HeavyFactory: ['Heavy', 'Artillery', 'Sniper'], SiegeWorkshop: ['Digger', 'SuicideBomber'] };
const NAMES = { Base: 'HQ', HeavyFactory: 'Heavy Factory', SiegeWorkshop: 'Siege Workshop', GunTurret: 'Gun turret', SuicideBomber: 'Suicide bomber' };
const nameOf = (t) => NAMES[t] || t;
// Mirrors GameInput's default bindings; the last two are new in v0.18 (spec sections 2.2 and 3).
const KEYS = {
  Select: 'LMB', Command: 'RMB', DragPan: 'MMB', FastPan: 'Shift', AttackMove: 'A', Stop: 'S', Hold: 'H', Roam: 'J', Repair: 'F', Dig: 'G', Detonate: 'B', Cancel: 'Del',
  QueueModifier: 'Shift', AssignModifier: 'Ctrl', Rotate: 'R', Menu: 'Esc', SelectIdleBuilders: '.', SelectArmy: ',', JumpToHq: 'Bksp', NextAlert: 'Space',
  CardTop1: 'Q', CardTop2: 'W', CardTop3: 'E', CardTop4: 'T', CardBottom1: 'Z', CardBottom2: 'X', CardBottom3: 'C', CardBottom4: 'V', CardBottom5: 'N',
  FieldManual: 'F1', ShowRanges: 'Alt',
};
// Theme.uss, standard palette.
const PLAYER_1 = [0x4d, 0xa3, 0xff];
const TEAM_GAIN = 1.55; // TeamSprite.shader
// Strong / Weak against: the balance sheet's fight, its default budget and packing.
const FIGHT = { budget: 3000, packing: 0.2 };
const MAX_LISTED = 3, MIN_MARGIN = 20; // up to 3 names; a fight closer than this is not a counter
const BOMBER_MARGIN = 90; // a bomber's best and worst targets rank near the top of those units' own lists
const WORKERS = ['Builder', 'Digger']; // nobody picks an army to beat them, so they aren't named as "strong against"

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const fmt = (x) => { const r = Math.round(x * 100) / 100; return Number.isInteger(r) && r >= 1000 ? r.toLocaleString('en-US') : String(r); };

// ---- Config ----

function loadConfig(file) {
  const src = fs.readFileSync(file, 'utf8');
  const gc = xml.parse(src).children.find((c) => c.tag === 'GameConfig');
  const one = (el, tag) => el.children.find((c) => c.tag === tag);
  const attr = (el, name) => (el.attrs.find(([k]) => k === name) || [])[1];
  const leaves = (el) => Object.fromEntries(el.children.filter((c) => !c.children.length).map((c) => { const t = xml.text(src, c).trim(); return [c.tag, t !== '' && !Number.isNaN(Number(t)) ? Number(t) : t]; }));
  const table = {};
  for (const e of one(gc, 'DamageTable').children) {
    const a = attr(e, 'attacker'), t = attr(e, 'target'), v = Number(xml.text(src, e));
    table[a] = table[a] || {};
    for (const cls of t === 'Unit' ? ['LightUnit', 'HeavyUnit'] : [t]) table[a][cls] = v;
  }
  const units = one(gc, 'Units').children.map((u) => ({ type: attr(u, 'type'), ...leaves(u) }));
  const buildings = one(gc, 'Buildings').children.map((b) => ({ type: attr(b, 'type'), ...leaves(b) }));
  const section = (...p) => { let el = gc; for (const t of p) el = el && one(el, t); return el; };
  const lookup = (p) => { // "Siege/BuildPower", "Match/Map/EdgeTiles"
    const parts = p.split('/');
    const el = section(...parts);
    if (!el) throw new Error(`no ${p} in the config`);
    return Number(xml.text(src, el));
  };
  return { units, buildings, table, lookup, version: `${xml.text(src, section('Release', 'Version'))} ${xml.text(src, section('Release', 'Name'))}` };
}

const multiplier = (cfg, type, cls) => { const row = cfg.table[type] || {}; return cls in row ? row[cls] : 1; };
const classOf = (u) => `${u.Armour}Unit`;
const dps = (f, m) => (f.Damage ? (f.Damage / f.AttackInterval) * m : 0);

/**
 * Every matchup as one signed score: +62 means the first side wins an equal-cost fight with 62 % of its health left.
 * Each fight is run both ways round and averaged, so score(a, b) is always -score(b, a), and a matchup can't read as
 * both strong and weak. A bomber has no gun for that fight, so it is ranked by the worth one blast takes from a packed
 * group of the target (after what the target shoots down on the way in): its top three targets count against them,
 * its bottom three count for them.
 */
function counterTable(configFile) {
  const cfg = sheet.load(fs.readFileSync(configFile, 'utf8'));
  const { budget, packing } = FIGHT;
  const turret = { ...cfg.buildings.GunTurret, speed: 0, minRange: 0, splash: 0, blastDamage: 0, cls: 'Building', radius: 0 };
  const fighters = [...cfg.units.filter((u) => u.dmg > 0 && !u.blastDamage), turret];
  const count = (u) => Math.max(1, Math.floor(budget / u.cost));
  const once = (a, b) => { const r = sheet.duel(a, count(a), b, count(b), packing); return r.winner === null ? 0 : r.winner === 0 ? r.keep : -r.keep; };
  const score = {};
  const set = (a, b, v) => { (score[a] = score[a] || {})[b] = v; (score[b] = score[b] || {})[a] = -v; };
  fighters.forEach((a, i) => fighters.slice(i + 1).forEach((b) => set(a.type, b.type, Math.round(((once(a, b) - once(b, a)) / 2) * 100))));
  const bomber = cfg.units.find((u) => u.blastDamage);
  if (bomber) {
    const worth = fighters.map((x) => {
      const hits = x.radius ? 1 + (packing / (Math.PI * x.radius * x.radius)) * Math.PI * bomber.blastRadius * bomber.blastRadius : 1;
      const perBlast = Math.min(1, (bomber.blastDamage * bomber.mult[x.cls]) / x.hp) * hits * x.cost;
      const shotDown = count(x) * (x.range / bomber.speed / x.interval) * Math.min(1, (x.dmg * x.mult[bomber.cls]) / bomber.hp);
      return { type: x.type, v: perBlast * Math.max(0, 1 - shotDown / count(bomber)) };
    }).sort((a, b) => b.v - a.v);
    worth.slice(0, MAX_LISTED).forEach((x) => set(bomber.type, x.type, BOMBER_MARGIN));
    worth.slice(-MAX_LISTED).forEach((x) => set(bomber.type, x.type, -BOMBER_MARGIN));
  }
  return score;
}

/** One type's lists from the table: the biggest wins and the biggest losses, biggest first. */
function counters(table, type) {
  const row = Object.entries(table[type] || {});
  return {
    strong: row.filter(([t, v]) => v >= MIN_MARGIN && !WORKERS.includes(t)).sort((a, b) => b[1] - a[1]).slice(0, MAX_LISTED).map(([t]) => t),
    weak: row.filter(([, v]) => v <= -MIN_MARGIN).sort((a, b) => a[1] - b[1]).slice(0, MAX_LISTED).map(([t]) => t),
  };
}

/** Everything the page must agree with. Returns a list of problems; the page isn't written unless it is empty. */
function check(cfg, text, table) {
  const out = [];
  const unitTypes = cfg.units.map((u) => u.type), buildingTypes = [...cfg.buildings.map((b) => b.type), 'Wall'];
  const same = (what, want, have) => {
    for (const t of want) if (!have.includes(t)) out.push(`${what}: no text for ${t}`);
    for (const t of have) if (!want.includes(t)) out.push(`${what}: text for ${t}, which isn't in the config`);
  };
  same('units', unitTypes, Object.keys(text.units));
  same('buildings', buildingTypes, Object.keys(text.buildings));
  // A matchup reads one way everywhere.
  const types = [...unitTypes, 'GunTurret'];
  for (const a of types) {
    const c = counters(table, a);
    for (const b of c.strong) {
      if (c.weak.includes(b)) out.push(`${nameOf(a)} is both strong and weak against ${nameOf(b)}`);
      if (counters(table, b).strong.includes(a)) out.push(`${nameOf(a)} and ${nameOf(b)} are each strong against the other`);
    }
    for (const b of c.weak) if (counters(table, b).weak.includes(a)) out.push(`${nameOf(a)} and ${nameOf(b)} are each weak against the other`);
    // What the page's words say about a matchup must be what the fight says.
    const page = text.units[a] || text.buildings[a];
    for (const [b, side] of Object.entries((page && page.claims) || {})) {
      const v = (table[a] || {})[b];
      if (v === undefined || (side === 'beats' ? v < MIN_MARGIN : v > -MIN_MARGIN)) out.push(`${nameOf(a)}'s words say it ${side === 'beats' ? 'beats' : 'loses to'} ${nameOf(b)}, but the fight scores ${v}`);
    }
  }
  // The role lines are the hover cards' (HoverText.UnitRole).
  const hover = fs.readFileSync(path.join(ROOT, 'Assets/Scripts/UI/Models/HoverCardContent.cs'), 'utf8');
  for (const t of unitTypes) {
    const m = new RegExp(`UnitType\\.${t} => "([^"]+)"`).exec(hover);
    if (!m) out.push(`HoverText.UnitRole has no ${t}`);
    else if (text.units[t] && text.units[t].role !== m[1]) out.push(`${t}'s role line differs from its hover card: "${m[1]}"`);
  }
  // No balance number typed into the words: all of them come from placeholders.
  const scan = (where, str) => {
    const bare = str.replace(/\{[^}]+\}/g, '').replace(/1–0/g, '');
    if (/\d/.test(bare) && !/^A hit does/.test(str)) out.push(`${where} has a typed number: ${str.slice(0, 70)}`);
  };
  for (const group of ['units', 'buildings']) for (const [t, p] of Object.entries(text[group])) [p.role, ...p.body].forEach((x) => scan(t, x));
  for (const r of text.rules) { scan(r.title, r.lead); r.sections.forEach((c) => c.paras.forEach((x) => scan(r.title, x))); }
  text.damageTable.notes.forEach((x) => scan('Damage table', x));
  return out;
}

function resolver(cfg) {
  const unit = (t) => cfg.units.find((u) => u.type === t);
  const bld = (t) => cfg.buildings.find((b) => b.type === t);
  const mining = cfg.lookup('Resources/MiningRate');
  const derived = {
    RichMinerRate: mining * cfg.lookup('Resources/RichGemMultiplier'),
    MinerPayback: bld('Miner').UpfrontCost / mining,
    BarracksMiners: bld('Barracks').RunningCost / mining,
    HeavyFactoryMiners: bld('HeavyFactory').RunningCost / mining,
    DecaySeconds: 100 / cfg.lookup('Resources/DecayPercentPerSecond'),
    DemolishPercent: cfg.lookup('Siege/DemolishRefund') * 100,
    FriendlyUnitPercent: cfg.lookup('Bomber/FriendlyUnitDamage') * 100,
  };
  return (p) => {
    const [head, ...rest] = p.split('/');
    if (head === 'Key') { if (!(rest[0] in KEYS)) throw new Error(`no key ${rest[0]}`); return `<span class="chip">${esc(KEYS[rest[0]])}</span>`; }
    if (head === 'Derived') { if (!(rest[0] in derived)) throw new Error(`no figure ${rest[0]}`); return fmt(derived[rest[0]]); }
    if (head === 'Unit' || head === 'Building') {
      const row = head === 'Unit' ? unit(rest[0]) : bld(rest[0]);
      if (!row || !(rest[1] in row)) throw new Error(`no ${p} in the config`);
      return fmt(row[rest[1]]);
    }
    return fmt(cfg.lookup(p));
  };
}

// ---- PNG, just what the atlases need ----

function readPng(file) {
  const b = fs.readFileSync(file);
  let i = 8, w = 0, h = 0, depth = 0, colour = 0;
  const idat = [];
  while (i < b.length) {
    const len = b.readUInt32BE(i), type = b.toString('latin1', i + 4, i + 8), d = b.subarray(i + 8, i + 8 + len);
    if (type === 'IHDR') { w = d.readUInt32BE(0); h = d.readUInt32BE(4); depth = d[8]; colour = d[9]; }
    if (type === 'IDAT') idat.push(d);
    i += 12 + len;
  }
  const channels = { 0: 1, 2: 3, 4: 2, 6: 4 }[colour];
  if (depth !== 8 || !channels) throw new Error(`${file}: unsupported PNG (depth ${depth}, colour ${colour})`);
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = w * channels;
  const px = Buffer.alloc(w * h * 4);
  let prev = Buffer.alloc(stride), cur = Buffer.alloc(stride);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], line = y * (stride + 1) + 1;
    for (let x = 0; x < stride; x++) {
      const v = raw[line + x], l = x >= channels ? cur[x - channels] : 0, u = prev[x], ul = x >= channels ? prev[x - channels] : 0;
      let p = 0;
      if (f === 1) p = l; else if (f === 2) p = u; else if (f === 3) p = (l + u) >> 1;
      else if (f === 4) { const e = l + u - ul, a = Math.abs(e - l), c = Math.abs(e - u), d = Math.abs(e - ul); p = a <= c && a <= d ? l : c <= d ? u : ul; }
      cur[x] = (v + p) & 255;
    }
    for (let x = 0; x < w; x++) {
      const s = x * channels, o = (y * w + x) * 4;
      if (channels >= 3) { px[o] = cur[s]; px[o + 1] = cur[s + 1]; px[o + 2] = cur[s + 2]; px[o + 3] = channels === 4 ? cur[s + 3] : 255; }
      else { px[o] = px[o + 1] = px[o + 2] = cur[s]; px[o + 3] = channels === 2 ? cur[s + 1] : 255; }
    }
    [prev, cur] = [cur, prev];
  }
  return { w, h, px };
}

const CRC = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
function crc(buf) { let c = 0xffffffff; for (const v of buf) c = CRC[(c ^ v) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
function writePng(w, h, px) {
  const chunk = (type, data) => { const t = Buffer.from(type, 'latin1'), len = Buffer.alloc(4), sum = Buffer.alloc(4); len.writeUInt32BE(data.length); sum.writeUInt32BE(crc(Buffer.concat([t, data]))); return Buffer.concat([len, t, data, sum]); };
  const head = Buffer.alloc(13); head.writeUInt32BE(w, 0); head.writeUInt32BE(h, 4); head[8] = 8; head[9] = 6;
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) px.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4);
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', head), chunk('IDAT', zlib.deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0))]);
}

/** Sprites from the built atlases in the player's colour, as the TeamSprite shader tints them, cropped to their pixels. */
function spriteSheet() {
  const dir = path.join(ROOT, 'Assets/Resources/Art');
  const atlas = (name) => ({ table: JSON.parse(fs.readFileSync(path.join(dir, `${name}.json`))), colour: readPng(path.join(dir, `${name}.png`)), mask: readPng(path.join(dir, `${name}_mask.png`)) });
  const units = atlas('Units'), buildings = atlas('Buildings'), tiles = atlas('Tiles');
  const frameOf = (a, id, cell = 0) => { const s = a.table.sprites.find((x) => x.id === id); return s && a.table.frames[s.first + cell]; };
  // Blends one atlas frame onto a canvas, its pivot at (px, py) measured from the canvas' bottom-left. Atlas frames count from the bottom.
  const stamp = (canvas, a, f, px, py) => {
    for (let y = 0; y < f.h; y++) for (let x = 0; x < f.w; x++) {
      const si = ((a.colour.h - 1 - (f.y + y)) * a.colour.w + f.x + x) * 4, alpha = a.colour.px[si + 3];
      if (!alpha) continue;
      const m = a.mask.px[si] / 255, cx = px - f.pivotX + x, cy = canvas.h - 1 - (py - f.pivotY + y);
      if (cx < 0 || cy < 0 || cx >= canvas.w || cy >= canvas.h) continue;
      const di = (cy * canvas.w + cx) * 4;
      for (let k = 0; k < 3; k++) {
        const c = a.colour.px[si + k] / 255, team = Math.min(1, c * (PLAYER_1[k] / 255) * TEAM_GAIN);
        canvas.px[di + k] = Math.round((c + (team - c) * m) * 255);
      }
      canvas.px[di + 3] = 255;
    }
  };
  // Crops every cell to the pixels any of them draws, and lays them in a sheet: one row per facing, one column per frame.
  const sheetOf = (grid, size, loop) => {
    const cells = grid.map((row) => row.map((layers) => { const c = { w: size, h: size, px: Buffer.alloc(size * size * 4) }; for (const [a, f] of layers) if (f) stamp(c, a, f, size >> 1, size >> 1); return c; }));
    let x0 = size, y0 = size, x1 = -1, y1 = -1;
    for (const row of cells) for (const c of row) for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (c.px[(y * size + x) * 4 + 3]) { x0 = Math.min(x0, x); x1 = Math.max(x1, x); y0 = Math.min(y0, y); y1 = Math.max(y1, y); }
    const cw = x1 - x0 + 1, ch = y1 - y0 + 1, cols = grid[0].length, rows = grid.length, out = Buffer.alloc(cw * cols * ch * rows * 4);
    cells.forEach((row, r) => row.forEach((c, k) => { for (let y = 0; y < ch; y++) c.px.copy(out, ((r * ch + y) * cw * cols + k * cw) * 4, ((y + y0) * size + x0) * 4, ((y + y0) * size + x1 + 1) * 4); }));
    return { uri: `data:image/png;base64,${writePng(cw * cols, ch * rows, out).toString('base64')}`, cw, ch, cols, rows, loop };
  };
  const range = (n) => Array.from({ length: n }, (_, i) => i);
  // Facings start at 7 (down-right) and turn anticlockwise; the cell is first + direction × frames + frame.
  const facings = (s) => range(s.directions).map((i) => (i + s.directions - 1) % s.directions);
  const out = {};
  // A unit's frames are move, move, action, action (style guide, item 3): cards and pages loop the first two.
  for (const s of units.table.sprites) out[s.id] = sheetOf(facings(s).map((d) => range(s.frames).map((f) => [[units, units.table.frames[s.first + d * s.frames + f]]])), 128, 2);
  const buildingFrames = (s) => range(s.directions).map((d) => range(s.frames).map((f) => [[buildings, buildings.table.frames[s.first + d * s.frames + f]]]));
  for (const s of buildings.table.sprites) if (s.id !== 'GunTurretHead' && s.id !== 'GunTurret') out[s.id] = sheetOf(buildingFrames(s), 512, s.frames);
  const head = buildings.table.sprites.find((s) => s.id === 'GunTurretHead'), base = buildings.table.sprites.find((s) => s.id === 'GunTurret');
  out.GunTurret = sheetOf(facings(head).map((d) => range(head.frames).map((f) => [[buildings, buildings.table.frames[base.first + f]], [buildings, buildings.table.frames[head.first + d * head.frames + f]]])), 256, head.frames);
  out.Wall = sheetOf([[[[tiles, frameOf(tiles, 'PlayerWall')]]]], 128, 1);
  return out;
}

// ---- The page ----

function render(cfg, text, sprites, fontUri, table) {
  const fill = resolver(cfg);
  const pageIds = {};
  for (const u of cfg.units) pageIds[nameOf(u.type)] = `unit-${u.type}`;
  for (const b of cfg.buildings) pageIds[nameOf(b.type)] = `building-${b.type}`;
  pageIds.Wall = 'building-Wall';
  pageIds['Damage table'] = 'damage';
  const link = (type, label) => `<a href="#${pageIds[nameOf(type)]}" data-go="${pageIds[nameOf(type)]}">${esc(label || nameOf(type))}</a>`;
  const words = (s) => esc(s)
    .replace(/\{([\w/]+)\}/g, (_, p) => fill(p))
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/\[\[(.+?)\]\]/g, (_, ref) => { const [n, label] = ref.split('|'); if (!pageIds[n]) throw new Error(`no page for [[${n}]]`); return `<a href="#${pageIds[n]}" data-go="${pageIds[n]}">${label || n}</a>`; });
  // A sprite is a <span> over its sheet; "move" loops its frames at 10 fps, "turn" also walks through its facings.
  const sprite = (type, box, mode = 'still') => {
    const m = sprites[type];
    if (!m) return `<span class="sprite missing">${esc(nameOf(type))}</span>`;
    const k = Math.min(box / m.cw, box / m.ch), w = Math.round(m.cw * k), h = Math.round(m.ch * k);
    const moving = m.loop > 1 && mode !== 'still', turning = m.rows > 1 && mode === 'turn';
    const animation = [moving ? `frames ${(m.loop / 10).toFixed(1)}s steps(${m.loop}) infinite` : '', turning ? `facings ${m.rows * 0.8}s steps(${m.rows}) infinite` : ''].filter(Boolean).join(', ');
    return `<span class="sprite" aria-hidden="true" style="width:${w}px;height:${h}px;background-image:url(${m.uri});background-size:${w * m.cols}px ${h * m.rows}px;--loop:${-w * m.loop}px;--turn:${-h * m.rows}px${animation ? `;--motion:${animation}` : ''}"></span>`;
  };
  const rows = (list) => list.filter(Boolean).map(([k, v]) => `<div class="stat"><dt>${esc(k)}</dt><dd>${v}</dd></div>`).join('');
  const card = (type, id, role) => `<a class="card" href="#${id}" data-go="${id}" data-name="${esc(nameOf(type).toLowerCase())}"><span class="art">${sprite(type, 72, 'move')}</span><span class="card-name">${esc(nameOf(type))}</span><span class="card-role">${esc(role)}</span></a>`;
  const need = (group, type) => { if (!text[group][type]) throw new Error(`manual-text.json: no ${group}.${type}`); return text[group][type]; };
  const pages = [];
  const addPage = (id, section, title, html) => pages.push({ id, section, title, html });
  const pageHead = (type, role, sectionId, sectionName) => `<p class="crumb"><a href="#${sectionId}" data-go="${sectionId}">${sectionName}</a></p><header class="page-head"><span class="art big">${sprite(type, 104, 'turn')}</span><div><h2>${esc(nameOf(type))}</h2><p class="role">${esc(role)}</p></div></header>`;

  const madeBy = (t) => Object.keys(PRODUCERS).find((b) => PRODUCERS[b].includes(t));
  const damageRow = (type, fighter) => `<table class="dmg-row"><thead><tr><th>Against</th><th>×</th><th>Per shot</th><th>Per second</th></tr></thead><tbody>${CLASSES.map((c) => {
    const m = multiplier(cfg, type, c);
    return `<tr><th>${CLASS_NAMES[c]}</th><td>${fmt(m)}</td><td>${fmt(fighter.Damage * m)}</td><td>${fmt(dps(fighter, m))}</td></tr>`;
  }).join('')}</tbody></table>`;
  const chips = (types) => (types.length ? types.map((t) => `<a class="mini" href="#${pageIds[nameOf(t)]}" data-go="${pageIds[nameOf(t)]}">${sprite(t, 32)}<span>${esc(nameOf(t))}</span></a>`).join('') : '<span class="none">No clear match-up</span>');

  const counterBlock = (type) => {
    if (!table[type]) return `<p class="note">${words(text.noFight)}</p>`;
    const c = counters(table, type);
    return `<div class="counters"><div><h3>Strong against</h3><div class="minis">${chips(c.strong)}</div></div><div><h3>Weak against</h3><div class="minis">${chips(c.weak)}</div></div></div><p class="note small">${words(type === 'SuicideBomber' ? text.counterNoteBomber : text.counterNote)}</p>`;
  };

  // Units.
  const unitCards = cfg.units.map((u) => card(u.type, `unit-${u.type}`, need('units', u.type).role)).join('');
  addPage('units', 'units', 'Units', `<h2>Units</h2><div class="cards">${unitCards}</div>`);
  for (const u of cfg.units) {
    const t = need('units', u.type), blast = !!u.BlastDamage;
    const stats = rows([
      ['Cost', fmt(u.UpfrontCost)], ['Build time', `${fmt(u.BuildSeconds)} s`], ['Upkeep', `${fmt(u.RunningCost)} /s`], ['Health', fmt(u.Health)], ['Armour', esc(u.Armour)],
      !blast && u.Damage ? ['Damage', `${fmt(u.Damage)} every ${fmt(u.AttackInterval)} s`] : null,
      !blast && u.Damage ? ['Per second', fmt(dps(u, 1))] : null,
      blast ? ['Blast', `${fmt(u.BlastDamage)}, once`] : null, blast ? ['Blast radius', `${fmt(u.BlastRadius)} tiles`] : null,
      !blast && u.Damage ? ['Range', `${fmt(u.Range)} tiles`] : null, u.MinRange ? ['Minimum range', `${fmt(u.MinRange)} tiles`] : null, u.Splash ? ['Splash', `${fmt(u.Splash)} tiles`] : null,
      ['Sight', `${fmt(u.Sight)} tiles`], ['Speed', `${fmt(u.MoveSpeed)} tiles/s`], ['Size', u.SizeClass ? 'Large' : 'Small'],
      ['Made by', link(madeBy(u.type))],
    ]);
    const table = blast
      ? `<table class="dmg-row"><thead><tr><th>Against</th><th>×</th><th>Blast</th></tr></thead><tbody>${CLASSES.map((k) => `<tr><th>${CLASS_NAMES[k]}</th><td>${fmt(multiplier(cfg, u.type, k))}</td><td>${fmt(u.BlastDamage * multiplier(cfg, u.type, k))}</td></tr>`).join('')}</tbody></table>`
      : u.Damage ? damageRow(u.type, u) : '';
    addPage(`unit-${u.type}`, 'units', nameOf(u.type), `${pageHead(u.type, t.role, 'units', 'Units')}<div class="split"><dl class="stats">${stats}</dl><div class="prose">${t.body.map((p) => `<p>${words(p)}</p>`).join('')}${table ? `<h3>Damage</h3>${table}` : ''}${counterBlock(u.type)}</div></div>`);
  }

  // Buildings.
  const mining = cfg.lookup('Resources/MiningRate');
  const specials = {
    Miner: () => [['Earns', `${fmt(mining)} /s`], ['On a rich gem', `${fmt(mining * cfg.lookup('Resources/RichGemMultiplier'))} /s`]],
    Bomb: () => [['Blast', fmt(cfg.lookup('Siege/BlastDamage'))], ['Blast radius', `${fmt(cfg.lookup('Siege/BlastRadius'))} tiles`]],
    Radar: () => [['Radar range', `${fmt(cfg.buildings.find((b) => b.type === 'Radar').Sight)} tiles`]],
    Jammer: () => [['Jam radius', `${fmt(cfg.lookup('Siege/JamRadius'))} tiles`], ['Sight inside a jam', `${fmt(cfg.lookup('Siege/JamSight'))} tiles`]],
    GunTurret: (b) => [['Damage', `${fmt(b.Damage)} every ${fmt(b.AttackInterval)} s`], ['Per second', fmt(dps(b, 1))], ['Range', `${fmt(b.Range)} tiles`]],
  };
  const power = cfg.lookup('Siege/BuildPower');
  const buildingCards = [...cfg.buildings.map((b) => card(b.type, `building-${b.type}`, need('buildings', b.type).role)), card('Wall', 'building-Wall', need('buildings', 'Wall').role)].join('');
  addPage('buildings', 'buildings', 'Buildings', `<h2>Buildings</h2><div class="cards">${buildingCards}</div>`);
  for (const b of cfg.buildings) {
    const t = need('buildings', b.type), makes = PRODUCERS[b.type];
    const stats = rows([
      ['Size', `${fmt(b.Width)} × ${fmt(b.Height)} tiles`], ['Cost', b.type === 'Base' ? 'Free' : fmt(b.UpfrontCost)], ['Upkeep', b.RunningCost ? `${fmt(b.RunningCost)} /s` : 'None'], ['Health', fmt(b.Health)],
      ['Sight', b.Sight ? `${fmt(b.Sight)} tiles` : 'None'], b.type === 'Base' ? null : ['Time for one Builder', `${fmt(b.Health / power)} s`],
      ...(specials[b.type] ? specials[b.type](b) : []),
    ]);
    const turret = b.type === 'GunTurret' ? `<h3>Damage</h3>${damageRow(b.type, b)}${counterBlock(b.type)}` : '';
    addPage(`building-${b.type}`, 'buildings', nameOf(b.type), `${pageHead(b.type, t.role, 'buildings', 'Buildings')}<div class="split"><dl class="stats">${stats}</dl><div class="prose">${t.body.map((p) => `<p>${words(p)}</p>`).join('')}${makes ? `<h3>Makes</h3><div class="minis">${chips(makes)}</div>` : ''}${turret}${b.type === 'Base' ? '' : `<p class="note">Starts as a blueprint that ${link('Builder', 'Builders')} build.</p>`}</div></div>`);
  }
  {
    const t = need('buildings', 'Wall');
    const stats = rows([['Cost', `${fmt(cfg.lookup('Siege/WallCost'))} a segment`], ['Upkeep', 'None'], ['Health', `${fmt(cfg.lookup('Siege/WallHealth'))} a segment`], ['Longest drag', `${fmt(cfg.lookup('Siege/MaxWallLine'))} segments`], ['Time for one Builder', `${fmt(cfg.lookup('Siege/WallHealth') / power)} s`]]);
    addPage('building-Wall', 'buildings', 'Wall', `${pageHead('Wall', t.role, 'buildings', 'Buildings')}<div class="split"><dl class="stats">${stats}</dl><div class="prose">${t.body.map((p) => `<p>${words(p)}</p>`).join('')}</div></div>`);
  }

  // Damage table.
  const attackers = [...cfg.units, cfg.buildings.find((b) => b.type === 'GunTurret')];
  const heat = (m) => (m === 0 ? 0 : m < 0.5 ? 1 : m < 1 ? 2 : m === 1 ? 3 : m <= 1.5 ? 4 : m <= 3 ? 5 : 6);
  const grid = `<div class="grid-wrap"><table class="grid"><thead><tr><th>Attacker</th>${CLASSES.map((c) => `<th>${CLASS_NAMES[c]}</th>`).join('')}</tr></thead><tbody>${attackers.map((a) => `<tr><th>${link(a.type)}</th>${CLASSES.map((c) => {
    const m = multiplier(cfg, a.type, c), blast = a.BlastDamage;
    const detail = blast ? `${nameOf(a.type)} against ${CLASS_NAMES[c].toLowerCase()}: a blast of ${fmt(blast * m)}` : !a.Damage ? `${nameOf(a.type)} does no damage` : `${nameOf(a.type)} against ${CLASS_NAMES[c].toLowerCase()}: ${fmt(a.Damage * m)} per shot, ${fmt(dps(a, m))} per second`;
    return `<td class="heat-${heat(m)}" tabindex="0" data-detail="${esc(detail)}">${fmt(m)}</td>`;
  }).join('')}</tr>`).join('')}</tbody></table></div><p class="readout" id="grid-readout" aria-live="polite">Hover a cell for its damage.</p>`;
  const legend = `<div class="legend"><span>Weak</span>${[1, 2, 3, 4, 5, 6].map((h) => `<i class="heat-${h}"></i>`).join('')}<span>Strong</span></div>`;
  addPage('damage', 'damage', 'Damage table', `<h2>Damage table</h2><p class="lead">${words(text.damageTable.lead)}</p>${grid}${legend}<div class="prose">${text.damageTable.notes.map((n) => `<p>${words(n)}</p>`).join('')}</div>`);

  // Rules.
  const diagrams = ruleDiagrams(cfg);
  const ruleCards = text.rules.map((r) => `<a class="rule-link" href="#rules-${r.id}" data-go="rules-${r.id}" data-name="${esc(r.title.toLowerCase())}"><span class="card-name">${esc(r.title)}</span><span class="card-role">${words(r.lead)}</span></a>`).join('');
  addPage('rules', 'rules', 'Rules', `<h2>Rules</h2><div class="rule-links">${ruleCards}</div>`);
  for (const r of text.rules) {
    addPage(`rules-${r.id}`, 'rules', r.title, `<p class="crumb"><a href="#rules" data-go="rules">Rules</a></p><h2>${esc(r.title)}</h2><p class="lead">${words(r.lead)}</p>${r.diagram ? `<figure class="diagram">${diagrams[r.diagram]}</figure>` : ''}<div class="prose rules">${r.sections.map((s) => `<h3>${esc(s.heading)}</h3>${s.paras.map((p) => `<p>${words(p)}</p>`).join('')}`).join('')}</div>`);
  }

  // Controls.
  addPage('controls', 'controls', 'Controls', `<div class="title-row"><h2>Controls</h2><button type="button" id="to-settings" class="btn">Change in Settings</button></div><p class="lead" id="settings-note">Your keys as they are set now. Default keys are shown here.</p><div class="controls">${text.controls.map((g) => `<section><h3>${esc(g.group)}</h3>${g.rows.map(([label, keys, isNew]) => `<div class="bind"><span>${esc(label)}${isNew ? ' <em>New</em>' : ''}</span><span class="keys">${words(keys)}</span></div>`).join('')}</section>`).join('')}</div>`);

  const sections = [['units', 'Units'], ['buildings', 'Buildings'], ['damage', 'Damage table'], ['rules', 'Rules'], ['controls', 'Controls']];
  const index = pages.filter((p) => !['units', 'buildings', 'rules'].includes(p.id)).map((p) => ({ id: p.id, title: p.title, section: sections.find((s) => s[0] === p.section)[1] }));

  return `<meta charset="utf-8"><title>Field Manual Mock</title>
<meta name="claude:viewport-fit-sides" content="cover">
<style>
/* A single dark look, the game's own: Theme.uss colours, Jersey 10, square corners and the v0.8 pixel bevel. Layout: the in-game manual's side list and page, at 1280×720 and up. */
@font-face { font-family: "Jersey 10"; src: url(${fontUri}) format("truetype"); font-display: block; }
:root {
  color-scheme: dark;
  --base: #26303A; --panel: #34404C; --raised: #40505F; --border: #52627A; --text: #F0F4F8; --muted: #AEBBCB; --disabled: #6B7A8C;
  --accent: #3FD0FF; --on-accent: #062633; --readout: #FFC857; --danger: #FF5263;
  --light: rgba(255,255,255,.22); --dark: rgba(0,0,0,.45);
  --game: "Jersey 10", "Courier New", monospace;
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--base); color: var(--text); font: 21px/1.3 var(--game); padding-block: 16px; padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); }
.window { max-width: 1248px; margin: 0 auto; background: var(--panel); box-shadow: inset 2px 2px 0 var(--light), inset -2px -2px 0 var(--dark); }
.bar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; padding: 12px 16px; background: var(--raised); box-shadow: inset 2px 2px 0 var(--light), inset 0 -2px 0 var(--dark); }
.bar h1 { font-size: 32px; font-weight: 400; margin: 0; line-height: 1; flex: 1 1 auto; }
#search { flex: 0 1 300px; min-width: 0; font: inherit; color: var(--text); background: var(--base); border: 2px solid var(--border); padding: 4px 10px; border-radius: 0; }
#search:focus { outline: 2px solid var(--accent); outline-offset: 0; }
#search::placeholder { color: var(--disabled); }
.chip { display: inline-block; font-size: 18px; line-height: 1; padding: 3px 6px 4px; background: var(--base); color: var(--text); box-shadow: inset 1px 1px 0 var(--light), inset -1px -1px 0 var(--dark); white-space: nowrap; }
.body { display: grid; grid-template-columns: 184px minmax(0, 1fr); }
nav { padding: 12px 8px; background: rgba(0,0,0,.14); display: flex; flex-direction: column; gap: 4px; }
nav a { display: block; padding: 8px 12px; color: var(--muted); text-decoration: none; font-size: 22px; }
nav a:hover { background: var(--raised); color: var(--text); }
nav a[aria-current="true"] { background: var(--accent); color: var(--on-accent); box-shadow: inset 2px 2px 0 var(--light), inset -2px -2px 0 var(--dark); }
main { padding: 16px 24px 24px; min-width: 0; min-height: 588px; }
main:focus { outline: none; }
h2 { font-size: 32px; font-weight: 400; margin: 0 0 12px; line-height: 1.05; text-wrap: balance; }
h3 { font-size: 22px; font-weight: 400; color: var(--readout); margin: 16px 0 6px; line-height: 1.1; }
p { margin: 0 0 10px; }
a { color: var(--accent); }
strong { font-weight: 400; color: var(--readout); }
.lead { color: var(--muted); max-width: 44em; }
.crumb { margin: 0 0 8px; font-size: 18px; }
.crumb a::before { content: "‹ "; }
.page { display: none; }
.page.on { display: block; }
.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(216px, 1fr)); gap: 12px; }
.card, .rule-link { display: flex; flex-direction: column; gap: 2px; padding: 10px 12px 12px; background: var(--raised); text-decoration: none; color: var(--text); box-shadow: inset 2px 2px 0 var(--light), inset -2px -2px 0 var(--dark); }
.card:hover, .card:focus-visible, .rule-link:hover, .rule-link:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.art { display: flex; align-items: center; justify-content: center; height: 88px; background: var(--base); margin-bottom: 6px; overflow: hidden; }
.card-name { font-size: 24px; line-height: 1.05; }
.card-role { font-size: 19px; line-height: 1.15; color: var(--muted); }
.sprite { image-rendering: pixelated; display: block; flex: none; background-repeat: no-repeat; background-position: 0 0; animation: var(--motion, none); }
.sprite.missing { font-size: 16px; color: var(--muted); border: 2px dashed var(--border); padding: 8px; }
@keyframes frames { to { background-position-x: var(--loop); } }
@keyframes facings { to { background-position-y: var(--turn); } }
.art.big { width: 120px; height: 120px; margin: 0; flex: none; }
.page-head { display: flex; gap: 16px; align-items: center; margin-bottom: 14px; }
.page-head h2 { margin-bottom: 2px; }
.role { color: var(--muted); margin: 0; }
.split { display: grid; grid-template-columns: 300px minmax(0, 1fr); gap: 24px; align-items: start; }
.stats { margin: 0; background: var(--base); padding: 6px 12px; }
.stat { display: flex; justify-content: space-between; gap: 12px; padding: 3px 0; border-bottom: 1px solid rgba(82,98,122,.5); }
.stat:last-child { border-bottom: 0; }
.stat dt { color: var(--muted); }
.stat dd { margin: 0; text-align: right; font-variant-numeric: tabular-nums; }
.prose { max-width: 40em; min-width: 0; }
.prose.rules { max-width: 46em; }
.note { color: var(--muted); }
.note.small { font-size: 18px; margin-top: 8px; }
.dmg-row { border-collapse: collapse; width: 100%; max-width: 460px; font-variant-numeric: tabular-nums; }
.dmg-row th, .dmg-row td { text-align: right; padding: 2px 8px; font-weight: 400; border-bottom: 1px solid rgba(82,98,122,.5); }
.dmg-row thead th { color: var(--muted); font-size: 18px; }
.dmg-row th:first-child { text-align: left; }
.counters { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 0 16px; }
.minis { display: flex; flex-wrap: wrap; gap: 6px; }
.mini { display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px 3px 3px; background: var(--raised); color: var(--text); text-decoration: none; font-size: 19px; }
.mini:hover, .mini:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.mini .sprite { background-color: transparent; }
.mini .sprite.missing { padding: 2px 4px; font-size: 14px; }
.none { color: var(--muted); }
.grid-wrap { overflow-x: auto; max-width: 720px; }
.grid { border-collapse: separate; border-spacing: 2px; width: 100%; font-variant-numeric: tabular-nums; }
.grid th { font-weight: 400; padding: 4px 8px; text-align: center; color: var(--muted); font-size: 19px; white-space: nowrap; }
.grid tbody th { text-align: left; font-size: 21px; }
.grid td { text-align: center; padding: 4px 8px; font-size: 22px; min-width: 76px; cursor: default; }
.grid td:hover, .grid td:focus { outline: 2px solid var(--accent); outline-offset: -2px; }
.heat-0 { background: var(--base); color: var(--disabled); }
.heat-1 { background: #3B3F46; color: var(--muted); }
.heat-2 { background: #574F3E; color: var(--text); }
.heat-3 { background: #7A6840; color: var(--text); }
.heat-4 { background: #A98A45; color: #1B1608; }
.heat-5 { background: #D9AB4D; color: #1B1608; }
.heat-6 { background: #FFC857; color: #1B1608; }
.readout { color: var(--readout); margin: 8px 0; min-height: 1.3em; }
.legend { display: flex; align-items: center; gap: 3px; color: var(--muted); font-size: 18px; margin-bottom: 12px; }
.legend i { width: 28px; height: 14px; display: block; }
.legend span:first-child { margin-right: 6px; } .legend span:last-child { margin-left: 6px; }
.rule-links { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }
.diagram { margin: 0 0 12px; background: var(--base); padding: 12px; max-width: 720px; }
.diagram svg { display: block; width: 100%; height: auto; }
.diagram text { font-family: var(--game); fill: var(--text); font-size: 18px; }
.diagram text.m { fill: var(--muted); }
.title-row { display: flex; flex-wrap: wrap; gap: 12px; justify-content: space-between; align-items: start; }
.btn { font: inherit; background: var(--raised); color: var(--text); border: 0; border-radius: 0; padding: 6px 14px; cursor: pointer; box-shadow: inset 2px 2px 0 var(--light), inset -2px -2px 0 var(--dark); }
.btn:hover { background: var(--border); } .btn:focus-visible { outline: 2px solid var(--accent); }
.controls { column-width: 420px; column-gap: 32px; }
.controls section { break-inside: avoid; }
.controls h3 { margin-top: 12px; }
.bind { display: flex; justify-content: space-between; gap: 12px; padding: 3px 0; border-bottom: 1px solid rgba(82,98,122,.5); }
.bind em { font-style: normal; color: var(--on-accent); background: var(--accent); padding: 0 5px; font-size: 16px; }
.keys { display: flex; flex-wrap: wrap; gap: 4px; justify-content: flex-end; align-items: center; flex: none; max-width: 60%; color: var(--muted); }
.results-note { color: var(--muted); }
.result-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); gap: 8px; }
.result { display: block; padding: 8px 12px; background: var(--raised); color: var(--text); text-decoration: none; }
.result:hover, .result:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.result small { display: block; font-size: 17px; color: var(--muted); }
.foot { max-width: 1248px; margin: 12px auto 0; color: var(--muted); font-size: 18px; }
@media (max-width: 860px) {
  .body { grid-template-columns: minmax(0, 1fr); }
  nav { flex-direction: row; flex-wrap: wrap; padding: 8px; }
  nav a { padding: 4px 10px; font-size: 20px; }
  main { padding: 12px; }
  .split { grid-template-columns: minmax(0, 1fr); }
  #search { flex: 1 1 100%; }
  .grid td { min-width: 56px; }
}
/* The approved v0.16 menu motion (Figma M1): forward slides left, back slides right, 200 ms; the screen rises 24 px in 180 ms; buttons lift and sink. Reduce motion swaps at once. */
.window { animation: rise .18s ease-out; }
.page.forward { animation: from-right .2s ease-out; }
.page.back { animation: from-left .2s ease-out; }
@keyframes rise { from { transform: translateY(24px); opacity: 0; } }
@keyframes from-right { from { transform: translateX(32px); opacity: 0; } }
@keyframes from-left { from { transform: translateX(-32px); opacity: 0; } }
.card, .rule-link, .mini, .btn, nav a, .result { transition: transform .1s ease-out, filter .1s ease-out, background-color .1s ease-out; }
.card:hover, .rule-link:hover, .mini:hover, .btn:hover, .result:hover { transform: translateY(-2px); filter: brightness(1.12); }
.card:active, .rule-link:active, .mini:active, .btn:active, .result:active { transform: translateY(1px); filter: brightness(.92); }
/* The rules' diagrams show what they explain. */
.page.on .grow { transform-box: fill-box; transform-origin: left center; animation: grow .6s ease-out backwards; }
@keyframes grow { from { transform: scaleX(0); } }
.page.on .blast { transform-box: fill-box; transform-origin: center; animation: blast 2.4s ease-out infinite; }
.page.on .blast.later { animation-delay: 1.2s; }
@keyframes blast { 0% { transform: scale(.1); fill-opacity: .7; } 35% { transform: scale(1); fill-opacity: .35; } 100% { transform: scale(1); fill-opacity: .12; } }
.page.on .flow { stroke-dasharray: 10 8; animation: flow .8s linear infinite; }
@keyframes flow { to { stroke-dashoffset: -18; } }
.page.on .sweep { transform-box: fill-box; transform-origin: center; animation: sweep 12s linear infinite; }
@keyframes sweep { to { transform: rotate(360deg); } }
.page.on .step { animation: step 4s steps(1) infinite; animation-delay: calc(var(--i) * 1s); }
@keyframes step { 0% { stroke: var(--accent); } 25%, 100% { stroke: var(--border); } }
@media (prefers-reduced-motion: reduce) {
  .sprite, .window, .page.forward, .page.back, .page.on .grow, .page.on .blast, .page.on .flow, .page.on .sweep, .page.on .step { animation: none; }
  .card, .rule-link, .mini, .btn, nav a, .result { transition: none; }
  .card:hover, .rule-link:hover, .mini:hover, .btn:hover, .result:hover, .card:active, .rule-link:active, .mini:active, .btn:active, .result:active { transform: none; }
}
</style>
<div class="window">
  <div class="bar"><h1>Field manual</h1><input id="search" type="search" placeholder="Search units, buildings, rules" aria-label="Search the manual" autocomplete="off"><span class="chip" title="Closes the manual in the game">F1 Close</span></div>
  <div class="body">
    <nav aria-label="Sections">${sections.map(([id, label]) => `<a href="#${id}" data-go="${id}" data-section="${id}">${label}</a>`).join('')}</nav>
    <main id="main" tabindex="-1">
      ${pages.map((p) => `<article class="page" id="p-${p.id}" data-section="${p.section}">${p.html}</article>`).join('\n')}
      <article class="page" id="p-search"><h2 id="search-title"></h2><div class="result-list" id="results"></div></article>
    </main>
  </div>
</div>
<p class="foot">Mock for review, generated from GameConfig.xml (v${esc(cfg.version)}). Sprites are from the built atlases in player colour 1.</p>
<script>
const INDEX = ${JSON.stringify(index)};
const store = { get() { try { return localStorage.getItem('manual-page'); } catch (e) { return null; } }, set(v) { try { localStorage.setItem('manual-page', v); } catch (e) {} } };
const search = document.getElementById('search');
function show(id, remember, way) {
  const page = document.getElementById('p-' + id) || document.getElementById('p-units');
  document.querySelectorAll('.page').forEach((p) => { p.classList.toggle('on', p === page); p.classList.remove('forward', 'back'); });
  if (way) page.classList.add(way);
  const section = page.dataset.section;
  document.querySelectorAll('nav a').forEach((a) => a.setAttribute('aria-current', a.dataset.section === section ? 'true' : 'false'));
  if (remember && id !== 'search') store.set(id);
}
function go(id, way) { search.value = ''; show(id, true, way); try { history.replaceState(null, '', '#' + id); } catch (e) {} window.scrollTo(0, 0); document.getElementById('main').focus({ preventScroll: true }); }
document.addEventListener('click', (e) => { const a = e.target.closest('[data-go]'); if (a) { e.preventDefault(); go(a.dataset.go, a.closest('.crumb') ? 'back' : 'forward'); } });
search.addEventListener('input', () => {
  const q = search.value.trim().toLowerCase();
  if (!q) { show(store.get() || 'units', false); return; }
  const hits = INDEX.filter((p) => p.title.toLowerCase().includes(q));
  document.getElementById('search-title').textContent = hits.length ? hits.length + (hits.length === 1 ? ' page' : ' pages') + ' for "' + search.value.trim() + '"' : 'Nothing called "' + search.value.trim() + '"';
  const list = document.getElementById('results');
  list.textContent = '';
  for (const h of hits) {
    const a = document.createElement('a'); a.className = 'result'; a.href = '#' + h.id; a.dataset.go = h.id; a.textContent = h.title;
    const s = document.createElement('small'); s.textContent = h.section; a.append(s); list.append(a);
  }
  show('search', false);
});
const readout = document.getElementById('grid-readout');
const detail = (e) => { const c = e.target.closest && e.target.closest('[data-detail]'); if (c) readout.textContent = c.dataset.detail; };
document.addEventListener('mouseover', detail); document.addEventListener('focusin', detail);
document.getElementById('to-settings').addEventListener('click', () => { document.getElementById('settings-note').textContent = 'In the game this button opens Settings → Controls. Default keys are shown here.'; });
const start = location.hash.slice(1);
show(start && document.getElementById('p-' + start) ? start : store.get() || 'units', false);
</script>
`;
}

/** The small diagrams on the rules pages, drawn to scale from the config. */
function ruleDiagrams(cfg) {
  const passive = cfg.lookup('Resources/PassiveGenerationRate'), mining = cfg.lookup('Resources/MiningRate'), rich = mining * cfg.lookup('Resources/RichGemMultiplier');
  const bld = (t) => cfg.buildings.find((b) => b.type === t), unit = (t) => cfg.units.find((u) => u.type === t);
  const bars = [
    ['Passive income', passive, '#5BE37D'], ['One Miner', mining, '#5BE37D'], ['Miner on a rich gem', rich, '#5BE37D'],
    ['10 Tanks, upkeep', 10 * unit('Tank').RunningCost, '#FF9A3D'], ['One Barracks, upkeep', bld('Barracks').RunningCost, '#FF9A3D'], ['One Heavy Factory, upkeep', bld('HeavyFactory').RunningCost, '#FF9A3D'],
  ];
  const max = Math.max(...bars.map((b) => b[1])), scale = 400 / max;
  const economy = `<svg viewBox="0 0 700 ${bars.length * 30 + 4}" role="img" aria-label="Income and upkeep per second, to scale">${bars.map(([n, v, c], i) => `<text x="0" y="${i * 30 + 20}" class="m">${n}</text><rect class="grow" style="animation-delay:${i * 60}ms" x="210" y="${i * 30 + 5}" width="${v * scale}" height="20" fill="${c}"/><text x="${218 + v * scale}" y="${i * 30 + 21}">${c === '#5BE37D' ? '+' : '−'}${fmt(v)} /s</text>`).join('')}</svg>`;

  // Fog: a Tank's sight on the 2-tile fog grid, with walls making a shadow, explored ground behind it.
  const size = cfg.lookup('Simulation/FogCellSize'), sight = unit('Tank').Sight;
  const cols = 34, rowsN = 14, ux = 12, uy = 7, px = 20;
  const cells = [];
  for (let y = 0; y < rowsN; y++) for (let x = 0; x < cols; x++) {
    const d = Math.hypot(x - ux, y - uy) * size;
    const wall = x === 18 && y >= 4 && y <= 10;
    const shadow = x > 18 && Math.abs((y - uy) / (x - ux)) < 3.5 / 6 + 0.02;
    const state = wall ? 'wall' : d <= sight && !shadow ? 'seen' : x < 18 ? 'explored' : 'dark';
    const fill = { seen: '#5A7F5A', explored: '#2F3B36', dark: '#0B0F13', wall: '#6B7A8C' }[state];
    cells.push(`<rect x="${x * px}" y="${y * px}" width="${px - 1}" height="${px - 1}" fill="${fill}"/>`);
  }
  const fog = `<svg viewBox="0 0 ${cols * px} ${rowsN * px + 32}" role="img" aria-label="Fog of war: visible ground around a Tank, a wall casting a shadow, explored ground dimmed, unexplored ground black">${cells.join('')}<circle cx="${ux * px + 10}" cy="${uy * px + 10}" r="${(sight / size) * px}" fill="none" stroke="#F0F4F8" stroke-dasharray="3 5" class="sweep"/><rect x="${ux * px + 3}" y="${uy * px + 3}" width="14" height="14" fill="#4DA3FF"/><text x="${ux * px - 22}" y="${uy * px - 4}">Tank</text><text x="20" y="${rowsN * px + 24}" class="m">Explored</text><text x="${ux * px - 8}" y="${rowsN * px + 24}" class="m">Visible (sight ${fmt(sight)})</text><text x="${18 * px - 14}" y="${rowsN * px + 24}" class="m">Wall</text><text x="${24 * px}" y="${rowsN * px + 24}" class="m">Unexplored</text></svg>`;

  const node = (x, y, c, n) => `<rect x="${x - 56}" y="${y - 20}" width="112" height="40" fill="#34404C" stroke="${c}" stroke-width="3"/><text x="${x}" y="${y + 6}" text-anchor="middle">${n}</text>`;
  const arrow = (x1, y1, x2, y2, c, label, lx, ly) => `<line class="flow" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${c}" stroke-width="3" marker-end="url(#h-${c.slice(1)})"/><text x="${lx}" y="${ly}" text-anchor="middle" class="m">${label}</text>`;
  const marker = (c) => `<marker id="h-${c.slice(1)}" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0 0 8 4 0 8z" fill="${c}"/></marker>`;
  const diplomacy = `<svg viewBox="0 0 700 170" role="img" aria-label="Diplomacy is one-way: you can stop attacking a player who still attacks you"><defs>${marker('#FF5263')}${marker('#5BE37D')}</defs>${node(100, 80, '#4DA3FF', 'You')}${node(600, 30, '#E5484D', 'IronFox')}${node(600, 140, '#46C46B', 'bluemoss')}${arrow(544, 30, 158, 66, '#FF5263', 'attacks you', 350, 28)}<line class="flow" x1="158" y1="90" x2="542" y2="130" stroke="#5BE37D" stroke-width="3" marker-end="url(#h-5BE37D)"/><text x="350" y="146" text-anchor="middle" class="m">you share vision</text><text x="100" y="134" text-anchor="middle" class="m">You don't have to</text><text x="100" y="156" text-anchor="middle" class="m">attack back</text></svg>`;

  // Siege: blast circles to scale over a tile grid, with a 2 × 2 Barracks for size.
  const t = 22, gridW = 30, gridH = 8;
  const bombR = cfg.lookup('Siege/BlastRadius'), bomberR = unit('SuicideBomber').BlastRadius;
  let tiles = '';
  for (let x = 0; x <= gridW; x++) tiles += `<line x1="${x * t}" y1="0" x2="${x * t}" y2="${gridH * t}" stroke="#34404C"/>`;
  for (let y = 0; y <= gridH; y++) tiles += `<line x1="0" y1="${y * t}" x2="${gridW * t}" y2="${y * t}" stroke="#34404C"/>`;
  const circle = (x, r, c, n, later) => `<circle class="blast${later ? ' later' : ''}" cx="${x * t}" cy="${4 * t}" r="${r * t}" fill="${c}" fill-opacity=".22" stroke="${c}" stroke-width="2"/><rect x="${x * t - t / 2}" y="${4 * t - t / 2}" width="${t}" height="${t}" fill="${c}"/><text x="${x * t}" y="${gridH * t + 24}" text-anchor="middle" class="m">${n}</text>`;
  const siege = `<svg viewBox="0 0 ${gridW * t} ${gridH * t + 34}" role="img" aria-label="Blast sizes to scale on the tile grid">${tiles}${circle(5, bombR, '#FF9A3D', `Bomb, radius ${fmt(bombR)}`)}${circle(14, bomberR, '#FF5263', `Bomber, radius ${fmt(bomberR)}`, true)}<rect x="${21 * t}" y="${3 * t}" width="${2 * t}" height="${2 * t}" fill="#4DA3FF"/><text x="${22 * t}" y="${gridH * t + 24}" text-anchor="middle" class="m">Barracks (2 × 2)</text><text x="${gridW * t - 4}" y="16" text-anchor="end" class="m">1 square = 1 tile</text></svg>`;

  const stepsW = 164, steps = ['HQ destroyed', 'Eliminated: all you own is lost', 'You can watch the rest', 'Survivors at peace: they win'];
  const winning = `<svg viewBox="0 0 700 92" role="img" aria-label="HQ destroyed, then eliminated, then spectating; the match ends when the survivors are at peace"><defs>${marker('#AEBBCB')}</defs>${steps.map((s, i) => { const x = i * (stepsW + 12); const w = s.split(': '); return `<rect class="step" style="--i:${i}" x="${x}" y="8" width="${stepsW}" height="72" fill="#34404C" stroke="#52627A" stroke-width="2"/><text x="${x + stepsW / 2}" y="${w[1] ? 40 : 50}" text-anchor="middle">${w[0]}${w[1] ? ':' : ''}</text>${w[1] ? `<text x="${x + stepsW / 2}" y="60" text-anchor="middle" class="m" style="font-size:16px">${w[1]}</text>` : ''}${i < 3 ? `<line x1="${x + stepsW}" y1="44" x2="${x + stepsW + 11}" y2="44" stroke="#AEBBCB" stroke-width="2" marker-end="url(#h-AEBBCB)"/>` : ''}`; }).join('')}</svg>`;

  return { economy, fog, diplomacy, siege, winning };
}

if (require.main === module) {
  const args = process.argv.slice(2);
  const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
  if (args.includes('--write-text')) {
    const json = fs.readFileSync(path.join(__dirname, 'manual-text.json'), 'utf8').replace(/\r/g, '');
    const target = path.join(ROOT, 'Assets/Scripts/UI/Models/ManualText.Data.cs');
    fs.writeFileSync(target, `// Written by tools/manual-mock.js --write-text from tools/manual-text.json, the words the owner approved. Don't edit by hand.\nnamespace WAR2D.UI\n{\n    public static partial class ManualText\n    {\n        private const string Json = @"${json.replace(/"/g, '""')}";\n    }\n}\n`);
    process.stdout.write(`${target}\n`);
    process.exit(0);
  }
  const out = opt('--out', null);
  if (!out) { process.stderr.write('Usage: node tools/manual-mock.js [--config <GameConfig.xml>] --out <file.html>\n'); process.exit(2); }
  const cfg = loadConfig(opt('--config', path.join(ROOT, 'Assets/Resources/GameConfig.xml')));
  const text = JSON.parse(fs.readFileSync(path.join(__dirname, 'manual-text.json'), 'utf8'));
  const font = fs.readFileSync(path.join(ROOT, 'Assets/UI/Fonts/Jersey10-Regular.ttf')).toString('base64');
  const configFile = opt('--config', path.join(ROOT, 'Assets/Resources/GameConfig.xml'));
  const table = counterTable(configFile);
  const problems = check(cfg, text, table);
  if (problems.length) { process.stderr.write(`The manual contradicts itself:\n${problems.map((p) => `- ${p}`).join('\n')}\n`); process.exit(1); }
  const html = render(cfg, text, spriteSheet(), `data:font/ttf;base64,${font}`, table);
  fs.writeFileSync(out, html);
  const report = [...cfg.units.map((u) => u.type), 'GunTurret'].map((t) => { const c = counters(table, t); return `${t}: strong ${c.strong.join(', ') || '-'}; weak ${c.weak.join(', ') || '-'}`; });
  process.stdout.write(`${out}: ${(html.length / 1024).toFixed(0)} KB, checks passed\n${report.join('\n')}\n`);
}

module.exports = { loadConfig, counterTable, counters, check };
