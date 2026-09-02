# Drawing symbols: free-hand polylines

Status: implemented, v1 (lines only).

## Goal

A new indicator type `Drawing` next to `ZigZag`. The user draws separate
polylines directly on the chart. A straight line is just a polyline with
one segment; storage is always polylines, so a line can later become a
longer polyline by adding segments.

## Data model and storage

No candles. The data is vector graphics: a list of polylines, each an
ordered list of points. A point is (minute on the time scale, value in
raw points of the source pair, same 1/100000 units as the source
candles). Display uses the source pair's transform (pip scale + mirror),
so the drawing lives in the same display space as the source symbol and
follows its offset (offsets are stored relative to the source).

The value is fractional (9 decimals of a point) so that a point can sit
exactly on a parallel slope, see docs/parallel-lines.md.

File: `data/<SYMBOL>/drawing.json`, format
`[[[unixSeconds, value], ...], ...]` (lines -> points -> pair). The file
sits in the same per-symbol folder as candle data, so Rename moves it
and Delete removes it with the folder.

A line has one flag: normal line or **level** (horizontal, see below).
A level point is written as a triple `[unixSeconds, value, 1]`. The flag
belongs to the line, not to the point: `DrawingStore.FromRaw` treats the
line as a level if any of its points carries the 1 and then sets
`PivotPoint.Level` on all of them. Old files have pairs only, so they
load as normal lines, and a file with no levels is written exactly as
before. Notes keep the same raw format, so a level survives a note too.

## Creation

The same Add/Edit dialog as ZigZag (right-click a pair -> Add, or the
"+ Add" button). Type combo has `ZigZag` and `Drawing`. For `Drawing`
the limit field is hidden and ignored. Creating (or changing
source/type of) a drawing symbol resets it to an empty drawing file.
Renaming keeps the data. Deleting removes the folder.

## Drawing UI

Two ways to start a line:

- Right-click the drawing symbol's row in the symbol bar -> `Draw line`
  or `Draw level`.
- Right-click empty chart space -> `Draw line - <name>` and
  `Draw level - <name>`. There is one pair of items per drawing
  indicator that is shown right now (hidden ones are skipped), then a
  separator and the `Forecast <day>` item. This way the first vertex is
  already placed where the right-click was, so the user only drags the
  mouse and clicks once more.

Then:

- Each left-click on the chart adds a vertex (time snapped to the
  current column's minute, price from the cursor, converted to raw
  source points through the offset chain and transform).
- A plain click ends the line as soon as it has 2 points. `Shift` +
  click adds an in-between vertex and keeps drawing, so a polyline is
  Shift-clicks for the middle and a plain click for the end.
- Started from the symbol bar there is no first vertex yet, so the
  first plain click only places it and the second one ends the line.
- A level has no in-between vertices: `Shift` is ignored, any click
  ends it (see "Levels").
- A preview polyline and a dashed segment to the cursor follow the
  mouse; the preview survives zoom and pan (re-projected on render).
- Right-click commits too (without adding a vertex). A line needs at
  least 2 points, otherwise it is discarded.
- Escape cancels the current line.
- Commit appends the polyline to drawing.json and re-renders.

## Levels

`Draw level` draws the same polyline, but horizontal, and it stays
horizontal forever:

- The first point sets the price (the right-click position, or the
  first click when started from the symbol bar). Every next point keeps
  that value, so only the time comes from the mouse; the dashed cursor
  segment is horizontal too.
- The next click ends the line whatever `Shift` does, so a level is
  always two points.
- Dragging a vertex of a level changes the time of that vertex only:
  the price is kept, so the mouse can wander vertically without moving
  the level off its price. Dragging the line body is what moves a level
  up and down (all points by the same delta, so it stays flat).
- Shift (keep the slope) is ignored on a level: it has no slope.
- `Add point left` / `Add point right` and `Delete point` work as usual;
  the new point inherits the flag and the price.
- Dragging the line body moves it as a whole, same as a normal line.

The flag lives in `PivotPoint.Level`, is set on every point of the line,
and is carried through edits with `with { }` instead of building a new
point, so no edit path can drop it.

While drawing, panning by left-drag and pivot editing are disabled;
wheel zoom works.

## Rendering

Polylines are rasterized into the chart bitmap after the candle series
(ChartRasterizer.DrawSegment: Liang-Barsky clip to the viewport, then
1px DDA in the series color). Coordinates: x = minute / K - startBucket
(fractional), y from the display value through the same top/offset/rows
math as candle lines. Drawing extents participate in the global time and
price range, so the view can scroll to lines drawn in the future.

## Selection and editing

- A left-click within 2 px of a committed line selects it. Circles
  appear on every vertex of the selected polyline.
- Dragging a circle moves that vertex (time and price, unconstrained,
  time snapped to the column minute).
- Holding Shift while dragging a vertex keeps the line slope: only the
  time follows the mouse, the price is recomputed so the point slides
  along the original line. The slope anchor is the previous vertex
  (the next one for the first vertex); slope is measured in trading
  time, so the visual slope on the chart is preserved across weekend
  gaps. Shift can be pressed or released mid-drag. A Shift-click that
  starts on a vertex of the selected line begins this drag instead of
  a range selection.
- Dragging the line body (within 2 px, not on a circle) moves the whole
  polyline; all points shift by the same time/price delta.
- During a drag the raster line is hidden and a WPF preview polyline
  follows the mouse; release commits (saves drawing.json, re-renders),
  a release without movement keeps the selection and changes nothing.
- Escape or a click away from the line deselects (the click falls
  through to normal panning).
- Delete removes the selected line after a Yes/No confirmation dialog.
- Right-click a line body opens a menu with `Clone 20 pips up` (see
  below, not shown for levels) and `Flatten by line` / `Unflatten`: the
  chart is bent vertically so that this line becomes horizontal, see
  docs/flatten-by-line.md. Both items are also in the per-point menu.
  Below them, after a separator, the menu repeats the common chart block
  (`Measure distance`, `Draw line - <name>` / `Draw level - <name>`,
  `Forecast YYYY-MM-DD`), so hitting a line never hides those.
- Right-click a vertex opens a per-point context menu: `Delete point`,
  `Add point left`, `Add point right`, `Clone 20 pips up` (the click
  selects the line first, so the circles show which line is edited).
  - `Delete point` removes just that vertex. If only one point would be
    left, the whole line is removed instead (a 2-point line disappears
    when either point is deleted).
  - `Add point left` / `Add point right` insert a new vertex next to the
    clicked one in the polyline order. Between two existing points it is
    their midpoint; at an end point it extends the line by mirroring the
    end segment. Drag the new vertex afterwards to place it.
  - `Clone 20 pips up` appends an exact copy of the whole line, moved
    20 pips higher, and selects the copy so it can be dragged right
    away. Levels have no clone item - the line body carries the price,
    so a copy of a level is just another level to draw.
  - Edits go through the same drawing.json save + re-render path as a
    vertex drag, and are refused while a DB op / download / load runs.
- While a line is drawn, or a vertex of it is dragged, lines parallel to
  the segment under the cursor are drawn 2 px thick and the committed
  point is placed exactly parallel to the first of them, see
  docs/parallel-lines.md.
- Selection circles re-project on zoom/pan/offset changes. Wheel zoom
  during a selection drag is ignored (same as pivot drags), otherwise
  the accumulated drag delta would be reinterpreted under the new
  scale. Hiding the symbol or reloading the series drops the selection.
- Commits are refused (with a log line) while a DB operation, history
  download, or chart load is running.

## Not in v1 (next steps)

- Other shapes than polylines.
