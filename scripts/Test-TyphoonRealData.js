"use strict";

const fs = require("node:fs");
const path = require("node:path");
const algorithm = require("../AISky.Desktop/MapHost/typhoon-algorithm.js");

const dataRoot = path.resolve(process.argv[2] || "");
const renderRoot = path.join(dataRoot, "cache", "render");
if (!fs.existsSync(renderRoot)) {
  throw new Error(`Render cache does not exist: ${renderRoot}`);
}

function findManifests(directory) {
  const result = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) result.push(...findManifests(fullPath));
    else if (entry.name === "run.json") result.push(fullPath);
  }
  return result;
}

const manifests = findManifests(path.join(renderRoot, "runs"))
  .map((manifestPath) => JSON.parse(fs.readFileSync(manifestPath, "utf8")));

const summaries = [];
for (const model of ["AISky-Energy", "AISky-SDS"]) {
  const modelRuns = manifests.filter((run) => run.model === model);
  const latestInit = modelRuns.map((run) => run.initKey).sort().at(-1);
  const runs = modelRuns
    .filter((run) => run.initKey === latestInit)
    .sort((first, second) => first.leadHours - second.leadHours);
  const runCandidates = [];
  for (const run of runs) {
    const slp = run.layers.find((layer) => layer.id === "slp");
    const wind = run.layers.find((layer) => layer.id === "wind10");
    if (!slp?.sample || !wind?.sample) continue;
    const slpGrid = JSON.parse(fs.readFileSync(path.join(renderRoot, slp.sample), "utf8"));
    const windGrid = JSON.parse(fs.readFileSync(path.join(renderRoot, wind.sample), "utf8"));
    const projectedRun = {
      id: run.id,
      forecastKey: run.forecastKey,
      leadHours: run.leadHours,
      grid: run.grid,
    };
    runCandidates.push({
      run: projectedRun,
      candidates: algorithm.detectCandidates(projectedRun, slpGrid, windGrid),
    });
  }
  const tracks = algorithm.buildTracks(model, latestInit, runCandidates);
  summaries.push({
    model,
    initKey: latestInit || null,
    runs: runCandidates.length,
    candidateRuns: runCandidates.filter((run) => run.candidates.length > 0).length,
    tracks: tracks.map((track) => ({
      points: track.points.length,
      confidence: track.confidence,
      peakWind: track.peakWind,
      minimumPressure: track.minimumPressure,
      startLead: track.points[0].leadHours,
      endLead: track.points.at(-1).leadHours,
      start: [track.points[0].lon, track.points[0].lat],
      end: [track.points.at(-1).lon, track.points.at(-1).lat],
      maximumStepKm: Math.round(Math.max(
        0,
        ...track.points.slice(1).map((point, index) =>
          algorithm.haversineDistance(track.points[index], point)),
      )),
    })),
  });
}

console.log(JSON.stringify({ status: "ok", summaries }, null, 2));
