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

File: `data/<SYMBOL>/drawing.json`, format
`[[[unixSeconds, value], ...], ...]` (lines -> points -> pair). The file
sits in the same per-symbol folder as candle data, so Rename moves it
and Delete removes it with the folder.

## Creation

The same Add/Edit dialog as ZigZag (right-click a pair -> Add, or the
"+ Add" button). Type combo has `ZigZag` and `Drawing`. For `Drawing`
the limit field is hidden and ignored. Creating (or changing
source/type of) a drawing symbol resets it to an empty drawing file.
Renaming keeps the data. Deleting removes the folder.

## Drawing UI

Right-click the drawing symbol's row in the symbol bar -> `Draw line`:

- Each left-click on the chart adds a vertex (time snapped to the
  current column's minute, price from the cursor, converted to raw
  source points through the offset chain and transform).
- A preview polyline and a dashed segment to the cursor follow the
  mouse; the preview survives zoom and pan (re-projected on render).
- Double-click adds the final vertex and commits the line. Right-click
  commits too (without adding a vertex). A line needs at least 2
  points, otherwise it is discarded.
- Escape cancels the current line.
- Commit appends the polyline to drawing.json and re-renders.

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
- Dragging the line body (within 2 px, not on a circle) moves the whole
  polyline; all points shift by the same time/price delta.
- During a drag the raster line is hidden and a WPF preview polyline
  follows the mouse; release commits (saves drawing.json, re-renders),
  a release without movement keeps the selection and changes nothing.
- Escape or a click away from the line deselects (the click falls
  through to normal panning).
- Delete removes the selected line after a Yes/No confirmation dialog.
- Right-click a line body opens a menu with `Flatten by line` /
  `Unflatten`: the chart is bent vertically so that this line becomes
  horizontal, see docs/flatten-by-line.md. The same item is at the bottom
  of the per-point menu.
- Right-click a vertex opens a per-point context menu: `Delete point`,
  `Add point left`, `Add point right` (the click selects the line first,
  so the circles show which line is edited).
  - `Delete point` removes just that vertex. If only one point would be
    left, the whole line is removed instead (a 2-point line disappears
    when either point is deleted).
  - `Add point left` / `Add point right` insert a new vertex next to the
    clicked one in the polyline order. Between two existing points it is
    their midpoint; at an end point it extends the line by mirroring the
    end segment. Drag the new vertex afterwards to place it.
  - Edits go through the same drawing.json save + re-render path as a
    vertex drag, and are refused while a DB op / download / load runs.
- Selection circles re-project on zoom/pan/offset changes. Wheel zoom
  during a selection drag is ignored (same as pivot drags), otherwise
  the accumulated drag delta would be reinterpreted under the new
  scale. Hiding the symbol or reloading the series drops the selection.
- Commits are refused (with a log line) while a DB operation, history
  download, or chart load is running.

## Not in v1 (next steps)

- Other shapes than polylines.
