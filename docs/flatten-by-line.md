# Flatten the chart by a drawn line

Status: implemented, v1.

## Goal

Take any line of a `Drawing` symbol (a straight segment or a polyline)
and turn the chart so that this line becomes horizontal. The candles are
not rotated and not rescaled - each one is moved vertically only, so its
size and its position relative to the line stay the same.

Rules:

- Only the part of the chart inside the time bounds of the line moves.
- The left end of the line is the anchor: the shift there is zero, so the
  chart before the line stays where it was and the picture is continuous.
- After the right end of the line the shift drops back to zero, so a
  vertical jump of the chart at that point is expected and allowed.
- A polyline works the same way: every segment of it becomes part of one
  straight horizontal line.

## The shift

The line is a list of points `(time, value)`. The points are sorted by
time and their values are converted to display points with the source
pair's transform (pip scale + mirror), the same space the chart draws in.
The anchor is the earliest point:

    shift(t_i) = display(t_0) - display(t_i)

Between two points the shift is interpolated linearly, outside
`[t_0, t_last]` it is zero. Every drawn value gets `+ shift(t)`:

    y = (topPrice - seriesOffset - display - shift(t)) / pointsPerRow

Time is measured on the chart's own x axis: virtual (weekend-compressed)
seconds when "No weekends" is on, real seconds otherwise, so the map is
rebuilt when that switch is toggled. `FlattenMap` (Chart/FlattenMap.cs)
holds the sorted times, the shifts and does the lookup by binary search.

The shift is one number per chart time, so it is applied to **all**
series - pairs, indicators and other drawings alike. Their vertical
distances to each other never change, the whole picture is bent as one.

## Rendering

- Candle lines: `FlattenMap.ColumnShifts` builds one shift value per
  column of the current view (the same column grid all series share) and
  it is passed to the rasterizer in `RenderLine.ColumnShift`. `DrawLine`
  adds it to the chosen value of the column. The jump after the end of
  the line is drawn as a normal vertical connector between two columns.
- Drawing lines: each segment is cut at the vertex times of the flatten
  line (`FlattenMap.CutsBetween`), the original value is interpolated at
  every cut and the shift is added there, so a segment that crosses a
  kink of the flatten line bends with it instead of cutting the corner.
  The flatten line itself collapses to one horizontal line, because at
  each of its own points `display + shift = display(t_0)`.
- Prices under the cursor, `Align to grid` (double click), pivot circles,
  pivot drag, drawing and line editing all go through
  `ChartView.DisplayToY` / `YToDisplay`, which add and subtract the same
  shift. So the cursor still shows true prices and a point dragged to a
  pixel stays under that pixel.
- Vertical panning limits are widened by the largest absolute shift, so
  the moved part of the chart can always be scrolled into view.

## UI

Right-click a drawn line (on its body or on one of its points) ->
`Flatten by line`. The same menu shows `Unflatten` when this line is the
active one. Only one line at a time flattens the chart; picking another
line replaces the previous one.

While the chart is flattened, one common `Unflatten` row appears in the
symbol bar under the tilted-grid rows (and disappears again when it is
clicked), so the chart can always be put back without finding the line first.

The choice is kept in the chart state (`FlattenSymbol`, `FlattenLine` in
the app config), so it survives a restart. It is dropped when the line or
its symbol disappears, and it follows a rename.

Editing the line while it is active is allowed: on commit the map is
rebuilt and the chart re-flattens. Moving the whole line up or down does
not change the shifts (they are relative), it only moves the horizontal
line and the chart with it.

## Not in v1 (next steps)

- More than one flatten line at a time.
- A hint in the symbol bar that the chart is flattened (only the log line
  and the horizontal line itself show it now).
- Keeping the part after the line's end glued (continuing the last shift
  instead of dropping it to zero).
