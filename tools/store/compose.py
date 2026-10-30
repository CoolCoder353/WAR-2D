"""Composes WAR store key art from the game's generated atlases (no HUD, no health bars, no selection).

Needs Pillow and numpy. Usage: python tools/store/compose.py docs/store/art/v2 [NAME ...]
Draws the capsules Valve's first store review (2026-10-22) asked for; see docs/store/store-text.md.
"""
import os, sys, math, random
import numpy as np
from PIL import Image, ImageFilter
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from atlas import Atlas

import os
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LOGO = os.path.join(ROOT, 'docs/store/art/library_logo_transparent.png')
BLUE, RED = '4DA3FF', 'E5484D'
T = 64  # art pixels per tile

units, fx, tiles, blds = Atlas('Units'), Atlas('Effects'), Atlas('Tiles'), Atlas('Buildings')
_cache = {}


def spr(atlas, id, frame=0, direction=0, team=None):
    key = (atlas.name, id, frame, direction, team)
    if key not in _cache:
        _cache[key] = atlas.get(id, frame, direction, team)
    return _cache[key]


def noise(w, h, scale, rng):
    """Smooth value noise in [0,1] on a w×h grid."""
    gw, gh = int(w / scale) + 3, int(h / scale) + 3
    g = rng.random((gh, gw))
    img = Image.fromarray((g * 255).astype(np.uint8)).resize((int(gw * scale), int(gh * scale)), Image.BICUBIC)
    return np.array(img)[:h, :w].astype(np.float32) / 255


def cave(cols, rows, open_fn, rng):
    """Solid mask: True where rock. open_fn(x, y) in tile units -> openness bias (higher = floor)."""
    n = noise(cols, rows, 4, rng) * 0.6 + noise(cols, rows, 2, rng) * 0.4
    solid = np.zeros((rows, cols), bool)
    for y in range(rows):
        for x in range(cols):
            solid[y, x] = n[y, x] - open_fn(x, y) > 0.5
    return solid


def draw_ground(canvas, solid, rng, ox, oy):
    rows, cols = solid.shape
    def S(x, y):
        return 0 <= x < cols and 0 <= y < rows and solid[y, x] or not (0 <= x < cols and 0 <= y < rows)
    gems = noise(cols, rows, 3, rng) > 0.62
    for y in range(rows):
        for x in range(cols):
            px, py = ox + x * T, oy + y * T
            canvas.alpha_composite(spr(tiles, 'Ground', int(rng.integers(4))), (px, py))
            if solid[y, x]:
                # Mask4 uses world y-up; the canvas row index grows downward.
                m = (1 if S(x, y - 1) else 0) | (2 if S(x + 1, y) else 0) | (4 if S(x, y + 1) else 0) | (8 if S(x - 1, y) else 0)
                if gems[y, x]:
                    canvas.alpha_composite(spr(tiles, 'Gem', int(rng.integers(4)) * 16 + m), (px, py))
                else:
                    canvas.alpha_composite(spr(tiles, 'Wall', m), (px, py))


def put(canvas, im, cx, cy):
    canvas.alpha_composite(im, (int(cx - im.width / 2), int(cy - im.height / 2)))


