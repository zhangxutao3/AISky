const canvas = document.querySelector("#map");
const context = canvas.getContext("2d", { alpha: false, desynchronized: true });
const windCanvas = document.querySelector("#wind");
const windContext = windCanvas.getContext("2d", { alpha: true, desynchronized: true });
const typhoonHover = document.querySelector("#typhoonHover");
const typhoonLegend = document.querySelector("#typhoonLegend");
const typhoonLegendStatus = document.querySelector("#typhoonLegendStatus");
const typhoonPanel = document.querySelector("#typhoonPanel");
const typhoonClose = document.querySelector("#typhoonClose");
const typhoonModel = document.querySelector("#typhoonModel");
const typhoonName = document.querySelector("#typhoonName");
const typhoonStrength = document.querySelector("#typhoonStrength");
const typhoonStrengthDot = document.querySelector("#typhoonStrengthDot");
const typhoonWind = document.querySelector("#typhoonWind");
const typhoonConfidence = document.querySelector("#typhoonConfidence");
const typhoonTime = document.querySelector("#typhoonTime");
const typhoonLead = document.querySelector("#typhoonLead");
const typhoonPressure = document.querySelector("#typhoonPressure");
const typhoonLocation = document.querySelector("#typhoonLocation");
const coordinateLabel = document.querySelector("#coordinates");
const pointPanel = document.querySelector("#pointPanel");
const pointClose = document.querySelector("#pointClose");
const pointLocation = document.querySelector("#pointLocation");
const pointLayerName = document.querySelector("#pointLayerName");
const pointValue = document.querySelector("#pointValue");
const pointUnit = document.querySelector("#pointUnit");
const pointValidTime = document.querySelector("#pointValidTime");
const pointRange = document.querySelector("#pointRange");
const pointCount = document.querySelector("#pointCount");
const pointChart = document.querySelector("#pointChart");
const pointChartBase = document.createElement("canvas");
const pointChartEmpty = document.querySelector("#pointChartEmpty");
const pointFirstTime = document.querySelector("#pointFirstTime");
const pointLastTime = document.querySelector("#pointLastTime");
const pointStatus = document.querySelector("#pointStatus");

const initialView = { left: 45, right: 165, top: 72, bottom: 5 };
const worldView = { left: -180, right: 180, top: 85, bottom: -85 };
const view = { ...initialView };
const textureBounds = { ...worldView };
const fieldCache = new Map();
const sampleCache = new Map();
let coastlines = [];
let countryBorders = [];
let provinceBorders = [];
let rivers = [];
let lakes = [];
let places = [];
let activeRun = null;
let forecastSeries = [];
let activeLayerId = "";
let activeLead = 0;
let activeField = null;
let renderGeneration = 0;
let pointGeneration = 0;
let renderQueued = false;
let dragState = null;
let suppressMapClick = false;
let pointSelection = null;
let pointSeriesData = [];
let pointChartPoints = [];
let pointChartRatio = 1;
let mapTheme = "light";
let fieldOpacity = 0.93;
let showGrid = true;
let showPlaces = true;
let showWindAnimation = true;
let showTyphoonPaths = false;
let displayUtcOffsetHours = 0;
let windField = null;
let windParticles = [];
let windFrame = 0;
let windLastFrame = 0;
let prefetchGeneration = 0;
let lastViewportWidth = 0;
let lastViewportHeight = 0;
let typhoonModels = [];
let typhoonTracks = [];
let typhoonHitPoints = [];
let selectedTyphoonPoint = null;
let typhoonWorker = null;
let typhoonRequestId = 0;
let typhoonAnalysisKey = "";
const typhoonTrackCache = new Map();

const TYPHOON_MODEL_STYLES = {
  "AISky-Energy": { color: "#ff7891", dash: [] },
  "AISky-SDS": { color: "#45c9e9", dash: [9, 6] },
};

// Coordinates follow the current Central Meteorological Observatory map script:
// https://typhoon.nmc.cn/js/typhoon/gis.js (checked 2026-08-06).
const GUARD_LINE_24 = [
  [126.993568, 34.005024],
  [126.993568, 21.971252],
  [118.995521, 17.96586],
  [118.995521, 10.97105],
  [113.018959, 4.48627],
  [104.998939, -0.035506],
];
const GUARD_LINE_48 = [
  [104.998939, -0.035506],
  [119.962318, -0.035506],
  [131.981361, 14.96886],
  [131.981361, 33.959474],
];

function clamp(value, minimum, maximum) {
  return Math.max(minimum, Math.min(maximum, value));
}

function wrapLongitude(lon) {
  return ((lon + 180) % 360 + 360) % 360 - 180;
}

function setConstrainedView(candidate) {
  const width = clamp(candidate.right - candidate.left, 12, 720);
  const height = clamp(candidate.top - candidate.bottom, 8, 170);
  const left = wrapLongitude(candidate.left);
  const top = clamp(candidate.top, worldView.bottom + height, worldView.top);
  Object.assign(view, {
    left,
    right: left + width,
    top,
    bottom: top - height,
  });
}

function resizeViewForViewport(width, height, force = false) {
  if (width <= 0 || height <= 0) return;
  if (!force && width === lastViewportWidth && height === lastViewportHeight) return;

  const longitudeSpan = view.right - view.left;
  const latitudeSpan = view.top - view.bottom;
  const referenceWidth = lastViewportWidth || width;
  const referenceHeight = lastViewportHeight || height;
  const degreesPerPixel = Math.max(
    longitudeSpan / referenceWidth,
    latitudeSpan / referenceHeight,
  );
  const targetWidth = clamp(degreesPerPixel * width, 12, 720);
  const targetHeight = clamp(degreesPerPixel * height, 8, 170);
  const centerLongitude = (view.left + view.right) / 2;
  const centerLatitude = (view.top + view.bottom) / 2;
  setConstrainedView({
    left: centerLongitude - targetWidth / 2,
    right: centerLongitude + targetWidth / 2,
    top: centerLatitude + targetHeight / 2,
    bottom: centerLatitude - targetHeight / 2,
  });
  lastViewportWidth = width;
  lastViewportHeight = height;
}

function project(lon, lat, width, height) {
  return [
    ((lon - view.left) / (view.right - view.left)) * width,
    ((view.top - lat) / (view.top - view.bottom)) * height,
  ];
}

function unproject(x, y) {
  return [
    wrapLongitude(view.left + (x / canvas.clientWidth) * (view.right - view.left)),
    view.top - (y / canvas.clientHeight) * (view.top - view.bottom),
  ];
}

function interpolateColor(stops, value) {
  const position = Math.max(0, Math.min(0.999999, value)) * (stops.length - 1);
  const index = Math.floor(position);
  const amount = position - index;
  const first = stops[index].match(/\w\w/g).map((part) => Number.parseInt(part, 16));
  const second = stops[Math.min(index + 1, stops.length - 1)]
    .match(/\w\w/g)
    .map((part) => Number.parseInt(part, 16));
  return first.map((channel, channelIndex) =>
    Math.round(channel + (second[channelIndex] - channel) * amount));
}

function drawEmptyField(width, height) {
  const gradient = context.createLinearGradient(0, 0, width, height);
  if (mapTheme === "dark") {
    gradient.addColorStop(0, "#102d38");
    gradient.addColorStop(0.48, "#173f49");
    gradient.addColorStop(1, "#0a2530");
  } else {
    gradient.addColorStop(0, "#d8eceb");
    gradient.addColorStop(0.48, "#edf3ea");
    gradient.addColorStop(1, "#b9dfe3");
  }
  context.fillStyle = gradient;
  context.fillRect(0, 0, width, height);

  const glow = context.createRadialGradient(
    width * 0.60,
    height * 0.40,
    10,
    width * 0.60,
    height * 0.40,
    width * 0.55,
  );
  if (mapTheme === "dark") {
    glow.addColorStop(0, "rgba(73, 190, 201, 0.18)");
    glow.addColorStop(0.6, "rgba(38, 120, 135, 0.07)");
    glow.addColorStop(1, "rgba(4, 20, 27, 0)");
  } else {
    glow.addColorStop(0, "rgba(255, 239, 184, 0.26)");
    glow.addColorStop(0.55, "rgba(129, 211, 216, 0.10)");
    glow.addColorStop(1, "rgba(18, 70, 82, 0)");
  }
  context.fillStyle = glow;
  context.fillRect(0, 0, width, height);
}

