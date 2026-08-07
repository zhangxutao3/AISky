# Map asset sources

- `countries-meteoinfo.json` is a simplified set of shared international
  boundaries derived from `country.shp` in the MeteoInfo map-data directory
  supplied by the AISky project owner.
- `china-border-meteoinfo.json` is a simplified China boundary, coastline and
  island overlay derived from `cn_border.shp` in the same directory.
- Coastlines, province lines, rivers, lakes and places remain derived from
  Natural Earth through Cartopy.

Only converted coordinates required by the Canvas renderer are distributed.
The original Shapefile attribute tables and unrelated raster/vector layers are
not included in AISky.