def scene(W, H, seed, clash, logo_box, logo_scale, rock_edges, buildings=(), reach=420):
    """clash: ((x0,y0),(x1,y1)) normalised line; blue on its left side, red on its right.
    logo_box: normalised (x, y, w, h) kept clear of units and darkened."""
    rng = np.random.default_rng(seed)
    random.seed(seed)
    margin = 2
    cols, rows = W // T + 2 * margin, H // T + 2 * margin
    ox, oy = -margin * T - (T - W % T) // 2, -margin * T - (T - H % T) // 2
    canvas = Image.new('RGBA', (cols * T, rows * T), (30, 30, 32, 255))

    def openness(x, y):
        nx, ny = (x * T + ox + T / 2) / W, (y * T + oy + T / 2) / H
        edge = min(nx - rock_edges[0], rock_edges[1] - nx, ny - rock_edges[2], rock_edges[3] - ny)
        return 0.18 + edge * 2.2

    solid = cave(cols, rows, openness, rng)
    # Keep the battle and buildings on floor.
    (x0, y0), (x1, y1) = clash
    def side(px, py):
        dx, dy = (x1 - x0) * W, (y1 - y0) * H
        return ((px - x0 * W) * dy - (py - y0 * H) * dx) / math.hypot(dx, dy)
    def along(px, py):
        dx, dy = (x1 - x0) * W, (y1 - y0) * H
        L = math.hypot(dx, dy)
        return ((px - x0 * W) * dx + (py - y0 * H) * dy) / L / L
    sub = Image.new('RGBA', (W, H))
    draw_ground(canvas, solid, rng, 0, 0)
    world = canvas.crop((-ox, -oy, -ox + W, -oy + H))

    floor_at = lambda px, py: not solid[int((py - oy) // T), int((px - ox) // T)] if 0 <= (py - oy) // T < rows and 0 <= (px - ox) // T < cols else False
    lx, ly, lw, lh = logo_box[0] * W, logo_box[1] * H, logo_box[2] * W, logo_box[3] * H
    in_logo = lambda px, py: lx - 40 < px < lx + lw + 40 and ly - 40 < py < ly + lh + 40

    for (bid, team, bx, by) in buildings:
        put(world, spr(blds, bid, 0, 0, team), bx * W, by * H)

    # Scorch / smoke on the ground along the clash.
    for _ in range(int(W * H / 60000)):
        px, py = rng.random() * W, rng.random() * H
        if abs(side(px, py)) < 140 and floor_at(px, py):
            put(world, spr(fx, 'Explosion', 5), px, py)

    roster = [('Tank', 10), ('Heavy', 3), ('Artillery', 2), ('Scout', 3), ('Flamethrower', 2), ('Sniper', 2)]
    pool = [u for u, n in roster for _ in range(n)]
    placed = []
    step = 58
    pts = []
    for gy in range(-step, H + step, step):
        for gx in range(-step, W + step, step):
            pts.append((gx + rng.normal(0, 12), gy + rng.normal(0, 12)))
    flames = []
    for px, py in pts:
        s = side(px, py)
        if abs(s) < 22 or abs(s) > reach or in_logo(px, py) or not floor_at(px, py):
            continue
        a = along(px, py)
        if a < -0.05 or a > 1.05:
            continue
        # Denser at the front.
        if rng.random() > 0.95 - abs(s) / (reach * 1.67):
            continue
        team = BLUE if s < 0 else RED
        u = random.choice(pool)
        if abs(s) > 260 and u in ('Scout', 'Flamethrower'):
            u = 'Artillery' if rng.random() < 0.5 else 'Tank'
        if abs(s) < 120 and u == 'Artillery':
            u = 'Tank'
        # Face across the line: perpendicular direction toward the enemy, snapped to 8 directions.
        dx, dy = (x1 - x0) * W, (y1 - y0) * H
        nx, ny = dy, -dx  # points to the positive side (red)
        if s > 0:
            nx, ny = -nx, -ny
        ang = math.atan2(-ny, nx) + rng.normal(0, 0.35)  # y-up angle
        d = int(round(ang / (math.pi / 4))) % 8
        placed.append((py, u, team, px, py, d, abs(s)))
        if u == 'Flamethrower' and abs(s) < 200:
            flames.append((px, py, d))
    placed.sort()
    for _, u, team, px, py, d, dist in placed:
        put(world, spr(units, u, int(rng.integers(4)), d, team), px, py)
    for px, py, d in flames:
        base = spr(fx, 'FlameStream', int(rng.integers(4)))
        im = base.rotate(d * 45, expand=True, resample=Image.NEAREST)
        a = d * math.pi / 4
        r = base.width / 2 + 18
        put(world, im, px + math.cos(a) * r, py - math.sin(a) * r)

    # Explosions and impacts along the line.
    for _ in range(int(W * H / 26000)):
        a = rng.random() * 1.1 - 0.05
        px = (x0 + (x1 - x0) * a) * W + rng.normal(0, 70)
        py = (y0 + (y1 - y0) * a) * H + rng.normal(0, 70)
        if in_logo(px, py):
            continue
        r = rng.random()
        if r < 0.35:
            put(world, spr(fx, 'Explosion', int(rng.integers(1, 4))), px, py)
        elif r < 0.55:
            put(world, spr(fx, 'ShellImpact', int(rng.integers(1, 4))), px, py)
        elif r < 0.58:
            put(world, spr(fx, 'BombBlast', int(rng.integers(2, 4))), px, py)
        else:
            put(world, spr(fx, 'MuzzleFlash', int(rng.integers(2))), px, py)

    # Darken behind the logo and vignette the edges.
    arr = np.array(world).astype(np.float32)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    cx, cy = lx + lw / 2, ly + lh / 2
    d = np.sqrt(((xx - cx) / (lw * 0.62)) ** 2 + ((yy - cy) / (lh * 0.75)) ** 2)
    dark = np.clip(1 - d, 0, 1) ** 0.8 * 0.72
    vig = np.sqrt(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H / 2) / (H * 0.62)) ** 2)
    dark = np.maximum(dark, np.clip(vig - 0.75, 0, 1) * 0.9)
    arr[..., :3] *= (1 - dark)[..., None]
    world = Image.fromarray(arr.astype(np.uint8), 'RGBA')

    if logo_scale:
        logo = Image.open(LOGO).convert('RGBA')
        k = min(lw / logo.width, lh / logo.height) * logo_scale
        logo = logo.resize((round(logo.width * k), round(logo.height * k)), Image.NEAREST)
        world.alpha_composite(logo, (int(cx - logo.width / 2), int(cy - logo.height / 2)))
    return world.convert('RGB')


