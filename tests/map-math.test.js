"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const mapMath = require(path.resolve(
  __dirname,
  "../AISky.Desktop/MapHost/map-math.js",
));

function assertUndistorted(view, width, height, message) {
  const longitudeDegreesPerPixel = (view.right - view.left) / width;
  const latitudeDegreesPerPixel = (view.top - view.bottom) / height;
  assert.ok(
    Math.abs(longitudeDegreesPerPixel - latitudeDegreesPerPixel) < 1e-12,
    message,
  );
}

const initialView = { left: 45, right: 165, top: 72, bottom: 5 };
const fittedInitial = mapMath.constrainCandidate(initialView, 1423, 800);
assertUndistorted(fittedInitial, 1423, 800, "initial view must use one geographic scale");

const maximized = mapMath.resizeView(fittedInitial, 1423, 800, 3440, 1440);
assertUndistorted(maximized, 3440, 1440, "maximized view must not stretch horizontally");
assert.ok(maximized.top <= 85 && maximized.bottom >= -85);
assert.ok(maximized.right - maximized.left <= 720);

const restored = mapMath.resizeView(maximized, 3440, 1440, 1423, 800);
assertUndistorted(restored, 1423, 800, "restored view must retain the same aspect scale");

const world = mapMath.fitBounds(
  { left: -180, right: 180, top: 85, bottom: -85 },
  3440,
  1440,
);
assertUndistorted(world, 3440, 1440, "global view must be cropped, never distorted");
assert.ok(world.right - world.left <= 360 + 1e-9);
assert.ok(world.top - world.bottom <= 170 + 1e-9);
assert.ok(
  Math.abs((world.right - world.left) - 360) < 1e-9
    || Math.abs((world.top - world.bottom) - 170) < 1e-9,
  "global cover mode should retain one complete world dimension",
);

const mapRatio = mapMath.boundedCanvasRatio(
  3440,
  1440,
  2,
  9_000_000,
  1.5,
  0.7,
);
assert.ok(mapRatio <= 1.5);
assert.ok(3440 * 1440 * mapRatio ** 2 <= 9_000_001);

const windRatio = mapMath.boundedCanvasRatio(
  3440,
  1440,
  2,
  2_600_000,
  1,
  0.6,
);
assert.ok(windRatio <= 1);
assert.ok(3440 * 1440 * windRatio ** 2 <= 2_600_001);

console.log("map math tests passed");