function normalizeLongitude(lon, first, last) {
  if (first >= 0 && lon < 0) return lon + 360;
  if (last <= 180 && lon > 180) return lon - 360;
  return lon;
}

function sampleValues(field, values, lon, lat) {
  const { grid, info } = field;
  const firstLat = grid.lat[0];
  const lastLat = grid.lat[1];
  const firstLon = grid.lon[0];
  const lastLon = grid.lon[1];
  const normalizedLon = normalizeLongitude(lon, firstLon, lastLon);
  const rowFraction = (lat - firstLat) / (lastLat - firstLat);
  const columnFraction = (normalizedLon - firstLon) / (lastLon - firstLon);
  if (
    !Number.isFinite(rowFraction) ||
    !Number.isFinite(columnFraction) ||
    rowFraction < 0 ||
    rowFraction > 1 ||
    columnFraction < 0 ||
    columnFraction > 1
  ) {
    return null;
  }
  const row = Math.max(0, Math.min(info.rows - 1, Math.round(rowFraction * (info.rows - 1))));
  const column = Math.max(0, Math.min(info.cols - 1, Math.round(columnFraction * (info.cols - 1))));
  const encoded = values[row * info.cols + column];
  if (encoded === info.missing) return null;
  const [low, high] = info.range;
  return low + (encoded / 65534) * (high - low);
}

function sampleField(field, lon, lat) {
  return sampleValues(field, field.values, lon, lat);
}

function sampleVector(field, lon, lat) {
  if (!field?.uValues || !field?.vValues) return null;
  const u = sampleValues(field, field.uValues, lon, lat);
  const v = sampleValues(field, field.vValues, lon, lat);
  return Number.isFinite(u) && Number.isFinite(v) ? { u, v } : null;
}

function sampleGrid(values, grid, lon, lat) {
  const rows = values?.length ?? 0;
  const columns = values?.[0]?.length ?? 0;
  if (rows === 0 || columns === 0 || grid?.lat?.length < 2 || grid?.lon?.length < 2) {
    return null;
  }
  const [firstLat, lastLat] = grid.lat;
  const [firstLon, lastLon] = grid.lon;
  const normalizedLon = normalizeLongitude(lon, firstLon, lastLon);
  const rowFraction = (lat - firstLat) / (lastLat - firstLat);
  const columnFraction = (normalizedLon - firstLon) / (lastLon - firstLon);
  if (
    !Number.isFinite(rowFraction) ||
    !Number.isFinite(columnFraction) ||
    rowFraction < 0 ||
    rowFraction > 1 ||
    columnFraction < 0 ||
    columnFraction > 1
  ) {
    return null;
  }
  const row = Math.max(0, Math.min(rows - 1, Math.round(rowFraction * (rows - 1))));
  const column = Math.max(0, Math.min(columns - 1, Math.round(columnFraction * (columns - 1))));
  const value = values[row]?.[column];
  return Number.isFinite(value) ? value : null;
}

function formatValue(value, digits = 2) {
  if (!Number.isFinite(value)) return "--";
  return Number(value).toLocaleString("zh-CN", {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  });
}

function formatForecastKey(key) {
  const match = String(key || "").match(/^(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})$/);
  if (!match) return String(key || "--");
  const utc = Date.UTC(
    Number(match[1]),
    Number(match[2]) - 1,
    Number(match[3]),
    Number(match[4]),
    Number(match[5]),
  );
  const value = new Date(utc + displayUtcOffsetHours * 3600000);
  const month = String(value.getUTCMonth() + 1).padStart(2, "0");
  const day = String(value.getUTCDate()).padStart(2, "0");
  const hour = String(value.getUTCHours()).padStart(2, "0");
  const minute = String(value.getUTCMinutes()).padStart(2, "0");
  return `${month}-${day} ${hour}:${minute}`;
}

function displayTimeZoneLabel() {
  if (displayUtcOffsetHours === 0) return "UTC";
  return `UTC${displayUtcOffsetHours > 0 ? "+" : ""}${displayUtcOffsetHours}`;
}

function typhoonModelsKey(models) {
  return (Array.isArray(models) ? models : [])
    .map((model) => {
      const runs = model.runs || [];
      return `${model.model}:${model.initKey}:${runs.length}:${runs[0]?.id || ""}:${runs.at(-1)?.id || ""}`;
    })
    .join("|");
}

function reportTyphoonStatus(status, trackCount = 0, message = "") {
  window.chrome?.webview?.postMessage({
    type: "typhoon-status",
    status,
    trackCount,
    message,
  });
}

function ensureTyphoonWorker() {
  if (typhoonWorker) return typhoonWorker;
  typhoonWorker = new Worker("./typhoon-worker.js");
  typhoonWorker.addEventListener("message", (event) => {
    const message = event.data || {};
    if (message.requestId !== typhoonRequestId) return;
    if (message.type === "progress") {
      typhoonLegendStatus.textContent =
        `${String(message.model || "").replace("AISky-", "")} · ${message.completed}/${message.total}`;
      return;
    }
    if (message.type === "result") {
      typhoonTracks = Array.isArray(message.tracks) ? message.tracks : [];
      typhoonTrackCache.set(typhoonAnalysisKey, typhoonTracks);
      while (typhoonTrackCache.size > 4) {
        typhoonTrackCache.delete(typhoonTrackCache.keys().next().value);
      }
      typhoonLegendStatus.textContent = typhoonTracks.length > 0
        ? `${typhoonTracks.length} 条未来 72 小时模拟路径`
        : "当前起报未识别到台风";
      typhoonLegend.hidden = !showTyphoonPaths;
      reportTyphoonStatus("ready", typhoonTracks.length);
      requestRender();
      return;
    }
    if (message.type === "error") {
      typhoonTracks = [];
      typhoonLegendStatus.textContent = "本地模式识别失败";
      reportTyphoonStatus("error", 0, message.message || "");
      requestRender();
    }
  });
  typhoonWorker.addEventListener("error", (event) => {
    typhoonTracks = [];
    typhoonLegendStatus.textContent = "本地模式识别失败";
    reportTyphoonStatus("error", 0, event.message || "");
    requestRender();
  });
  return typhoonWorker;
}

function refreshTyphoonAnalysis() {
  if (!showTyphoonPaths) {
    typhoonLegend.hidden = true;
    typhoonHover.hidden = true;
    typhoonPanel.hidden = true;
    selectedTyphoonPoint = null;
    requestRender();
    return;
  }
  typhoonLegend.hidden = false;
  const key = typhoonModelsKey(typhoonModels);
  if (!key) {
    typhoonTracks = [];
    typhoonLegendStatus.textContent = "两个模型均无可分析序列";
    reportTyphoonStatus("ready", 0);
    requestRender();
    return;
  }
  if (typhoonTrackCache.has(key)) {
    typhoonAnalysisKey = key;
    typhoonTracks = typhoonTrackCache.get(key);
    typhoonLegendStatus.textContent = typhoonTracks.length > 0
      ? `${typhoonTracks.length} 条未来 72 小时模拟路径`
      : "当前起报未识别到台风";
    reportTyphoonStatus("ready", typhoonTracks.length);
    requestRender();
    return;
  }
  typhoonAnalysisKey = key;
  typhoonTracks = [];
  typhoonLegendStatus.textContent = "正在分析本地 SLP 与 WIND10";
  reportTyphoonStatus("loading");
  typhoonRequestId += 1;
  ensureTyphoonWorker().postMessage({
    requestId: typhoonRequestId,
    models: typhoonModels,
  });
  requestRender();
}

async function loadSample(layer) {
  if (!layer?.sampleUrl) return null;
  if (sampleCache.has(layer.sampleUrl)) return sampleCache.get(layer.sampleUrl);
  const response = await fetch(layer.sampleUrl, { cache: "force-cache" });
  if (!response.ok) throw new Error(`抽样缓存读取失败：HTTP ${response.status}`);
  const values = await response.json();
  if (!Array.isArray(values) || !Array.isArray(values[0])) {
    throw new Error("抽样缓存格式无效");
  }
  sampleCache.set(layer.sampleUrl, values);
  while (sampleCache.size > 24) sampleCache.delete(sampleCache.keys().next().value);
  return values;
}

