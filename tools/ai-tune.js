#!/usr/bin/env node
// Tunes GameConfig.xml numbers (AI knobs, or any balance value) against the AI bench, with nobody watching: it
// writes a candidate config per setting, plays it with tools/ai-bench.sh --brief (AI_BENCH_CONFIG, so no rebuild),
// scores it, and keeps the best. Coordinate search: each pass tries every listed value of one knob at a time with
// the others at their best so far. Then a confirm stage replays the best few and the base config on fresh seeds,
// so a lucky screen doesn't win. It prints one line per candidate and a short final table, and writes everything
// under the tune directory: <id>.xml (the candidate config), <id>/ (its bench results and full report), tune.jsonl
// (one line per run; re-running with the same --out skips what it already played) and summary.md.
//
// Usage: node tools/ai-tune.js <spec.json> [--out <dir>] [--dry-run]
//        node tools/ai-tune.js apply <tune-dir> [candidate-id]   (write a candidate's values into the base config)
//
// The spec (JSON; see tools/ai-tune.example.json), every field but "knobs" optional:
//   knobs      { "<path>": [values...] }: config paths (tools/lib/config-xml.js, e.g. "Difficulty.Hard.AttackRatio",
//              "Unit.Tank.Damage", "DamageTable.Tank.Wall") and the values to try; the base value is always tried
//   config     the base config (default Assets/Resources/GameConfig.xml)
//   player     the Linux player (default Builds/Linux/WAR-2D.x86_64)
//   bench      { sizes, pairings, styles, seeds, jobs, cap, scale, seed0 }: AI_BENCH_* for the screen runs
//              (defaults "512", "Hard-Normal Normal-Easy", the four styles, 8, 4, 40, 32, 101)
//   objective  { kind: "stronger" }: the stronger side's win rate over the non-mirror pairings (default), or
//              { kind: "style", style: "Turtle" | "*", target: 0.5 }: in the mirror pairings (same difficulty), a style's
//              win rate against other styles as close to target as it gets ("*": the worst style's distance).
//              Both subtract failurePenalty (default 0.5) times the share of matches that didn't finish. Or
//              { kind: "finish" }: the share of matches that end (no cap, draw or error), any pairing.
//   passes     coordinate-search passes (default 2; stops early when a pass changes nothing)
//   minGain    how much a score must beat the best to replace it (default 0)
//   confirm    { top, seeds, seed0 }: candidates replayed on fresh seeds (default 3, 16, 1001); top 0 skips it
'use strict';
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const xml = require('./lib/config-xml');
const bench = require('./ai-bench-brief');

const ROOT = path.resolve(__dirname, '..');
const DEFAULTS = {
  config: 'Assets/Resources/GameConfig.xml',
  player: 'Builds/Linux/WAR-2D.x86_64',
  bench: { sizes: '512', pairings: 'Hard-Normal Normal-Easy', styles: 'Rusher Turtle Expander Balanced', seeds: 8, jobs: 4, cap: 40, scale: 32, seed0: 101 },
  objective: { kind: 'stronger', failurePenalty: 0.5 },
  passes: 2,
  minGain: 0,
  confirm: { top: 3, seeds: 16, seed0: 1001 },
};

const isNum = (v) => v !== '' && v !== null && !Number.isNaN(Number(v));
/** One spelling per value, so 1, 1.0 and "1.0" are the same candidate. */
const norm = (v) => (isNum(v) ? String(Number(v)) : String(v));
const keyOf = (values) => JSON.stringify(Object.keys(values).sort().map((k) => [k, values[k]]));
const pct = (x) => `${Math.round(x * 100)}%`;
/** A path relative to the repository when it is inside it. */
const shown = (p) => { const r = path.relative(ROOT, p); return r.startsWith('..') ? p : r; };
const stamp = () => new Date().toISOString().replace(/[-:]/g, '').replace('T', '-').slice(0, 15);

function loadSpec(file) {
  const raw = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (!raw.knobs || !Object.keys(raw.knobs).length) throw new Error(`${file}: no "knobs"`);
  return {
    ...DEFAULTS, ...raw,
    bench: { ...DEFAULTS.bench, ...raw.bench },
    objective: { ...DEFAULTS.objective, ...raw.objective },
    confirm: { ...DEFAULTS.confirm, ...raw.confirm },
  };
}

