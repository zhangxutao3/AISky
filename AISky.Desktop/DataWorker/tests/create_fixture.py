"""Create a small deterministic NetCDF fixture for AISky integration tests."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from netCDF4 import Dataset


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    rows, columns = 48, 96
    latitudes = np.linspace(-60.0, 80.0, rows, dtype=np.float32)
    longitudes = np.linspace(0.0, 357.5, columns, dtype=np.float32)
    longitude_grid, latitude_grid = np.meshgrid(longitudes, latitudes)
    wave = (
        np.sin(np.deg2rad(latitude_grid * 2.0))
        + np.cos(np.deg2rad(longitude_grid * 1.5))
    ).astype(np.float32)

    with Dataset(output, "w", format="NETCDF4") as dataset:
        dataset.createDimension("lat", rows)
        dataset.createDimension("lon", columns)
        dataset.createVariable("lat", "f4", ("lat",))[:] = latitudes
        dataset.createVariable("lon", "f4", ("lon",))[:] = longitudes

        fields = {
            "T2M": 286.0 + wave * 9.0,
            "SWGDN": 520.0 + wave * 180.0,
            "CLDTOT": np.clip(0.5 + wave * 0.22, 0.0, 1.0),
            "U10M": 4.0 + wave * 2.0,
            "V10M": 2.0 - wave * 1.5,
            "SLP": 101300.0 + wave * 1900.0,
            "DUEXTTAU": np.clip(0.18 + wave * 0.08, 0.0, 0.85),
            "PRECTOT": np.clip((wave + 1.5) / 86400.0, 0.0, None),
            "PBLH": 1100.0 + wave * 420.0,
        }
        for name, values in fields.items():
            variable = dataset.createVariable(name, "f4", ("lat", "lon"))
            variable[:] = np.asarray(values, dtype=np.float32)

    if output.stat().st_size < 4096:
        raise RuntimeError("Generated NetCDF fixture is unexpectedly small.")
    print(output)


if __name__ == "__main__":
    main()
