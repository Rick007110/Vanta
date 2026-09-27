"""Renders the Vanta logo mark (same geometry as ui/shared/brand.js) to app.ico + PNGs."""
from PIL import Image, ImageDraw
import sys, os
S = 1024  # supersampled canvas; artwork is defined on a 64x64 grid
def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
def grad(t):
    stops = [(0, (0xA7, 0x8B, 0xFA)), (.55, (0x6D, 0x5B, 0xFF)), (1, (0x3B, 0x82, 0xF6))]
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        if t <= t1: return lerp(c0, c1, (t - t0) / (t1 - t0))
    return stops[-1][1]
def render(size):
    k = S / 64
    bg = Image.new('RGB', (S, S))
    px = bg.load()
    for y in range(S):
        for x in range(S):
            px[x, y] = grad((x + y) / (2 * S))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([2 * k, 2 * k, 62 * k, 62 * k], radius=17 * k, fill=255)
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    img.paste(bg, (0, 0), mask)
    d = ImageDraw.Draw(img)
    v = [(17, 18), (26.2, 18), (32, 36.5), (37.8, 18), (47, 18), (36.4, 46.5), (27.6, 46.5)]
    d.polygon([(x * k, y * k) for x, y in v], fill=(250, 251, 255, 255))
    return img.resize((size, size), Image.LANCZOS)
out = sys.argv[1]
big = render(256)
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
big.save(out, format='ICO', sizes=[(s, s) for s in sizes])
big.save(os.path.join(os.path.dirname(out), 'logo-256.png'))
print('ok', out)
