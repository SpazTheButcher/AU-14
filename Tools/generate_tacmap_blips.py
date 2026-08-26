# CMU14 file: generates tacmap blip RSIs from job icon RSIs (upstream keeps separate
# lobby/tacmap sprite sets; this mirrors that split for our job icons)
#
# Trims each state's transparent margins and re-centers the glyph on a 12x12 canvas
# (lossless - no resampling). The tacmap stretches the whole canvas to the blip box,
# so glyph-vs-canvas ratio is what controls rendered icon size; 8x8 glyph in 12x12
# matches upstream map_blips coverage (~70%).
#
# Run after adding/changing job icon sprites:
#   python3 Tools/generate_tacmap_blips.py
# Then point the job's TacticalMapIcon at the generated RSI (states keep their names).
# Requires ImageMagick (magick).

import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TEXTURES = ROOT / "Resources" / "Textures"
CANVAS = 12

# source job icon RSI -> generated tacmap blip RSI
PAIRS = {
    TEXTURES / "_CMU14/Interface/cmucolonyjobicons.rsi": TEXTURES / "_CMU14/Interface/cmucolonyblips.rsi",
    TEXTURES / "_CMU14/Interface/cmugovforjobicons.rsi": TEXTURES / "_CMU14/Interface/cmugovforblips.rsi",
    TEXTURES / "_CMU14/Interface/cmuopforjobicons.rsi": TEXTURES / "_CMU14/Interface/cmuopforblips.rsi",
}


def generate(src: Path, dst: Path) -> None:
    meta = json.loads((src / "meta.json").read_text(encoding="utf-8"))
    dst.mkdir(parents=True, exist_ok=True)
    for stale in dst.glob("*.png"):
        stale.unlink()

    states = []
    for state in meta["states"]:
        png = src / f"{state['name']}.png"
        proc = subprocess.run(
            ["magick", str(png), "-trim", "+repage", "-format", "%wx%h", "info:"],
            capture_output=True, text=True, check=True)
        w, h = map(int, proc.stdout.split("x"))
        if w > CANVAS or h > CANVAS:
            sys.exit(f"{png.name}: glyph {w}x{h} exceeds {CANVAS}x{CANVAS} canvas - "
                     f"cannot trim losslessly; enlarge CANVAS or redraw smaller")
        subprocess.run(
            ["magick", str(png), "-trim", "+repage",
             "-gravity", "center", "-background", "none",
             "-extent", f"{CANVAS}x{CANVAS}", str(dst / png.name)], check=True)
        states.append(state)

    out = {
        "version": meta["version"],
        "license": meta["license"],
        "copyright": meta["copyright"]
            + " | Tacmap blips in this RSI are auto-generated from "
            + src.name
            + " by Tools/generate_tacmap_blips.py (margin trim, no artistic change).",
        "size": {"x": CANVAS, "y": CANVAS},
        "states": states,
    }
    (dst / "meta.json").write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{dst.relative_to(ROOT)}: {len(states)} states")


def verify(dst: Path) -> None:
    for png in dst.glob("*.png"):
        proc = subprocess.run(["magick", str(png), "-format", "%wx%h", "info:"],
                              capture_output=True, text=True, check=True)
        if proc.stdout != f"{CANVAS}x{CANVAS}":
            sys.exit(f"{png}: expected {CANVAS}x{CANVAS}, got {proc.stdout}")
    print(f"{dst.relative_to(ROOT)}: all {CANVAS}x{CANVAS} OK")


if __name__ == "__main__":
    if shutil.which("magick") is None:
        sys.exit("ImageMagick (magick) not found in PATH")
    for src, dst in PAIRS.items():
        if not (src / "meta.json").is_file():
            sys.exit(f"missing {src}/meta.json")
        generate(src, dst)
        verify(dst)
