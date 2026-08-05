const canvas = document.querySelector("#map");
const context = canvas.getContext("2d", { alpha: false, desynchronized: true });
const coordinateLabel = document.querySelector("#coordinates");
const layerLabel = document.querySelector("#layerName");
const leadLabel = document.querySelector("#leadName");
const dataStatus = document.querySelector("#dataStatus");
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
const pointChartEmpty = document.querySelector("#pointChartEmpty");
const pointFirstTime = document.querySelector("#pointFirstTime");
const pointLastTime = document.querySelector("#pointLastTime");
const pointStatus = document.querySelector("#pointStatus");

const defaultView = { left: 45, right: 165, top: 72, bottom: 5 };
const view = { ...defaultView };
const fieldCache = new Map();
const sampleCache = new Map();
let coastlines = [];
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
let mapTheme = "light";
let fieldOpacity = 0.93;
let showGrid = true;
let showPlaces = true;

function project(lon, lat, width, height) {
  return [
    ((lon - view.left) / (view.right - view.left)) * width,
    ((view.top - lat) / (view.top - view.bottom)) * height,
  ];
}

function unproject(x, y) {
  return [
    view.left + (x / canvas.clientWidth) * (view.right - view.left),
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

function sampleField(field, lon, lat) {
  const { grid, info, values } = field;
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
  return match ? `${match[2]}-${match[3]} ${match[4]}:${match[5]}` : String(key || "--");
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

function drawPointChart(series) {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.max(1, Math.round(pointChart.clientWidth * ratio));
  const height = Math.max(1, Math.round(pointChart.clientHeight * ratio));
  pointChart.width = width;
  pointChart.height = height;
  const chartContext = pointChart.getContext("2d");
  chartContext.clearRect(0, 0, width, height);

  const valid = series.filter((item) => Number.isFinite(item.value));
  if (valid.length === 0) {
    pointChartEmpty.hidden = false;
    pointChartEmpty.textContent = "该格点暂无有效预报值";
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
  const baseY = padding.top + plotHeight;
  const slotWidth = plotWidth / Math.max(1, series.length);
  const barWidth = Math.max(2 * ratio, Math.min(9 * ratio, slotWidth * 0.62));

  chartContext.font = `${10 * ratio}px "Segoe UI Variable"`;
  chartContext.textAlign = "right";
  chartContext.textBaseline = "middle";
  chartContext.fillStyle = mapTheme === "dark" ? "#b8d4da" : "#c5dce1";
  chartContext.strokeStyle = "rgba(205, 230, 234, 0.15)";
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

  for (let index = 0; index < series.length; index += 1) {
    const item = series[index];
    if (!Number.isFinite(item.value)) continue;
    const normalized = (item.value - minimum) / (maximum - minimum);
    const barHeight = Math.max(2 * ratio, normalized * (plotHeight - 3 * ratio));
    const x = padding.left + slotWidth * (index + 0.5) - barWidth / 2;
    const isCurrent = item.id === activeRun?.id;
    chartContext.fillStyle = isCurrent ? "#f2b63e" : "#20b7d0";
    chartContext.fillRect(x, baseY - barHeight, barWidth, barHeight);
  }
}

async function showPointDetails(lon, lat) {
  const generation = ++pointGeneration;
  pointSelection = { lon, lat };
  pointPanel.hidden = false;
  const layer = activeRun?.layers?.find((item) => item.id === activeLayerId)
    ?? activeRun?.layers?.[0];
  const current = activeField ? sampleField(activeField, lon, lat) : null;

  pointLocation.textContent = `经度 ${lon.toFixed(2)} · 纬度 ${lat.toFixed(2)}`;
  pointLayerName.textContent = layer ? `${layer.label} · ${layer.cn}` : "格点预报";
  pointValue.textContent = formatValue(current);
  pointUnit.textContent = layer?.unit || "";
  pointValidTime.textContent = activeRun
    ? `${formatForecastKey(activeRun.forecastKey)} UTC · ${activeRun.model} · ${activeLead >= 0 ? "+" : ""}${activeLead}h`
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
    ? "曲线来自当前起报的本地抽样缓存；主数值来自完整栅格"
    : "该格点没有有效值，可尝试选择其他位置或图层";
  requestAnimationFrame(() => drawPointChart(series));
}

function drawRealField(width, height, field) {
  drawEmptyField(width, height);
  const scale = Math.max(2, Math.round(window.devicePixelRatio * 2));
  const raster = document.createElement("canvas");
  raster.width = Math.max(360, Math.round(width / scale));
  raster.height = Math.max(220, Math.round(height / scale));
  const rasterContext = raster.getContext("2d", { alpha: true });
  const image = rasterContext.createImageData(raster.width, raster.height);
  const palette = field.layer.palette;
  const [low, high] = field.info.range;
  const span = Math.max(1e-6, high - low);
  const sparseLayer = ["prectot", "duexttau"].includes(field.layer.id);
  const renderPalette = sparseLayer && palette.length > 2 ? palette.slice(1) : palette;

  for (let y = 0; y < raster.height; y += 1) {
    const lat = view.top - (y / Math.max(1, raster.height - 1)) * (view.top - view.bottom);
    for (let x = 0; x < raster.width; x += 1) {
      const lon = view.left + (x / Math.max(1, raster.width - 1)) * (view.right - view.left);
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
  context.save();
  context.globalAlpha = fieldOpacity;
  context.imageSmoothingEnabled = true;
  context.imageSmoothingQuality = "high";
  context.drawImage(raster, 0, 0, width, height);
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

  const lonStep = view.right - view.left < 45 ? 5 : 10;
  const latStep = view.top - view.bottom < 30 ? 5 : 10;
  for (let lon = Math.ceil(view.left / lonStep) * lonStep; lon <= view.right; lon += lonStep) {
    const [x] = project(lon, view.bottom, width, height);
    context.beginPath();
    context.moveTo(x, 0);
    context.lineTo(x, height);
    context.stroke();
    const suffix = lon < 0 ? "W" : "E";
    context.fillText(`${Math.abs(lon)}°${suffix}`, x + 6, 92 * window.devicePixelRatio);
  }
  for (let lat = Math.ceil(view.bottom / latStep) * latStep; lat <= view.top; lat += latStep) {
    const [, y] = project(view.left, lat, width, height);
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(width, y);
    context.stroke();
    const suffix = lat < 0 ? "S" : "N";
    context.fillText(`${Math.abs(lat)}°${suffix}`, 10 * window.devicePixelRatio, y - 7);
  }
  context.restore();
}

function drawCoastlines(width, height) {
  context.save();
  context.strokeStyle = mapTheme === "dark"
    ? "rgba(216, 237, 240, 0.74)"
    : "rgba(16, 55, 66, 0.78)";
  context.lineWidth = 1.1 * window.devicePixelRatio;
  context.lineJoin = "round";
  context.lineCap = "round";
  for (const line of coastlines) {
    let started = false;
    context.beginPath();
    for (const point of line) {
      const [x, y] = project(point[0], point[1], width, height);
      if (!started) {
        context.moveTo(x, y);
        started = true;
      } else {
        context.lineTo(x, y);
      }
    }
    context.stroke();
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
  const longitudeSpan = view.right - view.left;
  const rankLimit = longitudeSpan > 90 ? 1 : 2;
  const candidates = places
    .filter((item) =>
      item.lon >= view.left &&
      item.lon <= view.right &&
      item.lat >= view.bottom &&
      item.lat <= view.top &&
      item.rank <= rankLimit)
    .sort((first, second) => first.rank - second.rank);
  for (const place of candidates) {
    const [x, y] = project(place.lon, place.lat, width, height);
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

function drawPointMarker(width, height) {
  if (!pointSelection) return;
  const [x, y] = project(pointSelection.lon, pointSelection.lat, width, height);
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

async function render() {
  const generation = ++renderGeneration;
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const width = Math.round(canvas.clientWidth * ratio);
  const height = Math.round(canvas.clientHeight * ratio);
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }

  const layer = activeRun?.layers?.find((item) => item.id === activeLayerId)
    ?? activeRun?.layers?.[0];
  try {
    if (activeRun && layer) {
      dataStatus.textContent = "正在读取本地栅格";
      const field = await loadField(activeRun, layer);
      if (generation !== renderGeneration) return;
      activeField = field;
      activeLayerId = layer.id;
      layerLabel.textContent = layer.label;
      drawRealField(width, height, field);
      dataStatus.textContent = `${activeRun.model} · ${activeRun.version} · 本地 NetCDF`;
    } else {
      activeField = null;
      drawEmptyField(width, height);
      dataStatus.textContent = "暂无本地预报数据";
    }
    drawGrid(width, height);
    drawCoastlines(width, height);
    drawPointMarker(width, height);
  } catch (error) {
    activeField = null;
    drawEmptyField(width, height);
    drawGrid(width, height);
    drawCoastlines(width, height);
    drawPointMarker(width, height);
    dataStatus.textContent = "栅格加载失败";
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
  const [coastResponse, placesResponse] = await Promise.all([
    fetch("./assets/coastlines-110m.json"),
    fetch("./assets/places-50m.json"),
  ]);
  coastlines = (await coastResponse.json()).lines || [];
  places = (await placesResponse.json()).places || [];
}

canvas.addEventListener("pointerdown", (event) => {
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
    view.left = dragState.view.left - deltaLon;
    view.right = dragState.view.right - deltaLon;
    view.top = Math.min(90, dragState.view.top + deltaLat);
    view.bottom = Math.max(-90, dragState.view.bottom + deltaLat);
    requestRender();
  }

  const [lon, lat] = unproject(event.offsetX, event.offsetY);
  const value = activeField ? sampleField(activeField, lon, lat) : null;
  const valueText = value === null
    ? ""
    : `　${value.toFixed(2)} ${activeField.layer.unit || ""}`.trimEnd();
  coordinateLabel.textContent =
    `经度 ${lon.toFixed(2)}　纬度 ${lat.toFixed(2)}${valueText}`;
});

canvas.addEventListener("click", (event) => {
  if (suppressMapClick) {
    suppressMapClick = false;
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
  requestRender();
  canvas.focus();
});

document.addEventListener("keydown", (event) => {
  if (event.key === "Escape" && !pointPanel.hidden) {
    pointClose.click();
  }
});

canvas.addEventListener("wheel", (event) => {
  event.preventDefault();
  const [focusLon, focusLat] = unproject(event.offsetX, event.offsetY);
  const factor = event.deltaY < 0 ? 0.82 : 1.22;
  const nextWidth = Math.min(300, Math.max(12, (view.right - view.left) * factor));
  const nextHeight = Math.min(150, Math.max(8, (view.top - view.bottom) * factor));
  const xRatio = event.offsetX / canvas.clientWidth;
  const yRatio = event.offsetY / canvas.clientHeight;
  view.left = focusLon - nextWidth * xRatio;
  view.right = view.left + nextWidth;
  view.top = Math.min(90, focusLat + nextHeight * yRatio);
  view.bottom = Math.max(-90, view.top - nextHeight);
  requestRender();
}, { passive: false });

window.chrome?.webview?.addEventListener("message", (event) => {
  const message = event.data;
  if (message.type === "set-data") {
    activeRun = message.run || null;
    forecastSeries = Array.isArray(message.series) ? message.series : [];
    activeLayerId = message.layer || activeRun?.layers?.[0]?.id || "";
    activeLead = Number(activeRun?.leadHours ?? message.lead ?? 0);
    leadLabel.textContent = activeLead >= 0 ? `+${activeLead}h` : `${activeLead}h`;
    void render().then(() => {
      if (pointSelection && !pointPanel.hidden) {
        void showPointDetails(pointSelection.lon, pointSelection.lat);
      }
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
    leadLabel.textContent = activeLead >= 0 ? `+${activeLead}h` : `${activeLead}h`;
  } else if (message.type === "reset-view") {
    Object.assign(view, defaultView);
    void render();
  } else if (message.type === "set-theme") {
    mapTheme = message.theme === "dark" ? "dark" : "light";
    document.documentElement.style.colorScheme = mapTheme;
    void render();
  } else if (message.type === "set-display") {
    fieldOpacity = Math.max(0.35, Math.min(1, Number(message.opacity) || 0.93));
    showGrid = message.showGrid !== false;
    showPlaces = message.showPlaces !== false;
    void render();
  }
});

window.addEventListener("resize", () => {
  requestRender();
  if (!pointPanel.hidden) {
    requestAnimationFrame(() => drawPointChart([]));
    void showPointDetails(pointSelection.lon, pointSelection.lat);
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
