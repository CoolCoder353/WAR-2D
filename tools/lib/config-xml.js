// A small reader and editor for Assets/Resources/GameConfig.xml, shared by tools/ai-tune.js and
// tools/balance-sheet.js (Node, no dependencies). It parses elements, attributes, text and comments, which is all
// GameConfig.xml uses, and edits a leaf's text in place so the rest of the file (comments, layout) is kept.
//
// Paths name one leaf element with dot-separated segments. A segment is either an element name (found anywhere below
// the elements matched so far) or a key attribute value, in attribute order:
//   Difficulty.Hard.AttackRatio      <Difficulty name="Hard"> ... <AttackRatio>
//   Style.Turtle.FirstAttackSeconds  <Style name="Turtle"> ... <FirstAttackSeconds>
//   Unit.Tank.Damage                 <Unit type="Tank"> ... <Damage>
//   DamageTable.Tank.LightUnit       <DamageTable> ... <Entry attacker="Tank" target="LightUnit">
//   Counter.Heavy                    <Counter seen="Heavy">
//   Siege.WallHealth                 <Siege> ... <WallHealth>
// A path must match exactly one element, and that element must hold only text.
'use strict';

/** Parses XML text into a tree of { tag, attrs: [[name, value]], children, parent, textStart, textEnd }. */
function parse(src) {
  const root = { tag: '#document', attrs: [], children: [], parent: null };
  let cur = root, i = 0;
  const fail = (msg) => { throw new Error(`XML: ${msg} at line ${src.slice(0, i).split('\n').length}`); };
  while (i < src.length) {
    const lt = src.indexOf('<', i);
    if (lt < 0) break;
    i = lt;
    if (src.startsWith('<!--', i)) { const e = src.indexOf('-->', i); if (e < 0) fail('unclosed comment'); i = e + 3; continue; }
    if (src.startsWith('<?', i) || src.startsWith('<!', i)) { const e = src.indexOf('>', i); if (e < 0) fail('unclosed declaration'); i = e + 1; continue; }
    if (src[i + 1] === '/') {
      const e = src.indexOf('>', i);
      const tag = src.slice(i + 2, e).trim();
      if (tag !== cur.tag) fail(`</${tag}> closes <${cur.tag}>`);
      if (cur.children.length === 0) cur.textEnd = i;
      cur.end = e + 1;
      cur = cur.parent;
      i = e + 1;
      continue;
    }
    const m = /^<([A-Za-z_][\w.-]*)((?:\s+[\w:.-]+\s*=\s*"[^"]*")*)\s*(\/?)>/.exec(src.slice(i, i + 4096));
    if (!m) fail('bad tag');
    const attrs = [...m[2].matchAll(/([\w:.-]+)\s*=\s*"([^"]*)"/g)].map((a) => [a[1], a[2]]);
    const el = { tag: m[1], attrs, children: [], parent: cur, start: i };
    cur.children.push(el);
    i += m[0].length;
    if (m[3] === '/') { el.textStart = el.textEnd = i; el.end = i; continue; }
    el.textStart = i;
    cur = el;
  }
  if (cur !== root) fail(`<${cur.tag}> is never closed`);
  return root;
}

function descendants(el, out = []) {
  for (const c of el.children) { out.push(c); descendants(c, out); }
  return out;
}

/** The text of a leaf element, trimmed. */
function text(src, el) {
  if (el.children.length) throw new Error(`<${el.tag}> holds elements, not a value`);
  return src.slice(el.textStart, el.textEnd).trim();
}

function describe(el) {
  return `<${el.tag}${el.attrs.map(([k, v]) => ` ${k}="${v}"`).join('')}>`;
}

/** The one element a dot path names (see the top of the file); throws when it names none or several. */
function find(root, path) {
  let set = [root].map((el) => ({ el, attr: 0 }));
  for (const seg of path.split('.')) {
    // 1. The next key attribute of the elements matched so far.
    let next = set.filter((s) => s.el.attrs[s.attr] && s.el.attrs[s.attr][1] === seg).map((s) => ({ el: s.el, attr: s.attr + 1 }));
    // 2. An element of that name anywhere below them.
    if (!next.length) next = set.flatMap((s) => descendants(s.el).filter((d) => d.tag === seg)).map((el) => ({ el, attr: 0 }));
    // 3. A child whose first attribute has that value (DamageTable.Tank.LightUnit).
    if (!next.length) next = set.flatMap((s) => s.el.children.filter((c) => c.attrs[0] && c.attrs[0][1] === seg)).map((el) => ({ el, attr: 1 }));
    if (!next.length) throw new Error(`${path}: nothing matches "${seg}"`);
    set = [...new Map(next.map((s) => [s.el, s])).values()];
  }
  if (set.length > 1) throw new Error(`${path}: matches ${set.length} elements (${set.slice(0, 3).map((s) => describe(s.el)).join(', ')}...); add a segment`);
  const el = set[0].el;
  if (el.children.length) throw new Error(`${path}: ${describe(el)} holds elements, not a value`);
  return el;
}

/** The trimmed text a dot path names. */
function get(src, path, root = parse(src)) {
  return text(src, find(root, path));
}

/** Returns src with each path's value replaced ({ path: value }); whitespace around a value and the rest of the file are kept. */
function setValues(src, values) {
  const root = parse(src);
  const edits = Object.entries(values).map(([path, value]) => {
    const el = find(root, path);
    const raw = src.slice(el.textStart, el.textEnd);
    const lead = raw.length - raw.trimStart().length, trail = raw.length - raw.trimEnd().length;
    return { start: el.textStart + lead, end: el.textEnd - trail, value: String(value), path };
  });
  edits.sort((a, b) => b.start - a.start);
  for (let k = 1; k < edits.length; k++) if (edits[k].start === edits[k - 1].start) throw new Error(`${edits[k].path} and ${edits[k - 1].path} name the same value`);
  let out = src;
  for (const e of edits) out = out.slice(0, e.start) + e.value + out.slice(e.end);
  return out;
}

module.exports = { parse, find, get, text, setValues, descendants };
