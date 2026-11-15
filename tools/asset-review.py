#!/usr/bin/env python3
"""Builds a review page for sprites and effects (asset-generation spec §3.3).

  tools/asset-review.py <name> [--only <glob>] [--show <glob>] [--title T] [--intro TEXT] [--from <assets folder>]

Runs tools/art-build.sh --check (nothing under Assets is written), then writes docs/art/review/<date>-<name>/ with,
for every sprite whose pixels would change: its cells before and after at 4x, and a contact sheet (the sprite on real
floor and rock in four player colours) at near, default and far zoom, before and after. --show <glob> adds sprites
that did not change, as they are built now (for approving art that has no record). --only <glob> renders just those
sprites (seconds instead of two minutes). --from reuses the output of an earlier art-build run.

The page asks the owner, per sprite: use the new one, keep the current one, or change it (with a note), and has a
box with the answers to paste back. Publish index.html with its PNGs as a private Artifact. Sounds have their own
tool, tools/sound-review.py. No sprite goes into Assets/Resources before the owner has approved it on such a page.
"""
import argparse, datetime, html, json, re, shutil, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def run_build(only, show):
    cmd = [str(ROOT / "tools/art-build.sh"), "--check"]
    if only:
        cmd += ["--only", only]
    if show:
        cmd += ["--show", show]
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True).stdout
    sys.stdout.write(out)
    folders = sorted(ROOT.glob("TestResults/*/assets/report.txt"))
    if not folders:
        sys.exit("art-build wrote no report")
    return folders[-1].parent


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("name")
    ap.add_argument("--only")
    ap.add_argument("--show")
    ap.add_argument("--title", default="Sprite review")
    ap.add_argument("--intro", default="")
    ap.add_argument("--from", dest="source")
    args = ap.parse_args()
    if not re.fullmatch(r"[a-z0-9-]+", args.name):
        sys.exit("name: lower-case letters, digits and hyphens")

    source = Path(args.source) if args.source else run_build(args.only, args.show)
    report = (source / "report.txt").read_text()
    if report.startswith("FAILED") or "\nFAILED" in report:
        sys.exit(report)
    sprites = []
    for line in report.splitlines():
        m = re.match(r"(changed|shown): (\w+)/(\w+)( \(new\))?", line)
        if m:
            state = "new" if m.group(4) else m.group(1)
            sprites.append({"atlas": m.group(2), "id": m.group(3), "state": state})
    if not sprites:
        sys.exit("nothing to review: no sprite changed (use --show <glob> to review sprites as they are)")

    out = ROOT / "docs/art/review" / f"{datetime.date.today().isoformat()}-{args.name}"
    out.mkdir(parents=True, exist_ok=True)
    for s in sprites:
        s["files"] = {}
        for part in ("before", "after", "before.contact", "after.contact"):
            f = source / f"{s['id']}.{part}.png"
            if f.exists():
                shutil.copy(f, out / f.name)
                s["files"][part] = f.name

    page = PAGE.replace("__TITLE__", html.escape(args.title)).replace("__INTRO__", html.escape(args.intro))
    page = page.replace("__KEY__", "sprite-review-" + args.name).replace("__DATA__", json.dumps(sprites))
    (out / "index.html").write_text(page)
    print(f"{len(sprites)} sprites: {out.relative_to(ROOT)}/index.html")