async function mapWithConcurrency(items, limit, worker) {
  const results = new Array(items.length);
  let nextIndex = 0;
  const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
    while (nextIndex < items.length) {
      const index = nextIndex;
      nextIndex += 1;
      results[index] = await worker(items[index], index);
    }
  });
  await Promise.all(runners);
  return results;
}

function paintPointChartFrame() {
  if (
    pointChartBase.width !== pointChart.width ||
    pointChartBase.height !== pointChart.height
  ) {
    return;
  }

  const chartContext = pointChart.getContext("2d");
  chartContext.clearRect(0, 0, pointChart.width, pointChart.height);
  chartContext.drawImage(pointChartBase, 0, 0);
  const activePoint = pointChartPoints.find((point) => point?.item.id === activeRun?.id);
  if (!activePoint) return;

  chartContext.beginPath();
  chartContext.arc(activePoint.x, activePoint.y, 4.5 * pointChartRatio, 0, Math.PI * 2);
  chartContext.fillStyle = "#ffb84a";
  chartContext.fill();
  chartContext.lineWidth = 2 * pointChartRatio;
  chartContext.strokeStyle = "#ffffff";
  chartContext.stroke();
}

function drawPointChart(series) {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.max(1, Math.round(pointChart.clientWidth * ratio));
  const height = Math.max(1, Math.round(pointChart.clientHeight * ratio));
  pointChart.width = width;
  pointChart.height = height;
  pointChartBase.width = width;
  pointChartBase.height = height;
  pointChartRatio = ratio;
  pointChartPoints = [];
  const chartContext = pointChartBase.getContext("2d");
  chartContext.clearRect(0, 0, width, height);

  const valid = series.filter((item) => Number.isFinite(item.value));
  if (valid.length === 0) {
    pointChartEmpty.hidden = false;
    pointChartEmpty.textContent = "该格点暂无有效预报值";
    pointChart.getContext("2d").clearRect(0, 0, width, height);
    return;
  }

  pointChartEmpty.hidden = true;
  const values = valid.map((item) => item.value);
  let minimum = Math.min(...values);
  let maximum = Math.max(...values);
  if (Math.abs(maximum - minimum) < 1e-6) {
    minimum -= 0.5;
    maximum += 0.5;
  }
  const padding = {
    top: 12 * ratio,
    right: 10 * ratio,
    bottom: 12 * ratio,
    left: 40 * ratio,
  };
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const slotWidth = plotWidth / Math.max(1, series.length - 1);

  chartContext.font = `${10 * ratio}px "Segoe UI Variable"`;
  chartContext.textAlign = "right";
  chartContext.textBaseline = "middle";
  chartContext.fillStyle = mapTheme === "dark" ? "#b8d4da" : "#55727b";
  chartContext.strokeStyle = mapTheme === "dark"
    ? "rgba(205, 230, 234, 0.15)"
    : "rgba(61, 117, 126, 0.14)";
  chartContext.lineWidth = ratio;
  for (let index = 0; index < 3; index += 1) {
    const amount = index / 2;
    const y = padding.top + plotHeight * amount;
    const value = maximum - (maximum - minimum) * amount;
    chartContext.beginPath();
    chartContext.moveTo(padding.left, y);
    chartContext.lineTo(width - padding.right, y);
    chartContext.stroke();
    chartContext.fillText(formatValue(value, 1), padding.left - 6 * ratio, y);
  }

  pointChartPoints = series.map((item, index) => {
    if (!Number.isFinite(item.value)) return null;
    return {
      x: padding.left + slotWidth * index,
      y: padding.top + (1 - (item.value - minimum) / (maximum - minimum)) * plotHeight,
      item,
    };
  });

  const area = chartContext.createLinearGradient(0, padding.top, 0, padding.top + plotHeight);
  area.addColorStop(0, "rgba(37, 190, 198, 0.28)");
  area.addColorStop(1, "rgba(37, 190, 198, 0.015)");
  chartContext.beginPath();
  let firstPoint = null;
  let lastPoint = null;
  for (const point of pointChartPoints) {
    if (!point) continue;
    if (!firstPoint) {
      firstPoint = point;
      chartContext.moveTo(point.x, point.y);
    } else {
      chartContext.lineTo(point.x, point.y);
    }
    lastPoint = point;
  }
  if (firstPoint && lastPoint) {
    chartContext.lineTo(lastPoint.x, padding.top + plotHeight);
    chartContext.lineTo(firstPoint.x, padding.top + plotHeight);
    chartContext.closePath();
    chartContext.fillStyle = area;
    chartContext.fill();
  }

  chartContext.beginPath();
  let started = false;
  for (const point of pointChartPoints) {
    if (!point) {
      started = false;
      continue;
    }
    if (!started) {
      chartContext.moveTo(point.x, point.y);
      started = true;
    } else {
      chartContext.lineTo(point.x, point.y);
    }
  }
  chartContext.strokeStyle = "#16aebe";
  chartContext.lineWidth = 2.25 * ratio;
  chartContext.lineJoin = "round";
  chartContext.lineCap = "round";
  chartContext.stroke();
  paintPointChartFrame();
}

function updatePointCurrentReading() {
  if (!pointSelection || pointPanel.hidden) return;
  const layer = activeRun?.layers?.find((item) => item.id === activeLayerId)
    ?? activeRun?.layers?.[0];
  const current = activeField
    ? sampleField(activeField, pointSelection.lon, pointSelection.lat)
    : null;
  pointLayerName.textContent = layer ? `${layer.label} · ${layer.cn}` : "格点预报";
  pointValue.textContent = formatValue(current);
  pointUnit.textContent = layer?.unit || "";
  pointValidTime.textContent = activeRun
    ? `${formatForecastKey(activeRun.forecastKey)} ${displayTimeZoneLabel()} · ${activeRun.model} · ${activeLead >= 0 ? "+" : ""}${activeLead}h`
    : "暂无本地预报数据";
  paintPointChartFrame();
}

async function showPointDetails(lon, lat) {
  const generation = ++pointGeneration;
  typhoonPanel.hidden = true;
  selectedTyphoonPoint = null;
  pointSelection = { lon, lat };
  pointSeriesData = [];
  pointPanel.hidden = false;
  const layer = activeRun?.layers?.find((item) => item.id === activeLayerId)
    ?? activeRun?.layers?.[0];
  const current = activeField ? sampleField(activeField, lon, lat) : null;

  pointLocation.textContent = `经度 ${lon.toFixed(2)} · 纬度 ${lat.toFixed(2)}`;
  pointLayerName.textContent = layer ? `${layer.label} · ${layer.cn}` : "格点预报";
  pointValue.textContent = formatValue(current);
  pointUnit.textContent = layer?.unit || "";
  pointValidTime.textContent = activeRun
    ? `${formatForecastKey(activeRun.forecastKey)} ${displayTimeZoneLabel()} · ${activeRun.model} · ${activeLead >= 0 ? "+" : ""}${activeLead}h`
    : "暂无本地预报数据";
  pointRange.textContent = "同一起报多时次预报";
  pointCount.textContent = "--";
  pointFirstTime.textContent = "--";
  pointLastTime.textContent = "--";
  pointChartEmpty.hidden = false;
  pointChartEmpty.textContent = "正在读取本地预报序列";
  pointStatus.textContent = "正在从本地轻量缓存提取该格点";
  drawPointChart([]);

  if (!layer || forecastSeries.length === 0) {
    pointChartEmpty.textContent = "当前起报暂无可用的时间序列";
    pointStatus.textContent = "请先选择包含本地数据的模型与起报时间";
    return;
  }

  let completed = 0;
  const series = await mapWithConcurrency(forecastSeries, 6, async (seriesRun) => {
    const seriesLayer = seriesRun.layers?.find((item) => item.id === layer.id);
    let value = null;
    try {
      const sample = await loadSample(seriesLayer);
      value = sample ? sampleGrid(sample, seriesRun.grid, lon, lat) : null;
    } catch {
      value = null;
    }
    completed += 1;
    if (generation === pointGeneration) {
      pointStatus.textContent = `正在读取本地预报序列 · ${completed}/${forecastSeries.length}`;
    }
    return {
      id: seriesRun.id,
      leadHours: seriesRun.leadHours,
      forecastKey: seriesRun.forecastKey,
      value,
    };
  });
  if (generation !== pointGeneration) return;

  const validCount = series.filter((item) => Number.isFinite(item.value)).length;
  pointCount.textContent = `${validCount}/${series.length} 个有效时次`;
  pointFirstTime.textContent = formatForecastKey(series[0]?.forecastKey);
  pointLastTime.textContent = formatForecastKey(series.at(-1)?.forecastKey);
  pointStatus.textContent = validCount > 0
    ? `已加载当前起报的 ${validCount} 个有效预报时次`
    : "该格点没有有效值，可尝试选择其他位置或图层";
  pointSeriesData = series;
  requestAnimationFrame(() => drawPointChart(series));
}

