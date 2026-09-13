# Moving average (SMA) indicator

Status: implemented, v4 (in-memory, no storage).

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

## Window size: by candles or by time

The period is always written as a number plus a unit, and it always
turns into the same number of minutes:

    windowBars = period * (minutes=1, hours=60, days=1440)

So "3 hours" = 180, "1 day" = 1440. What that number *means* is the
"Window by time" checkbox in the dialog (`AverageTimeWindow` in the
config, `AverageSpec.TimeWindow` in the math).

**On (the default): a real time span.** The window starts 180 trading
minutes back from the current minute and takes whatever candles fall
inside it, even if that is only 20 of them. Missing minutes are missing,
they do not push the window's start further back.

**Off: a count of candles.** The window is the last 180 candles that are
in the DB, whatever times they carry. A night that was never downloaded
costs nothing: the window just reaches further back to collect its 180
candles. This is what the indicator did before v4.

"Trading minutes" means the weekend is cut out, the same clock the chart
itself uses (`WeekendCompressor.ToVirtual`). So a 1 day window at Monday
09:00 reaches back to Friday's last trading minutes, not into Saturday.
Weekday gaps (a missed night, a holiday) are *not* cut out: they are
inside the span and they simply hold fewer candles.

Neither mode resets at week boundaries, so the line stays smooth across
the weekend gap. See MovingAverageSymbol.WindowBars.

At the very start (past) or very end (future) of the data the full window
does not fit; there the average uses whatever candles are available
(expanding / shrinking window), so every source minute gets a value and
the line is continuous. This makes a future MA converge to the last
candle at the right edge, and a past MA ramp up from the first candle.

## Wide spread minutes are always skipped

A minute flagged as wide spread (docs/wide-spread.md) never goes into
the average, in both modes, whatever the global "Hide wide spread
minutes" setting says. Those minutes are the post-close spikes; letting
them into the sum is exactly what the flag exists to prevent. This also
means the line looks the same whether the user hides them on the chart
or not.

What that does to the window:

- by candles: the window is the last N **usable** candles, so it walks
  over the flagged ones without counting them;
- by time: the flagged candles sit inside the span and are dropped, so
  the window just holds fewer of them.

If a whole window turns out to be flagged, the average holds the last
usable value (the nearest usable candle before the point, or after it
for a future MA). If there is no usable candle at all on that side, the
point falls back to the price of the minute itself. This rule depends on
the position only, never on where a recompute started, so a partial
recompute gives exactly the same numbers as a full one.

## No storage - computed in memory

An Average writes nothing to disk. The line is derived from the parent
series that is already loaded in the chart, in display space (after pip
scaling and mirror; averaging commutes with both because they are
affine). One flat candle (Min = Max = Avg) per parent minute.

`Chart/AverageSeries.cs` holds the math:

- `Compute` / `ComputeRange` - O(n). Both build a `Window` first: a
  prefix sum of the prices and a prefix count of the usable candles
  over the index span the longest window can reach, plus the trading
  minute of every candle when the window is by time. After that one
  value is two array reads, and the window edge only moves forward, so
  a band with 40 windows walks the prefix arrays 40 times instead of
  keeping 40 running sums over the candle structs. That is about 3x
  faster for a band and the same speed for a single average, at the
  cost of ~16 bytes per candle while the pass runs. The windows of a
  band run over the output in blocks of 16k minutes (one block through
  all the windows, then the next block), so the value array stays in
  cache. `ComputeRange` produces values for the requested index range
  only; the prefix arrays still cover the candles its window reaches
  into before (or after) that range.
- `Diff` - given the parent minutes array before and after a change,
  finds the common prefix and suffix (comparing timestamp + Avg + the
  wide spread flag) and returns the index range whose SMA values could
  have changed, already widened by one window in the direction the
  window looks. The widening walks the real window rule (`WindowStart` /
  `WindowEnd`), so it counts trading minutes in time mode and skips the
  flagged candles in count mode. Returns null when nothing relevant
  changed (for example a volume-flag patch that kept every Avg).
- `LiveTail` - SMA values for the parent's live tail candles. A past MA
  window reaches back through the tail into the loaded minutes; a
  future MA shrinks toward the newest tick.

`ChartView` owns the update points. Every average series carries its
`AverageSpec` on `SymbolSeries`, and the chart keeps the parent minutes
array reference each average was computed from (`_averageParents`).
`AverageSeries.Recompute` is the one entry point: it runs `Diff` and
patches only the changed index range via
`CandleHistory.WithReplacedRange`, so a year prepend recomputes that
year plus one window, not the whole history. It never touches chart
state, so it can run on any thread.

- `SetSeries` - full compute if the average arrived empty.
- `ReplaceSeries` (lazy year loads) - the loader has already run
  `Recompute` for every visible average on its own thread and passes
  the results in; the UI thread only swaps the ready histories in. See
  docs/lazy-loading.md.
- everything else that replaces the parent (`PatchSeriesHistory` after
  a volume patch, a shift retarget, an Alt+wheel window change) -
  `ScheduleAverageRefresh` starts a background task per average and
  applies the result on the dispatcher. No average is recomputed on the
  UI thread any more, so a year prepend on a pair that carries several
  40-window bands no longer freezes the app for seconds.
- `SetLiveTail` - recomputes the average's live tail, so the line
  follows live ticks now. This one stays on the UI thread: the tail is
  a handful of candles, so its prefix arrays cover one window and
  nothing more.

A hidden average is not recomputed at all: the loader leaves it out
(`AverageJobsOf`) and no task is started for it. Its cached parent
reference stays on the old array, so when the series is shown again
(tab switch, symbol bar click) `ScheduleAverageRefresh` sees the
mismatch and rebuilds it in the background.

Every background result is checked against the live state before it is
applied: the parent minutes array, the average's own history, its
cached parent array and its spec all have to be the same references (or
value, for the spec) as when the job started. If any of them moved on,
the result is dropped and a fresh job starts.

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
(Past / Future), a "Volume weighted" checkbox and a "Window by time"
checkbox. Apply just saves config and reloads the chart; there
is no Refresh or Rebuild anywhere (`IndicatorTypes.HasStorage` is false
for Average), and Compute derived skips it.

## Related

A second type, `AverageBand` ("Average max/min"), draws the highest or
the lowest of a whole set of such averages. It shares `AverageSeries`
and the whole no-storage pipeline with this one - see
docs/average-band.md.

## Not in v4 (next steps)

- Other average kinds (EMA / weighted).
- The label price does not tick with the live tail (it is set once per
  chart load).
- The label shows the window the same way in both modes, it does not say
  which one is on.
- Alt+wheel changes the window length, never the mode.
