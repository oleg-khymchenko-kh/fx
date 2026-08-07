# ZigZag: a point sequence over the source pair

Status: implemented (FXViewer/Compute/ZigZagSymbol.cs,
FXViewer/Storage/ZigZagStore.cs, FXViewer/Chart/ChartView.cs).

Replaces the old candle based ZigZag (derived M1 candles plus two pivot
flag bits per minute). Nothing of that is left: the flag bits are gone
from the record layout, the derived candles are gone, and old `.m1`
files of a ZigZag symbol are deleted on the next chart load.

## Goal

A derived symbol that keeps only the important extremums of a pair and
draws straight lines between them. Moves smaller than the threshold are
noise and must not produce points.

## Data model

The whole symbol is one flat list of points, ordered by time. A point is
`(minute on the time axis, value in raw source points)`. Raw source
points are the same 1/100000 units as the source candles, so 1 pip = 10
units on an FX pair. Display goes through the source pair's transform
(pip scale + mirror) and the source offset chain, exactly like a Drawing
symbol.

There are no candles and no per-week split. One continuous sequence
covers the whole history. A segment may cross a weekend, a holiday, or
any hole in the source data - it is drawn as a plain straight line, the
missing data does not interrupt it.

File: `data/<SYMBOL>/zigzag.json`, format `[[unixSeconds, value], ...]`.
It sits in the same per-symbol folder as candle data, so Rename moves it
and Delete removes it with the folder.

## The rule

Three parameters per indicator:

- `Limit1` - pips, the reversal threshold,
- `Limit2` - pips, the pullback threshold (`Limit2 <= Limit1`, checked
  by the dialog),
- `Limit2Delay` - minutes of **trading** time (weekends removed, the
  same virtual axis as `No weekends`, see docs/no-weekends.md).

Two ways a point appears; points strictly alternate high, low, high,
low either way. Thresholds are converted to raw points with the source
pip size (50 pips on EURUSD = 500 raw points).

**Rule 1 (reversal).** A local extremum where the price moved at least
`Limit1` away on both sides: `Limit1` into it and `Limit1` out of it.

**Rule 2 (pullback inside a trend).** The scenario is: a strong move, a
small pullback, then the strong move resumes. The pullback low (for an
up-trend; mirrored for a down-trend) becomes a point, together with the
top it fell from, when all of:

- the top is at least `Limit2` above the pullback low (the counter-move
  is against the previous movement),
- the low sits at least `Limit2Delay` trading minutes after the top - a
  fast V-shaped dip is not a pullback,
- there is no other extremum of the same kind to the right: no lower
  low happens before the trend resumes (a lower low simply becomes the
  new candidate, with its timer measured from the last top before it),
- the price then leaves the low by `Limit1` in the direction of the
  original trend without breaking below it.

The pair (top, pullback low) keeps the alternation: the top is a high,
the pullback low is a low. Since the pullback is smaller than `Limit1`
and the resume leg is at least `Limit1`, the resumed move always takes
out the old top. A pullback leg is therefore always bracketed by two
legs of at least `Limit1`; two pullback legs can never follow each
other.

## How it is computed

Every candle is turned into two ticks, its `Min` and its `Max`, both
stamped with the candle minute. The order of the two ticks is guessed
from the previous candle: price is continuous, so the extremum closer to
where price already was is assumed to be hit first. Concretely: if the
`Avg` of the previous candle is closer to this candle's `Max`, the `Max`
tick goes first, otherwise the `Min` tick. For the very first candle,
and on an exact tie, the fallback is the candle's own `Avg` against the
middle of its range (`Avg` below the middle means `Max` first). There is
no Open or Close in the DB, so the real order inside a minute is not
knowable; the previous candle is the strongest available signal.

`CollectSwings` runs one linear state machine over the tick stream. In
an up-trend it tracks:

- `High` - the running maximum since the last point (rule 1 confirms it
  as a point when the price drops `Limit1` from it),
- the **deep** pullback candidate: the lowest low since the last point
  was confirmed, anchored to the top it fell from (a new maximum does
  NOT clear it - the trend breaking the old top does not cancel the
  pullback; a lower low re-anchors it to the latest top),
- the **late** pullback candidate: the lowest low after the last new
  maximum. It only matters when the deep candidate fails its `Limit2` /
  delay check at confirmation time: then the late dip (measured from
  the newer top) takes its place and is checked against the same bar.

When the price reaches `deep low + Limit1`, the deep candidate is
checked: `Limit2` amplitude and `Limit2Delay` trading minutes from its
anchor top. Pass - both the anchor and the low are emitted as points.
Fail - the candidate is dropped and the late one, if any, is promoted.
A pullback of `Limit1` or more is an ordinary rule-1 reversal instead.

5.7M minutes of EURUSD produce 8 659 points in ~290 ms at
`Limit1 = 50, Limit2 = 20, Limit2Delay = 90`.

## Anchors

The first point is `(first minute, Avg of the first candle)` and the
last is `(last minute, Avg of the last candle)`. They pin the line to
the real start and end of the history, so the line reaches the current
price instead of stopping at the last confirmed extremum. Their legs are
free of the rules; every other leg follows them. An unconfirmed pullback
at the very end of the history (the resume leg has not reached `Limit1`
yet) produces no points - it appears on the next rebuild once the move
completes.

## Guarantees

Checked against the full EURUSD and USDCHF histories (2011-2026, 5.7M
minutes each) for seven parameter sets, including the degenerate
`Limit2 = Limit1` (rule 2 off) and `Limit2 = 0.1 pip, delay 0`:

- every point is the true extremum of its span (from the previous point
  to the next one),
- every inner leg is either at least `Limit1`, or at least `Limit2`
  with at least `Limit2Delay` trading minutes of duration,