/** The score and a short description of one bench summary, for the spec's objective. */
function scoreOf(summary, objective) {
  const failShare = summary.matches ? (summary.failures.caps + summary.failures.draws + summary.failures.errors) / summary.matches : 1;
  const unfinished = failShare ? `, unfinished ${pct(failShare)}` : '';
  const penalty = (objective.failurePenalty ?? 0.5) * failShare;
  if (objective.kind === 'stronger') {
    const rows = summary.rows.filter((r) => !r.mirror);
    const games = rows.reduce((n, r) => n + r.games, 0), wins = rows.reduce((n, r) => n + r.wins, 0);
    if (!games) throw new Error('objective "stronger" needs a non-mirror pairing (e.g. Hard-Normal)');
    const [lo, hi] = bench.wilson(wins, games);
    const per = rows.length > 1 ? `; ${rows.map((r) => `${r.pairing} ${pct(r.rate)}`).join(', ')}` : '';
    return { score: wins / games - penalty, detail: `stronger ${pct(wins / games)} (${wins}/${games}, 95% ${pct(lo)}–${pct(hi)}${per})${unfinished}` };
  }
  if (objective.kind === 'style') {
    const target = objective.target ?? 0.5;
    const styles = objective.style === '*' ? bench.STYLES : [objective.style];
    const seen = styles.map((st) => ({ st, ...summary.mirrorByStyle[st] })).filter((x) => x.versusOther > 0);
    if (!seen.length) throw new Error(`objective "style" needs mirror pairings (e.g. Normal-Normal) and bench styles with ${objective.style === '*' ? 'several styles' : `${objective.style} and another style`}`);
    const worst = seen.map((x) => ({ ...x, rate: x.versusOtherWins / x.versusOther })).sort((a, b) => Math.abs(b.rate - target) - Math.abs(a.rate - target));
    const desc = worst.map((x) => `${x.st} ${pct(x.rate)} (${x.versusOtherWins}/${x.versusOther})`).join(', ');
    return { score: -Math.abs(worst[0].rate - target) - penalty, detail: `vs other styles: ${desc}${unfinished}` };
  }
  if (objective.kind === 'finish') {
    const done = summary.matches - (summary.failures.caps + summary.failures.draws + summary.failures.errors);
    const [lo, hi] = bench.wilson(done, summary.matches);
    return { score: summary.matches ? done / summary.matches : 0, detail: `finished ${done}/${summary.matches} (95% ${pct(lo)}–${pct(hi)}; caps ${summary.failures.caps}, draws ${summary.failures.draws}, errors ${summary.failures.errors})` };
  }
  throw new Error(`unknown objective kind "${objective.kind}" (stronger, style or finish)`);
}

