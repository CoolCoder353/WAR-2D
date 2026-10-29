#!/usr/bin/env node
// A short summary of AI bench results (tools/ai-bench.sh): about eight lines in place of the full report, for quick
// tuning runs: per pairing, the stronger side's win rate with a 95% Wilson interval (by map size when there are
// several) and the matches that didn't finish; the stronger side's wins by its style and mirror wins by style; per
// difficulty, the economy at minute 10 and the mean highest balance; why each capped match didn't end; and which
// config it played (with the values that differ from the repo's). The exit criteria are the full report's: every match
// ends, and the stronger side wins at least 80% of every non-mirror pairing on every map size.
//
// Usage: node tools/ai-bench-brief.js <results-dir>... [--json]
//        (tools/ai-bench.sh --brief runs it; tools/ai-tune.js uses it as a module)
'use strict';
const fs = require('fs');
const path = require('path');
const xml = require('./lib/config-xml');

const ORDER = { Easy: 0, Normal: 1, Hard: 2 };
const STYLES = ['Rusher', 'Turtle', 'Expander', 'Balanced'];
const PAIRING_ORDER = ['Hard-Normal', 'Normal-Easy', 'Hard-Easy', 'Hard-Hard', 'Normal-Normal', 'Easy-Easy'];

/** Every result in the bench directories, plus an error entry for each job that never wrote one (as the full report). */
function loadGames(dirs) {
  const games = [];
  for (const dir of dirs) {
    const have = new Set();
    for (const f of fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort()) {
      let g;
      try { g = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')); } catch { g = { outcome: 'error', error: 'unreadable result', seats: [] }; }
      const [size, pairing, seed] = f.slice(0, -5).split('_');
      if (!pairing) continue;
      games.push({ ...g, file: f, size: g.size || Number(size), pairing, seed: g.seed || Number(seed), seats: g.seats || [] });
      have.add(f);
    }
    const jobs = path.join(dir, 'jobs.txt');
    if (!fs.existsSync(jobs)) continue;
    for (const line of fs.readFileSync(jobs, 'utf8').split('\n').filter(Boolean)) {
      const name = line.split(/\s+/)[0];
      if (have.has(`${name}.json`)) continue;
      const [size, pairing, seed] = name.split('_');
      games.push({ file: `${name}.json`, size: Number(size), pairing, seed: Number(seed), outcome: 'error', error: 'no result (crashed or killed)', seats: [], matchSeconds: 0 });
    }
  }
  return games;
}

const isMirror = (pairing) => { const [a, b] = pairing.split('-'); return a === b; };

/** The stronger and the weaker seat of a non-mirror match. */
function strongWeak(g) {
  const [a, b] = g.seats;
  return ORDER[a.difficulty] > ORDER[b.difficulty] ? [a, b] : [b, a];
}

/** The 95% Wilson score interval for k successes in n trials, as [low, high]. */
function wilson(k, n, z = 1.96) {
  if (!n) return [0, 1];
  const p = k / n, d = 1 + (z * z) / n;
  const c = (p + (z * z) / (2 * n)) / d;
  const h = (z * Math.sqrt((p * (1 - p)) / n + (z * z) / (4 * n * n))) / d;
  return [Math.max(0, c - h), Math.min(1, c + h)];
}

/** One line of a seat's per-minute timeline (Dev/AiBench.cs), or null. */
function parseMinute(line) {
  const m = /^(\d+)m res (-?\d+) inc (-?\d+) upk (-?\d+) army (\d+) bld (\d+)/.exec(line || '');
  if (!m) return null;
  const eb = / eb (\d+) /.exec(line), mode = / (\w+) free -?\d+ rq/.exec(line);
  return { minute: +m[1], res: +m[2], inc: +m[3], upk: +m[4], army: +m[5], bld: +m[6], bases: eb ? +eb[1] : null, mode: mode ? mode[1] : '?' };
}

const median = (xs) => { if (!xs.length) return NaN; const s = [...xs].sort((a, b) => a - b), m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; };
const mean = (xs) => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : NaN);