PAGE = r"""<title>__TITLE__</title>
<meta name="claude:viewport-fit-sides" content="cover">
<style>
:root { --bg: #f4f2ee; --panel: #fffdf9; --fg: #1d1b18; --dim: #6b655c; --line: #d9d3c8; --accent: #b3541e; --good: #2f6f4f; --chip: #ece7de; }
@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) { --bg: #171614; --panel: #201e1b; --fg: #ece8e1; --dim: #a39c90; --line: #38342e; --accent: #f0935a; --good: #7cc49b; --chip: #2b2824; color-scheme: dark } }
:root[data-theme="dark"] { --bg: #171614; --panel: #201e1b; --fg: #ece8e1; --dim: #a39c90; --line: #38342e; --accent: #f0935a; --good: #7cc49b; --chip: #2b2824; color-scheme: dark }
body { background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, sans-serif; padding-block: 24px 80px;
  padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); }
main { max-width: 1100px; margin: 0 auto; display: flex; flex-direction: column; gap: 20px; }
h1 { font-size: 26px; margin: 0; text-wrap: balance; }
h2 { font-size: 18px; margin: 0; }
p { margin: 0; max-width: 70ch; }
.dim { color: var(--dim); }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 16px; display: flex; flex-direction: column; gap: 12px; min-width: 0; }
.head { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
.chip { background: var(--chip); border-radius: 999px; padding: 1px 10px; font-size: 12px; letter-spacing: .04em; text-transform: uppercase; color: var(--dim); }
.pair { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 12px; }
.pair > div { min-width: 0; display: flex; flex-direction: column; gap: 4px; }
.label { font-size: 12px; letter-spacing: .06em; text-transform: uppercase; color: var(--dim); }
.scroll { overflow: auto; max-height: 420px; border: 1px solid var(--line); border-radius: 6px; background: repeating-conic-gradient(var(--chip) 0% 25%, var(--panel) 0% 50%) 0 0 / 16px 16px; }
.scroll.ground { background: #000; }
img { image-rendering: pixelated; display: block; max-width: none; }
img.far { image-rendering: auto; }
.zooms { display: flex; gap: 6px; flex-wrap: wrap; }
button { font: inherit; color: var(--fg); background: var(--chip); border: 1px solid var(--line); border-radius: 6px; padding: 3px 10px; cursor: pointer; }
button[aria-pressed="true"] { border-color: var(--accent); color: var(--accent); }
.answers { display: flex; flex-wrap: wrap; gap: 14px; align-items: center; }
.answers label { display: flex; gap: 6px; align-items: center; }
input[type=text] { font: inherit; flex: 1 1 220px; min-width: 0; padding: 4px 8px; border: 1px solid var(--line); border-radius: 6px; background: var(--bg); color: var(--fg); }
textarea { font: 13px/1.4 ui-monospace, monospace; width: 100%; min-height: 150px; box-sizing: border-box; border: 1px solid var(--line); border-radius: 6px; background: var(--bg); color: var(--fg); padding: 8px; }
</style>
<main>
  <h1>__TITLE__</h1>
  <p>__INTRO__</p>
  <p class="dim">Nothing here is in the game yet. For each sprite choose one answer; add a note where you want a change. The contact sheet shows the sprite on floor (top) and rock (bottom) in four player colours: switch between near, default and far zoom. Then copy the summary at the bottom and paste it back.</p>
  <div id="cards" style="display:flex;flex-direction:column;gap:20px"></div>
  <div class="card">
    <h2>Your answers</h2>
    <textarea id="summary" readonly></textarea>
    <div><button id="copy">Copy</button> <span id="copied" class="dim"></span></div>
  </div>
</main>
<script>
const KEY = "__KEY__", DATA = __DATA__;
let saved = {};
try { saved = JSON.parse(localStorage.getItem(KEY) || "{}"); } catch (e) {}
const store = () => { try { localStorage.setItem(KEY, JSON.stringify(saved)); } catch (e) {} };
const el = (tag, attrs = {}, kids = []) => { const n = document.createElement(tag); for (const k in attrs) k === "text" ? n.textContent = attrs[k] : n.setAttribute(k, attrs[k]); kids.forEach(c => n.append(c)); return n; };
const ZOOMS = [["Near", 2, ""], ["Default", 1, ""], ["Far", 0.5, "far"]];

function picture(label, file, ground) {
  const img = el("img", { src: file, alt: label });
  return [el("div", {}, [el("div", { class: "label", text: label }), el("div", { class: "scroll" + (ground ? " ground" : "") }, [img])]), img];
}

for (const s of DATA) {
  const options = s.state === "shown" ? ["Approve", "Change"] : ["Use new", "Keep current", "Change"];
  const card = el("div", { class: "card" });
  card.append(el("div", { class: "head" }, [el("h2", { text: s.id }), el("span", { class: "chip", text: s.atlas }), el("span", { class: "chip", text: s.state === "shown" ? "as built" : s.state })]));
  const cells = el("div", { class: "pair" });
  if (s.files.before && s.state !== "shown") cells.append(picture("Current, cells at 4×", s.files.before)[0]);
  if (s.files.after) cells.append(picture(s.state === "shown" ? "Cells at 4×" : "New, cells at 4×", s.files.after)[0]);
  card.append(cells);
  const contacts = [];
  const sheets = el("div", { class: "pair" });
  if (s.files["before.contact"] && s.state !== "shown") { const [box, img] = picture("Current, in a match", s.files["before.contact"], true); sheets.append(box); contacts.push(img); }
  if (s.files["after.contact"]) { const [box, img] = picture(s.state === "shown" ? "In a match" : "New, in a match", s.files["after.contact"], true); sheets.append(box); contacts.push(img); }
  if (contacts.length) {
    const zooms = el("div", { class: "zooms" });
    const set = (scale, cls, button) => {
      contacts.forEach(img => { const apply = () => { img.style.width = img.naturalWidth * scale + "px"; img.className = cls; }; img.complete ? apply() : img.addEventListener("load", apply, { once: true }); });
      zooms.querySelectorAll("button").forEach(b => b.setAttribute("aria-pressed", b === button));
    };
    ZOOMS.forEach(([name, scale, cls]) => { const b = el("button", { text: name, type: "button" }); b.onclick = () => set(scale, cls, b); zooms.append(b); });
    card.append(zooms, sheets);
    set(1, "", zooms.children[1]);
  }
  const answers = el("div", { class: "answers" });
  const mine = saved[s.id] || (saved[s.id] = {});
  options.forEach(o => {
    const radio = el("input", { type: "radio", name: "a-" + s.id, value: o });
    radio.checked = mine.answer === o;
    radio.onchange = () => { mine.answer = o; store(); summarise(); };
    answers.append(el("label", {}, [radio, o]));
  });
  const note = el("input", { type: "text", placeholder: "Note (what to change)" });
  note.value = mine.note || "";
  note.oninput = () => { mine.note = note.value; store(); summarise(); };
  answers.append(note);
  card.append(answers);
  document.getElementById("cards").append(card);
}

function summarise() {
  const groups = {};
  for (const s of DATA) {
    const a = saved[s.id] || {};
    (groups[a.answer || "Not answered"] ||= []).push(s.id + (a.note ? " (" + a.note + ")" : ""));
  }
  const order = ["Use new", "Approve", "Keep current", "Change", "Not answered"];
  document.getElementById("summary").value = "Sprite review " + KEY + "\n" +
    order.filter(k => groups[k]).map(k => k + " (" + groups[k].length + "): " + groups[k].join(", ")).join("\n");
}
summarise();
document.getElementById("copy").onclick = async () => {
  const box = document.getElementById("summary");
  try { await navigator.clipboard.writeText(box.value); document.getElementById("copied").textContent = "Copied"; }
  catch (e) { box.select(); document.getElementById("copied").textContent = "Select and copy the text"; }
};
</script>
"""

if __name__ == "__main__":
    main()
