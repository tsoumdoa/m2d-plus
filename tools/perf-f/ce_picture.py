"""Side-by-side picture of a curved-engine prototype run: native on the left, prototype on the right.

    python tools/perf-f/ce_picture.py <run folder>/<stem>-ce<k> <out.png> [x0 y0 x1 y1]

Black = Visible, light grey = Hidden, orange = Unresolved. Optional window in drawing coordinates (metres).
Needs Pillow only.
"""
import json, sys
from PIL import Image, ImageDraw, ImageFont

COLOURS = {"Hidden": (196, 200, 206), "Visible": (20, 22, 26), "Unresolved": (232, 120, 0)}
ORDER = ["Hidden", "Visible", "Unresolved"]

def load(path):
    return json.load(open(path, encoding="utf-8"))["pieces"]

def bounds(pieces):
    xs = [v for p in pieces for v in p["p"][0::2]]; ys = [v for p in pieces for v in p["p"][1::2]]
    return min(xs), min(ys), max(xs), max(ys)

def panel(pieces, window, width, scale, title):
    x0, y0, x1, y1 = window; height = int((y1 - y0) * scale) + 1
    image = Image.new("RGB", (width, height + 60), (255, 255, 255)); draw = ImageDraw.Draw(image)
    for cls in ORDER:
        for p in pieces:
            if p["c"] != cls: continue
            pts = [((p["p"][i] - x0) * scale, 60 + (y1 - p["p"][i + 1]) * scale) for i in range(0, len(p["p"]), 2)]
            if all(q[0] < 0 or q[0] > width for q in pts): continue
            draw.line(pts, fill=COLOURS[cls], width=3 if cls != "Hidden" else 2)
    try: font = ImageFont.truetype("arial.ttf", 34)
    except OSError: font = ImageFont.load_default()
    draw.text((16, 10), title, fill=(20, 22, 26), font=font)
    return image

def main(stem, out, window=None):
    native, engine = load(stem + "-native.json"), load(stem + "-engine.json")
    window = window or bounds(native + engine)
    width = 2400; scale = width / (window[2] - window[0])
    def length(pieces, cls):
        total = 0.0
        for p in pieces:
            if p["c"] != cls: continue
            c = p["p"]; total += sum(((c[i + 2] - c[i]) ** 2 + (c[i + 3] - c[i + 1]) ** 2) ** .5 for i in range(0, len(c) - 2, 2))
        return total
    left = panel(native, window, width, scale, f"Rhino native HLD   (orange = Unresolved, {length(native, 'Unresolved'):.1f} m)")
    right = panel(engine, window, width, scale, f"Prototype   (orange = Unresolved, {length(engine, 'Unresolved'):.1f} m)")
    sheet = Image.new("RGB", (width * 2 + 40, left.height), (255, 255, 255)); sheet.paste(left, (0, 0)); sheet.paste(right, (width + 40, 0))
    sheet = sheet.resize((sheet.width // 2, sheet.height // 2), Image.LANCZOS); sheet.save(out)
    print(out, sheet.size)

if __name__ == "__main__":
    args = sys.argv[1:]
    main(args[0], args[1], tuple(float(v) for v in args[2:6]) if len(args) >= 6 else None)
