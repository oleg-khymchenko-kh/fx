# Moving average (SMA) indicator

Status: implemented, v3 (in-memory, no storage).

## Goal

An indicator type `Average` next to `ZigZag` and `Drawing`. It draws a
plain simple moving average (SMA) of the source pair as a normal line.
The user sets a period as a number plus a unit (minutes / hours / days)
and a direction (past / future):

- Past: the average of the candles behind each point (classic trailing
  MA).
- Future: the average of the candles ahead of each point (the window
  runs forward instead of backward).

"Plain" means simple average - each candle in the window has equal
weight (no EMA / weighting).

## Volume weighted variant

A "Volume weighted" checkbox in the Average params row switches the
same indicator to a rolling VWAP. Each minute is weighted by its real
volume (the volume stored in the candle flags word, see docs/volume.md):

    value(t) = sum(avg[i] * volume[i]) / sum(volume[i])   over the window

Only plain arithmetic: multiply, add, divide. A minute without volume
data gets weight 0 and does not affect the result; when the whole
window has zero volume (history before CME coverage), the value falls
back to the plain SMA of the same window, so the line stays continuous.
The sliding window tracks four running sums (price sum + count, volume
sum + price*volume sum), so weighted and plain cost the same O(n).
When the volume collector patches fresh volume into the parent minutes,
the diff sees the change (it compares volume too in weighted mode) and
recomputes just the affected window.

## Window size (count-based)

The source is M1 (one candle per minute). The window length is measured
in candles, not wall-clock time:

    windowBars = period * (minutes=1, hours=60, days=1440)

So "3 hours" = 180 candles, "1 day" = 1440 candles. On M1 this matches
minutes/hours/days of trading time. The window does not reset at week
boundaries: at Monday open the trailing window still reaches back into
last week's candles, so the line continues smoothly across the weekend
gap. Gaps in the data are ignored (the window is a count of existing
candles, not a time span). See MovingAverageSymbol.WindowBars.

At the very start (past) or very end (future) of the data the full window
does not fit; there the average uses whatever candles are available
(expanding / shrinking window), so every source minute gets a value and
the line is continuous. This makes a future MA converge to the last
candle at the right edge, and a past MA ramp up from the first candle.

## No storage - computed in memory

An Average writes nothing to disk. The line is derived from the parent
series that is already loaded in the chart, in display space (after pip
scaling and mirror; averaging commutes with both because they are
affine). One flat candle (Min = Max = Avg) per parent minute.

`Chart/AverageSeries.cs` holds the math:

- `Compute` / `ComputeRange` - sliding-window sum, O(n), no prefix
  array allocation. `ComputeRange` seeds the running sum from up to
  `window` candles before (or after) the range and produces values for
  the requested index range only.
- `Diff` - given the parent minutes array before and after a change,
  finds the common prefix and suffix (comparing timestamp + Avg) and
  returns the index range whose SMA values could have changed, already
  widened by `window - 1` in the direction the window looks. Returns
  null when nothing relevant changed (for example a volume-flag patch
  that kept every Avg).
- `LiveTail` - SMA values for the parent's live tail candles. A past MA
  window reaches back through the tail into the loaded minutes; a
  future MA shrinks toward the newest tick.

`ChartView` owns the update points. Every average series carries
`AverageWindowBars` / `AverageFromFuture` on `SymbolSeries`, and the
chart keeps the parent minutes array reference each average was
computed from (`_averageParents`):

- `SetSeries` - full compute if the average arrived empty.
- `ReplaceSeries` / `PatchSeriesHistory` (lazy year loads, volume
  patches) - `UpdateAveragesOf(parent)` runs `Diff` and patches only
  the changed index range via `CandleHistory.WithReplacedRange`, so a
  year prepend recomputes that year plus one window, not the whole
  history.
- `SetLiveTail` - recomputes the average's live tail, so the line
  follows live ticks now.

The initial chart load computes averages on the background thread (in
the derived-slot pass of `LoadChartAsync`) from the parent slot's
already-transformed minutes, so the UI thread only picks up ready-made
histories.

If the parent pair is hidden but its average is visible, the parent's
candles still load: the startup reader and the lazy loader treat such a
parent as visible (`LoaderHidden` in MainWindow), the parent line just
stays hidden on the chart.

Old builds stored averages in the candle DB. On chart load
`DropStoredAverageCandles` deletes those folders once.

## Fixed vertical placement

An Average is glued to its source: it has no own vertical offset, ever.
`ChartView` keeps average symbols in `_offsetLockedSymbols` and ignores
them in every offset path (label wheel, align to grid, auto align,
align to source, align to selection, saved state restore). The label's
context menu hides the align items. When the parent is offset, the
average moves with it (offsets accumulate along the source chain).

## Label

The symbol bar label shows the averaging window between the name and
the price as days:hours:minutes; the days part is dropped when zero:
`EMA4 4:00: 1.08421` (4 hours), `EMA4 1:16:05: ...` (1 day 16 h 5 min),
`EMA4 0:15: ...` (15 minutes). It updates immediately on the wheel,
before the debounced recompute lands.

## Mouse wheel on the label

The plain wheel on an Average label does nothing (no vertical shift).
With modifiers it changes the averaging window:

- Alt + wheel: +/- 4 hours (240 bars) per notch.
- Alt + Ctrl + wheel: +/- 15 minutes per notch.

The window is clamped to 15 minutes .. 365 days. The new value is
normalized back into Period + Unit (days if divisible by 1440, hours if
divisible by 60, minutes otherwise), saved to config immediately, and
the line recomputes after a 150 ms debounce
(`MainWindow.OnAveragePeriodWheel` -> `ChartView.SetAverageWindow`), so
rolling the wheel through several notches costs one recompute.

## Creation and editing

The same Add/Edit dialog (SymbolEditorWindow). For `Average` the pips
limit field is hidden and an "Average params" row shows instead: Period
(number), Unit combo (Minutes / Hours / Days), Direction combo
(Past / Future). Apply just saves config and reloads the chart; there
is no Refresh or Rebuild anywhere (`IndicatorTypes.HasStorage` is false
for Average), and Compute derived skips it.

## Not in v3 (next steps)

- Other average kinds (EMA / weighted), or time-based (wall-clock)
  windows.
- The label price does not tick with the live tail (it is set once per
  chart load).
