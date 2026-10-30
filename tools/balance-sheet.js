#!/usr/bin/env node
// A one-page balance sheet worked out from GameConfig.xml alone (Node, no Unity, well under a second): per unit its
// damage per second against each target class, range, speed, price and upkeep; the economy (income, producers, how
// fast a producer can spend); equal-cost fights between every pair of army units; what it takes to kill a Gun
// turret; and a list of flags (outliers worth a look). Read the flags first.
//
// The fights are a model, not the game: two armies of the same budget on open ground, the shorter-ranged one
// walking in under fire, then every unit firing (per shot, at its AttackInterval) at the enemy's front unit, the
// excess of a killing shot wasted; a unit can't fire inside its MinRange. Splash (Flamethrower, Artillery) also hits
// the units behind the target, 100% at the centre falling to 50% at the edge, as many as the target army's packing
// puts in the radius. No formation depth, kiting, retreats or focus fire: a first look, to confirm in the AI bench.
//
// Usage: node tools/balance-sheet.js [config=Assets/Resources/GameConfig.xml] [--budget 3000] [--packing 0.2]
//                                    [--horizon 300] [--out <file>]
//   --budget   resources per side in the fights (default 3000; large enough that whole units round off little)
//   --packing  share of the ground an army's bodies cover, for splash (default 0.2; 0 turns splash off)
//   --horizon  seconds of upkeep in "cost over time" (default 300, a 5-minute life)
'use strict';
const fs = require('fs');
const path = require('path');
const xml = require('./lib/config-xml');

const CLASSES = ['LightUnit', 'HeavyUnit', 'Building', 'Wall'];
// Mirrors SpawnerRules.Produces (code, not config): keep the two in step.
const PRODUCERS = { Base: ['Builder'], Barracks: ['Scout', 'Tank', 'Flamethrower'], HeavyFactory: ['Heavy', 'Artillery', 'Sniper'], SiegeWorkshop: ['Digger', 'SuicideBomber'] };
// The producers of the main army (the Siege workshop's Diggers and bombers are special units, the HQ makes Builders).
const ARMY_PRODUCERS = ['Barracks', 'HeavyFactory'];
const SHORT = { Flamethrower: 'Flame', Artillery: 'Arty', SuicideBomber: 'Bomber' };
const short = (t) => SHORT[t] || t;

function load(src) {
  const root = xml.parse(src);
  const gc = root.children.find((c) => c.tag === 'GameConfig');
  const one = (el, tag) => el && el.children.find((c) => c.tag === tag);
  const num = (el, tag, def) => { const c = one(el, tag); return c ? Number(xml.text(src, c)) : def; };
  const attr = (el, name) => (el.attrs.find(([k]) => k === name) || [])[1];
  const table = {};
  for (const e of one(gc, 'DamageTable').children.filter((c) => c.tag === 'Entry')) {
    const a = attr(e, 'attacker'), t = attr(e, 'target'), v = Number(xml.text(src, e));
    table[a] = table[a] || {};
    for (const cls of t === 'Unit' ? ['LightUnit', 'HeavyUnit'] : [t]) table[a][cls] = v;
  }
  const mult = (type) => Object.fromEntries(CLASSES.map((c) => [c, (table[type] || {})[c] ?? 1]));
  const units = one(gc, 'Units').children.filter((c) => c.tag === 'Unit').map((u) => {
    const type = attr(u, 'type');
    return {
      type, hp: num(u, 'Health'), dmg: num(u, 'Damage', 0), range: num(u, 'Range'), interval: num(u, 'AttackInterval', 1),
      speed: num(u, 'MoveSpeed', 0), cost: num(u, 'UpfrontCost'), upkeep: num(u, 'RunningCost', 0), radius: num(u, 'Radius', 0.35),
      cls: `${xml.text(src, one(u, 'Armour'))}Unit`, build: num(u, 'BuildSeconds', 1), splash: num(u, 'Splash', 0), minRange: num(u, 'MinRange', 0),
      blastRadius: num(u, 'BlastRadius', 0), blastDamage: num(u, 'BlastDamage', 0), mult: mult(type),
      madeIn: Object.keys(PRODUCERS).find((b) => PRODUCERS[b].includes(type)) || '?',
    };
  });
  const buildings = Object.fromEntries(one(gc, 'Buildings').children.filter((c) => c.tag === 'Building').map((b) => {
    const type = attr(b, 'type');
    return [type, { type, hp: num(b, 'Health'), cost: num(b, 'UpfrontCost', 0), upkeep: num(b, 'RunningCost', 0), dmg: num(b, 'Damage', 0), range: num(b, 'Range', 0), interval: num(b, 'AttackInterval', 1), mult: mult(type) }];
  }));
  const res = one(gc, 'Resources'), siege = one(gc, 'Siege');
  const difficulties = (one(gc, 'Ai') ? one(gc, 'Ai').children.filter((c) => c.tag === 'Difficulty') : []).map((d) => ({ name: attr(d, 'name'), incomePerProducer: num(d, 'IncomePerProducer', 0) }));
  return {
    units, buildings, difficulties,
    passive: num(res, 'PassiveGenerationRate', 0), mining: num(res, 'MiningRate', 0),
    wallCost: num(siege, 'WallCost', 0), wallHp: num(siege, 'WallHealth', 0),
  };
}

