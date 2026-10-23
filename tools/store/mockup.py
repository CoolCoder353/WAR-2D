"""Builds docs/store/mockup/index.html: a Steam-style store page from docs/store/store-text.md and the store art.

The mockup is for review only; it reuses exactly the text that gets pasted into Steamworks.
Usage: python3 tools/store/mockup.py
"""
import html
import os
import re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
STORE = os.path.join(ROOT, 'docs', 'store')
TEXT = os.path.join(STORE, 'store-text.md')
OUT = os.path.join(STORE, 'mockup', 'index.html')

# Upload order (03 repeats 01, so it isn't used).
SCREENSHOTS = ['01-thousands-of-units.png', '02-artillery-and-flamethrowers.png', '05-base-and-production.png',
               '04-splash-damage.png', '06-diggers-tunnelling.png', '07-army-on-the-move.png']


def sections(md):
    """Splits the markdown into {'## heading': body}."""
    parts = re.split(r'^## (.+)$', md, flags=re.M)
    return {parts[i].strip(): parts[i + 1] for i in range(1, len(parts), 2)}


def blocks(body):
    """Every ```text block in a section, in order."""
    return [b.strip('\n') for b in re.findall(r'```text\n(.*?)```', body, flags=re.S)]


def labelled(body):
    """{'**Label**': first text block after it}."""
    out = {}
    for m in re.finditer(r'^\*\*(.+?)\*\*\s*\n+```text\n(.*?)```', body, flags=re.S | re.M):
        out[m.group(1)] = m.group(2).strip('\n')
    return out


def tables(body):
    """{'**Label**': [(field, value)]} for the system requirement tables."""
    out = {}
    for m in re.finditer(r'^\*\*(.+?)\*\*\s*\n+((?:\|.*\|\n)+)', body, flags=re.M):
        rows = []
        for line in m.group(2).strip().split('\n')[2:]:
            cells = [c.strip() for c in line.strip('|').split('|')]
            rows.append((cells[0], cells[1]))
        out[m.group(1)] = rows
    return out


def bbcode(text):
    """Steam markup to HTML (the subset the store text uses)."""
    t = html.escape(text)
    t = re.sub(r'\[h2\](.*?)\[/h2\]', r'<h2 class="bb_tag">\1</h2>', t)
    t = re.sub(r'\[b\](.*?)\[/b\]', r'<b>\1</b>', t)
    t = t.replace('[list]', '<ul class="bb_ul">').replace('[/list]', '</ul>').replace('[*]', '<li>')
    t = re.sub(r'\n(?=<h2)', '', t)
    t = re.sub(r'(</h2>)\n', r'\1', t)
    return t.replace('\n', '<br>')


def plain(text):
    """EA answers: plain text with '- ' bullets."""
    lines, out, in_list = text.split('\n'), [], False
    for line in lines:
        if line.startswith('- '):
            if not in_list:
                out.append('<ul>')
                in_list = True
            out.append('<li>' + html.escape(line[2:]) + '</li>')
        else:
            if in_list:
                out.append('</ul>')
                in_list = False
            out.append(html.escape(line) + '<br>' if line else '<br>')
    if in_list:
        out.append('</ul>')
    return ''.join(out)


