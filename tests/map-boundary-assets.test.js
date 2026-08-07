"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const assetRoot = path.resolve(__dirname, "../AISky.Desktop/MapHost/assets");
const globalBorders = JSON.parse(
  fs.readFileSync(path.join(assetRoot, "countries-meteoinfo.json"), "utf8"),
);
const chinaBorders = JSON.parse(
  fs.readFileSync(path.join(assetRoot, "china-border-meteoinfo.json"), "utf8"),
);
const mapSource = fs.readFileSync(
  path.resolve(__dirname, "../AISky.Desktop/MapHost/map.js"),
  "utf8",
);

function validateLines(lines, minimumLines, maximumLines, label) {
  assert.ok(Array.isArray(lines), `${label} must be an array`);
  assert.ok(
    lines.length >= minimumLines && lines.length <= maximumLines,
    `${label} line count is outside the expected range`,
  );
  let points = 0;
  for (const line of lines) {
    assert.ok(Array.isArray(line) && line.length >= 2, `${label} contains an empty line`);
    for (const coordinate of line) {
      assert.equal(coordinate.length, 2);
      assert.ok(Number.isFinite(coordinate[0]) && Number.isFinite(coordinate[1]));
      assert.ok(coordinate[0] >= -180.001 && coordinate[0] <= 180.001);
      assert.ok(coordinate[1] >= -90.001 && coordinate[1] <= 90.001);
      points += 1;
    }
  }
  return points;
}

assert.equal(globalBorders.source, "MeteoInfo country.shp");
const globalPointCount = validateLines(globalBorders.lines, 250, 600, "global borders");
assert.ok(globalPointCount >= 5_000 && globalPointCount <= 12_000);

assert.equal(chinaBorders.source, "MeteoInfo cn_border.shp");
assert.ok(chinaBorders.bbox[0] < 74 && chinaBorders.bbox[2] > 135);
assert.ok(chinaBorders.bbox[1] < 4 && chinaBorders.bbox[3] > 53);
validateLines(chinaBorders.landLines, 70, 90, "China land borders");
validateLines(chinaBorders.coastLines, 550, 650, "China coastlines");
validateLines(chinaBorders.coastMajorLines, 90, 140, "major China coastlines");
validateLines(chinaBorders.islandLines, 330, 400, "China island outlines");
validateLines(chinaBorders.islandMajorLines, 80, 130, "major China island outlines");

assert.match(mapSource, /countries-meteoinfo\.json/);
assert.match(mapSource, /china-border-meteoinfo\.json/);
assert.doesNotMatch(mapSource, /countries-110m\.json/);

console.log(JSON.stringify({
  status: "ok",
  globalLines: globalBorders.lines.length,
  globalPoints: globalPointCount,
  chinaLandLines: chinaBorders.landLines.length,
}));
