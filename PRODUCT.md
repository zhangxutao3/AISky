# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

Primary user: atmospheric-science and forecast-product developers who inspect their own AISky numerical prediction products on a Windows workstation. They repeatedly compare forecast hours, scan spatial structures, inspect exact grid values, and need long time series without leaving the map.

## Product Purpose

AISky Desktop is a local Windows weather-forecast visualization workstation. It turns local or remotely synchronized NetCDF products into a responsive interactive map, forecast timeline, layer browser, exact grid-point readout, and same-initialization time series.

## Positioning

The product combines a native Windows application shell, a local/offline map renderer, isolated NetCDF processing, and the user's own AISky-Energy and AISky-SDS forecast products. It is not a general public weather app or a wrapper around the reference website.

## Operating Context

The app is used for long desktop sessions with mouse and keyboard, often maximized on a scientific workstation. Forecast initialization and valid times are UTC. A complete run contains 120 files from +3h through +360h at three-hour intervals. The application may remain in the notification area to synchronize and maintain its cache.

## Capabilities and Constraints

- Preserve AISky-Energy and AISky-SDS model selection, initialization selection, forecast-hour selection, product-layer selection, map navigation, color scale, timeline playback, grid-point drawer, local NetCDF import, remote synchronization, settings, updates, tray behavior, and single-instance activation.
- Model comparison is intentionally excluded.
- Remote data comes from public direct links or password-protected CSTCloud share pages and is stored locally only after validation.
- The map must remain responsive at maximized desktop sizes and degrade gracefully at smaller window sizes.
- Light, dark, high-contrast, keyboard, mouse, and reduced-motion behavior must remain viable.

## Brand Commitments

The product name is AISky 智慧气象桌面平台. The cloud-and-sky identity may remain recognizable, but the interface should feel like a professional forecast operations instrument rather than a consumer weather card collection.

## Evidence on Hand

- Existing WinUI application and local canvas map renderer under `AISky.Desktop/`.
- User-provided reference screenshots of the AISky web platform.
- Real local AISky-SDS NetCDF-derived cache currently available on the workstation.
- No verified customer claims, benchmarks, or third-party brand assets may be invented.

## Product Principles

1. The forecast field is always the visual primary content.
2. Time, model, layer, and data provenance must remain continuously legible.
3. High-frequency operations stay one click away; maintenance actions remain progressive.
4. Motion explains flow, time, and spatial continuity without competing with meteorological data.
5. Local-first reliability and honest error states outrank decorative novelty.

## Accessibility & Inclusion

Core workflows must remain keyboard reachable and expose automation names. Color scales cannot be the only carrier of state. Reduced-motion users must be able to disable wind animation and nonessential transitions.
