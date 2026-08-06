"use strict";

importScripts("./typhoon-algorithm.js");

async function loadGrid(url) {
  if (!url) return null;
  const response = await fetch(url, { cache: "force-cache" });
  if (!response.ok) {
    throw new Error(`台风识别缓存读取失败：HTTP ${response.status}`);
  }
  const values = await response.json();
  return Array.isArray(values) && Array.isArray(values[0]) ? values : null;
}

async function mapWithConcurrency(items, limit, callback) {
  const result = new Array(items.length);
  let nextIndex = 0;
  const workers = Array.from(
    { length: Math.min(limit, Math.max(1, items.length)) },
    async () => {
      while (nextIndex < items.length) {
        const index = nextIndex;
        nextIndex += 1;
        result[index] = await callback(items[index], index);
      }
    },
  );
  await Promise.all(workers);
  return result;
}

self.addEventListener("message", async (event) => {
  const { requestId, models } = event.data || {};
  try {
    const tracks = [];
    for (const modelSeries of Array.isArray(models) ? models : []) {
      const forecastRuns = (modelSeries.runs || []).filter((run) => {
        const leadHours = Number(run?.leadHours);
        return Number.isFinite(leadHours)
          && leadHours >= 0
          && leadHours <= self.AISkyTyphoon.MAX_FORECAST_LEAD_HOURS;
      });
      let completed = 0;
      const runCandidates = await mapWithConcurrency(
        forecastRuns,
        6,
        async (run) => {
          const [slp, wind] = await Promise.all([
            loadGrid(run.slpSampleUrl),
            loadGrid(run.windSampleUrl),
          ]);
          completed += 1;
          self.postMessage({
            type: "progress",
            requestId,
            model: modelSeries.model,
            completed,
            total: forecastRuns.length,
          });
          return {
            run,
            candidates: slp && wind
              ? self.AISkyTyphoon.detectCandidates(run, slp, wind)
              : [],
          };
        },
      );
      tracks.push(...self.AISkyTyphoon.buildTracks(
        modelSeries.model,
        modelSeries.initKey,
        runCandidates,
      ));
    }
    self.postMessage({ type: "result", requestId, tracks });
  } catch (error) {
    self.postMessage({
      type: "error",
      requestId,
      message: String(error?.message || error),
    });
  }
});
