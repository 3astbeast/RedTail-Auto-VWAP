<p align="center">
  <img src="https://avatars.githubusercontent.com/u/209633456?v=4" width="160" alt="RedTail Indicators Logo"/>
</p>

<h1 align="center">RedTail Auto VWAP</h1>

<p align="center">
  <b>Automatic multi-anchor VWAP indicator for NinjaTrader 8 with Opening Range, Initial Balance, and voice alerts.</b><br>
  Nine VWAP types, standard deviation bands, session ranges, and spoken alerts — all in one indicator.
</p>

<p align="center">
  <a href="https://buymeacoffee.com/dmwyzlxstj">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-FFDD00?style=flat-square&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"/>
  </a>
</p>

---

**IMPORTANT INSTALLATION INSTRUCTIONS**

This indicator uses Windows Voice for alerts instead of Ninja Trader's built in beeps, honks and blips. If you install this .cs file and get an error on compilation, your NT install is missing a required .dll.  See step #7 below:

1. Download the .cs file from the indicator's repository
2. Copy the .cs to documents\Ninja Trader 8\bin\custom\indicators
3. Open Ninja Trader (if not already open) 
4. In control center, go to New --> Ninja Script Editor
5. Expand the Indicator Tree, find your new indicator, double click to open it
6. At the top of the Editor window, click the "Compile" button
7. If you get an error on compilation, do the following: Open Ninja Compiler, right click and choose references. Click add. Browse to this: C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll
8. After that .dll is referenced, the indicator will compile successfully.
   
---

## Overview

RedTail Auto VWAP automatically calculates and plots up to 9 different VWAP anchors along with NY Opening Range and Day Initial Balance zones. Every VWAP type resets automatically at the appropriate anchor point — no manual drawing required. The indicator uses SharpDX rendering for performance, is fully EST timezone-aware, and includes a voice alert system that announces VWAP touches and approaches with the instrument name.

---

## VWAP Types

### NY Session VWAP
VWAP anchored to the New York session open. Resets each NY session. Configurable historical lookback to display previous NY session VWAPs on the chart.

### Previous Day NY VWAP
The finalized NY Session VWAP from the prior day, extended forward as a flat reference level into the current session.

### Session VWAP
VWAP anchored to NinjaTrader's session definition (typically the full electronic session). Resets at each new session. Includes optional standard deviation bands with configurable multiplier. Supports dynamic session coloring — the VWAP line changes color based on whether price is trading above or below it (independent bullish/bearish colors). Configurable historical lookback.

### Previous Session VWAP
The finalized Session VWAP from the prior session, extended forward with optional standard deviation bands from the previous session's data.

### HOD VWAP (High of Day)
VWAP anchored to the bar that made the current session's high of day. The anchor point moves each time a new high is printed. Optional standard deviation bands with configurable multiplier and color.

### LOD VWAP (Low of Day)
VWAP anchored to the bar that made the current session's low of day. The anchor point moves each time a new low is printed. Optional standard deviation bands with configurable multiplier and color.

### Monthly VWAP
VWAP anchored to the start of each calendar month. Resets monthly. Configurable historical lookback.

### Yearly VWAP
VWAP anchored to the start of each calendar year. Resets yearly. Configurable historical lookback.

### HOY VWAP (High of Year)
VWAP anchored to the bar that made the yearly high. The anchor point moves each time a new yearly high is printed.

---

## NY Opening Range

Tracks the high and low of a configurable time window at the NY open, drawn as horizontal levels extending through the rest of the session.

- **Default window:** 9:30 AM – 9:45 AM ET
- Independent high and low line styles
- Configurable line color, thickness, and opacity
- Fill shading between high and low with configurable opacity
- Optional text label
- Historical lookback to display previous sessions' opening ranges

---

## Day Initial Balance

Tracks the high and low of a configurable initial balance period, drawn as horizontal levels extending through the rest of the session.

- **Default window:** 9:30 AM – 10:30 AM ET
- Independent high and low line styles
- Configurable line color, thickness, and opacity
- Fill shading between high and low with configurable opacity
- Optional text label
- Historical lookback to display previous sessions' initial balance ranges

---

## Level Merging

When the Opening Range and Initial Balance levels overlap (which they often do, since the OR window falls within the IB window), the indicator can merge overlapping levels to avoid visual clutter. This shows a single clean zone instead of doubled-up lines.

---

## Standard Deviation Bands

Available on Session VWAP, Previous Session VWAP, HOD VWAP, and LOD VWAP. Each set of bands is independently configurable with its own multiplier, color, and line style.

---

## VWAP Labels

Optional labels on each VWAP line showing the VWAP name. Configurable font size. Labels are rendered at the right edge of the chart for clean identification.

---

## Voice Alerts

An auto-generated spoken alert system that announces when price interacts with VWAP levels.

- **Touch alerts** — Triggered when a price bar touches or crosses a VWAP level
- **Approach alerts** — Triggered when price comes within a configurable tick distance of a VWAP
- Alerts include the instrument name (e.g., "MNQ has touched the Session VWAP")
- Uses edge-tts neural voice synthesis with SAPI5 fallback
- Configurable voice speed (−10 to +10, default: 2)
- Alert cooldown timer to prevent repeat alerts (default: 30 seconds)
- Fallback .wav sound file if voice generation fails

---

## Per-VWAP Styling

Every VWAP type has independent visual controls: color, line style (Solid/Dash/Dot/DashDot/DashDotDot), and for band-enabled VWAPs, independent band color and band line style.

---

## Installation

1. Download the .cs file from the indicator's repository
2. Copy the .cs to documents\Ninja Trader 8\bin\custom\indicators
3. Open Ninja Trader (if not already open) 
4. In control center, go to New --> Ninja Script Editor
5. Expand the Indicator Tree, find your new indicator, double click to open it
6. At the top of the Editor window, click the "Compile" button
7. That's it!

> **Note:** All session times are EST/ET timezone-aware. The indicator auto-detects the Eastern timezone and converts bar times accordingly.

---

## Part of the RedTail Indicators Suite

This indicator is part of the [RedTail Indicators](https://github.com/3astbeast/RedTailIndicators) collection — free NinjaTrader 8 tools built for futures traders who demand precision.

---

<p align="center">
  <a href="https://buymeacoffee.com/dmwyzlxstj">
    <img src="https://img.shields.io/badge/☕_Buy_Me_a_Coffee-Support_My_Work-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee"/>
  </a>
</p>