const dps = (f, cls) => (f.dmg / f.interval) * f.mult[cls];

/** Splash targets behind the one hit, weighted by the falloff (100% at the centre to 50% at the edge: 2πR²/3 per unit of density). */
function splashWeight(attacker, target, packing) {
  if (!attacker.splash || !packing || !target.radius) return 0;
  const density = packing / (Math.PI * target.radius * target.radius);
  return (density * 2 * Math.PI * attacker.splash * attacker.splash) / 3;
}

/**
 * One fight: nA of A against nB of B (B may be a building with speed 0). Returns { winner: 0 | 1 | null, keep, seconds },
 * keep being the winner's health left as a share of what it started with.
 */
function duel(A, nA, B, nB, packing) {
  const sides = [{ f: A, hp: new Array(nA).fill(A.hp), due: 0 }, { f: B, hp: new Array(nB).fill(B.hp), due: 0 }];
  const dt = 0.05;
  let gap = Math.max(A.range, B.range), t = 0;
  const alive = (s) => s.hp.reduce((n, h) => n + (h > 0), 0);
  while (t < 600) {
    const counts = sides.map(alive);
    if (!counts[0] || !counts[1]) break;
    // The side out of range walks in (the longer-ranged one holds).
    let moved = false;
    for (const s of sides) if (gap > s.f.range && s.f.speed > 0) { gap = Math.max(s.f.range, gap - s.f.speed * dt); moved = true; }
    // Both sides fire on the counts at the start of the tick, then the hits land.
    const can = sides.map((s) => s.f.dmg > 0 && gap <= s.f.range + 1e-9 && gap >= s.f.minRange - 1e-9);
    if (!moved && !can[0] && !can[1]) return { winner: null, keep: 0, seconds: t }; // nobody can close or fire
    const volleys = sides.map((s, i) => {
      if (!can[i]) { s.due = 0; return 0; }
      s.due += (counts[i] * dt) / s.f.interval;
      const shots = Math.floor(s.due);
      s.due -= shots;
      return shots;
    });
    sides.forEach((s, i) => {
      const enemy = sides[1 - i];
      const hit = s.f.dmg * s.f.mult[enemy.f.cls];
      const w = splashWeight(s.f, enemy.f, packing);
      for (let k = 0; k < volleys[i]; k++) {
        const front = enemy.hp.findIndex((h) => h > 0);
        if (front < 0) break;
        const behind = [];
        if (w > 0) for (let j = front + 1; j < enemy.hp.length && behind.length < Math.ceil(w); j++) if (enemy.hp[j] > 0) behind.push(j);
        enemy.hp[front] = Math.max(0, enemy.hp[front] - hit);
        if (behind.length) {
          const each = (hit * Math.min(w, behind.length)) / behind.length;
          for (const j of behind) enemy.hp[j] = Math.max(0, enemy.hp[j] - each);
        }
      }
    });
    t += dt;
  }
  const counts = sides.map(alive);
  if (counts[0] && counts[1]) return { winner: null, keep: 0, seconds: t };
  const winner = counts[0] ? 0 : 1;
  const s = sides[winner];
  return { winner, keep: s.hp.reduce((a, b) => a + b, 0) / (s.hp.length * s.f.hp), seconds: t, left: counts[winner] };
}

