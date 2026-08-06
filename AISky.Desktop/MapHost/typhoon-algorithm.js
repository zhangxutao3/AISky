(function (root) {
  "use strict";

  const INTENSITIES = [
    { code: "TD", label: "热带低压", minimum: 10.8, color: "#63d6c7" },
    { code: "TS", label: "热带风暴", minimum: 17.2, color: "#48a9f8" },
    { code: "STS", label: "强热带风暴", minimum: 24.5, color: "#72c96b" },
    { code: "TY", label: "台风", minimum: 32.7, color: "#f2cb52" },
    { code: "STY", label: "强台风", minimum: 41.5, color: "#f3944f" },
    { code: "SuperTY", label: "超强台风", minimum: 51.0, color: "#ed5972" },
  ];
  const MAX_FORECAST_LEAD_HOURS = 72;

  function clamp(value, minimum, maximum) {
    return Math.max(minimum, Math.min(maximum, value));
  }

  function normalizeLongitude(value) {
    return ((value % 360) + 360) % 360;
  }

  function haversineDistance(first, second) {
    const radians = Math.PI / 180;
    const lat1 = first.lat * radians;
    const lat2 = second.lat * radians;
    const deltaLat = (second.lat - first.lat) * radians;
    let deltaLon = (second.lon - first.lon) * radians;
    if (deltaLon > Math.PI) deltaLon -= Math.PI * 2;
    if (deltaLon < -Math.PI) deltaLon += Math.PI * 2;
    const a = Math.sin(deltaLat / 2) ** 2
      + Math.cos(lat1) * Math.cos(lat2) * Math.sin(deltaLon / 2) ** 2;
    return 6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  }

  function intensityFor(windSpeed) {
    let selected = INTENSITIES[0];
    for (const item of INTENSITIES) {
      if (windSpeed >= item.minimum) selected = item;
    }
    return { ...selected };
  }

  function coordinateAt(index, count, extent) {
    if (count <= 1) return Number(extent?.[0] ?? 0);
    return Number(extent[0]) + index / (count - 1) * (Number(extent[1]) - Number(extent[0]));
  }

  function isLocalMinimum(values, row, column) {
    const center = values[row]?.[column];
    if (!Number.isFinite(center)) return false;
    for (let rowOffset = -1; rowOffset <= 1; rowOffset += 1) {
      for (let columnOffset = -1; columnOffset <= 1; columnOffset += 1) {
        if (rowOffset === 0 && columnOffset === 0) continue;
        const neighbor = values[row + rowOffset]?.[column + columnOffset];
        if (Number.isFinite(neighbor) && neighbor < center) return false;
      }
    }
    return true;
  }

  function localEnvironment(slp, wind, grid, row, column) {
    const rows = slp.length;
    const columns = slp[0]?.length ?? 0;
    const center = {
      lat: coordinateAt(row, rows, grid.lat),
      lon: normalizeLongitude(coordinateAt(column, columns, grid.lon)),
    };
    let maximumWind = 0;
    let ringPressureTotal = 0;
    let ringPressureCount = 0;
    const latitudeStep = Math.abs(Number(grid.lat[1]) - Number(grid.lat[0])) / Math.max(1, rows - 1);
    const longitudeStep = Math.abs(Number(grid.lon[1]) - Number(grid.lon[0])) / Math.max(1, columns - 1);
    const rowRadius = Math.max(2, Math.ceil(9 / Math.max(0.1, latitudeStep)));
    const columnRadius = Math.max(2, Math.ceil(10 / Math.max(0.1, longitudeStep)));

    for (let rowOffset = -rowRadius; rowOffset <= rowRadius; rowOffset += 1) {
      const sampleRow = row + rowOffset;
      if (sampleRow < 0 || sampleRow >= rows) continue;
      for (let columnOffset = -columnRadius; columnOffset <= columnRadius; columnOffset += 1) {
        let sampleColumn = column + columnOffset;
        while (sampleColumn < 0) sampleColumn += columns;
        while (sampleColumn >= columns) sampleColumn -= columns;
        const sample = {
          lat: coordinateAt(sampleRow, rows, grid.lat),
          lon: normalizeLongitude(coordinateAt(sampleColumn, columns, grid.lon)),
        };
        const distance = haversineDistance(center, sample);
        const windValue = wind[sampleRow]?.[sampleColumn];
        const pressureValue = slp[sampleRow]?.[sampleColumn];
        if (distance <= 360 && Number.isFinite(windValue)) {
          maximumWind = Math.max(maximumWind, Number(windValue));
        }
        if (distance >= 430 && distance <= 900 && Number.isFinite(pressureValue)) {
          ringPressureTotal += Number(pressureValue);
          ringPressureCount += 1;
        }
      }
    }

    return {
      maximumWind,
      surroundingPressure: ringPressureCount > 0
        ? ringPressureTotal / ringPressureCount
        : Number.NaN,
    };
  }

  function detectCandidates(run, slp, wind) {
    if (!Array.isArray(slp) || !Array.isArray(slp[0])
      || !Array.isArray(wind) || !Array.isArray(wind[0])) {
      return [];
    }
    const rows = Math.min(slp.length, wind.length);
    const columns = Math.min(slp[0].length, wind[0].length);
    if (rows < 3 || columns < 3) return [];

    const raw = [];
    for (let row = 1; row < rows - 1; row += 1) {
      const latitude = coordinateAt(row, rows, run.grid.lat);
      if (latitude < 0 || latitude > 42) continue;
      for (let column = 1; column < columns - 1; column += 1) {
        const longitude = normalizeLongitude(coordinateAt(column, columns, run.grid.lon));
        if (longitude < 100 || longitude > 180) continue;
        const pressure = Number(slp[row]?.[column]);
        if (!Number.isFinite(pressure) || pressure > 1008 || pressure < 860) continue;
        if (!isLocalMinimum(slp, row, column)) continue;

        const environment = localEnvironment(slp, wind, run.grid, row, column);
        const pressureDeficit = environment.surroundingPressure - pressure;
        if (!Number.isFinite(pressureDeficit)
          || pressureDeficit < 1.5
          || environment.maximumWind < 10.8
          || environment.maximumWind > 90) {
          continue;
        }
        const confidence = clamp(
          0.38
            + (pressureDeficit - 1.5) * 0.055
            + (environment.maximumWind - 10.8) * 0.018,
          0.38,
          0.98,
        );
        raw.push({
          lat: latitude,
          lon: longitude > 180 ? longitude - 360 : longitude,
          pressure: Math.round(pressure * 10) / 10,
          windSpeed: Math.round(environment.maximumWind * 10) / 10,
          pressureDeficit: Math.round(pressureDeficit * 10) / 10,
          confidence: Math.round(confidence * 100) / 100,
          forecastKey: run.forecastKey,
          leadHours: Number(run.leadHours) || 0,
          score: pressureDeficit * 3
            + environment.maximumWind
            + (1008 - pressure) * 0.45,
        });
      }
    }

    raw.sort((first, second) => second.score - first.score);
    const selected = [];
    for (const candidate of raw) {
      if (selected.some((item) => haversineDistance(item, candidate) < 520)) continue;
      const intensity = intensityFor(candidate.windSpeed);
      selected.push({ ...candidate, intensity });
      if (selected.length >= 6) break;
    }
    return selected;
  }

  function buildTracks(model, initKey, runCandidates) {
    const forecastCandidates = runCandidates.filter(({ run }) => {
      const leadHours = Number(run?.leadHours);
      return Number.isFinite(leadHours)
        && leadHours >= 0
        && leadHours <= MAX_FORECAST_LEAD_HOURS;
    });
    const tracks = [];
    for (let runIndex = 0; runIndex < forecastCandidates.length; runIndex += 1) {
      const { run, candidates } = forecastCandidates[runIndex];
      const pairs = [];
      for (let trackIndex = 0; trackIndex < tracks.length; trackIndex += 1) {
        const track = tracks[trackIndex];
        const previous = track.points.at(-1);
        const gap = Number(run.leadHours) - Number(previous.leadHours);
        if (gap <= 0 || gap > 12) continue;
        for (let candidateIndex = 0; candidateIndex < candidates.length; candidateIndex += 1) {
          const candidate = candidates[candidateIndex];
          const distance = haversineDistance(previous, candidate);
          const maximumDistance = Math.max(430, gap * 105);
          if (distance > maximumDistance) continue;
          pairs.push({
            trackIndex,
            candidateIndex,
            cost: distance + Math.abs(previous.pressure - candidate.pressure) * 12,
          });
        }
      }

      pairs.sort((first, second) => first.cost - second.cost);
      const usedTracks = new Set();
      const usedCandidates = new Set();
      for (const pair of pairs) {
        if (usedTracks.has(pair.trackIndex) || usedCandidates.has(pair.candidateIndex)) continue;
        tracks[pair.trackIndex].points.push(candidates[pair.candidateIndex]);
        tracks[pair.trackIndex].lastRunIndex = runIndex;
        usedTracks.add(pair.trackIndex);
        usedCandidates.add(pair.candidateIndex);
      }
      candidates.forEach((candidate, candidateIndex) => {
        if (usedCandidates.has(candidateIndex)) return;
        tracks.push({
          model,
          initKey,
          points: [candidate],
          lastRunIndex: runIndex,
        });
      });
    }

    const complete = tracks
      .filter((track) => {
        if (track.points.length < 3) return false;
        return track.points.at(-1).leadHours - track.points[0].leadHours >= 6;
      })
      .map((track) => {
        const pointConfidence = track.points.reduce((sum, point) => sum + point.confidence, 0)
          / track.points.length;
        const continuity = clamp(
          track.points.length / Math.max(3, forecastCandidates.length * 0.55),
          0.72,
          1,
        );
        const confidence = Math.round(pointConfidence * continuity * 100);
        const peakWind = Math.max(...track.points.map((point) => point.windSpeed));
        const minimumPressure = Math.min(...track.points.map((point) => point.pressure));
        return {
          model,
          initKey,
          confidence,
          peakWind,
          minimumPressure,
          points: track.points,
          score: track.points.length * 10 + peakWind + (1010 - minimumPressure),
        };
      })
      .filter((track) =>
        track.confidence >= 65
        && track.peakWind >= 17.2
        && track.minimumPressure <= 1005)
      .sort((first, second) => second.score - first.score)
      .slice(0, 3);

    complete.forEach((track, index) => {
      track.id = `${model}-${initKey}-${index + 1}`;
      track.name = `${model.replace("AISky-", "")} 模拟路径 ${index + 1}`;
    });
    return complete;
  }

  const api = {
    INTENSITIES,
    MAX_FORECAST_LEAD_HOURS,
    buildTracks,
    detectCandidates,
    haversineDistance,
    intensityFor,
  };
  root.AISkyTyphoon = api;
  if (typeof module !== "undefined" && module.exports) {
    module.exports = api;
  }
}(typeof self !== "undefined" ? self : globalThis));