function prepareFieldTexture(field) {
  if (field.texture) return field.texture;
  const raster = document.createElement("canvas");
  raster.width = 1024;
  raster.height = 512;
  const rasterContext = raster.getContext("2d", { alpha: true });
  const image = rasterContext.createImageData(raster.width, raster.height);
  const palette = field.layer.palette;
  const [low, high] = field.info.range;
  const span = Math.max(1e-6, high - low);
  const sparseLayer = [
    "prectot",
    "duexttau",
    "duextt25",
    "duscatau",
    "duscat25",
    "ducmass",
    "ducmass25",
    "dusmass",
    "dusmass25",
    "duflux",
  ].includes(field.layer.id);
  const renderPalette = sparseLayer && palette.length > 2 ? palette.slice(1) : palette;

  for (let y = 0; y < raster.height; y += 1) {
    const lat = textureBounds.top
      - (y / Math.max(1, raster.height - 1)) * (textureBounds.top - textureBounds.bottom);
    for (let x = 0; x < raster.width; x += 1) {
      const lon = textureBounds.left
        + (x / Math.max(1, raster.width - 1)) * (textureBounds.right - textureBounds.left);
      const value = sampleField(field, lon, lat);
      const offset = (y * raster.width + x) * 4;
      if (value === null) {
        image.data[offset + 3] = 0;
      } else {
        const normalized = Math.max(0, Math.min(1, (value - low) / span));
        const [red, green, blue] = interpolateColor(renderPalette, normalized);
        image.data[offset] = red;
        image.data[offset + 1] = green;
        image.data[offset + 2] = blue;
        if (sparseLayer) {
          const threshold = field.layer.id === "prectot" ? 0.012 : 0.02;
          const intensity = Math.max(0, (normalized - threshold) / (1 - threshold));
          image.data[offset + 3] = normalized <= threshold
            ? 0
            : Math.round(52 + Math.sqrt(intensity) * 193);
        } else {
          image.data[offset + 3] = 238;
        }
      }
    }
  }

  rasterContext.putImageData(image, 0, 0);
  field.texture = raster;
  return raster;
}

function drawRealField(width, height, field) {
  drawEmptyField(width, height);
  const raster = prepareFieldTexture(field);
  const sourceY = ((textureBounds.top - view.top)
    / (textureBounds.top - textureBounds.bottom)) * raster.height;
  const sourceHeight = ((view.top - view.bottom)
    / (textureBounds.top - textureBounds.bottom)) * raster.height;
  context.save();
  context.globalAlpha = fieldOpacity;
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  const longitudeSpan = view.right - view.left;
  const firstTile = Math.floor((view.left - textureBounds.left) / 360);
  const lastTile = Math.floor((view.right - textureBounds.left - 1e-9) / 360);
  for (let tile = firstTile; tile <= lastTile; tile += 1) {
    const tileLeft = textureBounds.left + tile * 360;
    const tileRight = textureBounds.right + tile * 360;
    const visibleLeft = Math.max(view.left, tileLeft);
    const visibleRight = Math.min(view.right, tileRight);
    if (visibleRight <= visibleLeft) continue;
    const sourceX = ((visibleLeft - tileLeft) / 360) * raster.width;
    const sourceWidth = ((visibleRight - visibleLeft) / 360) * raster.width;
    const destinationX = ((visibleLeft - view.left) / longitudeSpan) * width;
    const destinationWidth = ((visibleRight - visibleLeft) / longitudeSpan) * width;
    context.drawImage(
      raster,
      sourceX,
      sourceY,
      sourceWidth,
      sourceHeight,
      destinationX,
      0,
      destinationWidth,
      height,
    );
  }
  context.restore();
}

function drawGrid(width, height) {
  if (!showGrid) return;
  context.save();
  context.lineWidth = Math.max(1, window.devicePixelRatio);
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(181, 222, 229, 0.15)"
    : "rgba(29, 73, 84, 0.18)";
  context.fillStyle = mapTheme === "dark"
    ? "rgba(218, 238, 241, 0.74)"
    : "rgba(15, 61, 74, 0.76)";
  context.font = `${12 * window.devicePixelRatio}px "Segoe UI Variable"`;

  const lonSpan = view.right - view.left;
  const latSpan = view.top - view.bottom;
  const lonStep = lonSpan <= 30 ? 5 : lonSpan <= 80 ? 10 : lonSpan <= 180 ? 20 : 30;
  const latStep = latSpan <= 24 ? 5 : latSpan <= 70 ? 10 : 20;
  const edgeInset = 7 * window.devicePixelRatio;
  context.textBaseline = "top";
  for (let lon = Math.ceil(view.left / lonStep) * lonStep; lon <= view.right; lon += lonStep) {
    const [x] = project(lon, view.bottom, width, height);
    context.beginPath();
    context.moveTo(x, 0);
    context.lineTo(x, height);
    context.stroke();
    const displayLon = wrapLongitude(lon);
    const suffix = displayLon === 0 ? "" : displayLon < 0 ? "W" : "E";
    const label = `${Math.abs(displayLon)}°${suffix}`;
    const labelWidth = context.measureText(label).width;
    context.fillText(
      label,
      clamp(x - labelWidth / 2, edgeInset, width - labelWidth - edgeInset),
      edgeInset,
    );
  }
  context.textBaseline = "middle";
  for (let lat = Math.ceil(view.bottom / latStep) * latStep; lat <= view.top; lat += latStep) {
    const [, y] = project(view.left, lat, width, height);
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(width, y);
    context.stroke();
    const suffix = lat === 0 ? "" : lat < 0 ? "S" : "N";
    const label = `${Math.abs(lat)}°${suffix}`;
    context.fillText(label, edgeInset, clamp(y, edgeInset, height - edgeInset));
  }
  context.restore();
}

function traceWrappedLine(line, width, height, longitudeOffset) {
  let started = false;
  context.beginPath();
  for (const point of line) {
    const [x, y] = project(point[0] + longitudeOffset, point[1], width, height);
    if (!started) {
      context.moveTo(x, y);
      started = true;
    } else {
      context.lineTo(x, y);
    }
  }
}

function drawWrappedLines(lines, width, height, firstCopy, lastCopy) {
  for (let copy = firstCopy; copy <= lastCopy; copy += 1) {
    const longitudeOffset = copy * 360;
    for (const line of lines) {
      traceWrappedLine(line, width, height, longitudeOffset);
      context.stroke();
    }
  }
}

function drawHydrology(width, height, firstCopy, lastCopy) {
  const longitudeSpan = view.right - view.left;
  if (longitudeSpan > 240) return;

  context.save();
  context.lineJoin = "round";
  context.lineCap = "round";
  context.fillStyle = mapTheme === "dark"
    ? "rgba(91, 185, 207, 0.22)"
    : "rgba(119, 207, 224, 0.28)";
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(132, 211, 229, 0.58)"
    : "rgba(35, 125, 157, 0.46)";
  context.lineWidth = 0.8 * window.devicePixelRatio;
  for (let copy = firstCopy; copy <= lastCopy; copy += 1) {
    const longitudeOffset = copy * 360;
    for (const polygon of lakes) {
      traceWrappedLine(polygon, width, height, longitudeOffset);
      context.closePath();
      context.fill();
      context.stroke();
    }
  }
  if (longitudeSpan <= 180) {
    context.strokeStyle = mapTheme === "dark"
      ? "rgba(133, 214, 231, 0.62)"
      : "rgba(35, 126, 158, 0.52)";
    context.lineWidth = 0.72 * window.devicePixelRatio;
    drawWrappedLines(rivers, width, height, firstCopy, lastCopy);
  }
  context.restore();
}

