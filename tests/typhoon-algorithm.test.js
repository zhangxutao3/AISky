"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const algorithm = require(path.resolve(
  __dirname,
  "../AISky.Desktop/MapHost/typhoon-algorithm.js",
));

function createVortexRun(index, weak = false) {
  const rows = 46;
  const columns = 81;
  const centerLat = 15 + index * 0.45;
  const centerLon = 121 + index * 0.8;
  const slp = [];
  const wind = [];
  for (let row = 0; row < rows; row += 1) {
    const latitude = row;
    const pressureRow = [];
    const windRow = [];
    for (let column = 0; column < columns; column += 1) {
      const longitude = 100 + column;
      const distanceSquared = (latitude - centerLat) ** 2
        + ((longitude - centerLon) * Math.cos(latitude * Math.PI / 180)) ** 2;
      pressureRow.push(1012 - 26 * Math.exp(-distanceSquared / 8));
      const radius = Math.sqrt(distanceSquared);
      windRow.push((weak ? 4 : 8) + (weak ? 4 : 31) * Math.exp(-((radius - 2.4) ** 2) / 1.7));
    }
    slp.push(pressureRow);
    wind.push(windRow);
  }
  return {
    run: {
      forecastKey: `20260806_${String(index * 3).padStart(2, "0")}00`,
      leadHours: index * 3,
      grid: { lat: [0, 45], lon: [100, 180] },
    },
    slp,
    wind,
  };
}

const runCandidates = Array.from({ length: 8 }, (_, index) => {
  const fixture = createVortexRun(index);
  const candidates = algorithm.detectCandidates(fixture.run, fixture.slp, fixture.wind);
  assert.ok(candidates.length >= 1, `run ${index} should contain a cyclone candidate`);
  assert.ok(candidates[0].windSpeed >= 32.7);
  return { run: fixture.run, candidates };
});

const tracks = algorithm.buildTracks("AISky-Energy", "20260806_0000", runCandidates);
assert.equal(tracks.length, 1);
assert.equal(tracks[0].points.length, 8);
assert.ok(tracks[0].confidence >= 70);
assert.equal(tracks[0].points[0].intensity.code, "TY");
assert.equal(algorithm.MAX_FORECAST_LEAD_HOURS, 72);

const extendedCandidates = [
  ...runCandidates,
  {
    run: { ...runCandidates.at(-1).run, leadHours: 75 },
    candidates: runCandidates.at(-1).candidates.map((candidate) => ({
      ...candidate,
      leadHours: 75,
    })),
  },
];
const limitedTracks = algorithm.buildTracks(
  "AISky-Energy",
  "20260806_0000",
  extendedCandidates,
);
assert.ok(limitedTracks.every((track) =>
  track.points.every((point) => point.leadHours <= 72)));

const weak = createVortexRun(0, true);
assert.equal(algorithm.detectCandidates(weak.run, weak.slp, weak.wind).length, 0);

console.log(JSON.stringify({
  status: "ok",
  tracks: tracks.length,
  points: tracks[0].points.length,
  confidence: tracks[0].confidence,
  peakWind: tracks[0].peakWind,
}));