/** Runs tools/ai-bench.sh; on a terminal it shows the matches finished so far on one line (stderr) while they play. */
function runBench(args, env, dir, total, label) {
  return new Promise((resolve, reject) => {
    const child = spawn('bash', ['tools/ai-bench.sh', ...args], { env, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    child.stdout.on('data', (d) => { output += d; });
    child.stderr.on('data', (d) => { output += d; });
    const t0 = Date.now();
    const timer = process.stderr.isTTY && setInterval(() => {
      let done = 0;
      try { done = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).length; } catch { /* not created yet */ }
      const line = `  ${done}/${total} matches, ${Math.round((Date.now() - t0) / 1000)} s: ${label}`;
      process.stderr.write(`\r\x1b[K${line.slice(0, (process.stderr.columns || 100) - 1)}`);
    }, 2000);
    child.on('error', reject);
    child.on('close', (status, signal) => {
      if (timer) { clearInterval(timer); process.stderr.write('\r\x1b[K'); }
      resolve({ status, signal, output });
    });
  });
}

async function tune(specFile, outArg, dryRun) {
  const spec = loadSpec(specFile);
  process.chdir(ROOT);
  const baseSrc = fs.readFileSync(spec.config, 'utf8');
  const root = xml.parse(baseSrc);
  const knobs = Object.entries(spec.knobs).map(([p, vals]) => {
    const base = norm(xml.text(baseSrc, xml.find(root, p)));
    const values = [...new Set([base, ...vals.map(norm)])];
    return { path: p, base, values };
  });
  const baseValues = Object.fromEntries(knobs.map((k) => [k.path, k.base]));
  const b = spec.bench;
  const perRun = (seeds) => seeds * b.sizes.split(/\s+/).filter(Boolean).length * b.pairings.split(/\s+/).filter(Boolean).length;
  const screenRuns = 1 + spec.passes * knobs.reduce((n, k) => n + k.values.length - 1, 0);
  const confirmRuns = spec.confirm.top > 0 ? spec.confirm.top + 1 : 0;
  console.log(`Knobs (base value first): ${knobs.map((k) => `${k.path} ${k.values.join('/')}`).join('; ')}`);
  console.log(`Objective ${JSON.stringify(spec.objective)}; at most ${screenRuns} screen runs of ${perRun(b.seeds)} matches, then ${confirmRuns} confirm runs of ${perRun(spec.confirm.seeds)} (${screenRuns * perRun(b.seeds) + confirmRuns * perRun(spec.confirm.seeds)} matches at most)`);
  // A fake summary checks the objective against the pairings and styles before hours of play.
  const probe = bench.summarise(b.pairings.split(/\s+/).filter(Boolean).flatMap((p, i) => {
    const [d1, d2] = p.split('-');
    const styles = b.styles.split(/\s+/);
    return styles.map((st, j) => ({ file: `0_${p}_${i}${j}.json`, size: 0, pairing: p, outcome: 'win', matchSeconds: 60, seats: [{ difficulty: d1, style: st, won: true }, { difficulty: d2, style: styles[(j + 1) % styles.length], won: false }] }));
  }));
  scoreOf(probe, spec.objective);
  if (dryRun) return;

  if (!fs.existsSync(spec.player)) throw new Error(`No player at ${spec.player} - make a Linux build first.`);
  const out = path.resolve(outArg || `PerfResults/ai-tune-${stamp()}`);
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(path.join(out, 'spec.json'), JSON.stringify({ ...spec, specFile }, null, 2));
  const logFile = path.join(out, 'tune.jsonl');
  const log = fs.existsSync(logFile) ? fs.readFileSync(logFile, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : [];
  const memo = new Map(log.map((e) => [`${e.seeds}@${e.seed0}|${keyOf(e.values)}`, e]));
  if (log.length) console.log(`Resuming ${out}: ${log.length} runs already played`);
  console.log(`Playing in ${shown(out)}: one line per run when its matches finish (a screen run is ${perRun(b.seeds)}). Ctrl-C stops; the same --out resumes.`);
  const nChanged = (values) => Object.entries(values).filter(([k, v]) => v !== baseValues[k]).length;
  const changes = (values) => Object.entries(values).filter(([k, v]) => v !== baseValues[k]).map(([k, v]) => `${k}=${v}`).join(' ') || '(base config)';

  async function evaluate(values, stage, seeds, seed0) {
    const key = `${seeds}@${seed0}|${keyOf(values)}`;
    if (memo.has(key)) return memo.get(key);
    const id = `c${String(log.length + 1).padStart(3, '0')}`;
    const cfgFile = path.join(out, `${id}.xml`);
    fs.writeFileSync(cfgFile, xml.setValues(baseSrc, values));
    const dir = path.join(out, id);
    fs.rmSync(dir, { recursive: true, force: true }); // what an interrupted attempt at this run left
    const env = {
      ...process.env, AI_BENCH_CONFIG: cfgFile, AI_BENCH_DIR: dir, AI_BENCH_OUT: path.join(dir, 'report.md'),
      AI_BENCH_SIZES: b.sizes, AI_BENCH_PAIRINGS: b.pairings, AI_BENCH_STYLES: b.styles, AI_BENCH_CAP: String(b.cap),
      AI_BENCH_SCALE: String(b.scale), AI_BENCH_SEED0: String(seed0),
    };
    const t0 = Date.now();
    const r = await runBench(['--brief', String(seeds), String(b.jobs), spec.player], env, dir, perRun(seeds), `${id} ${stage} ${changes(values)}`);
    if (r.status !== 0 && r.status !== 2) throw new Error(`ai-bench.sh failed (${r.status ?? r.signal}) for ${id}:\n${r.output.split('\n').slice(-12).join('\n')}`);
    const summary = bench.summarise(bench.loadGames([dir]));
    // Every match failing means the candidate config or the build is broken, not that it played badly.
    if (summary.matches && summary.failures.errors === summary.matches) throw new Error(`${id}: every match failed (${summary.failures.errorMessages.map(([e, n]) => `"${e}" ×${n}`).join(', ')}); config ${cfgFile}, logs in ${dir}`);
    const { score, detail } = scoreOf(summary, spec.objective);
    const entry = { id, stage, seeds, seed0, values, score, detail, wallSeconds: Math.round((Date.now() - t0) / 1000) };
    fs.appendFileSync(logFile, `${JSON.stringify(entry)}\n`);
    log.push(entry);
    memo.set(key, entry);
    console.log(`${id} ${stage.padEnd(7)} ${score.toFixed(3).padStart(6)}  ${changes(values)}: ${detail} [${entry.wallSeconds} s]`);
    return entry;
  }

  // Screen: coordinate search from the base config.
  let best = await evaluate(baseValues, 'screen', b.seeds, b.seed0);
  for (let pass = 1; pass <= spec.passes; pass++) {
    let changed = false;
    for (const k of knobs) {
      for (const v of k.values) {
        if (v === best.values[k.path]) continue;
        const e = await evaluate({ ...best.values, [k.path]: v }, 'screen', b.seeds, b.seed0);
        if (e.score > best.score + spec.minGain) { best = e; changed = true; }
      }
    }
    if (!changed) break;
  }

  // Confirm: the best few distinct screen results and the base config, on fresh seeds.
  const screened = [...new Map(log.filter((e) => e.seeds === b.seeds && e.seed0 === b.seed0).map((e) => [keyOf(e.values), e])).values()].sort((x, y) => y.score - x.score || nChanged(x.values) - nChanged(y.values));
  let ranked;
  if (spec.confirm.top > 0) {
    // Runs tied with the last pick are ones the screen couldn't tell apart: confirm them too, up to twice top.
    const cut = screened[Math.min(spec.confirm.top, screened.length) - 1].score;
    const picks = screened.filter((e, i) => i < spec.confirm.top || (e.score >= cut && i < 2 * spec.confirm.top));
    if (!picks.some((e) => keyOf(e.values) === keyOf(baseValues))) picks.push(screened.find((e) => keyOf(e.values) === keyOf(baseValues)));
    ranked = [];
    for (const e of picks) ranked.push({ screen: e, confirm: await evaluate(e.values, 'confirm', spec.confirm.seeds, spec.confirm.seed0) });
    ranked.sort((x, y) => y.confirm.score - x.confirm.score);
  } else {
    ranked = screened.slice(0, 5).map((e) => ({ screen: e, confirm: null }));
  }

  const lines = [
    `# AI tune: ${path.basename(out)}`, '',
    `Spec \`${shown(specFile)}\`, objective \`${JSON.stringify(spec.objective)}\`; screen ${b.seeds} seeds from ${b.seed0}, confirm ${spec.confirm.seeds} seeds from ${spec.confirm.seed0}; bench ${b.sizes} · ${b.pairings} · ${b.styles}.`, '',
    '| Rank | Run | Changes from the base config | Screen | Confirm |', '| --- | --- | --- | --- | --- |',
    ...ranked.map((r, i) => `| ${i + 1} | ${(r.confirm || r.screen).id} | ${changes(r.screen.values)} | ${r.screen.score.toFixed(3)} | ${r.confirm ? `${r.confirm.score.toFixed(3)}: ${r.confirm.detail}` : '–'} |`),
  ];
  const top = ranked[0].confirm || ranked[0].screen;
  const tied = screened.filter((e) => e.score === screened[0].score).length;
  if (tied > 1) lines.push('', `${tied} screen runs tied at the top score (${screened[0].score.toFixed(3)}): the screen couldn't tell them apart. More seeds, or a harder slice (more sizes or styles), would.`);
  lines.push('', keyOf(top.values) === keyOf(baseValues)
    ? 'No candidate beat the base config.'
    : `Best: ${changes(top.values)}. Apply it with \`node tools/ai-tune.js apply ${shown(out)} ${top.id}\`.`);
  const text = lines.join('\n') + '\n';
  fs.writeFileSync(path.join(out, 'summary.md'), text);
  console.log(`\n${text}`);
}

function apply(dir, id) {
  process.chdir(ROOT);
  const spec = JSON.parse(fs.readFileSync(path.join(dir, 'spec.json'), 'utf8'));
  const log = fs.readFileSync(path.join(dir, 'tune.jsonl'), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l));
  const pool = log.some((e) => e.stage === 'confirm') ? log.filter((e) => e.stage === 'confirm') : log;
  const entry = id ? log.find((e) => e.id === id) : [...pool].sort((x, y) => y.score - x.score)[0];
  if (!entry) throw new Error(`no run ${id} in ${dir}`);
  const src = fs.readFileSync(spec.config, 'utf8');
  const root = xml.parse(src);
  const changed = Object.fromEntries(Object.entries(entry.values).filter(([k, v]) => norm(xml.text(src, xml.find(root, k))) !== v));
  if (!Object.keys(changed).length) { console.log(`${entry.id}: ${spec.config} already has these values.`); return; }
  for (const [k, v] of Object.entries(changed)) console.log(`${k}: ${xml.text(src, xml.find(root, k))} -> ${v}`);
  fs.writeFileSync(spec.config, xml.setValues(src, changed));
  console.log(`Wrote ${entry.id}'s values into ${spec.config}.`);
}

if (require.main === module) {
  const args = process.argv.slice(2);
  try {
    if (args[0] === 'apply') {
      if (!args[1]) throw new Error('Usage: node tools/ai-tune.js apply <tune-dir> [candidate-id]');
      apply(path.resolve(args[1]), args[2]);
    } else {
      const spec = args.find((a) => !a.startsWith('--') && args[args.indexOf(a) - 1] !== '--out');
      if (!spec) throw new Error('Usage: node tools/ai-tune.js <spec.json> [--out <dir>] [--dry-run]\n       node tools/ai-tune.js apply <tune-dir> [candidate-id]');
      const o = args.indexOf('--out');
      tune(path.resolve(spec), o >= 0 ? path.resolve(args[o + 1]) : null, args.includes('--dry-run')).catch((e) => { console.error(e.message); process.exit(1); });
    }
  } catch (e) {
    console.error(e.message);
    process.exit(1);
  }
}

module.exports = { scoreOf, loadSpec };