- every pullback leg is bracketed by two legs of at least `Limit1`,
- points strictly alternate high, low, high, low,
- point minutes never decrease. Two points may share one minute (a news
  minute); this draws a vertical step at that minute.

## Rendering

No candles are involved. The point list is rasterized into the chart
bitmap after the candle series, the same way drawing polylines are
(`ChartRasterizer.DrawSegment`: Liang-Barsky clip to the viewport, then
1px DDA in the series color). Segments fully outside the visible time
range are skipped before projection.

ZigZag extents participate in the global time and price range and in the
auto scale, so the view can fit a ZigZag that reaches beyond the pair.

## Compute

`ZigZagSymbol.Generate` reads the whole source history, builds the
points, runs the merge pass, deletes the target symbol folder and writes
`zigzag.json`. Generation is always a full rebuild - the point list is
one sequence, so there is no cheap tail update.

Triggers: the "Compute derived" button on the Connection tab, `Rebuild`
in the symbol bar menu, and creating or editing the indicator with a
changed source, `Limit1`, `Limit2` or `Limit2Delay`. There is no
`Refresh` for ZigZag (the menu item and the dialog button are hidden),
because a refresh would be a full rebuild anyway and would wipe hand
edits.

## Editing on the chart

Points are edited with the mouse, directly on the chart. Any minute and
any price are allowed - a point does not have to sit on a source candle.

- Hover: when the mouse is within `EditHitRadiusPx` pixels (config.json,
  default 3) of a point, a small circle is drawn at the point in the
  series color.
- Drag: left-press on a point and move. The two legs into and out of the
  point follow the mouse as straight preview lines; the original two
  legs are hidden for the whole drag, and the circle is hidden too.
  Release commits. The time is clamped strictly between the neighbor
  points (at least one minute away from each), so points cannot pass
  each other. A member of a same-minute pair (a vertical step) may stay
  at its own minute, so its value can be edited without destroying the
  step. The first and the last point have a free side and can be dragged
  into the past or the future without limit. A release without an actual
  move commits nothing.
- Zoom during a drag is ignored. Losing mouse capture cancels the drag.
- Right-click on a point opens a menu:
  - `Delete point`: the two legs (prev -> point -> next) become one leg
    (prev -> next). Disabled when only two points are left.
  - `Add point left`: a new point in the middle of the previous leg, on
    the line. Disabled when the leg is shorter than two minutes.
  - `Add point right`: same for the next leg.
- Right-click on the indicator's label in the symbol bar offers
  `Align to source`: shifts the whole indicator vertically so it
  overlays its source symbol.
- Points that sit on a straight line (a just-added midpoint, or any
  point whose kink is under 1.5 px at the current zoom) are always drawn
  as circles, otherwise they would be invisible and impossible to grab.
  The circles appear only when both neighbors are at least 8 px away, so
  a zoomed-out chart is not flooded. The first and last point are never
  circled: the line end is visible by itself.
- Point time resolution follows the zoom: one pixel column is `K`
  minutes, so a dragged point lands on a whole column time. Zoom in for
  exact minutes.

## Commit pipeline

Points always live in RAW source values. The chart converts them to
display units only for drawing and hit-testing, so mirrored or
pip-scaled sources (USDJPY, USDCHF) stay lossless.

The chart raises `PivotEditRequested(symbol, points)` with the full new
point list in raw values; only the dragged point's new value is
converted from the cursor position (a pure time drag keeps the raw value
untouched). `MainWindow` then:

1. normalizes the list (`NormalizeEditedPoints`): snaps times to
   minutes, drops exact duplicates, and drops a same-minute point that
   does not reverse direction (such a point adds nothing visually),
2. writes the whole list to `zigzag.json` (atomic temp file + move) on a
   background thread,
3. swaps the new points into the chart (`ChartView.ReplacePivots`)
   without touching zoom, offsets, or hidden state.

Edits are refused while a DB operation, a history download, a chart
load, or a previous edit commit is running (a log line explains; points
stop hit-testing while a commit is in flight).

## Migration from the old candle format

On chart load, every ZigZag indicator that still has `.m1` files in its
folder gets the whole folder deleted (a `zigzag.json`, if any, is
written back afterwards) and a log line asks for a rebuild. The old
pivot flag bits are not read: the two bits stay unused in the record
layout, and `FlagProvisional` keeps its bit 4 so existing candle files
of every other symbol open unchanged.

## Known limits

- Recompute (Compute derived, Rebuild, or Edit with changed data)
  rebuilds the symbol from the source history and wipes all manual
  edits. This is by design: an edit is a correction of a generated
  result, not a separate data set.
- The last leg is provisional by nature: it repaints as new data
  arrives. There is no live update, only a manual rebuild.
- The tick order inside a minute is a guess from the previous candle.
  With M1 data no better answer exists. A wrong guess on a news minute
  with a range above `Limit1` changes the local shape (a vertical step
  versus a plain leg).
- `Limit2Delay` counts trading minutes, so weekends are free, but
  holidays and data holes are not: `WeekendCompressor` only removes the
  weekly Friday-to-Sunday gap.
- The delay is measured to the FIRST minute the pullback extremum
  traded at. If the same extreme price is touched again later, the
  later touch does not restart the timer.
- Inside a long leg the straight line can run far from the real price.
  The price stays between the two point values, but if it spends most of
  the leg near one end and then moves fast, the straight segment passes
  through prices that never traded at that time. This is the cost of
  "extremums only".
- A ZigZag symbol is not a candle series, so everything that reads
  candles skips it: the price under the cursor, selection statistics,
  `Align to selection max/min`, `Find similar`, and the lazy year
  loader. The whole point list is loaded at startup in one read.
