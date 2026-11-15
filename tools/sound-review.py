#!/usr/bin/env python3
"""Builds a before/after review page for the built sounds that differ from a git ref.

Usage: tools/sound-review.py <name> [--base REF] [--title TEXT] [--intro TEXT]
       tools/sound-review.py <name> --candidates docs/art/review/<folder> [--title TEXT] [--intro TEXT]

Writes docs/art/review/<today>-<name>/: before/ (the sounds at REF, default HEAD), after/ (the working
tree's) and index.html, a page with a player, waveform and spectrogram for each side and a choice per
sound. With --candidates it shows the candidates rendered into that folder (never into Assets/Resources)
beside the sound that ships. Publish index.html with its WAV folders as its files (asset generation spec,
sections 3.3 and 10).
"""
import argparse
import datetime
import html
import json
import os
import subprocess
import sys

SFX_OUT = "Assets/Resources/Audio/Sfx"
SFX_SRC = "Assets/Art/Audio/sfx"


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True).stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("name")
    parser.add_argument("--base", default="HEAD")
    parser.add_argument("--title", default="Sound review")
    parser.add_argument("--intro", default="")
    parser.add_argument("--candidates", metavar="DIR", help="review folder holding candidates/ (and notes.json): show them beside today's sounds")
    args = parser.parse_args()
    os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

    if args.candidates:
        out = args.candidates.rstrip("/")
        sounds = candidate_sounds(out)
    else:
        out = f"docs/art/review/{datetime.date.today().isoformat()}-{args.name}"
        sounds = changed_sounds(out, args.base)
    if not sounds:
        print("Nothing to review")
        return 1

    page = PAGE.replace("__TITLE__", html.escape(args.title)).replace("__INTRO__", args.intro) \
        .replace("__KEY__", json.dumps("sound-review-" + args.name)).replace("__SOUNDS__", json.dumps(sounds))
    open(out + "/index.html", "w").write(page)
    print(f"{len(sounds)} sounds in {out}/index.html")
    return 0


def spec_of(sound):
    path = f"{SFX_SRC}/{sound}.json"
    return json.load(open(path)) if os.path.exists(path) else {}


def changed_sounds(out, base):
    """The built sounds that differ from a git ref: before (at the ref) and after (the working tree)."""
    changed = [line[3:] for line in git("status", "--porcelain", "--", SFX_OUT).decode().splitlines() if line.endswith(".wav")]
    changed += [p for p in git("diff", "--name-only", base, "--", SFX_OUT).decode().splitlines() if p.endswith(".wav")]
    os.makedirs(out + "/before", exist_ok=True)
    os.makedirs(out + "/after", exist_ok=True)
    sounds = []
    for path in sorted(set(changed)):
        sound = os.path.splitext(os.path.basename(path))[0]
        spec = spec_of(sound)
        sides = []
        try:
            open(f"{out}/before/{sound}.wav", "wb").write(git("show", f"{base}:{path}"))
            sides.append({"key": "before", "label": "Before (ships today)", "src": f"before/{sound}.wav", "answer": "Keep the before sound"})
        except subprocess.CalledProcessError:
            pass  # new at this ref
        if os.path.exists(path):
            open(f"{out}/after/{sound}.wav", "wb").write(open(path, "rb").read())
            sides.append({"key": "after", "label": "After", "src": f"after/{sound}.wav", "answer": "Use the after sound"})
        meta = f"asks for {spec.get('wave', 'Square')}" + (f" at {spec['frequency']} Hz" if "frequency" in spec else "")
        sounds.append({"id": sound, "meta": meta, "what": "", "sides": sides})
    return sounds


