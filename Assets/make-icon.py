# Builds Assets/GUARD.ico from the two SVG masters. Run whenever the art
# changes; the .ico is committed, so this is a dev-time tool, not a build step.
#
#   python Assets/make-icon.py        (needs: pip install resvg_py pillow)
#
# guard-icon.svg is the full aero artwork (48 px and up); guard-icon-small.svg
# is the hand-simplified rendition whose G, rim, and dot are thickened to stay
# legible at 16-32 px. Pillow writes PNG-compressed entries, fine on the
# Windows 10+ GUARD targets.
import io
import pathlib

import resvg_py
from PIL import Image

HERE = pathlib.Path(__file__).parent
SRC = {"full": HERE / "guard-icon.svg", "small": HERE / "guard-icon-small.svg"}
PLAN = [(16, "small"), (24, "small"), (32, "small"),
        (48, "full"), (64, "full"), (256, "full")]

frames = []
for size, kind in PLAN:
    png = resvg_py.svg_to_bytes(svg_path=str(SRC[kind]), width=size, height=size)
    if size == 256:
        # Also the About dialog logo (embedded resource, see csproj).
        (HERE / "guard-icon-256.png").write_bytes(bytes(png))
    frames.append(Image.open(io.BytesIO(bytes(png))).convert("RGBA"))

out = HERE / "GUARD.ico"
frames[-1].save(out, format="ICO", sizes=[f.size for f in frames],
                append_images=frames[:-1])
print(f"wrote {out} ({out.stat().st_size} bytes) with sizes "
      f"{[f.size for f in frames]}")
