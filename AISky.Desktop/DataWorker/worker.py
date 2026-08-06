"""AISky isolated NetCDF worker.

The WinUI process communicates with this module through newline-delimited JSON
written to stdout. Heavy NetCDF I/O, validation, conversion, hashing and
network downloads stay outside the UI process.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import shutil
import sqlite3
import sys
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from contextlib import closing
from itertools import chain
from pathlib import Path
from typing import Any, Iterable
from urllib.parse import urljoin

import numpy as np
import requests
from netCDF4 import Dataset

# The desktop process consumes NDJSON as UTF-8. Windows otherwise inherits the
# active console code page (often GBK), which can corrupt Chinese status text.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="strict")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")


UTC_FORMAT = "%Y%m%d_%H%M"
ISO_FORMAT = "%Y-%m-%dT%H:%M:%SZ"
CACHE_SCHEMA_VERSION = 2
FIXED_MINUTES = ((1, 30), (4, 30), (7, 30), (10, 30), (13, 30), (16, 30), (19, 30), (22, 30))
FILE_RE = re.compile(
    r"^(?P<model>AISky-(?:Energy|SDS))_"
    r"(?P<init>\d{8}_\d{4})\+(?P<forecast>\d{8}_\d{4})_"
    r"(?P<version>V\d+)\.nc$",
    re.IGNORECASE,
)

BASE_URLS = {
    # AISky-Energy exposes public direct file URLs.  Using the /s/share/
    # password form makes a missing forecast look like an authentication
    # failure, so keep the public route even when the caller supplied a
    # password.
    "AISky-Energy": "https://obs.cstcloud.cn/s/aisky-energy/aisky-energy/forecast_data/",
    "AISky-SDS": "https://obs.cstcloud.cn/s/aisky-sds/aisky-sds/forecast_data/",
}

PALETTES = {
    "temperature": ["#312E81", "#2563EB", "#38BDF8", "#F8FAFC", "#FDE047", "#FB923C", "#DC2626", "#7F1D1D"],
    "solar": ["#155E75", "#0891B2", "#22C55E", "#D9F99D", "#FDE047", "#F59E0B", "#EF4444", "#7F1D1D"],
    "cloud": ["#F8FAFC", "#E0F2FE", "#CBD5E1", "#94A3B8", "#64748B", "#334155"],
    "wind": ["#DDF6FF", "#67E8F9", "#22C55E", "#D9F99D", "#FDE047", "#F97316", "#DC2626", "#581C87"],
    "pressure": ["#4C1D95", "#2563EB", "#38BDF8", "#F8FAFC", "#FDE68A", "#FB923C", "#B91C1C"],
    "dust": ["#F8FAFC", "#FDE68A", "#F59E0B", "#EF4444", "#7F1D1D", "#581C87"],
    "rain": ["#F8FAFC", "#BAE6FD", "#22D3EE", "#14B8A6", "#16A34A", "#166534"],
    "height": ["#EEF2FF", "#A5B4FC", "#38BDF8", "#14B8A6", "#FDE047", "#F97316"],
    "moisture": ["#F8FAFC", "#CFFAFE", "#67E8F9", "#14B8A6", "#0F766E", "#134E4A"],
    "land": ["#F8FAFC", "#DCFCE7", "#86EFAC", "#22C55E", "#15803D", "#365314"],
    "aerosol": ["#F8FAFC", "#DBEAFE", "#60A5FA", "#A78BFA", "#F472B6", "#9D174D"],
}


@dataclass(frozen=True)
class LayerSpec:
    id: str
    label: str
    name_cn: str
    unit: str
    aliases: tuple[str, ...]
    value_range: tuple[float, float]
    palette: tuple[str, ...]
    scale: float = 1.0
    offset: float = 0.0
    vector_aliases: tuple[tuple[str, ...], tuple[str, ...]] | None = None


class AuthenticationError(PermissionError):
    """The remote share explicitly rejected its access credential."""


COMMON_LAYERS = (
    LayerSpec("t2m", "T2M", "近地面气温", "°C", ("T2M", "TLML"), (-50.0, 45.0), tuple(PALETTES["temperature"]), offset=-273.15),
    LayerSpec("t10m", "T10M", "10 米气温", "°C", ("T10M",), (-50.0, 45.0), tuple(PALETTES["temperature"]), offset=-273.15),
    LayerSpec("ts", "TS", "地表皮肤温度", "°C", ("TS",), (-60.0, 60.0), tuple(PALETTES["temperature"]), offset=-273.15),
    LayerSpec("qv2m", "QV2M", "近地面比湿", "g/kg", ("QV2M", "QLML"), (0.0, 24.0), tuple(PALETTES["moisture"]), scale=1000.0),
    LayerSpec("qv10m", "QV10M", "10 米比湿", "g/kg", ("QV10M",), (0.0, 24.0), tuple(PALETTES["moisture"]), scale=1000.0),
    LayerSpec("swgdn", "SWGDN", "地表短波辐射", "W/m²", ("SWGDN",), (0.0, 1200.0), tuple(PALETTES["solar"])),
    LayerSpec("swgdnclr", "SWGDNCLR", "晴空地表短波", "W/m²", ("SWGDNCLR",), (0.0, 1200.0), tuple(PALETTES["solar"])),
    LayerSpec("swtdn", "SWTDN", "大气顶入射短波", "W/m²", ("SWTDN",), (0.0, 1400.0), tuple(PALETTES["solar"])),
    LayerSpec("swgnt", "SWGNT", "地表净短波", "W/m²", ("SWGNT",), (0.0, 1000.0), tuple(PALETTES["solar"])),
    LayerSpec("cldtot", "CLDTOT", "总云量", "%", ("CLDTOT",), (0.0, 100.0), tuple(PALETTES["cloud"]), scale=100.0),
    LayerSpec("cldlow", "CLDLOW", "低云量", "%", ("CLDLOW",), (0.0, 100.0), tuple(PALETTES["cloud"]), scale=100.0),
    LayerSpec("cldmid", "CLDMID", "中云量", "%", ("CLDMID",), (0.0, 100.0), tuple(PALETTES["cloud"]), scale=100.0),
    LayerSpec("cldhgh", "CLDHGH", "高云量", "%", ("CLDHGH",), (0.0, 100.0), tuple(PALETTES["cloud"]), scale=100.0),
    LayerSpec("tautot", "TAUTOT", "云光学厚度", "", ("TAUTOT",), (0.0, 80.0), tuple(PALETTES["cloud"])),
    LayerSpec("albedo", "ALBEDO", "地表反照率", "%", ("ALBEDO",), (0.0, 100.0), tuple(PALETTES["land"]), scale=100.0),
    LayerSpec("totexttau", "TOTEXTTAU", "总气溶胶光学厚度", "", ("TOTEXTTAU",), (0.0, 1.0), tuple(PALETTES["aerosol"])),
    LayerSpec(
        "wind10",
        "WIND10",
        "10 米风速",
        "m/s",
        ("WIND10",),
        (0.0, 22.0),
        tuple(PALETTES["wind"]),
        vector_aliases=(("U10M", "U10", "ULML"), ("V10M", "V10", "VLML")),
    ),
    LayerSpec(
        "wind50",
        "WIND50",
        "50 米风速",
        "m/s",
        ("WIND50",),
        (0.0, 30.0),
        tuple(PALETTES["wind"]),
        vector_aliases=(("U50M",), ("V50M",)),
    ),
    LayerSpec("slp", "SLP", "海平面气压", "hPa", ("SLP", "MSLP"), (940.0, 1050.0), tuple(PALETTES["pressure"]), scale=0.01),
    LayerSpec("ps", "PS", "地表气压", "hPa", ("PS",), (500.0, 1050.0), tuple(PALETTES["pressure"]), scale=0.01),
    LayerSpec("tqv", "TQV", "整层可降水量", "kg/m²", ("TQV",), (0.0, 75.0), tuple(PALETTES["moisture"])),
    LayerSpec("duexttau", "DUEXTTAU", "沙尘光学厚度", "", ("DUEXTTAU", "DUEXTTAU550"), (0.0, 0.85), tuple(PALETTES["dust"])),
    LayerSpec("duextt25", "DUEXTT25", "PM2.5 沙尘消光 AOT", "", ("DUEXTT25",), (0.0, 0.45), tuple(PALETTES["dust"])),
    LayerSpec("duscatau", "DUSCATAU", "沙尘散射 AOT", "", ("DUSCATAU",), (0.0, 0.8), tuple(PALETTES["dust"])),
    LayerSpec("duscat25", "DUSCAT25", "PM2.5 沙尘散射 AOT", "", ("DUSCAT25",), (0.0, 0.42), tuple(PALETTES["dust"])),
    LayerSpec("ducmass", "DUCMASS", "沙尘柱质量", "mg/m²", ("DUCMASS",), (0.0, 800.0), tuple(PALETTES["dust"]), scale=1_000_000.0),
    LayerSpec("ducmass25", "DUCMASS25", "PM2.5 沙尘柱质量", "mg/m²", ("DUCMASS25",), (0.0, 200.0), tuple(PALETTES["dust"]), scale=1_000_000.0),
    LayerSpec("dusmass", "DUSMASS", "地表沙尘浓度", "μg/m³", ("DUSMASS",), (0.0, 350.0), tuple(PALETTES["dust"]), scale=1_000_000_000.0),
    LayerSpec("dusmass25", "DUSMASS25", "地表 PM2.5 沙尘浓度", "μg/m³", ("DUSMASS25",), (0.0, 90.0), tuple(PALETTES["dust"]), scale=1_000_000_000.0),
    LayerSpec(
        "duflux",
        "DUFLUX",
        "沙尘柱质量通量",
        "kg/(m·s)",
        ("DUFLUX",),
        (0.0, 0.004),
        tuple(PALETTES["dust"]),
        vector_aliases=(("DUFLUXU",), ("DUFLUXV",)),
    ),
    LayerSpec("prectot", "PRECTOT", "总降水", "mm/day", ("PRECTOT",), (0.0, 50.0), tuple(PALETTES["rain"]), scale=86400.0),
    LayerSpec("pblh", "PBLH", "边界层高度", "m", ("PBLH",), (0.0, 3000.0), tuple(PALETTES["height"])),
    LayerSpec("ustar", "USTAR", "地表摩擦速度", "m/s", ("USTAR",), (0.0, 1.2), tuple(PALETTES["wind"])),
    LayerSpec("z0m", "Z0M", "地表粗糙度", "m", ("Z0M",), (0.0, 5.0), tuple(PALETTES["land"])),
    LayerSpec("gwettop", "GWETTOP", "表层土壤湿度", "%", ("GWETTOP",), (0.0, 100.0), tuple(PALETTES["moisture"]), scale=100.0),
    LayerSpec("frsno", "FRSNO", "积雪覆盖率", "%", ("FRSNO",), (0.0, 100.0), tuple(PALETTES["cloud"]), scale=100.0),
    LayerSpec("lai", "LAI", "叶面积指数", "", ("LAI",), (0.0, 7.0), tuple(PALETTES["land"])),
)


def emit(event_type: str, **payload: Any) -> None:
    print(json.dumps({"type": event_type, **payload}, ensure_ascii=False, separators=(",", ":")), flush=True)


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime(ISO_FORMAT)


def parse_key(value: str) -> datetime:
    return datetime.strptime(value, UTC_FORMAT).replace(tzinfo=timezone.utc)


def nice_time(value: str) -> str:
    return parse_key(value).strftime("%Y-%m-%d %H:%M UTC")


def parse_filename(path: Path) -> dict[str, Any]:
    match = FILE_RE.match(path.name)
    if not match:
        raise ValueError(
            "文件名无法识别。应类似 AISky-SDS_20260605_1930+20260607_0130_V01.nc"
        )
    result: dict[str, Any] = match.groupdict()
    result["model"] = "AISky-Energy" if result["model"].lower().endswith("energy") else "AISky-SDS"
    init_time = parse_key(result["init"])
    forecast_time = parse_key(result["forecast"])
    result["leadHours"] = int((forecast_time - init_time).total_seconds() // 3600)
    result["runId"] = (
        f"{result['model']}_{result['init']}__{result['forecast']}_{result['version']}"
    )
    return result


def initialize_database(database_path: Path, schema_path: Path) -> Path | None:
    database_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with closing(sqlite3.connect(database_path, timeout=30)) as connection:
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("PRAGMA foreign_keys=ON")
            connection.executescript(schema_path.read_text(encoding="utf-8"))
            migrate_database(connection)
        return None
    except sqlite3.DatabaseError:
        timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        backup = database_path.with_name(
            f"{database_path.name}.corrupt-{timestamp}"
        )
        if database_path.exists():
            database_path.replace(backup)
        for suffix in ("-wal", "-shm"):
            Path(f"{database_path}{suffix}").unlink(missing_ok=True)
        with closing(sqlite3.connect(database_path, timeout=30)) as connection:
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("PRAGMA foreign_keys=ON")
            connection.executescript(schema_path.read_text(encoding="utf-8"))
            migrate_database(connection)
        return backup


def migrate_database(connection: sqlite3.Connection) -> None:
    run_columns = {
        "source_file": "TEXT",
        "version": "TEXT",
        "file_size": "INTEGER NOT NULL DEFAULT 0",
        "checksum": "TEXT",
        "validation_state": "TEXT NOT NULL DEFAULT 'pending'",
        "parse_state": "TEXT NOT NULL DEFAULT 'pending'",
        "error_message": "TEXT",
        "manifest_path": "TEXT",
        "downloaded_at_utc": "TEXT",
        "last_accessed_utc": "TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP",
        "is_expired": "INTEGER NOT NULL DEFAULT 0",
        "is_plot_ready": "INTEGER NOT NULL DEFAULT 0",
    }
    job_columns = {
        "run_id": "TEXT",
        "init_time_utc": "TEXT",
        "forecast_time_utc": "TEXT",
        "version": "TEXT",
        "error_message": "TEXT",
        "created_at_utc": "TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP",
    }
    add_missing_columns(connection, "forecast_runs", run_columns)
    add_missing_columns(connection, "download_jobs", job_columns)


def add_missing_columns(connection: sqlite3.Connection, table: str, columns: dict[str, str]) -> None:
    existing = {row[1] for row in connection.execute(f"PRAGMA table_info({table})")}
    for name, declaration in columns.items():
        if name not in existing:
            connection.execute(f"ALTER TABLE {table} ADD COLUMN {name} {declaration}")


def file_checksum(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_netcdf(path: Path) -> None:
    if not path.exists():
        raise FileNotFoundError(f"文件不存在：{path}")
    if path.stat().st_size < 4096:
        raise ValueError("文件过小，不像有效的 NetCDF 数据。")
    header = path.read_bytes()[:8]
    if not (header.startswith(b"CDF") or header == b"\x89HDF\r\n\x1a\n"):
        raise ValueError("文件头不是 NetCDF/HDF，可能下载到了错误页面。")
    try:
        with Dataset(path) as dataset:
            variables = {name.lower() for name in dataset.variables}
            if "lat" not in variables or "lon" not in variables:
                raise ValueError("NetCDF 中缺少 lat/lon 坐标。")
    except Exception as error:
        raise ValueError(f"NetCDF 无法读取：{error}") from error


def find_variable(dataset: Dataset, aliases: Iterable[str]) -> str | None:
    lookup = {name.upper(): name for name in dataset.variables}
    for alias in aliases:
        if alias.upper() in lookup:
            return lookup[alias.upper()]
    return None


def read_array(dataset: Dataset, name: str) -> np.ndarray:
    raw = np.ma.filled(dataset[name][:], np.nan)
    data = np.asarray(raw, dtype=np.float32).squeeze()
    while data.ndim > 2:
        data = data[0]
    if data.ndim != 2:
        raise ValueError(f"变量 {name} 不是二维栅格。")
    return np.where(np.isfinite(data), data, np.nan).astype(np.float32)


def read_layer(dataset: Dataset, spec: LayerSpec) -> np.ndarray | None:
    if spec.vector_aliases is not None:
        u_name = find_variable(dataset, spec.vector_aliases[0])
        v_name = find_variable(dataset, spec.vector_aliases[1])
        if u_name and v_name:
            u = read_array(dataset, u_name)
            v = read_array(dataset, v_name)
            return np.sqrt(u * u + v * v).astype(np.float32)
    variable = find_variable(dataset, spec.aliases)
    if variable is None:
        return None
    data = read_array(dataset, variable)
    return (data * spec.scale + spec.offset).astype(np.float32)


def stats_for(data: np.ndarray) -> dict[str, float]:
    valid = data[np.isfinite(data)]
    if valid.size == 0:
        raise ValueError("变量没有可用数值。")
    return {
        "min": round(float(np.nanmin(valid)), 2),
        "mean": round(float(np.nanmean(valid)), 2),
        "max": round(float(np.nanmax(valid)), 2),
        "p02": round(float(np.nanpercentile(valid, 2)), 2),
        "p50": round(float(np.nanpercentile(valid, 50)), 2),
        "p98": round(float(np.nanpercentile(valid, 98)), 2),
    }


def atomic_bytes(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".part")
    temporary.write_bytes(content)
    temporary.replace(path)


def atomic_json(path: Path, payload: Any, *, compact: bool = False) -> None:
    text = json.dumps(
        payload,
        ensure_ascii=False,
        separators=(",", ":") if compact else None,
        indent=None if compact else 2,
    )
    atomic_bytes(path, text.encode("utf-8"))


def write_field(path: Path, data: np.ndarray, value_range: tuple[float, float]) -> dict[str, Any]:
    low, high = value_range
    span = max(1e-6, high - low)
    encoded = np.full(data.shape, np.uint16(65535), dtype=np.uint16)
    valid = np.isfinite(data)
    encoded[valid] = np.round(
        (np.clip(data[valid], low, high) - low) / span * 65534.0
    ).astype(np.uint16)
    atomic_bytes(path, encoded.astype("<u2", copy=False).tobytes(order="C"))
    return {
        "encoding": "uint16-le",
        "missing": 65535,
        "rows": int(data.shape[0]),
        "cols": int(data.shape[1]),
        "range": [low, high],
    }


def downsample_grid(data: np.ndarray, rows: int = 121, cols: int = 192) -> list[list[float | None]]:
    row_count = min(rows, data.shape[0])
    col_count = min(cols, data.shape[1])
    y_indices = np.linspace(0, data.shape[0] - 1, row_count).round().astype(int)
    x_indices = np.linspace(0, data.shape[1] - 1, col_count).round().astype(int)
    sampled = np.round(data[np.ix_(y_indices, x_indices)].astype(np.float64), 2)
    return [
        [None if not math.isfinite(value) else float(value) for value in row]
        for row in sampled
    ]


def process_netcdf(
    source: Path,
    render_root: Path,
    database_path: Path,
) -> dict[str, Any]:
    file_info = parse_filename(source)
    ensure_netcdf(source)
    checksum = file_checksum(source)
    run_id = file_info["runId"]
    run_directory = render_root / "runs" / run_id
    run_directory.mkdir(parents=True, exist_ok=True)

    emit("progress", operation="parse", stage="open", percent=4, message=f"正在读取 {source.name}")
    layers: list[dict[str, Any]] = []
    with Dataset(source) as dataset:
        lat_name = find_variable(dataset, ("lat", "latitude"))
        lon_name = find_variable(dataset, ("lon", "longitude"))
        if lat_name is None or lon_name is None:
            raise ValueError("NetCDF 中缺少纬度或经度坐标。")
        lat = np.asarray(dataset[lat_name][:], dtype=np.float64).squeeze()
        lon = np.asarray(dataset[lon_name][:], dtype=np.float64).squeeze()

        for index, spec in enumerate(COMMON_LAYERS):
            vector_components: tuple[np.ndarray, np.ndarray] | None = None
            if spec.vector_aliases is not None:
                u_name = find_variable(dataset, spec.vector_aliases[0])
                v_name = find_variable(dataset, spec.vector_aliases[1])
                if u_name and v_name:
                    u_data = read_array(dataset, u_name)
                    v_data = read_array(dataset, v_name)
                    data = np.sqrt(u_data * u_data + v_data * v_data).astype(np.float32)
                    vector_components = (u_data, v_data)
                else:
                    data = read_layer(dataset, spec)
            else:
                data = read_layer(dataset, spec)
            if data is None:
                continue
            percent = 8 + int((index + 1) / len(COMMON_LAYERS) * 76)
            emit(
                "progress",
                operation="parse",
                stage="layer",
                percent=percent,
                message=f"正在转换 {spec.label} · {spec.name_cn}",
            )
            field_path = run_directory / f"{spec.id}.field.u16"
            sample_path = run_directory / f"{spec.id}.sample.json"
            field_info = write_field(field_path, data, spec.value_range)
            atomic_json(sample_path, downsample_grid(data), compact=True)
            layer_payload: dict[str, Any] = {
                "id": spec.id,
                "label": spec.label,
                "cn": spec.name_cn,
                "unit": spec.unit,
                "range": list(spec.value_range),
                "palette": list(spec.palette),
                "field": field_path.relative_to(render_root).as_posix(),
                "fieldInfo": field_info,
                "sample": sample_path.relative_to(render_root).as_posix(),
                "stats": stats_for(data),
            }
            if vector_components is not None:
                vector_range = (-50.0, 50.0)
                u_path = run_directory / f"{spec.id}.u.field.u16"
                v_path = run_directory / f"{spec.id}.v.field.u16"
                vector_info = write_field(u_path, vector_components[0], vector_range)
                write_field(v_path, vector_components[1], vector_range)
                layer_payload["vector"] = {
                    "u": u_path.relative_to(render_root).as_posix(),
                    "v": v_path.relative_to(render_root).as_posix(),
                    "fieldInfo": vector_info,
                }
            layers.append(layer_payload)

    if not layers:
        raise ValueError("NetCDF 中没有找到当前版本支持的气象变量。")

    manifest = {
        "cacheSchemaVersion": CACHE_SCHEMA_VERSION,
        "id": run_id,
        "model": file_info["model"],
        "version": file_info["version"],
        "sourceFile": source.name,
        "sourcePath": str(source.resolve()),
        "initKey": file_info["init"],
        "forecastKey": file_info["forecast"],
        "initTime": nice_time(file_info["init"]),
        "forecastTime": nice_time(file_info["forecast"]),
        "leadHours": file_info["leadHours"],
        "grid": {
            "lat": [round(float(lat[0]), 6), round(float(lat[-1]), 6)],
            "lon": [round(float(lon[0]), 6), round(float(lon[-1]), 6)],
            "rows": int(len(lat)),
            "cols": int(len(lon)),
        },
        "layers": layers,
    }
    manifest_path = run_directory / "run.json"
    atomic_json(manifest_path, manifest)
    update_run_index(database_path, source, manifest_path, manifest, checksum)
    emit("progress", operation="parse", stage="index", percent=96, message="正在刷新本地索引")
    return manifest


def render_cache_ready(
    source: Path,
    render_root: Path,
    database_path: Path,
) -> bool:
    """Return true when a valid indexed run already has scalar and wind caches."""
    info = parse_filename(source)
    run_directory = render_root / "runs" / info["runId"]
    required = (
        run_directory / "run.json",
        run_directory / "wind10.field.u16",
        run_directory / "wind10.u.field.u16",
        run_directory / "wind10.v.field.u16",
    )
    if not all(path.is_file() and path.stat().st_size > 0 for path in required):
        return False
    try:
        manifest = json.loads((run_directory / "run.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False
    if manifest.get("cacheSchemaVersion") != CACHE_SCHEMA_VERSION:
        return False
    with sqlite3.connect(database_path, timeout=30) as connection:
        row = connection.execute(
            "SELECT is_plot_ready FROM forecast_runs WHERE id=?",
            (info["runId"],),
        ).fetchone()
    return row is not None and int(row[0]) == 1


def update_run_index(
    database_path: Path,
    source: Path,
    manifest_path: Path,
    manifest: dict[str, Any],
    checksum: str,
) -> None:
    now = utc_now()
    with sqlite3.connect(database_path, timeout=30) as connection:
        connection.execute("PRAGMA foreign_keys=ON")
        connection.execute(
            """
            INSERT INTO forecast_runs (
                id, model, init_time_utc, forecast_time_utc, lead_hours,
                source_path, source_file, version, file_size, checksum,
                state, validation_state, parse_state, error_message,
                manifest_path, downloaded_at_utc, last_accessed_utc,
                is_expired, is_plot_ready
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'ready', 'valid', 'ready',
                      NULL, ?, ?, ?, 0, 1)
            ON CONFLICT(id) DO UPDATE SET
                source_path=excluded.source_path,
                source_file=excluded.source_file,
                version=excluded.version,
                file_size=excluded.file_size,
                checksum=excluded.checksum,
                state='ready',
                validation_state='valid',
                parse_state='ready',
                error_message=NULL,
                manifest_path=excluded.manifest_path,
                downloaded_at_utc=COALESCE(forecast_runs.downloaded_at_utc, excluded.downloaded_at_utc),
                last_accessed_utc=excluded.last_accessed_utc,
                is_expired=0,
                is_plot_ready=1
            """,
            (
                manifest["id"],
                manifest["model"],
                manifest["initKey"],
                manifest["forecastKey"],
                manifest["leadHours"],
                str(source.resolve()),
                source.name,
                manifest["version"],
                source.stat().st_size,
                checksum,
                str(manifest_path.resolve()),
                now,
                now,
            ),
        )
        connection.execute("DELETE FROM cache_entries WHERE run_id=?", (manifest["id"],))
        for layer in manifest["layers"]:
            field_path = manifest_path.parent / Path(layer["field"]).name
            connection.execute(
                """
                INSERT INTO cache_entries
                    (cache_key, run_id, layer_id, relative_path, byte_length, checksum, last_accessed_utc)
                VALUES (?, ?, ?, ?, ?, NULL, ?)
                """,
                (
                    f"{manifest['id']}:{layer['id']}",
                    manifest["id"],
                    layer["id"],
                    layer["field"],
                    field_path.stat().st_size,
                    now,
                ),
            )


def mark_run_error(database_path: Path, source: Path, error: Exception) -> None:
    try:
        info = parse_filename(source)
    except ValueError:
        return
    with sqlite3.connect(database_path, timeout=30) as connection:
        connection.execute(
            """
            INSERT INTO forecast_runs (
                id, model, init_time_utc, forecast_time_utc, lead_hours,
                source_path, source_file, version, file_size, state,
                validation_state, parse_state, error_message, is_plot_ready
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'error', 'invalid', 'error', ?, 0)
            ON CONFLICT(id) DO UPDATE SET
                state='error', validation_state='invalid', parse_state='error',
                error_message=excluded.error_message, is_plot_ready=0
            """,
            (
                info["runId"],
                info["model"],
                info["init"],
                info["forecast"],
                info["leadHours"],
                str(source.resolve()),
                source.name,
                info["version"],
                source.stat().st_size if source.exists() else 0,
                str(error),
            ),
        )


def index_payload(database_path: Path) -> dict[str, Any]:
    runs: list[dict[str, Any]] = []
    with sqlite3.connect(database_path, timeout=30) as connection:
        connection.row_factory = sqlite3.Row
        rows = connection.execute(
            """
            SELECT id, model, init_time_utc, forecast_time_utc, lead_hours,
                   source_file, version, file_size, manifest_path
            FROM forecast_runs
            WHERE is_plot_ready=1 AND state='ready'
            ORDER BY model, init_time_utc DESC, lead_hours ASC
            """
        ).fetchall()
    for row in rows:
        manifest_path = Path(row["manifest_path"] or "")
        if not manifest_path.exists():
            continue
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        runs.append(
            {
                "id": row["id"],
                "model": row["model"],
                "initKey": row["init_time_utc"],
                "forecastKey": row["forecast_time_utc"],
                "leadHours": row["lead_hours"],
                "version": row["version"],
                "sourceFile": row["source_file"],
                "fileSize": row["file_size"],
                "grid": manifest.get("grid", {}),
                "layers": manifest.get("layers", []),
            }
        )
    return {
        "operation": "index",
        "generatedAtUtc": utc_now(),
        "models": sorted({run["model"] for run in runs}),
        "runs": runs,
    }


def fixed_init_times(start: datetime, end: datetime) -> list[datetime]:
    current_day = start.date()
    result: list[datetime] = []
    while current_day <= end.date():
        for hour, minute in FIXED_MINUTES:
            candidate = datetime(
                current_day.year,
                current_day.month,
                current_day.day,
                hour,
                minute,
                tzinfo=timezone.utc,
            )
            if start <= candidate <= end:
                result.append(candidate)
        current_day += timedelta(days=1)
    return result


def latest_fixed_times(now: datetime, probe_days: int) -> list[datetime]:
    if probe_days < 1 or probe_days > 14:
        raise ValueError("最新数据探测天数必须在 1 到 14 天之间。")
    return list(reversed(fixed_init_times(now - timedelta(days=probe_days), now)))


def share_url(
    model: str,
    init_key: str,
    forecast_key: str,
    version: str,
    base_url: str | None = None,
) -> str:
    filename = f"{model}_{init_key}+{forecast_key}_{version}.nc"
    return urljoin(base_url or BASE_URLS[model], f"{init_key}/{filename}")


def csrf_token(html: str) -> str | None:
    match = re.search(r'name="csrfmiddlewaretoken" value="([^"]+)"', html)
    return match.group(1) if match else None


def response_is_netcdf(first_chunk: bytes) -> bool:
    return first_chunk.startswith(b"CDF") or first_chunk[:8] == b"\x89HDF\r\n\x1a\n"


def write_download_response(
    response: requests.Response,
    iterator: Iterable[bytes],
    first_chunk: bytes,
    output: Path,
    job_id: str,
) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + f".{job_id}.part")
    received = 0
    total = int(response.headers.get("content-length") or 0)
    last_update = 0.0
    with temporary.open("wb") as stream:
        for chunk in chain((first_chunk,), iterator):
            if not chunk:
                continue
            stream.write(chunk)
            received += len(chunk)
            now = time.monotonic()
            if now - last_update >= 0.25:
                emit(
                    "progress",
                    operation="download",
                    stage="transfer",
                    jobId=job_id,
                    bytesReceived=received,
                    totalBytes=total or None,
                    percent=round(received / total * 100, 1) if total else None,
                    message=f"已下载 {received / 1024 / 1024:.1f} MB",
                )
                last_update = now
    temporary.replace(output)


def download_share_file(
    session: requests.Session,
    url: str,
    password: str,
    output: Path,
    job_id: str,
) -> bool:
    # Current CSTCloud links may return the NetCDF file directly.  Older or
    # protected shares first return an HTML password form.  Stream the first
    # response so an 80+ MB file is never decoded as HTML or buffered in RAM.
    page = session.get(url, stream=True, timeout=(15, 180))
    if page.status_code in (404, 410):
        page.close()
        return False
    if page.status_code == 403:
        page.close()
        raise AuthenticationError("访问密码验证失败，请重新填写数据访问密码。")
    page.raise_for_status()
    page_iterator = page.iter_content(chunk_size=1024 * 1024)
    first_chunk = next(page_iterator, b"")
    if response_is_netcdf(first_chunk):
        write_download_response(page, page_iterator, first_chunk, output, job_id)
        page.close()
        return True

    page_content = b"".join(chain((first_chunk,), page_iterator))
    page.close()
    token = csrf_token(page_content.decode("utf-8", errors="replace"))
    if token is None:
        return False

    response = session.post(
        url,
        data={"csrfmiddlewaretoken": token, "password": password},
        stream=True,
        timeout=(15, 180),
    )
    if response.status_code == 403:
        response.close()
        raise AuthenticationError("访问密码验证失败，请重新填写数据访问密码。")
    if response.status_code in (404, 410):
        return False
    response.raise_for_status()
    iterator = response.iter_content(chunk_size=1024 * 1024)
    first_chunk = next(iterator, b"")
    if not response_is_netcdf(first_chunk):
        response.close()
        raise AuthenticationError(
            "访问密码验证失败，或服务器返回了非 NetCDF 内容。"
        )

    write_download_response(response, iterator, first_chunk, output, job_id)
    response.close()
    return True


def upsert_download_job(
    database_path: Path,
    job_id: str,
    model: str,
    init_key: str,
    forecast_key: str,
    version: str,
    url: str,
    target: Path,
    state: str,
    error: str | None = None,
) -> None:
    with sqlite3.connect(database_path, timeout=30) as connection:
        connection.execute(
            """
            INSERT INTO download_jobs (
                id, model, init_time_utc, forecast_time_utc, version,
                remote_uri, local_path, state, attempts, error_message,
                created_at_utc, updated_at_utc
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 1, ?, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(id) DO UPDATE SET
                state=excluded.state,
                attempts=CASE
                    WHEN excluded.state='running'
                    THEN download_jobs.attempts + 1
                    ELSE download_jobs.attempts
                END,
                error_message=excluded.error_message,
                updated_at_utc=CURRENT_TIMESTAMP
            """,
            (
                job_id,
                model,
                init_key,
                forecast_key,
                version,
                url,
                str(target),
                state,
                error,
            ),
        )


def run_download_range(args: argparse.Namespace) -> dict[str, Any]:
    start = parse_key(args.start)
    end = parse_key(args.end)
    if start > end:
        raise ValueError("开始时间不能晚于截止时间。")
    init_times = fixed_init_times(start, end)
    if not init_times:
        raise ValueError("所选范围内没有 01:30、04:30 等固定起报时刻。")

    downloaded = 0
    skipped = 0
    failed = 0
    requested = 0
    with requests.Session() as session:
        session.headers["User-Agent"] = "AISky-Desktop/0.4"
        for init_index, init_time in enumerate(init_times):
            init_key = init_time.strftime(UTC_FORMAT)
            locked_version: str | None = None
            consecutive_missing = 0
            emit(
                "progress",
                operation="download",
                stage="run",
                percent=round(init_index / len(init_times) * 100, 1),
                message=f"正在检查 {args.model} · {nice_time(init_key)}",
            )
            # AISky forecast products begin at +3h.  A complete 15-day run is
            # therefore exactly 120 files for the full range: +3h, +6h, ... +360h.
            minimum_lead = max(3, args.min_lead_hours)
            minimum_lead += (-minimum_lead) % 3
            leads = list(range(minimum_lead, args.max_lead_hours + 1, 3))
            report_file_progress = getattr(args, "report_file_progress", True)
            for lead_index, lead in enumerate(leads, start=1):
                if report_file_progress:
                    emit(
                        "progress",
                        operation="download",
                        stage="file",
                        currentItem=lead_index,
                        totalItems=len(leads),
                        percent=round((lead_index - 1) / max(1, len(leads)) * 100, 1),
                        message=f"正在准备第 {lead_index}/{len(leads)} 个预报时次",
                    )
                forecast_time = init_time + timedelta(hours=lead)
                forecast_key = forecast_time.strftime(UTC_FORMAT)
                existing_target = (
                    Path(args.data_root)
                    / args.model
                    / init_key
                )
                existing_files = sorted(
                    existing_target.glob(
                        f"{args.model}_{init_key}+{forecast_key}_V*.nc"
                    ),
                    reverse=True,
                )
                existing_version = (
                    parse_filename(existing_files[0])["version"]
                    if existing_files
                    else None
                )
                versions = (
                    (locked_version,)
                    if locked_version
                    else (existing_version,)
                    if existing_version
                    else tuple(
                        f"V{number:02d}"
                        for number in range(args.max_version, 0, -1)
                    )
                )
                found = False
                for version in versions:
                    filename = f"{args.model}_{init_key}+{forecast_key}_{version}.nc"
                    target = existing_target / filename
                    if target.exists():
                        try:
                            ensure_netcdf(target)
                            if not render_cache_ready(
                                target,
                                Path(args.render_root),
                                Path(args.database),
                            ):
                                process_netcdf(
                                    target,
                                    Path(args.render_root),
                                    Path(args.database),
                                )
                            skipped += 1
                            found = True
                            locked_version = version
                            break
                        except Exception:
                            pass

                    url = share_url(
                        args.model,
                        init_key,
                        forecast_key,
                        version,
                        args.base_url,
                    )
                    job_id = hashlib.sha1(url.encode("utf-8")).hexdigest()
                    requested += 1
                    upsert_download_job(
                        Path(args.database), job_id, args.model, init_key,
                        forecast_key, version, url, target, "running"
                    )
                    try:
                        downloaded_file = False
                        for attempt in range(1, 4):
                            try:
                                downloaded_file = download_share_file(
                                    session, url, args.password, target, job_id
                                )
                                break
                            except (requests.ConnectionError, requests.Timeout) as error:
                                if attempt >= 3:
                                    raise
                                upsert_download_job(
                                    Path(args.database), job_id, args.model, init_key,
                                    forecast_key, version, url, target, "running", str(error)
                                )
                                emit(
                                    "warning",
                                    operation="download",
                                    message=(
                                        f"{filename} 网络中断，正在进行第 {attempt + 1}/3 次重试"
                                    ),
                                )
                                time.sleep(attempt)

                        if downloaded_file:
                            ensure_netcdf(target)
                            process_netcdf(target, Path(args.render_root), Path(args.database))
                            upsert_download_job(
                                Path(args.database), job_id, args.model, init_key,
                                forecast_key, version, url, target, "completed"
                            )
                            downloaded += 1
                            found = True
                            locked_version = version
                            break
                        upsert_download_job(
                            Path(args.database), job_id, args.model, init_key,
                            forecast_key, version, url, target, "not_found",
                            "远端未找到该版本文件"
                        )
                    except AuthenticationError as error:
                        failed += 1
                        upsert_download_job(
                            Path(args.database), job_id, args.model, init_key,
                            forecast_key, version, url, target, "failed", str(error)
                        )
                        raise RuntimeError(
                            "访问密码验证失败，请在设置中重新填写后重试。"
                        ) from error
                    except Exception as error:
                        if target.exists():
                            try:
                                ensure_netcdf(target)
                            except Exception:
                                target.unlink(missing_ok=True)
                        failed += 1
                        upsert_download_job(
                            Path(args.database), job_id, args.model, init_key,
                            forecast_key, version, url, target, "failed", str(error)
                        )
                        emit(
                            "warning",
                            operation="download",
                            message=f"{filename} 下载失败：{error}",
                        )
                        if isinstance(error, requests.RequestException):
                            raise RuntimeError(
                                "数据服务器连接超时或网络不可用；本次尚未完成密码验证，请检查网络后重试。"
                            ) from error
                consecutive_missing = 0 if found else consecutive_missing + 1
                if locked_version and consecutive_missing >= 4:
                    break
                if not locked_version and consecutive_missing >= 4:
                    break
                if report_file_progress:
                    emit(
                        "progress",
                        operation="download",
                        stage="file-complete",
                        currentItem=lead_index,
                        totalItems=len(leads),
                        percent=round(lead_index / max(1, len(leads)) * 100, 1),
                        message=f"已处理 {lead_index}/{len(leads)} 个预报时次",
                    )

    return {
        "operation": "download-range",
        "initCount": len(init_times),
        "requested": requested,
        "downloaded": downloaded,
        "skipped": skipped,
        "failed": failed,
        "index": index_payload(Path(args.database)),
    }


def run_sync_latest(args: argparse.Namespace) -> dict[str, Any]:
    now = parse_key(args.now) if args.now else datetime.now(timezone.utc)
    candidates = latest_fixed_times(now, args.probe_days)
    totals = {"requested": 0, "downloaded": 0, "skipped": 0, "failed": 0}
    found_init: str | None = None

    for index, candidate in enumerate(candidates):
        init_key = candidate.strftime(UTC_FORMAT)
        emit(
            "progress",
            operation="sync",
            stage="probe",
            percent=round(index / max(1, len(candidates)) * 35, 1),
            message=f"正在探测 {args.model} · {nice_time(init_key)}",
        )
        probe_args = argparse.Namespace(**vars(args))
        probe_args.start = init_key
        probe_args.end = init_key
        probe_args.max_lead_hours = 3
        probe_args.report_file_progress = False
        probe = run_download_range(probe_args)
        for name in totals:
            totals[name] += int(probe[name])
        if any(
            run["model"] == args.model and run["initKey"] == init_key
            for run in probe["index"]["runs"]
        ):
            found_init = init_key
            break

    if found_init is None:
        return {
            "operation": "sync-latest",
            "model": args.model,
            "found": False,
            "initKey": None,
            **totals,
            "index": index_payload(Path(args.database)),
        }

    emit(
        "progress",
        operation="sync",
        stage="download",
        percent=40,
        message=f"已找到 {args.model} 最新起报，正在补齐预报时次",
    )
    full_args = argparse.Namespace(**vars(args))
    full_args.start = found_init
    full_args.end = found_init
    full_args.report_file_progress = True
    full = run_download_range(full_args)
    for name in totals:
        totals[name] += int(full[name])
    return {
        "operation": "sync-latest",
        "model": args.model,
        "found": True,
        "initKey": found_init,
        **totals,
        "index": full["index"],
    }


def within_root(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def remove_empty_parents(path: Path, stop: Path) -> None:
    current = path
    stop = stop.resolve()
    while current.exists() and current.resolve() != stop:
        try:
            current.rmdir()
        except OSError:
            break
        current = current.parent


def run_cleanup(args: argparse.Namespace) -> dict[str, Any]:
    if args.retention_days < 1 or args.retention_days > 365:
        raise ValueError("缓存保留天数必须在 1 到 365 天之间。")
    now = parse_key(args.now) if args.now else datetime.now(timezone.utc)
    cutoff = now - timedelta(days=args.retention_days)
    data_root = Path(args.data_root).resolve()
    render_root = Path(args.render_root).resolve()
    removed_runs = 0
    reclaimed_bytes = 0
    failed = 0

    with sqlite3.connect(args.database, timeout=30) as connection:
        connection.row_factory = sqlite3.Row
        rows = connection.execute(
            """
            SELECT id, init_time_utc, source_path, file_size, manifest_path
            FROM forecast_runs
            ORDER BY init_time_utc ASC
            """
        ).fetchall()

    expired = [
        row for row in rows
        if parse_key(row["init_time_utc"]) < cutoff
    ]
    for index, row in enumerate(expired):
        emit(
            "progress",
            operation="cleanup",
            stage="remove",
            percent=round(index / max(1, len(expired)) * 100, 1),
            message=f"正在清理过期缓存 {index + 1}/{len(expired)}",
        )
        source = Path(row["source_path"] or "")
        manifest = Path(row["manifest_path"] or "")
        render_directory = manifest.parent
        try:
            if source.exists():
                if not within_root(source, data_root):
                    raise ValueError("索引中的原始数据路径超出缓存目录，已拒绝删除。")
                reclaimed_bytes += source.stat().st_size
                source.unlink()
                remove_empty_parents(source.parent, data_root)
            if render_directory.exists():
                if not within_root(render_directory, render_root):
                    raise ValueError("索引中的渲染缓存路径超出缓存目录，已拒绝删除。")
                reclaimed_bytes += sum(
                    item.stat().st_size
                    for item in render_directory.rglob("*")
                    if item.is_file()
                )
                shutil.rmtree(render_directory)
                remove_empty_parents(render_directory.parent, render_root)
            with sqlite3.connect(args.database, timeout=30) as connection:
                connection.execute("DELETE FROM cache_entries WHERE run_id=?", (row["id"],))
                connection.execute("DELETE FROM forecast_runs WHERE id=?", (row["id"],))
            removed_runs += 1
        except (OSError, ValueError) as error:
            failed += 1
            emit(
                "warning",
                operation="cleanup",
                message=f"{row['id']} 清理失败：{error}",
            )

    return {
        "operation": "cleanup",
        "removedRuns": removed_runs,
        "reclaimedBytes": reclaimed_bytes,
        "failed": failed,
        "cutoffUtc": cutoff.strftime(ISO_FORMAT),
        "index": index_payload(Path(args.database)),
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="AISky NetCDF data worker")
    parser.add_argument("--database", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    parser.add_argument(
        "--command",
        choices=(
            "init",
            "status",
            "index",
            "import",
            "download-range",
            "sync-latest",
            "cleanup",
        ),
        default="init",
    )
    parser.add_argument("--source", type=Path)
    parser.add_argument("--data-root", type=Path)
    parser.add_argument("--render-root", type=Path)
    parser.add_argument("--model", choices=("AISky-Energy", "AISky-SDS"))
    parser.add_argument("--start")
    parser.add_argument("--end")
    parser.add_argument("--password", default="")
    parser.add_argument("--max-lead-hours", type=int, default=360)
    parser.add_argument("--min-lead-hours", type=int, default=3)
    parser.add_argument("--max-version", type=int, default=9)
    parser.add_argument("--base-url")
    parser.add_argument("--probe-days", type=int, default=3)
    parser.add_argument("--retention-days", type=int, default=3)
    parser.add_argument("--now")
    parser.add_argument("--copy-source", action="store_true")
    parser.add_argument("--init-only", action="store_true")
    return parser


def main() -> None:
    args = build_parser().parse_args()
    if args.init_only:
        args.command = "init"
    recovered_database = initialize_database(args.database, args.schema)

    try:
        if recovered_database is not None:
            emit(
                "warning",
                operation="database",
                message=(
                    "本地索引数据库损坏，已自动重建；"
                    f"原文件保存在 {recovered_database.name}"
                ),
            )
        if args.command == "init":
            emit("ready", database=str(args.database))
            return
        if args.command == "status":
            with sqlite3.connect(args.database, timeout=30) as connection:
                integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
                journal = connection.execute("PRAGMA journal_mode").fetchone()[0]
            emit(
                "result",
                operation="status",
                available=True,
                integrity=integrity,
                journalMode=journal,
                python=sys.version.split()[0],
                numpy=np.__version__,
            )
            return
        if args.command == "index":
            emit("result", **index_payload(args.database))
            return
        if args.command == "import":
            if args.source is None or args.render_root is None:
                raise ValueError("import 命令需要 --source 和 --render-root。")
            source = args.source.resolve()
            if args.copy_source:
                if args.data_root is None:
                    raise ValueError("--copy-source 同时需要 --data-root。")
                info = parse_filename(source)
                target = args.data_root / info["model"] / info["init"] / source.name
                target.parent.mkdir(parents=True, exist_ok=True)
                if source != target:
                    shutil.copy2(source, target)
                source = target
            try:
                manifest = process_netcdf(source, args.render_root, args.database)
            except Exception as error:
                mark_run_error(args.database, source, error)
                raise
            emit("result", operation="import", manifest=manifest, index=index_payload(args.database))
            return
        if args.command == "download-range":
            required = (args.model, args.start, args.end, args.data_root, args.render_root)
            if any(value is None for value in required):
                raise ValueError(
                    "download-range 需要 --model、--start、--end、--data-root 和 --render-root。"
                )
            emit("result", **run_download_range(args))
            return
        if args.command == "sync-latest":
            required = (args.model, args.data_root, args.render_root)
            if any(value is None for value in required):
                raise ValueError(
                    "sync-latest 需要 --model、--data-root 和 --render-root。"
                )
            emit("result", **run_sync_latest(args))
            return
        if args.command == "cleanup":
            if args.data_root is None or args.render_root is None:
                raise ValueError("cleanup 需要 --data-root 和 --render-root。")
            emit("result", **run_cleanup(args))
            return
    except Exception as error:
        emit(
            "error",
            operation=args.command,
            errorType=type(error).__name__,
            message=str(error),
        )
        raise SystemExit(2) from error


if __name__ == "__main__":
    main()