function drawCoastlines(width, height) {
  const firstCopy = Math.floor((view.left + 180) / 360) - 1;
  const lastCopy = Math.floor((view.right + 180) / 360) + 1;
  const longitudeSpan = view.right - view.left;
  drawHydrology(width, height, firstCopy, lastCopy);

  context.save();
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(216, 237, 240, 0.74)"
    : "rgba(16, 55, 66, 0.78)";
  context.lineWidth = 1.1 * window.devicePixelRatio;
  context.lineJoin = "round";
  context.lineCap = "round";
  drawWrappedLines(coastlines, width, height, firstCopy, lastCopy);

  context.strokeStyle = mapTheme === "dark"
    ? "rgba(218, 237, 240, 0.58)"
    : "rgba(18, 58, 68, 0.62)";
  context.lineWidth = (longitudeSpan > 240 ? 0.58 : 0.82) * window.devicePixelRatio;
  drawWrappedLines(countryBorders, width, height, firstCopy, lastCopy);

  if (longitudeSpan <= 110) {
    context.strokeStyle = mapTheme === "dark"
      ? "rgba(197, 228, 233, 0.42)"
      : "rgba(30, 75, 84, 0.42)";
    context.lineWidth = 0.62 * window.devicePixelRatio;
    context.setLineDash([2.2 * window.devicePixelRatio, 2.8 * window.devicePixelRatio]);
    drawWrappedLines(provinceBorders, width, height, firstCopy, lastCopy);
    context.setLineDash([]);
  }

  context.fillStyle = mapTheme === "dark"
    ? "rgba(231, 243, 245, 0.90)"
    : "rgba(13, 57, 70, 0.88)";
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(8, 31, 40, 0.88)"
    : "rgba(238, 247, 245, 0.88)";
  context.lineWidth = 3 * window.devicePixelRatio;
  context.lineJoin = "round";
  context.font = `600 ${12 * window.devicePixelRatio}px "Segoe UI Variable"`;
  context.textBaseline = "alphabetic";
  if (!showPlaces) {
    context.restore();
    return;
  }
  const occupied = [];
  const rankLimit = longitudeSpan > 90 ? 1 : 2;
  const candidates = places
    .flatMap((item) => {
      const copies = [];
      for (let copy = firstCopy; copy <= lastCopy; copy += 1) {
        copies.push({ ...item, displayLon: item.lon + copy * 360 });
      }
      return copies;
    })
    .filter((item) =>
      item.displayLon >= view.left &&
      item.displayLon <= view.right &&
      item.lat >= view.bottom &&
      item.lat <= view.top &&
      item.rank <= rankLimit)
    .sort((first, second) => first.rank - second.rank);
  for (const place of candidates) {
    const [x, y] = project(place.displayLon, place.lat, width, height);
    const labelX = x + 5 * window.devicePixelRatio;
    const labelY = y - 5 * window.devicePixelRatio;
    const labelWidth = context.measureText(place.name).width;
    const box = {
      left: labelX - 3,
      top: labelY - 14 * window.devicePixelRatio,
      right: labelX + labelWidth + 3,
      bottom: labelY + 3,
    };
    const overlaps = occupied.some((item) =>
      box.left < item.right &&
      box.right > item.left &&
      box.top < item.bottom &&
      box.bottom > item.top);
    if (overlaps) continue;
    occupied.push(box);
    context.strokeText(place.name, labelX, labelY);
    context.fillText(place.name, labelX, labelY);
  }
  context.restore();
}

function traceCoordinateLine(points, width, height, longitudeOffset = 0) {
  context.beginPath();
  points.forEach((point, index) => {
    const [x, y] = project(point[0] + longitudeOffset, point[1], width, height);
    if (index === 0) context.moveTo(x, y);
    else context.lineTo(x, y);
  });
}

function traceSmoothCoordinateLine(points, width, height, longitudeOffset = 0) {
  if (points.length < 2) return;
  const projected = points.map((point) =>
    project(point[0] + longitudeOffset, point[1], width, height));
  context.beginPath();
  context.moveTo(projected[0][0], projected[0][1]);
  if (projected.length === 2) {
    context.lineTo(projected[1][0], projected[1][1]);
    return;
  }
  // A cardinal spline passes through every model centre while rounding the
  // right-angle steps introduced by the lower-resolution analysis grid.
  const tension = 0.72;
  for (let index = 0; index < projected.length - 1; index += 1) {
    const first = projected[Math.max(0, index - 1)];
    const start = projected[index];
    const end = projected[index + 1];
    const last = projected[Math.min(projected.length - 1, index + 2)];
    const firstControl = [
      start[0] + (end[0] - first[0]) * tension / 6,
      start[1] + (end[1] - first[1]) * tension / 6,
    ];
    const secondControl = [
      end[0] - (last[0] - start[0]) * tension / 6,
      end[1] - (last[1] - start[1]) * tension / 6,
    ];
    context.bezierCurveTo(
      firstControl[0],
      firstControl[1],
      secondControl[0],
      secondControl[1],
      end[0],
      end[1],
    );
  }
}

function drawRoundedLabel(text, x, y, color, width, height) {
  const ratio = window.devicePixelRatio || 1;
  context.save();
  context.font = `650 ${11 * ratio}px "Segoe UI Variable", "Microsoft YaHei UI"`;
  const paddingX = 7 * ratio;
  const labelHeight = 23 * ratio;
  const labelWidth = context.measureText(text).width + paddingX * 2;
  const left = clamp(x - labelWidth / 2, 5 * ratio, width - labelWidth - 5 * ratio);
  const top = clamp(y - labelHeight / 2, 5 * ratio, height - labelHeight - 5 * ratio);
  context.beginPath();
  context.roundRect(left, top, labelWidth, labelHeight, 7 * ratio);
  context.fillStyle = mapTheme === "dark"
    ? "rgba(18, 39, 47, 0.90)"
    : "rgba(249, 253, 253, 0.91)";
  context.fill();
  context.strokeStyle = color;
  context.lineWidth = ratio;
  context.stroke();
  context.fillStyle = mapTheme === "dark" ? "#effafa" : "#24444d";
  context.textAlign = "center";
  context.textBaseline = "middle";
  context.fillText(text, left + labelWidth / 2, top + labelHeight / 2);
  context.restore();
}

function drawGuardLines(width, height, firstCopy, lastCopy) {
  const ratio = window.devicePixelRatio || 1;
  const guardColor = mapTheme === "dark" ? "#f8d66d" : "#d9a51c";
  const haloColor = mapTheme === "dark"
    ? "rgba(20, 34, 39, 0.70)"
    : "rgba(255, 255, 255, 0.82)";
  context.save();
  context.lineJoin = "round";
  context.lineCap = "round";
  for (let copy = firstCopy; copy <= lastCopy; copy += 1) {
    const offset = copy * 360;
    for (const [points, dashed] of [[GUARD_LINE_24, false], [GUARD_LINE_48, true]]) {
      context.setLineDash(dashed ? [7 * ratio, 6 * ratio] : []);
      traceCoordinateLine(points, width, height, offset);
      context.strokeStyle = haloColor;
      context.lineWidth = 4.4 * ratio;
      context.stroke();
      traceCoordinateLine(points, width, height, offset);
      context.strokeStyle = guardColor;
      context.lineWidth = 1.7 * ratio;
      context.stroke();
    }
    const label24 = project(126.993568 + offset, 29.5, width, height);
    const label48 = project(131.981361 + offset, 27.5, width, height);
    if (label24[0] > -120 * ratio && label24[0] < width + 120 * ratio) {
      drawRoundedLabel("24 小时警戒线", label24[0] - 66 * ratio, label24[1], guardColor, width, height);
    }
    if (label48[0] > -120 * ratio && label48[0] < width + 120 * ratio) {
      drawRoundedLabel("48 小时警戒线", label48[0] + 66 * ratio, label48[1], guardColor, width, height);
    }
  }
  context.restore();
}

