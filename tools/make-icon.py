#!/usr/bin/env python3
"""Generates assets/app.ico and assets/icon-256.png for WinExplorer.

The artwork is drawn at 8x and downsampled so the small sizes stay legible.
Requires: pillow (pip install pillow)
"""
from __future__ import annotations

import os
from PIL import Image, ImageDraw

SCALE = 8
SIZE = 256 * SCALE

BG_TOP = (31, 41, 55, 255)       # slate-800
BG_BOTTOM = (17, 24, 39, 255)    # gray-900
FOLDER_BACK = (255, 196, 87, 255)  # amber-300
FOLDER_FRONT = (245, 158, 11, 255)  # amber-500
FOLDER_EDGE = (217, 119, 6, 255)   # amber-600
TAB = (96, 165, 250, 255)          # blue-400
TAB_ACTIVE = (219, 234, 254, 255)  # blue-100


def rounded(draw: ImageDraw.ImageDraw, box, radius, fill, outline=None, width=0):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def render() -> Image.Image:
    img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # App tile with a vertical gradient.
    tile = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    td = ImageDraw.Draw(tile)
    for y in range(SIZE):
        t = y / (SIZE - 1)
        color = tuple(int(BG_TOP[i] + (BG_BOTTOM[i] - BG_TOP[i]) * t) for i in range(4))
        td.line([(0, y), (SIZE, y)], fill=color)
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, SIZE - 1, SIZE - 1], radius=int(SIZE * 0.22), fill=255)
    img.paste(tile, (0, 0), mask)

    # Tab strip peeking out of the folder.
    tab_y = int(SIZE * 0.17)
    tab_h = int(SIZE * 0.16)
    tab_w = int(SIZE * 0.25)
    tab_radius = int(SIZE * 0.055)
    positions = [int(SIZE * 0.135), int(SIZE * 0.375), int(SIZE * 0.615)]
    for idx, x in enumerate(positions):
        fill = TAB_ACTIVE if idx == 1 else TAB
        rounded(d, [x, tab_y, x + tab_w, tab_y + tab_h + tab_radius], tab_radius, fill)

    # Folder body.
    body_left = int(SIZE * 0.13)
    body_right = int(SIZE * 0.87)
    body_top = int(SIZE * 0.35)
    body_bottom = int(SIZE * 0.82)
    d.rounded_rectangle(
        [int(SIZE * 0.12), int(SIZE * 0.29), int(SIZE * 0.45), int(SIZE * 0.395)],
        radius=int(SIZE * 0.03),
        fill=FOLDER_BACK,
    )
    d.rounded_rectangle(
        [body_left, body_top, body_right, body_bottom],
        radius=int(SIZE * 0.05),
        fill=FOLDER_BACK,
    )
    d.rounded_rectangle(
        [body_left, int(SIZE * 0.44), body_right, body_bottom],
        radius=int(SIZE * 0.05),
        fill=FOLDER_FRONT,
    )
    # Front panel highlight.
    d.rounded_rectangle(
        [body_left + int(SIZE * 0.05), int(SIZE * 0.485), body_right - int(SIZE * 0.05), int(SIZE * 0.515)],
        radius=int(SIZE * 0.015),
        fill=(255, 255, 255, 60),
    )
    # Outline for contrast on light backgrounds.
    d.rounded_rectangle(
        [body_left, body_top, body_right, body_bottom],
        radius=int(SIZE * 0.05),
        outline=FOLDER_EDGE,
        width=int(SIZE * 0.012),
    )

    # A small "plus" badge in the lower right corner.
    cx, cy, r = int(SIZE * 0.80), int(SIZE * 0.80), int(SIZE * 0.135)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(37, 99, 235, 255), outline=(191, 219, 254, 255), width=int(SIZE * 0.012))
    bar = int(SIZE * 0.055)
    d.rounded_rectangle([cx - bar, cy - int(bar * 0.28), cx + bar, cy + int(bar * 0.28)], radius=int(bar * 0.28), fill=(255, 255, 255, 255))
    d.rounded_rectangle([cx - int(bar * 0.28), cy - bar, cx + int(bar * 0.28), cy + bar], radius=int(bar * 0.28), fill=(255, 255, 255, 255))
    return img


def main() -> None:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "assets")
    os.makedirs(assets, exist_ok=True)

    master = render()
    sizes = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    frames = [master.resize((s, s), Image.LANCZOS) for s in sizes]

    # Pillow writes multi-resolution .ico files directly.
    frames[0].save(
        os.path.join(assets, "app.ico"),
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames[1:],
    )
    frames[0].save(os.path.join(assets, "icon-256.png"), format="PNG")
    print("wrote assets/app.ico and assets/icon-256.png")


if __name__ == "__main__":
    main()