/** Everything the brief, the tuner's scores and the exit criteria need, from a list of games. */
function summarise(games) {
  const sizes = [...new Set(games.map((g) => g.size))].sort((a, b) => a - b);
  const pairings = PAIRING_ORDER.filter((p) => games.some((g) => g.pairing === p));
  const fails = games.filter((g) => g.outcome !== 'win');
  let met = fails.length === 0;
  const rows = pairings.map((p) => {
    const gs = games.filter((g) => g.pairing === p);
    const mirror = isMirror(p);
    const wonByStronger = (g) => g.outcome === 'win' && g.seats.length === 2 && strongWeak(g)[0].won;
    const bySize = sizes.map((s) => {
      const c = gs.filter((g) => g.size === s);
      const wins = mirror ? 0 : c.filter(wonByStronger).length;
      if (!mirror && c.length && wins / c.length < 0.8) met = false;
      return { size: s, games: c.length, wins };
    }).filter((c) => c.games);
    const wins = mirror ? 0 : gs.filter(wonByStronger).length;
    return {
      pairing: p, mirror, games: gs.length, wins, rate: mirror ? null : wins / gs.length, ci: mirror ? null : wilson(wins, gs.length),
      bySize, caps: gs.filter((g) => g.outcome === 'cap').length, draws: gs.filter((g) => g.outcome === 'draw').length,
      errors: gs.filter((g) => g.outcome === 'error').length,
      medianMinutes: median(gs.filter((g) => g.outcome === 'win').map((g) => g.matchSeconds / 60)),
    };
  });
  // The stronger side's results by the style it played, over the non-mirror pairings.
  const strongerByStyle = {};
  for (const st of STYLES) {
    const gs = games.filter((g) => !isMirror(g.pairing) && g.seats.length === 2 && strongWeak(g)[0].style === st);
    strongerByStyle[st] = { games: gs.length, wins: gs.filter((g) => g.outcome === 'win' && strongWeak(g)[0].won).length };
  }
  // Mirror matches: wins per seat of each style, all of them and only against a different style.
  const mirrorByStyle = {};
  for (const st of STYLES) {
    const seats = [], versusOther = [];
    for (const g of games.filter((g) => isMirror(g.pairing) && g.seats.length === 2)) {
      g.seats.forEach((x, i) => {
        if (x.style !== st) return;
        seats.push(x);
        if (g.seats[1 - i].style !== st) versusOther.push(x);
      });
    }
    mirrorByStyle[st] = { seats: seats.length, wins: seats.filter((x) => x.won).length, versusOther: versusOther.length, versusOtherWins: versusOther.filter((x) => x.won).length };
  }
  const byDifficulty = {};
  for (const d of ['Hard', 'Normal', 'Easy']) {
    const seats = games.flatMap((g) => g.seats).filter((x) => x.difficulty === d);
    if (!seats.length) continue;
    byDifficulty[d] = {
      seats: seats.length,
      highestBalance: mean(seats.map((x) => x.peakResources || 0)),
      unpaidSeconds: mean(seats.map((x) => x.unpaidSeconds || 0)),
      peakArmy: mean(seats.map((x) => x.peakArmy || 0)),
      firstAttackMinutes: mean(seats.filter((x) => x.firstAttackSeconds >= 0).map((x) => x.firstAttackSeconds / 60)),
    };
    // The economy at minute 10 (seats still playing then): unlike the highest balance, it doesn't grow with match length.
    const at10 = seats.map((x) => (x.timeline || []).map(parseMinute).find((t) => t && t.minute === 10)).filter(Boolean);
    if (at10.length) byDifficulty[d].at10 = { seats: at10.length, ...Object.fromEntries(['res', 'inc', 'upk', 'army'].map((k) => [k, mean(at10.map((t) => t[k]))])) };
  }
  // Each capped match: both seats' state at their last timeline minute and the army's last log line.
  const caps = games.filter((g) => g.outcome === 'cap').map((g) => ({
    file: g.file.slice(0, -5), minutes: (g.matchSeconds || 0) / 60,
    seats: g.seats.map((x) => ({ difficulty: x.difficulty, style: x.style, last: parseMinute((x.timeline || []).slice(-1)[0]), log: (x.armyLog || []).slice(-1)[0] || '' })),
  }));
  return {
    matches: games.length, sizes, met, rows, strongerByStyle, mirrorByStyle, byDifficulty, caps,
    failures: {
      caps: fails.filter((g) => g.outcome === 'cap').length, draws: fails.filter((g) => g.outcome === 'draw').length,
      errors: fails.filter((g) => g.outcome === 'error').length, files: fails.map((g) => g.file.slice(0, -5)),
      // Why matches errored, most common first: [message, count].
      errorMessages: Object.entries(fails.filter((g) => g.outcome === 'error').reduce((m, g) => { const e = g.error || 'no message'; m[e] = (m[e] || 0) + 1; return m; }, {})).sort((a, b) => b[1] - a[1]),
    },
  };
}

const pct = (x) => `${Math.round(x * 100)}%`;