function drawTrackPath(points, width, height, longitudeOffset, style) {
  if (points.length < 2) return;
  const ratio = window.devicePixelRatio || 1;
  context.save();
  context.lineJoin = "round";
  context.lineCap = "round";
  context.setLineDash(style.dash.map((value) => value * ratio));
  traceSmoothCoordinateLine(
    points.map((point) => [point.lon, point.lat]),
    width,
    height,
    longitudeOffset,
  );
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(5, 23, 30, 0.76)"
    : "rgba(255, 255, 255, 0.86)";
  context.lineWidth = 7 * ratio;
  context.stroke();
  traceSmoothCoordinateLine(
    points.map((point) => [point.lon, point.lat]),
    width,
    height,
    longitudeOffset,
  );
  context.strokeStyle = style.color;
  context.globalAlpha = 0.90;
  context.lineWidth = 4.2 * ratio;
  context.shadowColor = style.color;
  context.shadowBlur = 7 * ratio;
  context.stroke();
  context.shadowBlur = 0;
  context.globalAlpha = 1;
  context.setLineDash([]);
  context.restore();
}

function drawTyphoonPaths(width, height) {
  typhoonHitPoints = [];
  if (!showTyphoonPaths) return;
  const firstCopy = Math.floor((view.left + 180) / 360) - 1;
  const lastCopy = Math.floor((view.right + 180) / 360) + 1;
  const ratio = window.devicePixelRatio || 1;
  const longitudeSpan = view.right - view.left;
  const pointInterval = longitudeSpan <= 40 ? 1 : longitudeSpan <= 80 ? 2 : 4;
  drawGuardLines(width, height, firstCopy, lastCopy);

  for (const track of typhoonTracks) {
    const style = TYPHOON_MODEL_STYLES[track.model]
      || { color: "#50bfd0", dash: [] };
    for (let copy = firstCopy; copy <= lastCopy; copy += 1) {
      const longitudeOffset = copy * 360;
      drawTrackPath(track.points, width, height, longitudeOffset, style);
      for (let pointIndex = 0; pointIndex < track.points.length; pointIndex += 1) {
        const point = track.points[pointIndex];
        const isActive = Math.abs(Number(point.leadHours) - activeLead) < 1.5;
        const isSelected = selectedTyphoonPoint
          && selectedTyphoonPoint.track.id === track.id
          && selectedTyphoonPoint.point.forecastKey === point.forecastKey;
        if (pointIndex % pointInterval !== 0
          && pointIndex !== track.points.length - 1
          && !isActive
          && !isSelected) {
          continue;
        }
        const [x, y] = project(point.lon + longitudeOffset, point.lat, width, height);
        if (x < -18 * ratio || x > width + 18 * ratio
          || y < -18 * ratio || y > height + 18 * ratio) {
          continue;
        }
        const radius = (isActive || isSelected ? 6.2 : 4.2) * ratio;
        context.save();
        context.beginPath();
        context.arc(x, y, radius, 0, Math.PI * 2);
        context.fillStyle = point.intensity?.color || style.color;
        context.shadowColor = point.intensity?.color || style.color;
        context.shadowBlur = (isActive ? 10 : 4) * ratio;
        context.fill();
        context.shadowBlur = 0;
        context.lineWidth = (isActive || isSelected ? 2.5 : 1.7) * ratio;
        context.strokeStyle = mapTheme === "dark" ? "#edfafa" : "#ffffff";
        context.stroke();
        if (isActive || isSelected) {
          context.beginPath();
          context.arc(x, y, 9.5 * ratio, 0, Math.PI * 2);
          context.strokeStyle = style.color;
          context.globalAlpha = 0.75;
          context.lineWidth = 1.4 * ratio;
          context.stroke();
        }
        context.restore();
        typhoonHitPoints.push({ x, y, point, track });
      }
      const lastPoint = track.points.at(-1);
      if (lastPoint && longitudeSpan <= 90) {
        const [labelX, labelY] = project(
          lastPoint.lon + longitudeOffset,
          lastPoint.lat,
          width,
          height,
        );
        if (labelX > -100 * ratio && labelX < width + 100 * ratio
          && labelY > -40 * ratio && labelY < height + 40 * ratio) {
          drawRoundedLabel(
            track.name,
            labelX + 12 * ratio,
            labelY - 19 * ratio,
            style.color,
            width,
            height,
          );
        }
      }
    }
  }
}

function findTyphoonHit(offsetX, offsetY) {
  if (!showTyphoonPaths || typhoonHitPoints.length === 0) return null;
  const ratio = canvas.width / Math.max(1, canvas.clientWidth);
  const x = offsetX * ratio;
  const y = offsetY * ratio;
  let closest = null;
  let distance = 15 * ratio;
  for (const item of typhoonHitPoints) {
    const candidate = Math.hypot(item.x - x, item.y - y);
    if (candidate < distance) {
      closest = item;
      distance = candidate;
    }
  }
  return closest;
}

function formatCoordinate(value, positive, negative) {
  const suffix = value < 0 ? negative : positive;
  return `${Math.abs(value).toFixed(2)}°${suffix}`;
}

function showTyphoonDetails(hit) {
  if (!hit) return;
  selectedTyphoonPoint = hit;
  pointGeneration += 1;
  pointPanel.hidden = true;
  pointSelection = null;
  pointSeriesData = [];
  const { point, track } = hit;
  typhoonPanel.hidden = false;
  typhoonModel.textContent = `${track.model} · 起报 ${formatForecastKey(track.initKey)} ${displayTimeZoneLabel()}`;
  typhoonName.textContent = track.name;
  typhoonStrength.textContent = point.intensity?.label || "热带气旋";
  typhoonStrengthDot.style.background = point.intensity?.color || "#63d6c7";
  typhoonWind.textContent = `${point.windSpeed.toFixed(1)} m/s · ${point.intensity?.code || "TC"}`;
  typhoonConfidence.textContent = `${track.confidence}%`;
  typhoonTime.textContent = `${formatForecastKey(point.forecastKey)} ${displayTimeZoneLabel()}`;
  typhoonLead.textContent = `+${point.leadHours}h`;
  typhoonPressure.textContent = `${point.pressure.toFixed(1)} hPa`;
  typhoonLocation.textContent =
    `${formatCoordinate(point.lon, "E", "W")} · ${formatCoordinate(point.lat, "N", "S")}`;
  requestRender();
}

function drawPointMarker(width, height) {
  if (!pointSelection) return;
  const viewCenter = (view.left + view.right) / 2;
  const displayLon = pointSelection.lon
    + Math.round((viewCenter - pointSelection.lon) / 360) * 360;
  const [x, y] = project(displayLon, pointSelection.lat, width, height);
  if (x < -20 || y < -20 || x > width + 20 || y > height + 20) return;
  context.save();
  context.beginPath();
  context.arc(x, y, 7 * window.devicePixelRatio, 0, Math.PI * 2);
  context.fillStyle = "#e43b32";
  context.fill();
  context.lineWidth = 3 * window.devicePixelRatio;
  context.strokeStyle = "#ffffff";
  context.stroke();
  context.restore();
}

async function loadField(run, layer) {
  const cacheKey = layer.fieldUrl;
  if (fieldCache.has(cacheKey)) return fieldCache.get(cacheKey);
  const response = await fetch(cacheKey, { cache: "force-cache" });
  if (!response.ok) throw new Error(`栅格缓存读取失败：HTTP ${response.status}`);
  const buffer = await response.arrayBuffer();
  const values = new Uint16Array(buffer);
  const expected = layer.fieldInfo.rows * layer.fieldInfo.cols;
  if (values.length !== expected) {
    throw new Error(`栅格尺寸不匹配：期望 ${expected}，实际 ${values.length}`);
  }
  const field = { values, info: layer.fieldInfo, grid: run.grid, layer };
  fieldCache.set(cacheKey, field);
  while (fieldCache.size > 6) fieldCache.delete(fieldCache.keys().next().value);
  return field;
}

