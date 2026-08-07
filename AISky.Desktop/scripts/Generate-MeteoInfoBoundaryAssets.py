"""Convert selected MeteoInfo shapefiles into lightweight AISky map overlays."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Iterable

import shapefile
from shapely.geometry import LineString, shape as shapely_shape
from shapely.ops import linemerge, unary_union
from shapely.strtree import STRtree

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_TARGET = ROOT / "MapHost" / "assets"


def line_strings(geometry) -> Iterable[LineString]:
    if geometry is None or geometry.is_empty:
        return
    if geometry.geom_type == "LineString":
        if len(geometry.coords) >= 2 and geometry.length > 1e-5:
            yield geometry
        return
    for child in getattr(geometry, "geoms", ()):
        yield from line_strings(child)


def coordinates(line: LineString, tolerance: float) -> list[list[float]]:
    simplified = line.simplify(tolerance, preserve_topology=False)
    return [[round(float(lon), 4), round(float(lat), 4)] for lon, lat in simplified.coords]


def sorted_coordinates(
    lines: Iterable[LineString],
    tolerance: float,
) -> list[list[list[float]]]:
    result = [coordinates(line, tolerance) for line in lines]
    result = [line for line in result if len(line) >= 2]
    result.sort(
        key=lambda line: (
            round(min(point[0] for point in line), 4),
            round(min(point[1] for point in line), 4),
            round(max(point[0] for point in line), 4),
            round(max(point[1] for point in line), 4),
        )
    )
    return result


def country_border_lines(country_path: Path) -> list[LineString]:
    reader = shapefile.Reader(str(country_path), encoding="latin1")
    field_names = [field[0] for field in reader.fields[1:]]
    geometries = []
    country_names = []
    for item in reader.iterShapeRecords():
        geometry = shapely_shape(item.shape.__geo_interface__)
        geometries.append(geometry if geometry.is_valid else geometry.buffer(0))
        attributes = dict(zip(field_names, item.record))
        country_names.append(str(attributes.get("CNTRY_NAME", "")))

    tree = STRtree(geometries)
    shared_boundaries: list[LineString] = []
    for first_index, first in enumerate(geometries):
        for second_index in tree.query(first):
            second_index = int(second_index)
            if second_index <= first_index:
                continue
            if "China" in {country_names[first_index], country_names[second_index]}:
                continue
            intersection = first.boundary.intersection(geometries[second_index].boundary)
            shared_boundaries.extend(line_strings(intersection))

    merged = linemerge(unary_union(shared_boundaries))
    return list(line_strings(merged))


def china_border_lines(
    china_border_path: Path,
) -> tuple[list[LineString], list[LineString], list[LineString], list[float]]:
    reader = shapefile.Reader(str(china_border_path), encoding="gbk", encodingErrors="replace")
    field_names = [field[0] for field in reader.fields[1:]]
    land: list[LineString] = []
    coast: list[LineString] = []
    islands: list[LineString] = []
    for item in reader.iterShapeRecords():
        attributes = dict(zip(field_names, item.record))
        boundary_type = int(attributes.get("GB", 0))
        points = [(float(point[0]), float(point[1])) for point in item.shape.points]
        if len(points) < 2:
            continue
        line = LineString(points)
        if boundary_type in {620201, 620202}:
            land.append(line)
        elif boundary_type == 250200:
            coast.append(line)
        else:
            islands.append(line)
    return land, coast, islands, [round(float(value), 4) for value in reader.bbox]


def write_json(path: Path, payload: dict) -> None:
    path.write_text(
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--source",
        type=Path,
        required=True,
        help="Directory containing MeteoInfo country.shp and cn_border.shp.",
    )
    parser.add_argument("--target", type=Path, default=DEFAULT_TARGET)
    arguments = parser.parse_args()

    country_path = arguments.source / "country.shp"
    china_border_path = arguments.source / "cn_border.shp"
    for source_path in (country_path, china_border_path):
        if not source_path.is_file():
            raise FileNotFoundError(source_path)

    arguments.target.mkdir(parents=True, exist_ok=True)
    global_lines = country_border_lines(country_path)
    land, coast, islands, china_bbox = china_border_lines(china_border_path)

    global_payload = {
        "source": "MeteoInfo country.shp",
        "lines": sorted_coordinates(global_lines, 0.045),
    }
    china_payload = {
        "source": "MeteoInfo cn_border.shp",
        "bbox": china_bbox,
        "landLines": sorted_coordinates(land, 0.008),
        "coastLines": sorted_coordinates(coast, 0.01),
        "coastMajorLines": sorted_coordinates(
            (line for line in coast if line.length >= 0.15),
            0.01,
        ),
        "islandLines": sorted_coordinates(islands, 0.003),
        "islandMajorLines": sorted_coordinates(
            (line for line in islands if line.length >= 0.15),
            0.003,
        ),
    }
    write_json(arguments.target / "countries-meteoinfo.json", global_payload)
    write_json(arguments.target / "china-border-meteoinfo.json", china_payload)

    print(
        json.dumps(
            {
                "globalLines": len(global_payload["lines"]),
                "chinaLandLines": len(china_payload["landLines"]),
                "chinaCoastLines": len(china_payload["coastLines"]),
                "chinaIslandLines": len(china_payload["islandLines"]),
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    main()
