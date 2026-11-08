"""Draws the logos of the MSIX package (src/*.App/Images) from assets/logo.svg.

Usage (from the repository root):  python tools/msix/make_logos.py [--chrome PATH]

The SVG is drawn once by a headless Chrome or Chromium at 1024 px, then scaled down to every size Windows asks for:
the tile sizes at each scale, the taskbar and Start sizes (targetsize), the wide tile and the Store logo.
Needs Pillow. The images are committed: this only has to run when the drawing changes.
"""
import argparse
import subprocess
import tempfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
SCALES = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}


def render_master(chrome: str, svg: Path, size: int = 1024) -> Image.Image:
    with tempfile.TemporaryDirectory() as tmp:
        page = Path(tmp) / "logo.html"
        page.write_text(f'<html><body style="margin:0;background:transparent"><img src="{svg.as_uri()}" width="{size}" height="{size}"></body></html>')
        out = Path(tmp) / "logo.png"
        subprocess.run([chrome, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars", "--default-background-color=00000000",
                        f"--window-size={size},{size}", f"--screenshot={out}", page.as_uri()], check=True, capture_output=True)
        return Image.open(out).convert("RGBA")


def fit(master: Image.Image, width: int, height: int, glyph: float = 1.0) -> Image.Image:
    """The logo, as large as fits in width x height (times glyph), centred on a transparent canvas."""
    side = round(min(width, height) * glyph)
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    canvas.paste(master.resize((side, side), Image.LANCZOS), ((width - side) // 2, (height - side) // 2))
    return canvas


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--chrome", default="chromium")
    args = parser.parse_args()
    app = next((ROOT / "src").glob("*.App"))
    out = app / "Images"
    out.mkdir(exist_ok=True)
    master = render_master(args.chrome, ROOT / "assets" / "logo.svg")

    for scale, factor in SCALES.items():
        fit(master, round(44 * factor), round(44 * factor)).save(out / f"Square44x44Logo.scale-{scale}.png")
        fit(master, round(150 * factor), round(150 * factor), 0.9).save(out / f"Square150x150Logo.scale-{scale}.png")
        fit(master, round(310 * factor), round(150 * factor), 0.9).save(out / f"Wide310x150Logo.scale-{scale}.png")
        fit(master, round(50 * factor), round(50 * factor)).save(out / f"StoreLogo.scale-{scale}.png")
    for size in (16, 24, 32, 48, 256):
        for form in ("", "_altform-unplated", "_altform-lightunplated"):
            fit(master, size, size).save(out / f"Square44x44Logo.targetsize-{size}{form}.png")
    print(f"{len(list(out.glob('*.png')))} images in {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
