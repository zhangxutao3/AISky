(function initAISkyMapMath(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
  if (root) {
    root.AISkyMapMath = api;
  }
}(typeof globalThis !== "undefined" ? globalThis : this, () => {
  "use strict";

  const DEFAULT_LIMITS = {
    minimumLongitudeSpan: 12,
    maximumLongitudeSpan: 720,
    minimumLatitudeSpan: 8,
    maximumLatitudeSpan: 170,
    minimumLatitude: -85,
    maximumLatitude: 85,
  };

  function clamp(value, minimum, maximum) {
    return Math.max(minimum, Math.min(maximum, value));
  }

  function wrapLongitude(longitude) {
    return ((longitude + 180) % 360 + 360) % 360 - 180;
  }

  function viewportSize(width, height) {
    return {
      width: Math.max(1, Number(width) || 1),
      height: Math.max(1, Number(height) || 1),
    };
  }

  function constrainDegreesPerPixel(requested, width, height, limits = DEFAULT_LIMITS) {
    const viewport = viewportSize(width, height);
    const minimum = Math.max(
      limits.minimumLongitudeSpan / viewport.width,
      limits.minimumLatitudeSpan / viewport.height,
    );
    const maximum = Math.min(
      limits.maximumLongitudeSpan / viewport.width,
      limits.maximumLatitudeSpan / viewport.height,
    );
    // An extremely narrow or wide viewport can make the nominal span limits
    // incompatible. Preserve the geographic aspect ratio first.
    if (minimum > maximum) {
      return Math.max(Number.EPSILON, maximum);
    }
    return clamp(Number(requested) || minimum, minimum, maximum);
  }

  function viewFromCenter(
    centerLongitude,
    centerLatitude,
    requestedDegreesPerPixel,
    width,
    height,
    limits = DEFAULT_LIMITS,
  ) {
    const viewport = viewportSize(width, height);
    const degreesPerPixel = constrainDegreesPerPixel(
      requestedDegreesPerPixel,
      viewport.width,
      viewport.height,
      limits,
    );
    const longitudeSpan = degreesPerPixel * viewport.width;
    const latitudeSpan = degreesPerPixel * viewport.height;
    const minimumCenterLatitude = limits.minimumLatitude + latitudeSpan / 2;
    const maximumCenterLatitude = limits.maximumLatitude - latitudeSpan / 2;
    const safeCenterLatitude = clamp(
      centerLatitude,
      minimumCenterLatitude,
      maximumCenterLatitude,
    );
    const left = wrapLongitude(centerLongitude - longitudeSpan / 2);
    return {
      left,
      right: left + longitudeSpan,
      top: safeCenterLatitude + latitudeSpan / 2,
      bottom: safeCenterLatitude - latitudeSpan / 2,
      degreesPerPixel,
    };
  }

  function constrainCandidate(candidate, width, height, limits = DEFAULT_LIMITS) {
    const viewport = viewportSize(width, height);
    const longitudeSpan = Math.max(
      Number.EPSILON,
      Number(candidate.right) - Number(candidate.left),
    );
    const latitudeSpan = Math.max(
      Number.EPSILON,
      Number(candidate.top) - Number(candidate.bottom),
    );
    // "Cover" the viewport without stretching: crop the excess geographic
    // dimension instead of assigning a different horizontal and vertical scale.
    const requestedDegreesPerPixel = Math.min(
      longitudeSpan / viewport.width,
      latitudeSpan / viewport.height,
    );
    return viewFromCenter(
      (Number(candidate.left) + Number(candidate.right)) / 2,
      (Number(candidate.top) + Number(candidate.bottom)) / 2,
      requestedDegreesPerPixel,
      viewport.width,
      viewport.height,
      limits,
    );
  }

  function resizeView(view, previousWidth, previousHeight, width, height, limits = DEFAULT_LIMITS) {
    const previous = viewportSize(previousWidth || width, previousHeight || height);
    const longitudeSpan = Number(view.right) - Number(view.left);
    const latitudeSpan = Number(view.top) - Number(view.bottom);
    const degreesPerPixel = Math.min(
      longitudeSpan / previous.width,
      latitudeSpan / previous.height,
    );
    return viewFromCenter(
      (Number(view.left) + Number(view.right)) / 2,
      (Number(view.top) + Number(view.bottom)) / 2,
      degreesPerPixel,
      width,
      height,
      limits,
    );
  }

  function fitBounds(bounds, width, height, limits = DEFAULT_LIMITS) {
    const viewport = viewportSize(width, height);
    const degreesPerPixel = Math.min(
      (Number(bounds.right) - Number(bounds.left)) / viewport.width,
      (Number(bounds.top) - Number(bounds.bottom)) / viewport.height,
    );
    return viewFromCenter(
      (Number(bounds.left) + Number(bounds.right)) / 2,
      (Number(bounds.top) + Number(bounds.bottom)) / 2,
      degreesPerPixel,
      viewport.width,
      viewport.height,
      limits,
    );
  }

  function boundedCanvasRatio(
    width,
    height,
    devicePixelRatio,
    pixelBudget,
    maximumRatio,
    minimumRatio,
  ) {
    const viewport = viewportSize(width, height);
    const budgetRatio = Math.sqrt(
      Math.max(1, Number(pixelBudget) || 1) / (viewport.width * viewport.height),
    );
    return clamp(
      Math.min(Number(devicePixelRatio) || 1, maximumRatio, budgetRatio),
      minimumRatio,
      maximumRatio,
    );
  }

  return {
    DEFAULT_LIMITS,
    boundedCanvasRatio,
    constrainCandidate,
    constrainDegreesPerPixel,
    fitBounds,
    resizeView,
    viewFromCenter,
    wrapLongitude,
  };
}));
