"""Build lightweight Natural Earth overlays for the local Canvas map."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Iterable

import cartopy.io.shapereader as shapereader
from shapely.geometry import LineString, MultiLineString, MultiPolygon, Polygon

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "MapHost" / "assets"


def attribute(item: dict, name: str, default=None):
    return item.get(name, item.get(name.upper(), item.get(name.lower(), default)))


def coordinates(line: LineString) -> list[list[float]]:
    return [[round(lon, 4), round(lat, 4)] for lon, lat in line.coords]


def lines_from_geometry(geometry, tolerance: float) -> Iterable[list[list[float]]]:
    if geometry is None:
        return
    simplified = geometry.simplify(tolerance, preserve_topology=True)
    if isinstance(simplified, LineString):
        yield coordinates(simplified)
    elif isinstance(simplified, MultiLineString):
        for line in simplified.geoms:
            yield coordinates(line)
    elif isinstance(simplified, Polygon):
        yield coordinates(LineString(simplified.exterior.coords))
    elif isinstance(simplified, MultiPolygon):
        for polygon in simplified.geoms:
            yield coordinates(LineString(polygon.exterior.coords))


def polygons_from_geometry(geometry, tolerance: float) -> Iterable[list[list[float]]]:
    if geometry is None:
        return
    simplified = geometry.simplify(tolerance, preserve_topology=True)
    if isinstance(simplified, Polygon):
        yield coordinates(LineString(simplified.exterior.coords))
    elif isinstance(simplified, MultiPolygon):
        for polygon in simplified.geoms:
            yield coordinates(LineString(polygon.exterior.coords))


def write_lines(
    name: str,
    resolution: str,
    category: str,
    source_name: str,
    *,
    tolerance: float,
    record_filter=lambda _: True,
) -> None:
    source = shapereader.natural_earth(
        resolution=resolution,
        category=category,
        name=source_name,
    )
    result: list[list[list[float]]] = []
    for record in shapereader.Reader(source).records():
        if record_filter(record.attributes):
            result.extend(lines_from_geometry(record.geometry, tolerance))
    (TARGET / name).write_text(
        json.dumps({"lines": result}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def write_lakes() -> None:
    source = shapereader.natural_earth(
        resolution="50m",
        category="physical",
        name="lakes",
    )
    polygons: list[list[list[float]]] = []
    for record in shapereader.Reader(source).records():
        if int(attribute(record.attributes, "scalerank", 99)) <= 5:
            polygons.extend(polygons_from_geometry(record.geometry, 0.045))
    (TARGET / "lakes-50m.json").write_text(
        json.dumps({"polygons": polygons}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def main() -> None:
    TARGET.mkdir(parents=True, exist_ok=True)
    write_lines(
        "countries-110m.json",
        "110m",
        "cultural",
        "admin_0_boundary_lines_land",
        tolerance=0.06,
    )
    write_lines(
        "china-provinces-50m.json",
        "50m",
        "cultural",
        "admin_1_states_provinces_lines",
        tolerance=0.035,
        record_filter=lambda item: attribute(item, "adm0_a3") in {"CHN", "TWN"},
    )
    write_lines(
        "rivers-50m.json",
        "50m",
        "physical",
        "rivers_lake_centerlines",
        tolerance=0.045,
        record_filter=lambda item: int(attribute(item, "scalerank", 99)) <= 7,
    )
    write_lakes()
    for path in (
        "countries-110m.json",
        "china-provinces-50m.json",
        "rivers-50m.json",
        "lakes-50m.json",
    ):
        print(path)


if __name__ == "__main__":
    main()
