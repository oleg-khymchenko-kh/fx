# Shift indicator (same pair, moved along the time axis)

Status: implemented, v1.

## Goal

A new indicator type `Shift` next to `ZigZag`, `Average` and `Drawing`.
It draws the source pair again, but moved along the time axis, so an old
piece of history can be put under the current price.

The user sets two time points:

- Source time - a moment on the source pair's own time scale.
- Chart time - the moment on the chart where that source moment must
  land.

The two points are glued together. Example: chart time = 24-Jul-2026,
source time = 1-Apr-2026. Then at the chart position 24-Jul-2026 the
indicator shows what the pair did on 1-Apr-2026, and the whole history
follows with the same shift.

## Source and target

- **Source** is the template pair: the fragment for `Find` is taken
  from it, the indicator sits next to it in the symbol bar, and its
  vertical offset is stored relative to it.
- **Target** (`TargetSymbol`, empty means "same as source") is the pair
  whose candles are actually copied and drawn. It is set by picking a
  row in the find results, or by hand in the Add/Edit dialog.

So the indicator can show a GBPUSD window over an EURUSD fragment. Pip
scaling and the mirror flag are taken from the target pair, so a JPY or
a mirrored pair keeps its own price scale. See docs/find-similar.md.

## No storage

The indicator has no DB folder. On chart load it reads the candles of
its source symbol and re-stamps their timestamps in memory. Nothing is
written to disk, so:

- Recompute indicators / Refresh skip it (`IndicatorTypes.HasStorage`).
- Delete only drops it from the config.
- `IndicatorSymbol.SameData` returns true for it, like Drawing: changing
  the two time points is a config-only change, the new shift appears
  after the normal chart reload.

The price is not touched at all - only `Candle.MinuteUnixSeconds`. Pip
scaling and mirror run afterwards exactly as for the source pair, so a
mirrored pair (USDCHF, USDJPY, USDCAD) gets the same mirror base as its
source and the shifted line lies on the same price scale.

A Shift symbol is a plain candle line and is never editable. A ZigZag
source has no candles at all, so it cannot be a shift target.

## The shift is counted in trading time

The offset is not a plain difference in seconds. It is measured in the
virtual time of `WeekendCompressor` (real time with all weekend gaps
removed), see docs/no-weekends.md:

    delta   = ToVirtual(chartTime) - ToVirtual(sourceTime)
    newTime = ToReal(ToVirtual(oldTime) + delta)

So every trading minute of the source lands on a trading minute of the
chart. Nothing falls into a weekend gap, nothing is lost or squeezed into
one column, and the weekends of the shifted copy line up with the
weekends of the chart. This holds in both modes - the timestamps are
computed once at load, they do not change when the "No weekends" switch
is toggled.

The price of that is weekday drift: in the example above 1-Apr-2026 is a
Wednesday and 24-Jul-2026 is a Friday, so Wednesday data is drawn on a
Friday. That is the trade-off for never hiding data. If you want the
weekdays to match, pick two points on the same weekday and time - then
the shift is a whole number of weeks and both models agree.

`ShiftedSymbol.Shift` also drops any candle whose new timestamp is not
strictly after the previous one. This can only happen for the ~130
minutes in 5.7 M that the broker recorded inside a modelled weekend gap
(see docs/no-weekends.md); they collapse to the same glue point. The
result stays sorted and strictly increasing, which the whole chart
assumes.

## Creation and editing

Same Add/Edit dialog (SymbolEditorWindow). For `Shift` the pips limit and
the average params are hidden and a "Shift params" block shows two rows:
a DatePicker plus an `HH:mm` box for the source time and the same pair
for the chart time. Times are UTC, like everything else in the DB.

Defaults for a new indicator: chart time = today 00:00 UTC, source time =
the same date one year earlier.

Model: `IndicatorSymbol` gains `SourceTimeUnix` and `ChartTimeUnix`
(unix seconds) and `Flip` next to `LimitPips` / `Period` / `Unit` /
`FromFuture`.

## Only the distance between the two points matters

The pair is kept as two absolute UTC timestamps, but the drawing uses
only the virtual distance between them. The wheel and the Alt drag move
the chart point alone, so a copy that started from an empty pair (both
zero) walked away from 1-Jan-1970 and ended up with a correct shift and
two meaningless dates. The Edit dialog then showed the source point as
"today minus one year" (the fallback for an unset field) next to a 1969
chart point, and saving that pair turned a one day shift into a fifty
year one.

`AppConfig.Load` now rebases any Shift pair that sits before the first
weekend gap of the calendar (2007, so nothing real can be there):

    source = today 00:00 UTC (Friday close if that midnight is inside a
             weekend gap)
    chart  = ToReal(ToVirtual(source) + delta)

The virtual delta is unchanged, so nothing moves on the chart - only the
two dates in the dialog become readable. If the delta itself is already
garbage - a pair saved from the broken dialog can hold fifty years - the
rebased chart point would land before 2007 again and the rebase would
repeat on every start, so such a pair is reset to zero shift instead. The same rebase runs for the
shift placements of every tab (`ChartTab.Shifts`) and of every note, so
switching tabs does not bring the old pair back, and the Edit dialog
repeats it in memory for anything that still slips through.

## Quick shift with the wheel