def main():
    md = open(TEXT, encoding='utf-8').read()
    s = sections(md)
    short = blocks(s['Short description'])[0]
    about = blocks(s['About This Game (long description)'])[0]
    ea = labelled(s['Early Access questions'])
    reqs = tables(s['System requirements'])
    controller = blocks(s['Controller support'])[0]
    tag_line = [l for l in s['Tags'].split('\n') if l.count(',') > 5][0]
    tags = [t.strip().rstrip('.') for t in tag_line.split(',')]
    access = re.findall(r'^\| (?!Feature|---)(.+?) \| (.+?) \|$', s['Accessibility'], flags=re.M)
    basic = dict(re.findall(r'^\| (?!Field|---)(.+?) \| (.+?) \|$', s['Basic Info'], flags=re.M))

    shots = SCREENSHOTS
    shot_html = ''.join(
        f'<a href="../screenshots/{f}" target="_blank"><img src="../screenshots/{f}" alt="Screenshot {i + 1}"></a>'
        for i, f in enumerate(shots))
    tag_html = ''.join(f'<a class="app_tag">{html.escape(t)}</a>' for t in tags[:7]) + '<a class="app_tag">+</a>'
    ea_html = ''.join(f'<div class="ea_q">{html.escape(q)}</div><div class="ea_a">{plain(a)}</div>' for q, a in ea.items())

    def req_list(rows):
        return '<ul class="bb_ul">' + ''.join(f'<li><strong>{html.escape(k)}:</strong> {html.escape(v)}</li>' for k, v in rows) + '</ul>'

    def req_block(os_name, key):
        return (f'<div class="sysreq_tab">{os_name}</div><div class="sysreq_cols">'
                f'<div><strong>MINIMUM:</strong>{req_list(reqs[key + ", minimum"])}</div>'
                f'<div><strong>RECOMMENDED:</strong>{req_list(reqs[key + ", recommended"])}</div></div>')

    access_html = ''.join(f'<li><b>{html.escape(a)}</b>: {html.escape(b)}</li>' for a, b in access)

    page = f'''<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>WAR Store Mockup</title>
<style>
:root {{ --bg:#1b2838; --panel:#16202d; --text:#c6d4df; --muted:#8f98a0; --accent:#67c1f5; --green:#5c7e10; --ea:#4c6b22; }}
* {{ box-sizing:border-box; }}
body {{ margin:0; background:radial-gradient(ellipse at top,#2a475e 0%,#1b2838 55%) fixed; color:var(--text); font:14px/1.5 "Motiva Sans",Arial,Helvetica,sans-serif; }}
.page {{ max-width:960px; margin:0 auto; padding:16px; }}
.note {{ background:#3a2a00; color:#ffd479; padding:8px 12px; border-radius:3px; margin-bottom:12px; font-size:12px; }}
.crumbs {{ color:var(--muted); font-size:12px; }}
h1.title {{ font-weight:normal; font-size:26px; color:#fff; margin:4px 0 12px; }}
.top {{ display:grid; grid-template-columns:600px 324px; gap:16px; background:rgba(0,0,0,.2); padding:0; }}
.hero img.main {{ width:100%; aspect-ratio:16/9; object-fit:cover; display:block; }}
.strip {{ display:flex; gap:4px; overflow-x:auto; margin-top:4px; }}
.strip img {{ height:65px; display:block; }}
.side img.header {{ width:100%; display:block; }}
.side .desc {{ font-size:13px; padding:10px 0; color:#c6d4df; }}
.meta {{ font-size:11px; color:var(--muted); line-height:1.9; }}
.meta b {{ color:#8f98a0; font-weight:normal; display:inline-block; width:110px; text-transform:uppercase; }}
.meta span {{ color:var(--accent); }}
.app_tag {{ display:inline-block; background:rgba(103,193,245,.2); color:var(--accent); padding:0 7px; margin:2px 2px 0 0; border-radius:2px; font-size:11px; line-height:19px; }}
.ea_banner {{ margin:16px 0; background:linear-gradient(90deg,#3f5d23,#2b3f17); padding:12px 16px; border-radius:3px; }}
.ea_banner h3 {{ margin:0; color:#d2efa9; font-size:15px; text-transform:uppercase; letter-spacing:.5px; }}
.ea_banner p {{ margin:4px 0 0; color:#d2efa9; font-size:13px; }}
.cols {{ display:grid; grid-template-columns:616px 308px; gap:36px; }}
.purchase {{ background:linear-gradient(-60deg,rgba(226,244,255,.3) 5%,rgba(84,107,115,.3) 95%); padding:16px; border-radius:4px; position:relative; margin-bottom:28px; }}
.purchase h2 {{ margin:0 0 8px; font-size:21px; font-weight:normal; color:#fff; }}
.btn {{ display:inline-block; background:linear-gradient(90deg,#75b022 5%,#588a1b 95%); color:#d2efa9; padding:6px 15px; border-radius:2px; position:absolute; right:12px; bottom:-14px; border:0; }}
.price {{ position:absolute; right:110px; bottom:-14px; background:#000; padding:6px 12px; }}
.section h2.hd {{ font-size:14px; text-transform:uppercase; color:#fff; letter-spacing:2px; font-weight:normal; border-bottom:1px solid; border-image:linear-gradient(to right,#3b6e8c 5%,rgba(0,0,0,0) 75%) 1; padding-bottom:2px; margin:24px 0 10px; }}
.bb_tag {{ color:#fff; font-size:15px; margin:18px 0 4px; font-weight:normal; text-transform:uppercase; letter-spacing:1px; }}
.ea_q {{ color:#fff; font-weight:bold; margin-top:12px; }}
.ea_a {{ color:#acb2b8; }}
.ea_a ul, .bb_ul {{ margin:4px 0; padding-left:18px; }}
.sysreq_tab {{ display:inline-block; background:#2a475e; padding:3px 10px; margin-top:10px; color:#fff; font-size:12px; }}
.sysreq_cols {{ display:grid; grid-template-columns:1fr 1fr; gap:12px; font-size:12px; color:#acb2b8; padding:8px 0; }}
.block {{ background:rgba(0,0,0,.2); padding:12px; margin-bottom:12px; font-size:12px; }}
.block h4 {{ margin:0 0 6px; color:#fff; font-weight:normal; text-transform:uppercase; font-size:12px; letter-spacing:1px; }}
.capsules {{ display:flex; flex-wrap:wrap; gap:12px; align-items:flex-start; }}
.capsules figure {{ margin:0; font-size:11px; color:var(--muted); }}
.capsules img {{ display:block; max-width:100%; }}
@media (max-width:980px) {{ .top, .cols {{ grid-template-columns:1fr; }} .sysreq_cols {{ grid-template-columns:1fr; }} }}
</style></head><body><div class="page">
<div class="note">Mockup for review, built from docs/store/store-text.md. Not the real Steam page.</div>
<div class="crumbs">All Games &gt; Strategy Games &gt; {html.escape(basic.get("Game name", ""))}</div>
<h1 class="title">{html.escape(basic.get("Game name", ""))}</h1>
<div class="top">
  <div class="hero"><img class="main" src="../screenshots/{shots[0]}" alt="Main screenshot"><div class="strip">{shot_html}</div></div>
  <div class="side">
    <img class="header" src="../art/v2/header_capsule_920x430.png" alt="Header capsule">
    <div class="desc">{html.escape(short)}</div>
    <div class="meta"><b>All reviews:</b> No user reviews<br><b>Release date:</b> To be announced<br>
    <b>Developer:</b> <span>{html.escape(basic.get("Developer", ""))}</span><br><b>Publisher:</b> <span>{html.escape(basic.get("Publisher", ""))}</span></div>
    <div class="meta" style="margin-top:6px">Popular user-defined tags for this product:</div>{tag_html}
  </div>
</div>
<div class="ea_banner"><h3>Early Access Game</h3><p>Get instant access and start playing; get involved with this game as it develops.</p></div>
<div class="cols">
 <div>
  <div class="purchase"><h2>Play {html.escape(basic.get("Game name", ""))}</h2><span class="price">Free To Play</span><span class="btn">Play Game</span></div>
  <div class="section"><h2 class="hd">What the developers have to say</h2>{ea_html}</div>
  <div class="section"><h2 class="hd">About this game</h2>{bbcode(about)}</div>
  <div class="section"><h2 class="hd">System requirements</h2>{req_block("Windows", "Windows")}{req_block("SteamOS + Linux", "Linux + SteamOS")}</div>
 </div>
 <div>
  <div class="block"><h4>Players and features</h4>{html.escape(basic.get("Players", ""))}</div>
  <div class="block"><h4>Languages</h4>{html.escape(basic.get("Languages", ""))}</div>
  <div class="block"><h4>Controller</h4>{html.escape(controller)}</div>
  <div class="block"><h4>Accessibility</h4><ul class="bb_ul">{access_html}</ul></div>
  <div class="block"><h4>All tags</h4>{"".join(f'<a class="app_tag">{html.escape(t)}</a>' for t in tags)}</div>
  <div class="block"><h4>Legal</h4>{html.escape(basic.get("Legal lines", ""))}</div>
 </div>
</div>
<div class="section"><h2 class="hd">Every capsule (where Steam shows them)</h2><div class="capsules">
<figure><img src="../art/v2/main_capsule_1232x706.png" width="616" alt="Main capsule">Main capsule: front-page carousel</figure>
<figure><img src="../art/v2/vertical_capsule_748x896.png" width="250" alt="Vertical capsule">Vertical capsule: seasonal sales</figure>
<figure><img src="../art/v2/small_capsule_462x174.png" width="231" alt="Small capsule">Small capsule: search and lists (shown at 231 × 87)</figure>
<figure><img src="../art/v2/library_capsule_600x900.png" width="200" alt="Library capsule">Library capsule: library grid</figure>
<figure><img src="../art/v2/library_header_920x430.png" width="460" alt="Library header">Library header</figure>
<figure style="position:relative;width:920px;max-width:100%"><img src="../art/v2/library_hero_3840x1240.png" width="920" alt="Library hero"><img src="../art/library_logo_1280.png" alt="Library logo" style="position:absolute;left:3%;bottom:34px;width:32%">Library hero with the library logo over it, as the Steam client places it (bottom left)</figure>
</div></div>
</div></body></html>
'''
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, 'w', encoding='utf-8').write(page)
    print('wrote', os.path.relpath(OUT, ROOT), f'({len(ea)} EA answers, {len(tags)} tags, {len(shots)} screenshots)')


if __name__ == '__main__':
    main()