async function loadWindField(run) {
  const layer = run?.layers?.find((item) => item.id === "wind10" && item.vector);
  if (!layer?.vector) return null;
  const cacheKey = `${layer.vector.uUrl}|${layer.vector.vUrl}`;
  if (fieldCache.has(cacheKey)) return fieldCache.get(cacheKey);
  const [uResponse, vResponse] = await Promise.all([
    fetch(layer.vector.uUrl, { cache: "force-cache" }),
    fetch(layer.vector.vUrl, { cache: "force-cache" }),
  ]);
  if (!uResponse.ok || !vResponse.ok) {
    throw new Error(`风场缓存读取失败：HTTP ${uResponse.status}/${vResponse.status}`);
  }
  const [uBuffer, vBuffer] = await Promise.all([
    uResponse.arrayBuffer(),
    vResponse.arrayBuffer(),
  ]);
  const uValues = new Uint16Array(uBuffer);
  const vValues = new Uint16Array(vBuffer);
  const info = layer.vector.fieldInfo;
  const expected = info.rows * info.cols;
  if (uValues.length !== expected || vValues.length !== expected) {
    throw new Error("风场矢量尺寸不匹配");
  }
  const field = { uValues, vValues, info, grid: run.grid, layer, runId: run.id };
  fieldCache.set(cacheKey, field);
  while (fieldCache.size > 8) fieldCache.delete(fieldCache.keys().next().value);
  return field;
}

function scheduleFramePrefetch(run, layerId) {
  const generation = ++prefetchGeneration;
  if (!run) return;

  const prefetch = async () => {
    if (generation !== prefetchGeneration) return;
    const layer = run.layers?.find((item) => item.id === layerId) ?? run.layers?.[0];
    if (!layer) return;
    try {
      const field = await loadField(run, layer);
      if (generation !== prefetchGeneration) return;
      prepareFieldTexture(field);
      if (showWindAnimation) {
        await loadWindField(run);
      }
    } catch {
      // Prefetch is opportunistic; the normal render path reports real failures.
    }
  };

  if ("requestIdleCallback" in window) {
    window.requestIdleCallback(() => void prefetch(), { timeout: 240 });
  } else {
    window.setTimeout(() => void prefetch(), 48);
  }
}

function resizeWindCanvas() {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.max(1, Math.round(windCanvas.clientWidth * ratio));
  const height = Math.max(1, Math.round(windCanvas.clientHeight * ratio));
  if (windCanvas.width !== width || windCanvas.height !== height) {
    windCanvas.width = width;
    windCanvas.height = height;
    windParticles = [];
  }
}

function resetWindParticle(particle, randomAge = true) {
  particle.lon = view.left + Math.random() * (view.right - view.left);
  particle.lat = view.bottom + Math.random() * (view.top - view.bottom);
  particle.age = randomAge ? Math.floor(Math.random() * 40) : 0;
  particle.maxAge = 28 + Math.floor(Math.random() * 28);
}

function stopWindAnimation(clear = true) {
  if (windFrame) cancelAnimationFrame(windFrame);
  windFrame = 0;
  windLastFrame = 0;
  if (clear) windContext.clearRect(0, 0, windCanvas.width, windCanvas.height);
}

function animateWind(timestamp) {
  if (!showWindAnimation || !windField) {
    stopWindAnimation();
    return;
  }
  windFrame = requestAnimationFrame(animateWind);
  if (timestamp - windLastFrame < 32) return;
  windLastFrame = timestamp;
  resizeWindCanvas();
  const width = windCanvas.width;
  const height = windCanvas.height;
  const targetCount = Math.min(1550, Math.max(480, Math.round((width * height) / 2700)));
  while (windParticles.length < targetCount) {
    const particle = {};
    resetWindParticle(particle);
    windParticles.push(particle);
  }
  if (windParticles.length > targetCount) windParticles.length = targetCount;

  windContext.globalCompositeOperation = "destination-in";
  windContext.fillStyle = "rgba(0, 0, 0, 0.80)";
  windContext.fillRect(0, 0, width, height);
  windContext.globalCompositeOperation = "source-over";
  windContext.lineCap = "round";
  windContext.lineWidth = Math.max(1.25, (window.devicePixelRatio || 1) * 1.12);

  const lonSpan = view.right - view.left;
  const latSpan = view.top - view.bottom;
  for (const particle of windParticles) {
    if (particle.age++ > particle.maxAge) {
      resetWindParticle(particle, false);
      continue;
    }
    const vector = sampleVector(windField, wrapLongitude(particle.lon), particle.lat);
    if (!vector) {
      resetWindParticle(particle);
      continue;
    }
    const speed = Math.hypot(vector.u, vector.v);
    const scale = 0.016 * Math.max(0.7, Math.min(2.4, 100 / lonSpan));
    const nextLon = particle.lon + vector.u * scale;
    const nextLat = particle.lat + vector.v * scale;
    if (
      nextLon < view.left || nextLon > view.right ||
      nextLat < view.bottom || nextLat > view.top
    ) {
      resetWindParticle(particle);
      continue;
    }
    const x1 = ((particle.lon - view.left) / lonSpan) * width;
    const y1 = ((view.top - particle.lat) / latSpan) * height;
    const x2 = ((nextLon - view.left) / lonSpan) * width;
    const y2 = ((view.top - nextLat) / latSpan) * height;
    const alpha = 0.34 + Math.min(0.54, speed / 38);
    windContext.strokeStyle = `rgba(243, 255, 255, ${alpha})`;
    windContext.beginPath();
    windContext.moveTo(x1, y1);
    windContext.lineTo(x2, y2);
    windContext.stroke();
    particle.lon = nextLon;
    particle.lat = nextLat;
  }
}

async function refreshWindAnimation(generation) {
  if (!showWindAnimation || !activeRun) {
    stopWindAnimation();
    windField = null;
    return;
  }
  if (windField?.runId === activeRun.id) {
    if (!windFrame) windFrame = requestAnimationFrame(animateWind);
    return;
  }
  stopWindAnimation();
  windParticles = [];
  try {
    const field = await loadWindField(activeRun);
    if (generation !== renderGeneration) return;
    windField = field;
    if (field) windFrame = requestAnimationFrame(animateWind);
  } catch {
    windField = null;
  }
}

async function render() {
  const generation = ++renderGeneration;
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const clientWidth = Math.max(1, canvas.clientWidth);
  const clientHeight = Math.max(1, canvas.clientHeight);
  resizeViewForViewport(clientWidth, clientHeight);
  const width = Math.round(clientWidth * ratio);
  const height = Math.round(clientHeight * ratio);
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }
  resizeWindCanvas();

  const layer = activeRun?.layers?.find((item) => item.id === activeLayerId)
    ?? activeRun?.layers?.[0];
  try {
    if (activeRun && layer) {
      const field = await loadField(activeRun, layer);
      if (generation !== renderGeneration) return;
      activeField = field;
      activeLayerId = layer.id;
      drawRealField(width, height, field);
    } else {
      activeField = null;
      drawEmptyField(width, height);
    }
    drawGrid(width, height);
    drawCoastlines(width, height);
    drawTyphoonPaths(width, height);
    drawPointMarker(width, height);
    void refreshWindAnimation(generation);
  } catch (error) {
    activeField = null;
    drawEmptyField(width, height);
    drawGrid(width, height);
    drawCoastlines(width, height);
    drawTyphoonPaths(width, height);
    drawPointMarker(width, height);
    void refreshWindAnimation(generation);
    window.chrome?.webview?.postMessage({ type: "map-error", message: String(error.message || error) });
  }
}

function requestRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    void render();
  });
}

async function loadAssets() {
  const [
    coastResponse,
    countryResponse,
    provinceResponse,
    riverResponse,
    lakeResponse,
    placesResponse,
  ] = await Promise.all([
    fetch("./assets/coastlines-110m.json"),
    fetch("./assets/countries-110m.json"),
    fetch("./assets/china-provinces-50m.json"),
    fetch("./assets/rivers-50m.json"),
    fetch("./assets/lakes-50m.json"),
    fetch("./assets/places-50m.json"),
  ]);
  coastlines = (await coastResponse.json()).lines || [];
  countryBorders = (await countryResponse.json()).lines || [];
  provinceBorders = (await provinceResponse.json()).lines || [];
  rivers = (await riverResponse.json()).lines || [];
  lakes = (await lakeResponse.json()).polygons || [];
  places = (await placesResponse.json()).places || [];
}

