#!/usr/bin/env python3
"""Builds Assets/StreamingAssets/Mods/gostop/cards.png from Hwatu_overview.svg.

The artwork is "Hwatu overview.svg" by Marcus Richert, based on Hanafuda graphics by Louie Mantia, Jr.,
https://commons.wikimedia.org/wiki/File:Hwatu_overview.svg, licensed CC BY-SA 4.0
(https://creativecommons.org/licenses/by-sa/4.0/). The atlas this script writes is an adaptation of it
(cards cut apart, a white card face put behind the line art, cards put in Korean month order, a card back and
three bonus cards added) and is under the same license.

    python3 Tools/Hwatu/make_atlas.py path/to/Hwatu_overview.svg

Needs rsvg-convert (librsvg) and Pillow; the bonus cards need a Korean font (Noto Sans CJK KR).

Cells of the atlas, 8 across, left to right and top to bottom: cards 1..48 as (month - 1) * 4 + slot in the order of
gostop.luau, then the back, two double-junk bonus cards and one triple-junk bonus card.
"""
import io
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

SCALE = 2
FRAME_W, FRAME_H = 103.2, 168.2
COLS = [51.6, 160.15, 269.35, 377.9, 498.8, 607.35, 716.55, 825.1, 947.3, 1055.8, 1165, 1273.6]
ROWS = [84.1, 270.4, 457.85, 647.6]
INNER_W, INNER_H, INNER_R = 90.6, 153.8, 7
CARD_W, CARD_H = round(FRAME_W * SCALE), round(FRAME_H * SCALE)
PAD = 2
CELL_W, CELL_H = CARD_W + 2 * PAD, CARD_H + 2 * PAD
ACROSS, DOWN = 8, 7

# The sheet is in Japanese month order (11 willow, 12 paulownia); Korean decks swap the two. Columns of a month on
# the sheet, in the order of our slots.
USUAL = [0, 2, 1, 3]          # bright or animal, ribbon, junk, junk
SHEET = {m: (m, USUAL) for m in range(1, 11)}
SHEET[8] = (8, [0, 2, 1, 3])  # moon, geese, junk, junk
SHEET[11] = (12, [0, 2, 1, 3])  # paulownia: phoenix, double junk, junk, junk
SHEET[12] = (11, [0, 1, 2, 3])  # rain: bright, swallow, ribbon, double junk

FONTS = [
    "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc",
    "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
    "C:/Windows/Fonts/malgunbd.ttf",
]


def render(svg_path: Path) -> Image.Image:
    text = svg_path.read_text(encoding="utf-8")
    marker = '<g enable-background="new">'
    if marker not in text:
        raise SystemExit("the drawing group of the SVG was not found")
    backs = "".join(
        f'<rect x="{cx - INNER_W / 2:.2f}" y="{cy - INNER_H / 2:.2f}" width="{INNER_W}" height="{INNER_H}" rx="{INNER_R}" fill="#fff"/>'
        for cy in ROWS for cx in COLS)
    text = text.replace(marker, marker + backs, 1)
    png = subprocess.run(["rsvg-convert", "-w", str(1326 * SCALE)], input=text.encode("utf-8"), stdout=subprocess.PIPE, check=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")


def frame(fill: str) -> Image.Image:
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {FRAME_W} {FRAME_H}" width="{CARD_W}" height="{CARD_H}">'
           f'<rect width="{FRAME_W}" height="{FRAME_H}" rx="5" fill="#EF1D1E"/>'
           f'<rect x="{(FRAME_W - INNER_W) / 2}" y="{(FRAME_H - INNER_H) / 2}" width="{INNER_W}" height="{INNER_H}" rx="{INNER_R}" fill="{fill}"/></svg>')
    png = subprocess.run(["rsvg-convert"], input=svg.encode("utf-8"), stdout=subprocess.PIPE, check=True).stdout
    return Image.open(io.BytesIO(png)).convert("RGBA")


def font(size: int) -> ImageFont.FreeTypeFont:
    for path in FONTS:
        if Path(path).exists():
            return ImageFont.truetype(path, size, index=1 if path.endswith("Bold.ttc") or path.endswith("Regular.ttc") else 0)
    raise SystemExit("no Korean font was found for the bonus cards")


def back() -> Image.Image:
    card = frame("#B3121B")
    draw = ImageDraw.Draw(card)
    inset = 24
    draw.rounded_rectangle([inset, inset, CARD_W - inset, CARD_H - inset], radius=10, outline="#EF1D1E", width=4)
    return card


def bonus(count: int) -> Image.Image:
    card = frame("#ffffff")
    draw = ImageDraw.Draw(card)
    draw.text((CARD_W / 2, CARD_H * 0.26), "보너스", font=font(40), fill="#B3121B", anchor="mm")
    draw.ellipse([CARD_W / 2 - 52, CARD_H * 0.55 - 52, CARD_W / 2 + 52, CARD_H * 0.55 + 52], fill="#EF1D1E")
    draw.text((CARD_W / 2, CARD_H * 0.55), str(count), font=font(72), fill="#ffffff", anchor="mm")
    draw.text((CARD_W / 2, CARD_H * 0.84), "쌍피" if count == 2 else "쓰리피", font=font(34), fill="#161616", anchor="mm")
    return card


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    sheet = render(Path(sys.argv[1]))
    cards = []
    for month in range(1, 13):
        sheet_month, columns = SHEET[month]
        row, group = divmod(sheet_month - 1, 3)
        for column in columns:
            cx, cy = COLS[group * 4 + column], ROWS[row]
            x, y = round((cx - FRAME_W / 2) * SCALE), round((cy - FRAME_H / 2) * SCALE)
            cards.append(sheet.crop((x, y, x + CARD_W, y + CARD_H)))
    cards += [back(), bonus(2), bonus(2), bonus(3)]

    atlas = Image.new("RGBA", (CELL_W * ACROSS, CELL_H * DOWN), (179, 18, 27, 255))
    for index, card in enumerate(cards):
        column, row = index % ACROSS, index // ACROSS
        flat = Image.new("RGBA", card.size, (239, 29, 30, 255))
        flat.alpha_composite(card)
        atlas.paste(flat, (column * CELL_W + PAD, row * CELL_H + PAD))
    out = Path(__file__).resolve().parents[2] / "Assets" / "StreamingAssets" / "Mods" / "gostop" / "cards.png"
    atlas.convert("RGB").quantize(colors=192, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE).save(out, optimize=True)
    print(f"{len(cards)} cards of {CARD_W}x{CARD_H} in {atlas.width}x{atlas.height}: {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
