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
(unix seconds), `Flip`, `ReverseTime` (see Mirror in time below) and
`ZoomPercent` (see Zoom below) next to
`LimitPips` / `Period` / `Unit` / `FromFuture`.

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
wheel down to the left. Alt + Shift + wheel changes the zoom instead (see
Zoom below).

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

## Mirror in time

A "Mirror in time (minutes run right to left)" checkbox in the Shift params
(`ReverseTime`) turns the copy around in time. The source time and the chart
time still meet on the screen, but now they are a mirror axis: what the pair
did after the source time is drawn to the left of the chart time, going
back, and what it did before the source time is drawn to the right.

The mapping is done in the same weekend-free virtual time:

    K       = ToVirtual(chartTime) + ToVirtual(sourceTime) - 60
    newTime = ToReal(K - ToVirtual(oldTime))

A minute candle covers `[t, t + 60)`, so the `- 60` keeps the instants in
place: the source minute that starts at the source time lands on
`[chartTime - 60, chartTime)`, the minute before it on
`[chartTime, chartTime + 60)`. Prices are not touched. The candles come out
in reverse order, so `ShiftedSymbol.Shift(candles, K, true)` walks the source
backwards; the result is sorted and strictly increasing like the forward
copy, and minutes that collide on a weekend glue point are dropped the same
way.

One number describes both modes (`ShiftedSymbol.MapValue`): the virtual
delta for a forward copy, `K` for a mirrored one. The loader keeps it with a
`Reverse` flag. In both modes the time wheel and the Alt drag move the chart
point and add the same step to that number, so the in-memory re-stamp is the
same code: the mirrored copy moves right with wheel up, like a forward one.

What changes with the flag:

- Lazy loading maps the visible range back to source years with
  `ShiftedSymbol.ChartToSource`, which swaps the ends for a mirrored copy.
  Older source years land on the right of the copy, so a "prepend" job of
  the loader appends to the mirrored copy and the other way round.
- A tab placement and a preset keep the flag (`ReverseTime`); turning it on
  or off by a tab switch or a preset rebuilds the copy from the target pair
  in memory (the retarget path), like a flip change. The Presets window has
  a "Back" column, and a default preset name gets "reversed".
- A Find result sets the flag from the result: a Levels2 result found in
  the backward pass (`R` in the results window, docs/find-similar.md, "Both
  time directions") turns it on, every other result turns it off.
- No live tail for a mirrored copy: the newest source minutes land on the
  left edge of the copy, and the live tail can only add candles on the right.
  New minutes show up after the next chart load.

## Zoom

A `Zoom, %` box in the Shift params (default 100) stretches the copy
vertically. Every price of the copy in pips is multiplied by the zoom, and
these pips are drawn: at 101% a move of 100 pips is drawn as 101 pips. The
zoom is the last step, after pip scaling, mirror and flip:

    display = round(mirror(price) * zoom / 100)

So the drawn level moves too (1.10000 at 101% is drawn at 1.11100); the
copy's vertical offset brings it back to the screen.

`SeriesTransform` carries the zoom (`Zoom`, a factor, 1 = 100%), so
everything that turns a drawn value back into a price divides by it first:
the cursor price in the symbol bar (`ToTruePrices`), range stats, the
measure tool. They show the real price of the target pair, not the
stretched one. The live tail runs through the same transform, so new
minutes are zoomed too.

### Alt + Shift + wheel

Alt + Shift + mouse wheel over the copy's name in the symbol bar changes
the zoom by one step per notch:

- wheel up: zoom x 1.01, 100 pips become 101 pips;
- wheel down: zoom / 1.01, 100 pips become 99.01 pips.

Up and down are exact inverses, so N notches up and N notches down return
to the same zoom (a fixed x 0.99 step would drift: 100 -> 101 -> 99.99).
The zoom stays in 1% .. 10000% and is rounded to 6 decimals. The new value
is written to the config right away and logged (`<name>: zoom 101%`).

Snap to 100% (user's rule 2026-10-18): a zoom that comes from a Find
result or the Edit dialog can be anything (98.65%), and from there the
x1.01 steps never hit 100% exactly. So after every wheel step, if one more
step up would give 100.5% or more AND one more step down would give 99.5%
or less, the zoom is set to exactly 100%. That window is about
99.505% .. 100.495%, so a step from 98.65% up gives 100%, while the
normal steps from 100% (101%, 99.01%) stay as they are.
(The user wrote 95.5% for the lower edge; with a 1% step that condition
can never be true, so 99.5% is used, the mirror of 100.5%.)

The loaded candles are not read from the DB again. They are rescaled in
memory by new/old zoom (`ShiftedSymbol.Move`, off the UI thread), the same
way the Alt + wheel time step re-stamps them. Both wheels share one queue
(`_pendingShiftNudge`): notches that arrive while a rebuild runs are summed
up and applied as one step, time and zoom together. The loader gets the
new zoom (`SetShiftZoom`), so years loaded later are zoomed the same way.

Each rescale rounds to a whole point, so after many steps a candle can be
1-2 points (0.1-0.2 pip) away from the exact value. The next chart load
reads exact values from the DB again.

### The copy stays in place

Before the zoomed candles go on the chart, `ChartView.ReplaceZoomedSeries`
picks an anchor `a`: the middle of the copy's visible part (min .. max over
the visible columns, without the flatten shift). The copy's own offset is
moved by

    a * (1 - newZoom / oldZoom)

so the anchor stays at the same screen height and the copy grows or
shrinks around it. A hidden copy has no candles loaded; it uses the price
at the screen center as the anchor. The offset and the new candles go in
with one render, so there is no frame where the copy jumps.

A zoom typed in the Edit dialog does the same before the chart reload: the
anchor is taken from the chart that is still on screen and the shift is
added to the saved offset of the active tab.

### Where the zoom is kept

- `IndicatorSymbol.ZoomPercent` - the current value.
- `ShiftPlacement.ZoomPercent` - per tab (and per note), like the two time
  points and flip. A tab's saved offsets were made with its own zoom, so a
  tab switch re-applies the tab's zoom (in memory, same path as the time
  re-stamp) together with its offsets.