canvas.addEventListener("pointerdown", (event) => {
  canvas.focus({ preventScroll: true });
  canvas.setPointerCapture(event.pointerId);
  dragState = {
    x: event.clientX,
    y: event.clientY,
    moved: false,
    view: { ...view },
  };
});

canvas.addEventListener("pointerup", (event) => {
  suppressMapClick = Boolean(dragState?.moved);
  if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
  dragState = null;
});

canvas.addEventListener("pointercancel", () => {
  dragState = null;
});

canvas.addEventListener("pointermove", (event) => {
  if (dragState) {
    if (Math.hypot(event.clientX - dragState.x, event.clientY - dragState.y) > 4) {
      dragState.moved = true;
    }
  }
  if (dragState?.moved) {
    const lonPerPixel = (dragState.view.right - dragState.view.left) / canvas.clientWidth;
    const latPerPixel = (dragState.view.top - dragState.view.bottom) / canvas.clientHeight;
    const deltaLon = (event.clientX - dragState.x) * lonPerPixel;
    const deltaLat = (event.clientY - dragState.y) * latPerPixel;
    setConstrainedView({
      left: dragState.view.left - deltaLon,
      right: dragState.view.right - deltaLon,
      top: dragState.view.top + deltaLat,
      bottom: dragState.view.bottom + deltaLat,
    });
    requestRender();
  }

  const [lon, lat] = unproject(event.offsetX, event.offsetY);
  const value = activeField ? sampleField(activeField, lon, lat) : null;
  const valueText = value === null
    ? ""
    : `　${value.toFixed(2)} ${activeField.layer.unit || ""}`.trimEnd();
  coordinateLabel.textContent =
    `经度 ${lon.toFixed(2)}　纬度 ${lat.toFixed(2)}${valueText}`;

  const typhoonHit = dragState?.moved ? null : findTyphoonHit(event.offsetX, event.offsetY);
  canvas.style.cursor = typhoonHit ? "pointer" : dragState ? "grabbing" : "grab";
  if (typhoonHit) {
    const { point, track } = typhoonHit;
    typhoonHover.hidden = false;
    typhoonHover.style.left = `${event.offsetX}px`;
    typhoonHover.style.top = `${event.offsetY}px`;
    typhoonHover.innerHTML =
      `<strong>${track.name} · ${point.intensity?.label || "热带气旋"}</strong>`
      + `${formatForecastKey(point.forecastKey)} ${displayTimeZoneLabel()} · `
      + `${point.pressure.toFixed(1)} hPa · ${point.windSpeed.toFixed(1)} m/s`;
  } else {
    typhoonHover.hidden = true;
  }
});

canvas.addEventListener("click", (event) => {
  if (suppressMapClick) {
    suppressMapClick = false;
    return;
  }
  const typhoonHit = findTyphoonHit(event.offsetX, event.offsetY);
  if (typhoonHit) {
    typhoonHover.hidden = true;
    showTyphoonDetails(typhoonHit);
    return;
  }
  const [lon, lat] = unproject(event.offsetX, event.offsetY);
  void showPointDetails(lon, lat);
  requestRender();
});

pointClose.addEventListener("click", () => {
  pointGeneration += 1;
  pointPanel.hidden = true;
  pointSelection = null;
  pointSeriesData = [];
  requestRender();
  canvas.focus();
});

typhoonClose.addEventListener("click", () => {
  typhoonPanel.hidden = true;
  selectedTyphoonPoint = null;
  requestRender();
  canvas.focus();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !typhoonPanel.hidden) {
    typhoonClose.click();
    return;
  }
  if (event.key === "Escape" && !pointPanel.hidden) {
    pointClose.click();
    return;
  }
  if (event.target !== canvas) return;

  if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    event.preventDefault();
    window.chrome?.webview?.postMessage({
      type: "step-frame",
      delta: event.key === "ArrowLeft" ? -1 : 1,
    });
  } else if (event.key === " ") {
    event.preventDefault();
    window.chrome?.webview?.postMessage({ type: "toggle-playback" });
  } else if (event.key === "Home") {
    event.preventDefault();
    Object.assign(view, worldView);
    requestRender();
  }
});

canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  const [focusLon, focusLat] = unproject(event.offsetX, event.offsetY);
  const factor = event.deltaY < 0 ? 0.82 : 1.22;
  const nextWidth = clamp((view.right - view.left) * factor, 12, 360);
  const nextHeight = clamp((view.top - view.bottom) * factor, 8, 170);
  const xRatio = event.offsetX / canvas.clientWidth;
  const yRatio = event.offsetY / canvas.clientHeight;
  const left = focusLon - nextWidth * xRatio;
  const top = focusLat + nextHeight * yRatio;
  setConstrainedView({
    left,
    right: left + nextWidth,
    top,
    bottom: top - nextHeight,
  });
  requestRender();
}, { passive: false });

window.chrome?.webview?.addEventListener("message", (event) => {
  const message = event.data;
  if (message.type === "set-data") {
    activeRun = message.run || null;
    forecastSeries = Array.isArray(message.series) ? message.series : [];
    typhoonModels = Array.isArray(message.typhoonModels) ? message.typhoonModels : [];
    activeLayerId = message.layer || activeRun?.layers?.[0]?.id || "";
    activeLead = Number(activeRun?.leadHours ?? message.lead ?? 0);
    refreshTyphoonAnalysis();
    void render().then(() => {
      scheduleFramePrefetch(message.nextRun, activeLayerId);
      if (pointSelection && !pointPanel.hidden) {
        void showPointDetails(pointSelection.lon, pointSelection.lat);
      }
    });
  } else if (message.type === "set-frame") {
    activeRun = message.run || null;
    activeLayerId = message.layer || activeLayerId || activeRun?.layers?.[0]?.id || "";
    activeLead = Number(activeRun?.leadHours ?? 0);
    void render().then(() => {
      scheduleFramePrefetch(message.nextRun, activeLayerId);
      updatePointCurrentReading();
    });
  } else if (message.type === "set-layer") {
    activeLayerId = String(message.layer || "").toLowerCase();
    void render().then(() => {
      if (pointSelection && !pointPanel.hidden) {
        void showPointDetails(pointSelection.lon, pointSelection.lat);
      }
    });
  } else if (message.type === "set-lead") {
    activeLead = Number(message.lead) || 0;
  } else if (message.type === "reset-view") {
    Object.assign(view, worldView);
    resizeViewForViewport(canvas.clientWidth, canvas.clientHeight, true);
    void render();
  } else if (message.type === "set-theme") {
    mapTheme = message.theme === "dark" ? "dark" : "light";
    document.documentElement.style.colorScheme = mapTheme;
    document.documentElement.dataset.theme = mapTheme;
    void render();
  } else if (message.type === "set-display") {
    fieldOpacity = Math.max(0.35, Math.min(1, Number(message.opacity) || 0.93));
    showGrid = message.showGrid !== false;
    showPlaces = message.showPlaces !== false;
    showWindAnimation = message.windAnimation !== false;
    const nextTyphoonVisibility = message.typhoonPaths === true;
    const typhoonVisibilityChanged = showTyphoonPaths !== nextTyphoonVisibility;
    showTyphoonPaths = nextTyphoonVisibility;
    displayUtcOffsetHours = Math.max(-12, Math.min(14, Number(message.utcOffsetHours) || 0));
    if (typhoonVisibilityChanged) {
      refreshTyphoonAnalysis();
    } else if (selectedTyphoonPoint && !typhoonPanel.hidden) {
      showTyphoonDetails(selectedTyphoonPoint);
    }
    void render();
    updatePointCurrentReading();
  }
});

window.addEventListener("resize", () => {
  stopWindAnimation();
  resizeViewForViewport(canvas.clientWidth, canvas.clientHeight);
  requestRender();
  if (!pointPanel.hidden) {
    requestAnimationFrame(() => drawPointChart(pointSeriesData));
  }
});
loadAssets()
  .catch((error) => {
    window.chrome?.webview?.postMessage({ type: "map-error", message: String(error.message || error) });
  })
  .finally(() => {
    void render();
    window.chrome?.webview?.postMessage("map-ready");
  });
