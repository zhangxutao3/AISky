"""Generate AISky application artwork and fixed data-driven layer thumbnails."""

from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import numpy as np
from netCDF4 import Dataset
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "DataWorker"))
import worker  # noqa: E402


def color(value: str) -> tuple[int, int, int]:
    value = value.lstrip("#")
    return tuple(int(value[index:index + 2], 16) for index in (0, 2, 4))


def interpolate(palette: tuple[str, ...], amount: np.ndarray) -> np.ndarray:
    stops = np.asarray([color(item) for item in palette], dtype=np.float32)
    scaled = np.nan_to_num(np.clip(amount, 0, 0.999999), nan=0.0) * (len(stops) - 1)
    first = np.floor(scaled).astype(np.int32)
    blend = (scaled - first)[..., None]
    return (stops[first] * (1 - blend) + stops[np.minimum(first + 1, len(stops) - 1)] * blend).astype(np.uint8)


def read_spec(dataset: Dataset, spec: worker.LayerSpec) -> np.ndarray | None:
    if spec.vector_aliases:
        u_name = worker.find_variable(dataset, spec.vector_aliases[0])
        v_name = worker.find_variable(dataset, spec.vector_aliases[1])
        if not u_name or not v_name:
            return None
        u = worker.read_array(dataset, u_name)
        v = worker.read_array(dataset, v_name)
        return np.hypot(u, v).astype(np.float32)
    return worker.read_layer(dataset, spec)


def globe_thumbnail(
    data: np.ndarray,
    spec: worker.LayerSpec,
    size: int = 96,
) -> Image.Image:
    scale = 3
    pixels = size * scale
    yy, xx = np.mgrid[0:pixels, 0:pixels]
    radius = pixels * 0.45
    cx = cy = pixels / 2
    x = (xx - cx) / radius
    y = (cy - yy) / radius
    rho = np.hypot(x, y)
    inside = rho <= 1

    lat0 = math.radians(22)
    lon0 = math.radians(105)
    c = np.arcsin(np.clip(rho, 0, 1))
    safe_rho = np.where(rho == 0, 1, rho)
    lat = np.arcsin(
        np.cos(c) * math.sin(lat0)
        + y * np.sin(c) * math.cos(lat0) / safe_rho
    )
    lon = lon0 + np.arctan2(
        x * np.sin(c),
        safe_rho * math.cos(lat0) * np.cos(c)
        - y * math.sin(lat0) * np.sin(c),
    )
    lat = np.degrees(lat)
    lon = (np.degrees(lon) + 180) % 360 - 180

    rows, cols = data.shape
    row = np.clip(np.rint((lat + 90) / 180 * (rows - 1)), 0, rows - 1).astype(np.int32)
    column = np.clip(np.rint((lon + 180) / 360 * (cols - 1)), 0, cols - 1).astype(np.int32)
    sampled = data[row, column]
    low, high = spec.value_range
    amount = (sampled - low) / max(1e-6, high - low)
    rgb = interpolate(spec.palette, amount)

    rgba = np.zeros((pixels, pixels, 4), dtype=np.uint8)
    rgba[..., :3] = rgb
    rgba[..., 3] = np.where(inside & np.isfinite(sampled), 255, 0)
    image = Image.fromarray(rgba, "RGBA")
    return image.resize((size, size), Image.Resampling.LANCZOS)


def vertical_gradient(size: int) -> Image.Image:
    top = np.array(color("#56D7D0"), dtype=np.float32)
    bottom = np.array(color("#126F91"), dtype=np.float32)
    amount = np.linspace(0, 1, size, dtype=np.float32)[:, None]
    rows = (top * (1 - amount) + bottom * amount).astype(np.uint8)
    rgb = np.repeat(rows[:, None, :], size, axis=1)
    return Image.fromarray(rgb, "RGB").convert("RGBA")


def mark(size: int) -> Image.Image:
    master = Image.open(ROOT / "Assets" / "AISkyIconMaster.png").convert("RGBA")
    return master.resize((size, size), Image.Resampling.LANCZOS)


def app_artwork(output: Path) -> None:
    output.mkdir(parents=True, exist_ok=True)
    mark(88).save(output / "Square44x44Logo.scale-200.png")
    mark(300).save(output / "Square150x150Logo.scale-200.png")
    mark(50).save(output / "StoreLogo.png")
    mark(48).save(output / "LockScreenLogo.scale-200.png")
    mark(24).save(output / "Square44x44Logo.targetsize-24_altform-unplated.png")
    mark(48).save(output / "Square44x44Logo.targetsize-48_altform-lightunplated.png")
    mark(256).save(output / "AppIcon.png")
    mark(256).save(output / "AppIcon.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

    font_path = Path("C:/Windows/Fonts/seguisb.ttf")
    for name, canvas_size, logo_size in (
        ("Wide310x150Logo.scale-200.png", (620, 300), 142),
        ("SplashScreen.scale-200.png", (1240, 600), 230),
    ):
        canvas = Image.new("RGBA", canvas_size, (241, 250, 249, 255))
        icon = mark(logo_size)
        x = int(canvas_size[0] * .31 - logo_size / 2)
        y = int((canvas_size[1] - logo_size) / 2)
        canvas.alpha_composite(icon, (x, y))
        if font_path.exists():
            font = ImageFont.truetype(str(font_path), max(34, int(logo_size * .34)))
            ImageDraw.Draw(canvas).text(
                (x + logo_size + int(logo_size * .16), canvas_size[1] / 2),
                "AISky",
                font=font,
                fill=(21, 61, 73, 255),
                anchor="lm",
            )
        canvas.save(output / name)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sds", type=Path, required=True)
    parser.add_argument("--energy", type=Path, required=True)
    args = parser.parse_args()

    app_artwork(ROOT / "Assets")
    target = ROOT / "Assets" / "Layers"
    target.mkdir(parents=True, exist_ok=True)
    with Dataset(args.energy) as energy, Dataset(args.sds) as sds:
        for spec in worker.COMMON_LAYERS:
            data = read_spec(energy, spec)
            if data is None:
                data = read_spec(sds, spec)
            if data is None:
                continue
            globe_thumbnail(data, spec).save(target / f"{spec.id}.png")
            print(spec.id)


if __name__ == "__main__":
    main()
