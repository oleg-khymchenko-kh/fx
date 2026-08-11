# Price density indicator

Status: implemented, v1.

## Goal

A new indicator type `Density` (label "Density" in the Add / Edit
dialog). For an anchor minute and a lookback window it answers, for
every pip level:

- How many minutes of the window covered this pip?

A minute covers a pip when the pip is inside the candle range
`[Min, Max]`. The answer is drawn as a horizontal profile at the right
edge of the chart: one bar per pip level, growing to the left, the
longest bar is the biggest count in the window (120 px).

The anchor minute follows the mouse: the profile shows the window that
ends at the candle under the cursor and updates as the cursor moves.
When the mouse is not on the chart the anchor is the right edge of the
view.

## Parameters

Nine lookback options, each a period plus a unit (Minutes / Hours /
Days). Defaults, in trading days: 1, 2, 5, 10, 20, 40, 60, 120, 240
(a trading month is 20 trading days, so 60 is 3 months, 120 half a
trading year, 240 a trading year). Keys 1-9 on the chart switch the
active option at runtime. Plus the source symbol (a pair or a USD
Index symbol) and the color.

## Definition

- Counting works on the display-transformed source minutes, the same
  data the source price line is drawn from, so mirrored pairs and
  PriceDiv scaling need no special handling: one pip is always 10
  display points.
- The pip levels of a candle are `round(Min / 10)` to `round(Max / 10)`,
  both ends included - the same rounding as price age.
- The window is counted in bars of the dense minute array, so it counts
  trading minutes: weekends and data gaps do not shrink it. A 1 day
  option is 1440 bars.
- The anchor is the last stored minute at or before the hovered column.
  At zoomed-out levels one screen column holds several minutes; the
  profile is recomputed per column, anchored at the column's last
  minute.
- Live provisional candles are not in the dense array, so they are not
  counted.
- Only loaded history counts: if lazy loading has not brought the whole
  window into memory, the profile uses what is loaded.

## Algorithm

`DensityProfile.Build` is a difference array over pip levels: each bar
adds +1 at its low pip and -1 after its high pip, one prefix pass turns
that into counts and the maximum. Cost is two array writes per bar plus
one pass over the pip range - a 20 day window is about 29k bars, a
trading year about 346k, still millisecond scale. Nothing is
precomputed or cached; the histogram is
rebuilt from scratch on every anchor change, which is fast enough for
mouse-move updates without any debounce.

## Storage

None. Like Shift and Deals the type has no stored symbol data
(`IndicatorTypes.HasStorage` is false), so Compute derived, Refresh and
Rebuild skip it. The nine options and the selected option index live on
`IndicatorSymbol` (`DensityPeriods`, `DensityUnits`, `DensitySelected`)
in config.json. Missing or invalid list entries fall back to the
defaults (`DensityPeriodAt` / `DensityUnitAt`).

## Rendering

The profile is not part of the chart raster pass. It is a separate
Pbgra32 overlay bitmap (`ChartView._densityImage`, `DensityMaxWidthPx`
= 120 px wide, chart height) anchored to the right edge, below the
crosshair layer. Redrawing it never re-rasterizes the chart, so it
follows the mouse instantly:

- `OnCrosshairMove` recomputes when the hovered column changes.
- `Present` recomputes after every chart rebuild (pan, zoom, resize,
  data load, tab switch).
- Keys 1-9 and `MouseLeave` recompute immediately.

Each pixel row maps to its pip range through `YToDisplay` (series
offset chain and flatten shift at the anchor time included) and takes
the biggest count among its pips. Bar length is `count / maxCount` of
the window times 120 px; the fill is the indicator color at alpha 96
(`DensityFillAlpha`) with an opaque pixel at the left edge of each bar.
Several visible Density indicators draw into the same bitmap,
alpha-blended in series order.

A label per visible Density indicator sits at the top right and shows
the active option as `Name 3: 1d` in the indicator color.

The series itself is an empty `CandleHistory` with
`SymbolSeries.DensityPanel = true`, which joins the shared
`BottomPanel` flag, so every price-line code path (render lines, auto
scale, pan range, stats, align, hit tests) skips it. Density names are
also in the align-to-selection excluded set.

## Runtime switching

Keys 1-9 (digit row and numpad) are handled in the ChartView KeyDown
lambda, so they work whenever the mouse is over the chart. The keys are
consumed only when at least one Density indicator exists. The switch is
global: every Density indicator follows the same selected index. The
overlay and labels update at once; `DensitySelectedChanged` then writes
`DensitySelected` into every Density indicator and saves config.json,
so the choice survives restarts.

## Creation, compute and refresh

Same Add / Edit dialog. Picking `Density` shows nine period + unit
rows, one per key. Periods must be positive whole numbers; a lookback
over 10M minutes is rejected. `SameData` compares only the source, and
there is nothing stored, so OK never computes anything - it saves the
config and reloads the chart, which rebuilds the series with the new
windows. Refresh / Rebuild are not offered (`HasStorage` is false).
Deleting the indicator removes the config entry.

## Not in v1 (next steps)

- Live candles are not counted; the profile ends at the last stored
  minute.
- A window reaching into unloaded years is silently truncated; no
  background load is requested for it.
- No numeric scale on the profile, only relative lengths, and no
  tooltip with the exact count.
- The symbol bar row prints a meaningless price for the density row.