- `ShiftPreset.ZoomPercent` - a preset's offset is saved with its zoom, so
  applying it puts the copy back exactly. The Presets window has a Zoom
  column, and a default preset name gets the zoom when it is not 100%.
  The mirror base correction on apply is multiplied by the zoom.

A Find result sets the zoom (user's rule 2026-10-18): clicking a row
makes the copy as tall as the search found it fits the fragment. For
Similarity and Levels2 the result's `Zoom` says "fragment = Zoom x found
window", so the copy gets `Zoom`; for ZigZag it says "found window = Zoom
x fragment", so the copy gets `1 / Zoom` (a ZigZag row with Zoom 1.25
gives 80%). The offset that puts the copy on the fragment is
`MeanA - MeanB * zoom` (with flip: `MeanA - (flipBase - MeanB) * zoom`).
The zoom is written to the indicator and the tab placement like a wheel
change, and the log line of the apply shows it.

"Align to source" with zoom 100% works as before (same price, same
height). With another zoom the two lines can match at one price only, so
the copy is aligned at the middle of the source's visible part (or its
last price when nothing is on screen).

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
menu.

Its name in the symbol bar carries the target pair, and the zoom when it
is not 100% (user's rule 2026-10-18):

    Find GBP2 (AUDUSD): 0.6944
    Find GBP2 (AUDUSD, 80%): 0.6944

The pair is always shown (it is the Source when no target is set). The
zoom is shown in whole percent, rounded half up (98.65% -> `99%`), and
not at all when it rounds to 100% (`ShiftedSymbol.ZoomLabel`). MainWindow sets the note with
`SymbolBarView.SetLabelNote` after a chart load and after every change
of the copy (time wheel, zoom wheel, Find result, tab switch, preset);
the hit test of the row uses the full text, so the wheel and the menus
work on the longer label too.

Its vertical offset is absolute (since 2026-10-04, user's rule): the
offset chain in `ChartView.EffectiveSeriesOffset` stops at a `TimeShift`
series, so moving the source pair up or down does not move the copy.
Indicators built on the copy (Levels2) still chain to it and follow it.
"Align to source", "Auto align", the selection align and a Find result
compute the copy's offset above the source and add the source's own
offset to it (`SetSeriesOffsetOverSource`). Saved view states from before
that day hold the old relative value; `ChartViewState.AbsoluteShiftOffsets`
is false for them and `RestoreState` adds the source offset once, so
nothing jumps on the first load.

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

A shown Levels2 result draws circles on the copy at the levels that
matched the `Levels` rows (red = Required, light blue = Preferred). The
switch is "Show key points of a Levels2 result" in the Shift params
(`ShowFindKeyPoints`, default on) and the same checkbox in the results
window. See docs/find-similar.md, section "Key points".

## Presets

`Presets` in the right-click menu of a Shift indicator opens a window
like `Show results`. A preset is a named parameter set:

- source pair, target pair, source time, chart time, flip, mirror in
  time, zoom - the same fields as a tab's `ShiftPlacement`;
- the copy's vertical offset above its source (`OffsetOverSource`,
  points), so applying a preset puts the copy where it was saved;
- the copy's mirror base (`MirrorBase`, 0 when the copy is not
  mirrored). A mirrored copy (USDXXX target, or flip) is drawn as
  `MirrorBase - price`, and a retarget recomputes the base from the
  loaded range, so the same offset would land somewhere else. On apply
  with a target or flip change the saved base is written to
  `AppConfig.MirrorBases` before the reapply, so `RetargetShiftAsync`
  uses it as the fixed base; without a retarget the series keeps its
  base and the offset is corrected by the base difference.

Presets are global (`AppConfig.ShiftPresets`), one list shared by all
Shift indicators, kept in config order. `AppConfig.Load` rebases their
anchors together with the indicators and tabs.

In the window:

- Click a row (or Enter) - the preset is applied to the indicator the
  window was opened for. The tab's placement for that indicator is set
  to the preset and `ApplyShiftPlacements` runs, so the same in-memory
  re-stamp / retarget path as a tab switch is used; the offset is set
  with `SetSeriesOffsetOverSource`. If a DB operation is running the
  config is updated and the chart reload at its end picks it up.
- The row whose parameters equal the indicator's current ones is
  selected on open and after every change.
- `Save as new...` asks for a name (default: target + source date +
  "flip"); an existing name asks to overwrite that preset.
- `Save to selected` overwrites the selected preset with the current
  parameters, `Rename...` and `Delete` (also the Delete key) do what
  they say, each after a confirmation where data is lost.
- Drag a row and drop it on another row to reorder; a drop below the
  last row moves it to the end.

## Not in v1 (next steps)

- Setting the two points by clicking on the chart instead of typing them.
- A quick "shift by N weeks" mode without picking absolute dates.
- Alt + wheel directly over the line on the chart (today the wheel works
  only over the name in the symbol bar; on the chart Alt + wheel belongs
  to the tilted grid).