/** The brief: about eight lines. */
function formatBrief(s) {
  const out = [`AI bench: ${s.matches} matches, map ${s.sizes.join('/')}; exit criteria ${s.met ? 'met' : 'NOT met'}`];
  for (const r of s.rows) {
    const fin = `caps ${r.caps}, draws ${r.draws}, errors ${r.errors}, median ${Number.isNaN(r.medianMinutes) ? '–' : r.medianMinutes.toFixed(1)} min`;
    if (r.mirror) { out.push(`  ${r.pairing.padEnd(13)} ${String(r.games).padStart(3)} games            ${fin}`); continue; }
    const sizes = r.bySize.length > 1 ? ` [${r.bySize.map((c) => `${c.size}: ${c.wins}/${c.games}`).join(', ')}]` : '';
    out.push(`  ${r.pairing.padEnd(13)} ${pct(r.rate).padStart(4)} (${r.wins}/${r.games}, 95% ${pct(r.ci[0])}–${pct(r.ci[1])})${sizes}  ${fin}`);
  }
  const strong = STYLES.filter((st) => s.strongerByStyle[st].games).map((st) => `${st} ${s.strongerByStyle[st].wins}/${s.strongerByStyle[st].games}`);
  if (strong.length) out.push(`  Stronger side wins, by its style: ${strong.join(' · ')}`);
  const mirror = STYLES.filter((st) => s.mirrorByStyle[st].seats).map((st) => `${st} ${s.mirrorByStyle[st].wins}/${s.mirrorByStyle[st].seats}`);
  if (mirror.length) out.push(`  Mirror wins, by style: ${mirror.join(' · ')}`);
  const diff = Object.entries(s.byDifficulty);
  const at10 = diff.filter(([, x]) => x.at10);
  if (at10.length) out.push(`  At 10 min (mean): ${at10.map(([d, { at10: t }]) => `${d} bank ${Math.round(t.res)}, income ${Math.round(t.inc)}/s, upkeep ${Math.round(t.upk)}/s, army ${Math.round(t.army)}`).join(' · ')}`);
  if (diff.length) out.push(`  Mean highest balance: ${diff.map(([d, x]) => `${d} ${Math.round(x.highestBalance)}`).join(' · ')}; unpaid s: ${diff.map(([d, x]) => `${d} ${Math.round(x.unpaidSeconds)}`).join(' · ')}`);
  const f = s.failures;
  if (f.files.length) out.push(`  Unfinished: ${f.caps} caps, ${f.draws} draws, ${f.errors} errors (${f.files.slice(0, 4).join(', ')}${f.files.length > 4 ? ', …' : ''})`);
  if (f.errorMessages.length) out.push(`  Errors: ${f.errorMessages.slice(0, 3).map(([e, n]) => `"${e}" ×${n}`).join(' · ')} (each match's log is kept in the results directory)`);
  if (s.caps.length) {
    out.push('  Caps (each seat at its last minute: army, buildings, enemy bases known, army mode; then its army\'s last log line):');
    const seat = (x) => `${x.difficulty} ${x.style}: ${x.last ? `army ${x.last.army}, bld ${x.last.bld}, knows ${x.last.bases}, ${x.last.mode}` : 'no timeline'}${x.log ? ` "${x.log.length > 60 ? `${x.log.slice(0, 59)}…` : x.log}"` : ''}`;
    for (const c of s.caps.slice(0, 8)) out.push(`    ${c.file}: ${c.seats.map(seat).join(' | ')}`);
    if (s.caps.length > 8) out.push(`    … and ${s.caps.length - 8} more (--json lists them all)`);
  }
  return out.join('\n');
}

/** Which config a results directory played: the build's own, or an AI_BENCH_CONFIG copy and how it differs from the repo's. */
function configNote(dir, repoConfig = path.join(__dirname, '..', 'Assets/Resources/GameConfig.xml')) {
  const copy = path.join(dir, 'GameConfig.xml');
  if (!fs.existsSync(copy)) return 'Config: the build\'s own';
  const leaves = (src) => {
    const out = new Map();
    const walk = (el, pre) => {
      for (const c of el.children) {
        const key = [pre, c.tag, ...c.attrs.map(([, v]) => v)].filter(Boolean).join('.');
        if (c.children.length) walk(c, key); else out.set(key, xml.text(src, c));
      }
    };
    walk(xml.parse(src), '');
    return out;
  };
  const norm = (v) => (v !== undefined && v !== '' && !Number.isNaN(Number(v)) ? String(Number(v)) : v);
  const a = leaves(fs.readFileSync(repoConfig, 'utf8')), b = leaves(fs.readFileSync(copy, 'utf8'));
  const diffs = [...new Set([...a.keys(), ...b.keys()])].filter((k) => norm(a.get(k)) !== norm(b.get(k)))
    .map((k) => `${k.replace(/^GameConfig\./, '')} ${a.has(k) ? a.get(k) : '(none)'} → ${b.has(k) ? b.get(k) : '(none)'}`);
  if (!diffs.length) return 'Config: an AI_BENCH_CONFIG copy, the same values as the repo\'s';
  return `Config: an AI_BENCH_CONFIG copy; vs the repo's: ${diffs.slice(0, 5).join(', ')}${diffs.length > 5 ? `, and ${diffs.length - 5} more` : ''}`;
}

module.exports = { loadGames, summarise, formatBrief, configNote, parseMinute, wilson, strongWeak, isMirror, STYLES };

if (require.main === module) {
  const args = process.argv.slice(2);
  const json = args.includes('--json');
  const dirs = args.filter((a) => a !== '--json');
  if (!dirs.length) { console.error('Usage: node tools/ai-bench-brief.js <results-dir>... [--json]'); process.exit(1); }
  const s = summarise(loadGames(dirs));
  console.log(json ? JSON.stringify(s, null, 1) : `${formatBrief(s)}\n  ${[...new Set(dirs.map((d) => configNote(d)))].join('\n  ')}`);
  process.exit(s.met ? 0 : 2);
}