def candidate_sounds(out):
    """Candidates rendered into <out>/candidates/<id>.<label>.wav (WAR-2D/Audio/Render review candidates), beside
    the sound that ships today. <out>/notes.json says what each sound is for and what each candidate tries.
    Nothing here touches Assets/Resources."""
    notes = json.load(open(out + "/notes.json")) if os.path.exists(out + "/notes.json") else {}
    os.makedirs(out + "/now", exist_ok=True)
    by_id = {}
    for name in sorted(os.listdir(out + "/candidates")):
        if name.endswith(".wav"):
            sound, label = name[:-4].rsplit(".", 1)
            by_id.setdefault(sound, []).append(label)
    sounds = []
    for sound, labels in sorted(by_id.items()):
        note = notes.get(sound, {})
        sides = []
        shipped = f"{SFX_OUT}/{sound}.wav"
        if os.path.exists(shipped):
            open(f"{out}/now/{sound}.wav", "wb").write(open(shipped, "rb").read())
            sides.append({"key": "now", "label": "Now (ships today)", "src": f"now/{sound}.wav", "answer": "Keep today's"})
        for label in labels:
            sides.append({"key": label, "label": f"{label.upper()}: {note.get(label, '')}".rstrip(": "), "src": f"candidates/{sound}.{label}.wav",
                          "answer": f"Use {label.upper()}"})
        sounds.append({"id": sound, "meta": "", "what": note.get("what", ""), "sides": sides})
    return sounds


