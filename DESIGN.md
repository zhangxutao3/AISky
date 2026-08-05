---
name: AISky
description: A fresh, map-first Windows weather forecast workspace.
colors:
  sky-teal: "#159AAA"
  bright-cyan: "#35C5CE"
  fresh-mint: "#4DCEB5"
  warm-sun: "#F3B84B"
  cloud-white: "#F8FCFD"
  mist-white: "#F2F8FA"
  slate: "#17313A"
  muted-slate: "#476871"
  map-mist: "#BFDDE0"
  deep-slate: "#102C37"
  coordinate-slate: "#355760"
  quiet-slate: "#647F87"
  soft-slate: "#6A858D"
  success-mint: "#36C6AD"
  focus-cyan: "#5FD7E5"
  cool-outline: "rgba(109, 154, 163, 0.36)"
  success-halo: "rgba(54, 198, 173, 0.18)"
  quiet-action: "rgba(54, 135, 148, 0.09)"
  quiet-action-hover: "rgba(54, 135, 148, 0.16)"
  reading-outline: "rgba(80, 177, 182, 0.16)"
  chart-surface: "rgba(221, 242, 243, 0.44)"
  chart-outline: "rgba(71, 150, 159, 0.10)"
typography:
  title:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, sans-serif"
    fontSize: "22px"
    fontWeight: 600
    lineHeight: 1.2
  body:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, sans-serif"
    fontSize: "12px"
    fontWeight: 600
    lineHeight: 1.35
  micro:
    fontSize: "11px"
  compact:
    fontSize: "13px"
  subsection:
    fontSize: "17px"
  reading:
    fontSize: "27px"
rounded:
  hairline: "1px"
  control: "10px"
  soft-control: "11px"
  card: "12px"
  panel: "14px"
  elevated-panel: "16px"
  point-panel: "18px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "18px"
  xl: "24px"
components:
  floating-panel:
    backgroundColor: "{colors.mist-white}"
    textColor: "{colors.slate}"
    rounded: "{rounded.panel}"
    padding: "12px"
  elevated-panel:
    backgroundColor: "{colors.cloud-white}"
    textColor: "{colors.slate}"
    rounded: "{rounded.elevated-panel}"
    padding: "12px"
  play-button:
    backgroundColor: "{colors.bright-cyan}"
    textColor: "{colors.slate}"
    rounded: "26px"
    size: "52px"
---

# Design System: AISky

## Overview

**Creative North Star: "Cloud-Edge Observatory"**

AISky is a map-first weather workspace that feels like clear air after rain: bright, calm, and approachable. Cloud-white controls float above the forecast without turning the screen into an academic console. Mint and sky-cyan indicate live state and action; forecast palettes remain the most colorful element.

The system is compact enough for professional inspection but deliberately avoids a dense scientific-dashboard mood. Native Windows behavior, legible Chinese labels, and responsive reflow take priority over ornamental novelty.

**Key Characteristics:**

- The map is always the visual hero.
- Floating controls use translucent cloud surfaces and slate text.
- Accent color is restrained to state, action, and small highlights.
- Point inspection enters from the left and never blocks the central map by default.

## Colors

The palette combines cool cloud neutrals with a narrow mint-to-sky accent family and one warm forecast marker.

### Primary

- **Sky Teal:** Primary interactive and focus color.
- **Bright Cyan:** Play controls, active feedback, and lightweight emphasis.

### Secondary

- **Fresh Mint:** Healthy service state and wind-animation status.
- **Warm Sun:** Current-time markers and exceptional attention only.

### Neutral

- **Cloud White:** Elevated data and layer panels.
- **Mist White:** Compact floating controls and timelines.
- **Slate:** Primary text and icons.
- **Muted Slate:** Supporting labels, timestamps, and secondary status.

**The Map-Owns-Color Rule.** UI chrome stays quiet so meteorological palettes carry the screen.

## Typography

**Display Font:** Segoe UI Variable with Microsoft YaHei UI fallback  
**Body Font:** Segoe UI Variable with Microsoft YaHei UI fallback

**Character:** Native, compact, and friendly. Weight and spacing create hierarchy; the interface does not rely on oversized headings.

### Hierarchy

- **Title** (600, 22px): timeline lead and principal readings.
- **Body** (400, 14px): controls, values, and descriptions.
- **Label** (600, 12px): layer codes, chart annotations, and compact status.

**The Numbers-Stay-Calm Rule.** Forecast values use tabular numerals where possible and avoid decorative display styling.

## Layout

The composition uses four stable zones: command and run controls at the top, products on the right, time navigation at the bottom, and an on-demand point drawer from the left. WinUI `VisualStateManager` breakpoints at 980px and 1420px collapse brand/status details and reduce panel widths before overlap can occur. The map remains full-bleed at every size.

## Elevation & Depth

Depth is ambient rather than structural. Native XAML panels use thin translucent borders; the HTML point drawer adds a soft diffuse shadow and backdrop blur. Heavy dark slabs are reserved for loading/error states, not normal operation.

**The Cloud-Layer Rule.** A floating surface should read as mist above the map, never as an opaque dashboard wall.

## Shapes

Controls use gently curved 10px corners; primary floating panels use 14–16px corners; the left point drawer uses 18px. Circular geometry is reserved for the play control and status dots. Borders are thin and low contrast.

## Components

### Buttons

- **Shape:** Compact native controls, with a circular 52px play action.
- **Primary:** Bright cyan with slate foreground.
- **Hover / Focus:** Native WinUI states and system focus visuals.

### Cards / Containers

- **Corner Style:** 14px for compact floaters; 16px for elevated panels.
- **Background:** Cloud or mist white with controlled translucency.
- **Shadow Strategy:** Ambient shadow only for the HTML point drawer.
- **Border:** One-pixel cool gray-teal stroke.

### Inputs / Fields

- **Style:** Native WinUI ComboBox and Slider controls with 10px compact corners.
- **Focus:** System focus treatment; no custom glow that competes with the map.

### Point Forecast Drawer

The drawer slides in from the left, presents the current value first, then a continuous line-and-area time series. The selected lead is a warm point on the cyan curve.

### Wind Streamlines

Short white trails animate on their own transparent canvas at approximately 30fps. The animation is functional data visualization, user-toggleable, and independent from base-map redraws.

## Do's and Don'ts

### Do:

- **Do** preserve the full-bleed map and four-zone control layout.
- **Do** use cloud-white surfaces, slate text, and small mint/cyan state accents.
- **Do** adapt before panels overlap, and keep network/file work asynchronous.
- **Do** show progress for downloads and local cache preparation.

### Don't:

- **Don't** turn the application into a dark scientific control room by default.
- **Don't** add model comparison; it is outside the product.
- **Don't** use decorative gradients or accent stripes on ordinary cards.
- **Don't** redraw the full weather raster for every wind-animation frame.