Alt + mouse wheel over the indicator's name in the left symbol bar moves the
copy along the time axis by 15 minutes per notch. Alt + Ctrl + wheel uses a
1 minute step. Wheel up moves the copy to the right (later on the chart),
wheel down to the left.

The step is trading time, like the shift itself: the chart time point is
moved by `ToReal(ToVirtual(chartTime) + step)` and the new virtual delta
follows from it. The new chart time is written to the config right away, so
the Edit dialog and the next chart load show it.

The already loaded candles are not read from the DB again - their timestamps
are re-stamped in memory with the same virtual step and the rollup levels are
rebuilt (`ShiftedSymbol.Restamp` + `CandleHistory.Build`, off the UI thread).
`ToReal` is strictly increasing, so no candle is lost by re-stamping. The
loader gets the new delta (`SetShiftDelta`) and keeps the loaded year span,
so years that are still missing are read with the new shift later.

Notches that arrive while a re-stamp is running are summed up and applied as
one step, so a fast wheel spin does not queue one full rebuild per notch.

## Drag on the chart with Alt

Hold **Alt** over the chart. If a Shift indicator line runs within 2
pixels of the cursor, that line is picked instead of the tilted grid
(docs/tilted-grid.md):

- it is drawn last, over every other series, and 2 pixels wide, so it is
  clear which copy is picked;
- the nearest one wins when two of them cross under the cursor;
- if no Shift line is that close, nothing changes - Alt still darkens the
  nearest tilted grid line.

**Alt + left drag** on the picked line moves the copy in both directions
at once. While the button is down the line goes back to 1 pixel but stays
on top, and the tilted grid does not move - one Alt drag moves either the
grid or one indicator, never both.

- **Vertical**: the indicator's own price offset, the same value the
  Alt + wheel over the symbol bar name changes. It follows the cursor 1:1
  and is saved with the chart state.
- **Horizontal**: the time shift. One screen column = `k` minutes of
  trading time, sent to `MainWindow` as the same step the wheel nudge
  uses (`SeriesTimeShiftRequested` -> `QueueShiftStep`), so it re-stamps
  the loaded candles, writes the new chart time to the config and logs
  one line per applied step. Steps that arrive while a re-stamp is
  running are summed up, so the copy catches up with the cursor instead
  of queueing one rebuild per mouse move.

The step is trading time (weekends removed), like the shift itself. With
"No weekends" on, the copy follows the cursor exactly; with weekends
shown, a drag across a weekend gap moves the copy by fewer real columns
than the cursor.

The hit test uses the columns of the last render (`ChartView._shiftLines`),
so it costs nothing per mouse move. The horizontal ray that a line draws
from its last candle to the right edge is not part of the hit test - only
the line body can be grabbed.

## Flip

A "Flip vertically (min up, max down)" checkbox in the Shift params
mirrors the copy around the midpoint of its own price range. It is
implemented as the same mirror transform the mirrored pairs use: the
effective mirror flag of the shifted series is the source pair's mirror
XOR the checkbox, and the mirror base is min+max of the series itself.
Changing the flip is a config-only edit (no recompute), applied on the
chart reload. Applying a find result sets the flip automatically from
the result's mirror flag (docs/find-similar.md).

## Live minutes

The live stream feeds only the broker pairs (`MainWindow._live` is keyed by
the symbols in `_baseInfo`). A Shift copy is built from the DB at chart load,
so without extra work its right edge stops at the last row that was on disk
when the chart was loaded.

`PushLiveTail` now also builds a tail for every Shift indicator whose source
is that pair: the same raw live points, run through the Shift series' own
`SeriesTransform`, with the timestamp moved by the shift
(`ToReal(ToVirtual(minute) + delta)`). It is a handful of candles per flush,
so the cost is nothing. The tail is re-pushed after anything that replaces
the series (Alt+wheel nudge, applying a find result), because
`ChartView.ReplaceSeries` builds a new `CandleHistory` with an empty live
array, and cleared for all Shift symbols in `StopLive`.

Limits:

- If the source is a computed symbol (USD index, currency index), there is
  still nothing live to copy: that symbol only gets new data from
  "Compute derived" (docs/live-candles.md).
- Right after a find result with flip, the live tail uses the transform from
  the last chart load, not the flip base that `ApplyFindResultAsync` computed
  in memory, so the tail can sit at the wrong level until the next chart
  load. The rest of that path has the same gap (the symbol bar price too).

## Rendering

A Shift symbol is a normal non-editable line series with `SourceSymbol`
set to its pair, so it keeps the Edit / Delete / Align to source context
menu and its vertical offset is stored relative to the source pair like
any other indicator.

If the chart time is later than the source time, the copy runs past the
last real candle: `RecomputeGlobalRange` takes the maximum over all
series, so the chart simply scrolls further to the right.

## Cost

The shifted copy is a full second copy of the source candles in memory
(EURUSD M1 2011..2026 is about 5.7 M candles), plus the rollup levels
built on top of it - the same cost as one more pair on the chart.

## Find similar

The two time points can also be set automatically by the similarity
search: select a fragment on the chart, right-click the Shift indicator,
pick `Find`. See docs/find-similar.md.

## Not in v1 (next steps)

- Setting the two points by clicking on the chart instead of typing them.
- A quick "shift by N weeks" mode without picking absolute dates.
- Alt + wheel directly over the line on the chart (today the wheel works
  only over the name in the symbol bar; on the chart Alt + wheel belongs
  to the tilted grid).