PAGE = r"""<title>__TITLE__</title>
<meta name="claude:viewport-fit-sides" content="cover">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Jersey+10&family=IBM+Plex+Sans:wght@400;600&family=IBM+Plex+Mono:wght@400&display=swap">
<style>
/* Layout: one column of sounds, each a before and an after side by side (stacked on a phone), with the
   choices summed in a bar at the end. One dark look on purpose, as on the v0.12 to v0.20 review pages. */
:root {
  --bg: #15191e;
  --panel: #20262d;
  --edge: #323b45;
  --text: #e8edf2;
  --muted: #aab6c2;
  --gold: #f2c14e;
  --accent: #3fd0ff;
  --warn: #ff9a3d;
  --ok: #8fd18f;
  --display: "Jersey 10", "Courier New", monospace;
  --body: "IBM Plex Sans", system-ui, sans-serif;
  --mono: "IBM Plex Mono", ui-monospace, monospace;
  color-scheme: dark;
}
body { background: var(--bg); color: var(--text); font: 15px/1.55 var(--body); }
main { max-width: 1080px; margin: 0 auto; padding-block: 28px 72px; padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); display: grid; gap: 22px; }
h1, h2 { font-family: var(--display); font-weight: 400; letter-spacing: 0.02em; text-wrap: balance; margin: 0; }
h1 { font-size: 46px; line-height: 1; }
h2 { font-size: 26px; line-height: 1.1; color: var(--gold); }
p { margin: 0; max-width: 72ch; color: var(--muted); }
p strong { color: var(--text); font-weight: 600; }
code { font: 13px var(--mono); color: var(--text); }
button { font: 14px var(--body); color: var(--bg); background: var(--accent); border: 0; padding: 7px 14px; cursor: pointer; }
button.quiet { background: transparent; color: var(--accent); border: 1px solid var(--edge); }
button:focus-visible, input:focus-visible, textarea:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.lede { display: grid; gap: 10px; }
.bar { display: flex; flex-wrap: wrap; gap: 10px 16px; align-items: center; }
.tally { font: 13px var(--mono); color: var(--muted); }
#list { display: grid; gap: 14px; }
.sound { background: var(--panel); border: 1px solid var(--edge); padding: 14px 16px; display: grid; gap: 10px; min-width: 0; }
.sound > header { display: flex; flex-wrap: wrap; gap: 6px 14px; align-items: baseline; justify-content: space-between; }
.meta { font: 12px var(--mono); letter-spacing: 0.06em; text-transform: uppercase; color: var(--muted); }
.sides { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 12px 18px; }
.sound > p { color: var(--text); }
.side { display: grid; gap: 6px; min-width: 0; }
.side > span { font: 12px/1.4 var(--mono); letter-spacing: 0.04em; color: var(--gold); min-height: 2.8em; }
.side canvas { width: 100%; height: 56px; background: var(--bg); border: 1px solid var(--edge); display: block; image-rendering: pixelated; }
.side canvas.spec { height: 84px; }
.side audio { width: 100%; height: 34px; }
.choice { display: flex; flex-wrap: wrap; gap: 6px 18px; font-size: 14px; }
.choice label { display: flex; gap: 6px; align-items: center; cursor: pointer; }
.choice input[type="text"] { flex: 1 1 220px; min-width: 0; font: 14px var(--body); color: var(--text); background: var(--bg); border: 1px solid var(--edge); padding: 4px 8px; }
textarea { width: 100%; box-sizing: border-box; min-height: 150px; font: 13px/1.5 var(--mono); color: var(--text); background: var(--panel); border: 1px solid var(--edge); padding: 10px; }
</style>
<main>
  <div class="lede">
    <h1>__TITLE__</h1>
    __INTRO__
    <p>Each sound shows its waveform and its spectrogram (0 to 11 kHz, bottom to top, brighter is louder). Pick one answer per sound; your answers are kept in this browser. When done, copy the summary at the end and paste it back to Claude.</p>
  </div>
  <div class="bar">
    <button id="clear" class="quiet" type="button">Clear answers</button>
    <span class="tally" id="tally"></span>
  </div>
  <div id="list"></div>
  <h2>Summary</h2>
  <textarea id="summary" readonly aria-label="Summary of your answers"></textarea>
  <div class="bar"><button id="copy" type="button">Copy summary</button><span class="tally" id="copied"></span></div>
</main>
<script>
const SOUNDS = __SOUNDS__, KEY = __KEY__;
let state = {};
try { state = JSON.parse(localStorage.getItem(KEY) || "{}"); } catch (e) { state = {}; }
const save = () => { try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) {} };
const list = document.getElementById("list");

for (const s of SOUNDS) {
  const el = document.createElement("section");
  el.className = "sound";
  const answers = s.sides.map(x => [x.key, x.answer]).concat([["change", "None of these: change it"]]);
  const esc = t => String(t).replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
  el.innerHTML = `<header><h2>${s.id}</h2><span class="meta">${esc(s.meta)}</span></header>
    ${s.what ? `<p>${esc(s.what)}</p>` : ""}
    <div class="sides">${s.sides.map(x => `<div class="side"><span>${esc(x.label)}</span><canvas data-src="${x.src}"></canvas><canvas class="spec"></canvas><audio controls preload="none" src="${x.src}"></audio></div>`).join("")}</div>
    <div class="choice" role="radiogroup" aria-label="Answer for ${s.id}">
      ${answers.map(([v, t]) => `<label><input type="radio" name="a-${s.id}" id="a-${s.id}-${v}" value="${v}">${esc(t)}</label>`).join("")}
      <input type="text" id="n-${s.id}" placeholder="Note (what to change)" aria-label="Note for ${s.id}">
    </div>`;
  list.appendChild(el);
  for (const r of el.querySelectorAll("input[type=radio]")) {
    r.checked = (state[s.id] || {}).answer === r.value;
    r.addEventListener("change", () => { state[s.id] = { ...(state[s.id] || {}), answer: r.value }; save(); summarise(); });
  }
  const note = el.querySelector("input[type=text]");
  note.value = (state[s.id] || {}).note || "";
  note.addEventListener("input", () => { state[s.id] = { ...(state[s.id] || {}), note: note.value }; save(); summarise(); });
}

function summarise() {
  const groups = new Map(), order = [];
  const put = (k, v) => { if (!groups.has(k)) { groups.set(k, []); order.push(k); } groups.get(k).push(v); };
  let unanswered = 0;
  for (const s of SOUNDS) {
    const a = state[s.id] || {}, side = s.sides.find(x => x.key === a.answer);
    const title = side ? side.answer : a.answer === "change" ? "Change" : "Not answered";
    if (title === "Not answered") unanswered++;
    put(title, s.id + (a.note ? ` (${a.note})` : ""));
  }
  const lines = [`Sound review ${KEY}`];
  for (const k of order.sort()) lines.push(`${k} (${groups.get(k).length}): ${groups.get(k).join(", ")}`);
  document.getElementById("summary").value = lines.join("\n");
  document.getElementById("tally").textContent = `${SOUNDS.length - unanswered} of ${SOUNDS.length} answered`;
}
summarise();

document.getElementById("clear").addEventListener("click", () => {
  state = {}; save();
  for (const i of list.querySelectorAll("input[type=radio]")) i.checked = false;
  for (const i of list.querySelectorAll("input[type=text]")) i.value = "";
  summarise();
});
document.getElementById("copy").addEventListener("click", async () => {
  const box = document.getElementById("summary"), done = document.getElementById("copied");
  try { await navigator.clipboard.writeText(box.value); done.textContent = "Copied"; }
  catch (e) { box.focus(); box.select(); done.textContent = "Select all is on: copy with Ctrl+C"; }
});

// 16-bit PCM WAV to mono floats.
function samplesOf(buffer) {
  const v = new DataView(buffer);
  let at = 12, channels = 1, rate = 44100, data = null;
  while (at + 8 <= v.byteLength) {
    const id = String.fromCharCode(v.getUint8(at), v.getUint8(at + 1), v.getUint8(at + 2), v.getUint8(at + 3)), size = v.getUint32(at + 4, true);
    if (id === "fmt ") { channels = v.getUint16(at + 10, true); rate = v.getUint32(at + 12, true); }
    if (id === "data") { data = { at: at + 8, size: Math.min(size, v.byteLength - at - 8) }; break; }
    at += 8 + size + (size & 1);
  }
  if (!data) return { rate, x: new Float32Array(0) };
  const n = Math.floor(data.size / 2 / channels), x = new Float32Array(n);
  for (let i = 0; i < n; i++) x[i] = v.getInt16(data.at + i * 2 * channels, true) / 32768;
  return { rate, x };
}

function drawWave(canvas, x) {
  const w = canvas.width = 560, h = canvas.height = 56, g = canvas.getContext("2d");
  g.fillStyle = "#3fd0ff";
  for (let px = 0; px < w; px++) {
    let lo = 0, hi = 0;
    const a = Math.floor(px * x.length / w), b = Math.max(a + 1, Math.floor((px + 1) * x.length / w));
    for (let i = a; i < b && i < x.length; i++) { if (x[i] < lo) lo = x[i]; if (x[i] > hi) hi = x[i]; }
    g.fillRect(px, h / 2 - hi * h / 2, 1, Math.max(1, (hi - lo) * h / 2));
  }
}

// In-place radix-2 FFT.
function fft(re, im) {
  const n = re.length;
  for (let i = 1, j = 0; i < n; i++) {
    let bit = n >> 1;
    for (; j & bit; bit >>= 1) j ^= bit;
    j ^= bit;
    if (i < j) { [re[i], re[j]] = [re[j], re[i]]; [im[i], im[j]] = [im[j], im[i]]; }
  }
  for (let len = 2; len <= n; len <<= 1) {
    const ang = -2 * Math.PI / len;
    for (let i = 0; i < n; i += len)
      for (let k = 0; k < len / 2; k++) {
        const c = Math.cos(ang * k), s = Math.sin(ang * k), a = i + k, b = a + len / 2;
        const tr = re[b] * c - im[b] * s, ti = re[b] * s + im[b] * c;
        re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
      }
  }
}

function drawSpec(canvas, x, rate) {
  const N = 512, cols = 280, bins = Math.floor(N / 2 * Math.min(1, 11025 / (rate / 2)));
  const w = canvas.width = cols, h = canvas.height = 84, g = canvas.getContext("2d"), img = g.createImageData(w, h);
  const re = new Float32Array(N), im = new Float32Array(N);
  for (let c = 0; c < cols; c++) {
    const start = Math.floor(c * Math.max(1, x.length - N) / cols);
    for (let i = 0; i < N; i++) { re[i] = (x[start + i] || 0) * (0.5 - 0.5 * Math.cos(2 * Math.PI * i / N)); im[i] = 0; }
    fft(re, im);
    for (let y = 0; y < h; y++) {
      const bin = Math.floor((h - 1 - y) * bins / h), db = 10 * Math.log10(re[bin] * re[bin] + im[bin] * im[bin] + 1e-9);
      const t = Math.max(0, Math.min(1, (db + 30) / 60)), p = (y * w + c) * 4;
      img.data[p] = 21 + t * t * 221; img.data[p + 1] = 25 + t * 168; img.data[p + 2] = 30 + Math.sqrt(t) * 48; img.data[p + 3] = 255;
    }
  }
  g.putImageData(img, 0, 0);
}

(async () => {
  for (const canvas of document.querySelectorAll("canvas[data-src]")) {
    try {
      const { rate, x } = samplesOf(await (await fetch(canvas.dataset.src)).arrayBuffer());
      drawWave(canvas, x);
      drawSpec(canvas.nextElementSibling, x, rate);
    } catch (e) { /* the players still work without the drawings */ }
  }
})();
</script>
"""

if __name__ == "__main__":
    sys.exit(main())