function sheet(cfg, opts) {
  const { budget, packing, horizon } = opts;
  const army = cfg.units.filter((u) => u.dmg > 0 && !u.blastDamage);
  const turretB = cfg.buildings.GunTurret;
  const hq = cfg.buildings.Base;
  const turret = turretB && { ...turretB, speed: 0, minRange: 0, cls: 'Building', radius: 0 };
  const lines = [];
  const flags = [];
  const r1 = (x) => (Math.round(x * 10) / 10).toString();

  lines.push('# Balance sheet', '', `From \`${opts.config}\`. Fights: ${budget} resources a side, packing ${packing}, upkeep over ${horizon} s. A model (open ground, everyone fires once in range, no micro): confirm what it flags in the AI bench.`, '');

  // Units.
  lines.push('## Units', '', `| Unit | Made in | Armour | HP | DPS light/heavy/bldg/wall | Range | Speed | Cost | Upkeep/s | Cost over ${horizon / 60} min | Per Miner | Build s | HQ s @${budget} | Wall s |`, '| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |');
  for (const u of cfg.units) {
    const d = u.blastDamage ? `blast ${u.blastDamage} r${u.blastRadius}` : CLASSES.map((c) => r1(dps(u, c))).join('/') + (u.splash ? ` +splash ${u.splash}` : '');
    const n = Math.floor(budget / u.cost);
    const hqS = dps(u, 'Building') > 0 && hq ? r1(hq.hp / (n * dps(u, 'Building'))) : '–';
    const wallS = dps(u, 'Wall') > 0 ? r1(cfg.wallHp / dps(u, 'Wall')) : '–';
    const range = u.range + (u.minRange ? ` (min ${u.minRange})` : '');
    const perMiner = u.upkeep > 0 ? r1(cfg.mining / u.upkeep) : '∞';
    lines.push(`| ${u.type} | ${u.madeIn} | ${u.cls.replace('Unit', '')} | ${u.hp} | ${d} | ${range} | ${u.speed} | ${u.cost} | ${u.upkeep} | ${Math.round(u.cost + horizon * u.upkeep)} | ${perMiner} | ${u.build} | ${hqS} | ${wallS} |`);
  }
  lines.push('', `Per Miner: how many of the unit one Miner's income (${cfg.mining}/s) pays upkeep for. HQ s: seconds for ${budget} resources of it to destroy an HQ (${hq ? hq.hp : '?'} HP) unopposed. Wall s: seconds one unit takes per wall segment (${cfg.wallHp} HP).`, '');

  // Economy.
  const spend = Object.entries(PRODUCERS).map(([b, types]) => {
    const us = types.map((t) => cfg.units.find((u) => u.type === t)).filter(Boolean);
    const best = us.reduce((m, u) => (u.cost / u.build > m.rate ? { rate: u.cost / u.build, u } : m), { rate: 0, u: null });
    return { b, rate: best.rate, unit: best.u && best.u.type };
  });
  const B = cfg.buildings;
  const bline = (t) => (B[t] ? `${t} ${B[t].cost} + ${B[t].upkeep}/s${B[t].upkeep && cfg.mining ? ` (${r1(B[t].upkeep / cfg.mining)} Miners)` : ''}` : null);
  const upkeepRatio = army.map((u) => (horizon * u.upkeep) / u.cost);
  lines.push('## Economy', '',
    `- Income: passive ${cfg.passive}/s; a Miner (${B.Miner ? B.Miner.cost : '?'}) makes ${cfg.mining}/s while it faces a gem, paying for itself in ${B.Miner ? r1(B.Miner.cost / cfg.mining) : '?'} s.`,
    `- Producers: ${['Barracks', 'HeavyFactory', 'SiegeWorkshop'].map(bline).filter(Boolean).join('; ')}. Defences: ${['GunTurret', 'Jammer', 'Radar', 'Bomb'].map(bline).filter(Boolean).join('; ')}; a wall segment ${cfg.wallCost} for ${cfg.wallHp} HP, no upkeep.`,
    `- Most a producer can spend (cost ÷ build time): ${spend.map((s) => `${s.b} ${r1(s.rate)}/s (${s.unit})`).join(', ')}.`,
    `- Upkeep over ${horizon} s is ${r1(Math.min(...upkeepRatio))}–${r1(Math.max(...upkeepRatio))}× an army unit's price.`, '');
  const maxSpend = Math.max(...spend.filter((s) => ARMY_PRODUCERS.includes(s.b)).map((s) => s.rate));
  for (const d of cfg.difficulties) {
    if (d.incomePerProducer > maxSpend) flags.push(`AI ${d.name}: a new producer needs ${d.incomePerProducer}/s of income per producer it has (IncomePerProducer), but a ${ARMY_PRODUCERS.join(' or ')} can spend at most ${r1(maxSpend)}/s, so income its producers can't use goes to upkeep or sits in the bank.`);
  }

  // Equal-cost fights.
  const fight = (A, Bu, pk) => duel(A, Math.max(1, Math.floor(budget / A.cost)), Bu, Math.max(1, Math.floor(budget / Bu.cost)), pk);
  const cell = (r) => (r.winner === null ? '=' : r.winner === 0 ? `+${Math.round(r.keep * 100)}` : `-${Math.round(r.keep * 100)}`);
  const results = {};
  for (const A of army) for (const Bu of army) if (A !== Bu) results[`${A.type}|${Bu.type}`] = { with: fight(A, Bu, packing), without: packing ? fight(A, Bu, 0) : null };
  lines.push('## Equal-cost fights', '', `Row against column, ${budget} resources each: +62 = the row wins with 62% of its health left, -40 = the column wins with 40% left, = a draw (10 minutes).`, '',
    `| | ${army.map((u) => short(u.type)).join(' | ')} | Wins |`, `| --- | ${army.map(() => '---').join(' | ')} | --- |`);
  for (const A of army) {
    const row = army.map((Bu) => (A === Bu ? '·' : cell(results[`${A.type}|${Bu.type}`].with)));
    const wins = army.filter((Bu) => Bu !== A && results[`${A.type}|${Bu.type}`].with.winner === 0).length;
    lines.push(`| **${short(A.type)}** | ${row.join(' | ')} | ${wins} |`);
    if (wins === army.length - 1) flags.push(`${A.type} beats every other army unit at equal cost.`);
    if (wins === 0) flags.push(`${A.type} loses to every other army unit at equal cost (fine only if it's a utility unit).`);
  }
  lines.push('');
  // Seat order shouldn't matter; a fight that comes out differently both ways round is a model artefact worth knowing.
  for (const A of army) for (const Bu of army) {
    if (A.type >= Bu.type) continue;
    const ab = results[`${A.type}|${Bu.type}`], ba = results[`${Bu.type}|${A.type}`];
    if (ab.with.winner !== null && ba.with.winner !== null && ab.with.winner === ba.with.winner) flags.push(`${A.type} vs ${Bu.type}: the result depends on who is listed first (a model artefact; treat it as even).`);
    if (packing && ab.without && ab.with.winner !== ab.without.winner) {
      const w = (r) => (r.winner === null ? 'draws' : r.winner === 0 ? `${A.type} wins` : `${Bu.type} wins`);
      flags.push(`${A.type} vs ${Bu.type}: splash decides it (${w(ab.without)} spread out, ${w(ab.with)} at packing ${packing}).`);
    }
  }
  // Clear stat dominance: no worse anywhere, cheaper or better somewhere.
  for (const A of army) for (const Bu of army) {
    if (A === Bu) continue;
    const ge = [A.hp >= Bu.hp, A.range >= Bu.range, A.speed >= Bu.speed, A.cost <= Bu.cost, A.upkeep <= Bu.upkeep, A.minRange <= Bu.minRange, A.splash >= Bu.splash, ...CLASSES.map((c) => dps(A, c) >= dps(Bu, c))];
    const gt = [A.hp > Bu.hp, A.range > Bu.range, A.speed > Bu.speed, A.cost < Bu.cost, A.upkeep < Bu.upkeep, A.minRange < Bu.minRange, A.splash > Bu.splash, ...CLASSES.map((c) => dps(A, c) > dps(Bu, c))];
    if (ge.every(Boolean) && gt.some(Boolean)) flags.push(`${A.type} is at least as good as ${Bu.type} in every number: ${Bu.type} has no reason to exist.`);
  }

  // Gun turret.
  if (turret) {
    lines.push('## Against a Gun turret', '', `The fewest units that kill one turret (${turret.hp} HP, ${r1(dps(turret, 'LightUnit'))}/${r1(dps(turret, 'HeavyUnit'))} DPS against light/heavy, range ${turret.range}, ${turret.cost} + ${turret.upkeep}/s) and what they lose.`, '',
      '| Unit | Units | Resources (× turret cost) | Lost | Seconds |', '| --- | --- | --- | --- | --- |');
    for (const u of army) {
      let n = 1, r = duel(u, 1, turret, 1, 0);
      while (r.winner !== 0 && n < 400) { n++; r = duel(u, n, turret, 1, 0); }
      if (r.winner !== 0) { lines.push(`| ${u.type} | > 400 | – | – | – |`); continue; }
      const lost = (n - r.left) * u.cost;
      lines.push(`| ${u.type} | ${n} | ${n * u.cost} (${r1((n * u.cost) / turret.cost)}×) | ${lost} | ${Math.round(r.seconds)} |`);
      if (u.range > turret.range) flags.push(`${u.type} out-ranges the Gun turret (${u.range} vs ${turret.range}): one kills it unharmed in ${Math.round(r.seconds)} s.`);
      else if ((n * u.cost) / turret.cost >= 3) flags.push(`A Gun turret holds off ${n} ${u.type} units, ${r1((n * u.cost) / turret.cost)}× its cost.`);
      else if (lost <= turret.cost / 3) flags.push(`${n === 1 ? 'One' : n} ${u.type} unit${n > 1 ? 's kill' : ' kills'} a Gun turret (cost ${turret.cost}) ${lost ? `losing only ${lost}` : 'without a loss'}.`);
    }
    lines.push('');
  }
  const tank = cfg.units.find((x) => x.type === 'Tank');
  for (const u of cfg.units.filter((x) => x.blastDamage)) {
    const blast = `${u.type}: one blast of ${u.blastDamage} (× class: ${CLASSES.map((c) => `${c.replace('Unit', '')} ${Math.round(u.blastDamage * u.mult[c])}`).join(', ')}) in radius ${u.blastRadius}, friends included`;
    if (!tank) { lines.push(`${blast}.`, ''); continue; }
    const hits = 1 + (packing / (Math.PI * tank.radius * tank.radius)) * Math.PI * u.blastRadius * u.blastRadius;
    const killed = hits * Math.min(1, (u.blastDamage * u.mult[tank.cls]) / tank.hp);
    lines.push(`${blast}. In a Tank clump at packing ${packing} it reaches about ${r1(hits)} Tanks and kills ${r1(killed)}: ${r1((killed * tank.cost) / u.cost)}× its cost, if it gets there.`, '');
  }

  lines.push('## Flags', '', ...(flags.length ? flags.map((f) => `- ${f}`) : ['None.']));
  return lines.join('\n') + '\n';
}

if (require.main === module) {
  const args = process.argv.slice(2);
  const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
  const positional = args.filter((a, i) => !a.startsWith('--') && !(i > 0 && args[i - 1].startsWith('--')));
  const root = path.resolve(__dirname, '..');
  const config = positional[0] || path.join(root, 'Assets/Resources/GameConfig.xml');
  const opts = { config: path.relative(root, path.resolve(config)) || config, budget: Number(opt('--budget', 3000)), packing: Number(opt('--packing', 0.2)), horizon: Number(opt('--horizon', 300)) };
  const text = sheet(load(fs.readFileSync(config, 'utf8')), opts);
  const out = opt('--out', null);
  if (out) fs.writeFileSync(out, text);
  process.stdout.write(text);
}

module.exports = { load, duel, sheet };
