"""Generate AISky application artwork and fixed data-driven layer thumbnails."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import cartopy.crs as ccrs
import matplotlib
import numpy as np
from netCDF4 import Dataset
from PIL import Image, ImageDraw, ImageFilter, ImageFont

matplotlib.use("Agg")
from matplotlib import pyplot as plt  # noqa: E402
from matplotlib.colors import LinearSegmentedColormap, Normalize  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "DataWorker"))
import worker  # noqa: E402


def color(value: str) -> tuple[int, int, int]:
    value = value.lstrip("#")
    return tuple(int(value[index:index + 2], 16) for index in (0, 2, 4))


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
    latitudes: np.ndarray,
    longitudes: np.ndarray,
    size: int = 96,
) -> Image.Image:
    """Render a reusable border-free Orthographic field thumbnail with Cartopy."""
    scale = 3
    pixels = size * scale
    figure = plt.figure(figsize=(1, 1), dpi=pixels)
    figure.patch.set_alpha(0)
    axis = figure.add_axes(
        (0.04, 0.04, 0.92, 0.92),
        projection=ccrs.Orthographic(central_longitude=105, central_latitude=22),
    )
    axis.set_global()
    axis.patch.set_facecolor((0, 0, 0, 0))
    axis.spines["geo"].set_visible(False)
    palette = LinearSegmentedColormap.from_list(
        f"aisky-{spec.id}",
        spec.palette,
        N=256,
    )
    axis.pcolormesh(
        longitudes,
        latitudes,
        np.ma.masked_invalid(data),
        transform=ccrs.PlateCarree(),
        cmap=palette,
        norm=Normalize(*spec.value_range, clip=True),
        shading="auto",
        rasterized=True,
    )
    figure.canvas.draw()
    rgba = np.asarray(figure.canvas.buffer_rgba()).copy()
    plt.close(figure)
    return Image.fromarray(rgba, "RGBA").resize(
        (size, size),
        Image.Resampling.LANCZOS,
    )


def read_coordinates(dataset: Dataset) -> tuple[np.ndarray, np.ndarray]:
    latitude_name = worker.find_variable(dataset, ("lat", "latitude"))
    longitude_name = worker.find_variable(dataset, ("lon", "longitude"))
    if not latitude_name or not longitude_name:
        raise ValueError("NetCDF 中缺少缩略图所需的经纬度坐标。")
    latitudes = np.asarray(dataset[latitude_name][:], dtype=np.float32).squeeze()
    longitudes = np.asarray(dataset[longitude_name][:], dtype=np.float32).squeeze()
    if latitudes.ndim != 1 or longitudes.ndim != 1:
        raise ValueError("缩略图生成器仅支持一维经纬度坐标。")
    return latitudes, longitudes


def simplify_legacy_thumbnail(path: Path, size: int = 96) -> None:
    """Keep the broad field pattern while removing legacy line overlays."""
    image = Image.open(path).convert("RGBA")
    image = image.resize((18, 18), Image.Resampling.LANCZOS)
    image = image.filter(ImageFilter.GaussianBlur(radius=0.75))
    image = image.resize((size, size), Image.Resampling.BICUBIC)
    mask = Image.new("L", (size, size), 0)
    padding = round(size * 0.05)
    ImageDraw.Draw(mask).ellipse(
        (padding, padding, size - padding - 1, size - padding - 1),
        fill=255,
    )
    image.putalpha(mask)
    image.save(path)


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
    parser.add_argument(
        "--only",
        nargs="+",
        default=(),
        help="只重建指定的小写变量 ID；省略时重建全部缩略图。",
    )
    args = parser.parse_args()

    selected = {item.lower() for item in args.only}
    if not selected:
        app_artwork(ROOT / "Assets")
    target = ROOT / "Assets" / "Layers"
    target.mkdir(parents=True, exist_ok=True)
    generated: set[str] = set()
    with Dataset(args.energy) as energy, Dataset(args.sds) as sds:
        energy_coordinates = read_coordinates(energy)
        sds_coordinates = read_coordinates(sds)
        for spec in worker.COMMON_LAYERS:
            if selected and spec.id not in selected:
                continue
            data = read_spec(energy, spec)
            coordinates = energy_coordinates
            if data is None:
                data = read_spec(sds, spec)
                coordinates = sds_coordinates
            if data is None:
                continue
            globe_thumbnail(data, spec, *coordinates).save(target / f"{spec.id}.png")
            generated.add(spec.id)
            print(spec.id)
    if not selected:
        for path in target.glob("*.png"):
            if path.stem not in generated:
                simplify_legacy_thumbnail(path)
                print(f"{path.stem} (simplified)")


if __name__ == "__main__":
    main()