SIZES = {
    # name: (W, H, clash, logo_box, logo_scale, rock_edges(left,right,top,bottom in norm), buildings)
    'header_capsule_920x430': (920, 430, ((0.66, -0.1), (0.98, 1.1)), (0.03, 0.12, 0.64, 0.76), 1.0, (0.0, 1.0, 0.05, 0.95), ()),
    'small_capsule_462x174': (462, 174, ((0.86, -0.1), (1.0, 1.1)), (0.02, 0.04, 0.96, 0.92), 1.0, (0.0, 1.0, 0.0, 1.0), ()),
    'main_capsule_1232x706': (1232, 706, ((0.66, -0.1), (0.97, 1.1)), (0.03, 0.18, 0.64, 0.64), 1.0, (0.0, 1.0, 0.05, 0.95), ()),
    'vertical_capsule_748x896': (748, 896, ((-0.1, 0.55), (1.1, 0.85)), (0.08, 0.06, 0.84, 0.32), 1.0, (0.03, 0.97, 0.0, 1.0), ()),
    'library_capsule_600x900': (600, 900, ((-0.1, 0.55), (1.1, 0.85)), (0.06, 0.06, 0.88, 0.3), 1.0, (0.03, 0.97, 0.0, 1.0), ()),
    # The hero has no logo (Steam draws the library logo over it, bottom left by default), so that corner stays calm.
    # Drawn at half size and scaled 2x: at 1x the field of tiny units tripped Steam's "contains text" check.
    'library_hero_3840x1240': (3840, 1240, ((0.5, -0.1), (0.72, 1.1)), (0.0, 0.0, 0.0, 0.0), 0, (0.0, 1.0, 0.03, 0.97), (), 1000, 2),
    'library_header_920x430': (920, 430, ((0.66, -0.1), (0.98, 1.1)), (0.03, 0.12, 0.64, 0.76), 1.0, (0.0, 1.0, 0.05, 0.95), ()),
}

if __name__ == '__main__':
    out = sys.argv[1]
    only = sys.argv[2:] or list(SIZES)
    for name in only:
        W, H, clash, box, ls, edges, b = SIZES[name][:7]
        reach = SIZES[name][7] if len(SIZES[name]) > 7 else 420
        k = SIZES[name][8] if len(SIZES[name]) > 8 else 1
        # k > 1 draws at 1/k size and scales up by whole pixels, so the sprites read bigger.
        img = scene(W // k, H // k, 7, clash, box, ls, edges, b, reach // k)
        if k > 1:
            img = img.resize((W, H), Image.NEAREST)
        img.save(f'{out}/{name}.png')
        print(name, img.size)
