# Price density indicator

Status: implemented, v1.

## Goal

A new indicator type `Density` (label "Density" in the Add / Edit
dialog). For an anchor minute and a lookback window it answers, for
every pip level:

- How many minutes of the window covered this pip?

A minute covers a pip when the pip is inside the candle range
`[Min, Max]`. The answer is drawn as a horizontal profile at the right
edge of the chart: one bar per pip level, growing to the left. By
default the biggest count in the window is 120 px long; `Ctrl` + wheel
over the indicator's row in the symbol bar pins that scale and then
moves it (see "Scale" below).

The anchor minute follows the mouse: the profile shows the window that
ends at the candle under the cursor and updates as the cursor moves.
When the mouse is not on the chart the anchor is the right edge of the
view.

## Parameters

Nine lookback options, each a period plus a unit (Minutes / Hours /
Days). Defaults, in trading days: 1, 2, 5, 10, 20, 40, 60, 120, 240
(a trading month is 20 trading days, so 60 is 3 months, 120 half a
trading year, 240 a trading year). Keys 1-9 on the chart switch the
active option at runtime; key 0 is a fixed tenth option that uses the
whole history. Plus the source symbol (a pair or a USD Index symbol)
and the color.

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
  window into memory, the profile uses what is loaded. Picking the
  all-history option (key 0) asks the loader for the full history of
  every Density source (`EnsureFullAsync`), and the profile refreshes
  as the years splice in; the same request runs at startup when the
  saved selection is the all option.

## Algorithm

`DensityProfile.Build` is a difference array over pip levels: each bar
adds +1 at its low pip and -1 after its high pip, one prefix pass turns
that into counts and the maximum. Cost is two array writes per bar plus
one pass over the pip range - a 20 day window is about 29k bars, a
trading year about 346k, still millisecond scale. The all-history
option scans every loaded bar (a 15 year pair is roughly 5.6M bars,
around 10-30 ms), which is still fine per column change. Nothing is
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

Each pixel row maps to its pip range through `YToDisplay` (the source
symbol's offset chain and flatten shift at the anchor time included;
the density row's own drag offset is ignored, so the profile always
lines up with the source price line) and takes
the biggest count among its pips. Bar length is `count / unit` times
120 px; the fill is the indicator color at alpha 96
(`DensityFillAlpha`) with an opaque pixel at the left edge of each bar.
Several visible Density indicators draw into the same bitmap,
alpha-blended in series order.

## Scale

The scale is one number per indicator: **counts per pixel** - minutes
per pixel for Density, contracts per pixel for Volume. At 80 per pixel
a level holding 9,600 contracts draws a 120 px bar and one holding 800
draws 10 px. It is set two ways, both writing the same config field:

- `Ctrl` + wheel over the indicator's row in the symbol bar, 1.25 per
  notch, saved immediately;
- the `Scale` box in the Add / Edit dialog.

Leave the box empty and the scale is automatic again: the window
maximum fills the band, which is what the profile always did. As soon
as the wheel moves it or a number is typed, it is fixed - the same
count is the same bar length whatever the window, the cursor or a
background year load does. The value is not printed on the chart - it
lives in the `Scale` box of the Add / Edit dialog.

Each key gets its own percent on top of that one scale, in the same
dialog, `Key 1` through `Key 9` plus `Key 0` for the all-history
option (`Key 0` has no period to edit). 100 means the scale as is, 50
draws bars half as long, 200 twice as long. This is what keeps a
240 day window readable next to a 1 day window without touching the
wheel. The wheel never changes the percents.

Bars are clamped to the chart width, not to the band: a pinned scale
larger than the current data lets bars run left across the chart
instead of being cut at 120 px. The overlay bitmap is therefore chart
wide, but only the occupied strip is cleared and blitted per frame,
and the overlay canvas has hit testing off so it never eats mouse
input.

A label per visible Density indicator sits at the top right and shows
the active option as `Name 3: 1d` in the indicator color. A Volume
indicator adds its group after a `|`, e.g. `Name 3: 1d | 15m`, with a
`*` when the group is locked (see "Grouping" in docs/volume.md).

The series itself is an empty `CandleHistory` with
`SymbolSeries.DensityPanel = true`, which joins the shared
`BottomPanel` flag, so every price-line code path (render lines, auto
scale, pan range, stats, align, hit tests) skips it. Density names are
also in the align-to-selection excluded set.

## Runtime switching

Keys 0-9 (digit row and numpad) are handled in the ChartView KeyDown
lambda, so they work whenever the mouse is over the chart. The keys are
consumed only when at least one Density indicator exists. The switch is
global: every Density indicator follows the same selected index. Key 0
selects the all-history option: internally the selected index equals
`DensityWindows.Length` (`IndicatorSymbol.DensityAllOption`), the
window becomes unbounded and the label shows `0: all`. The overlay and
labels update at once; `DensitySelectedChanged` then writes
`DensitySelected` into every Density indicator, saves config.json (so
the choice survives restarts) and, for the all option, kicks off a
full-history load of every Density source.

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
- A finite window reaching into unloaded years is silently truncated;
  only the all-history option (key 0) requests a full load.
- No numeric scale on the profile, only relative lengths, and no
  tooltip with the exact count.
- The symbol bar row prints a meaningless price for the density row.

## Selection mode

While a time range is selected on the chart (Shift + drag), the
profile ignores the lookback keys and the cursor anchor: it is built
only from the minutes inside the selection, `[start, end of the last
selected column]`. The label shows "NAME: selection". The profile
follows the band live while the selection is dragged, and Escape
returns to the normal cursor-anchored window. Applies to Density and
Volume alike.
