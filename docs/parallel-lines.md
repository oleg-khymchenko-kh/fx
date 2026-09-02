# Parallel line snapping

Status: implemented, v1.

## Goal

While a drawing line is being made or a point of it is being dragged,
the app looks for already drawn lines that are parallel to the segment
under the cursor. Every parallel line found is redrawn 2 px thick, and
when the edit ends the moved point is placed so that the segment is
exactly parallel to the first line found.

Works for `Drawing` symbols (docs/drawing-symbols.md).

## When it runs

- Drawing a normal line: the rubber-band segment from the last placed
  vertex to the cursor. The first vertex has no segment yet, so nothing
  is searched until the second point is being placed.
- Dragging a vertex of a selected line, after the mouse has actually
  moved.

It does not run for:

- Levels. A level is horizontal by definition, there is no slope to fit.
- `Shift` + vertex drag. Shift already keeps the original slope.
- Dragging the line body. That moves every point by the same delta, so
  the slope never changes.

## The active segment

One segment is fitted at a time:

- Drawing: last placed vertex -> cursor.
- Vertex drag: the anchor neighbour -> cursor. The anchor is the
  previous vertex, or the next one when the first vertex is dragged.
  This is the same anchor rule as the `Shift` slope-keeping drag.

The moving end is taken at the pixel x of the **snapped minute**, not at
the raw cursor x, because that is where the point will really land. Its
y is the cursor y.

## Candidates

Every segment of every visible `Drawing` line of every symbol, in symbol
order, then line order, then segment order. The line being edited is
skipped whole. Segments shorter than 1 px are skipped. Levels count as
candidates - a level is a horizontal line like any other.

## Parallel test

The test is a sign-change bracket around the cursor, so the tolerance is
exactly one pixel of mouse movement:

- If the active segment is closer to horizontal than to vertical
  (`|dx| >= |dy|`), the two probe positions are one pixel above and one
  pixel below the cursor.
- Otherwise the probes are one pixel left and one pixel right. A
  horizontal probe moves the point by whole minutes; when both probes
  land on the same minute (very deep time zoom) the test falls back to
  the vertical probes.

For each probe the angle between the active segment and the candidate is
measured modulo 180 degrees (lines have no direction). If one probe
gives a positive angle difference and the other a negative one, the
cursor sits on the parallel position and the candidate is a hit. A
difference above 45 degrees is rejected, which keeps a near
perpendicular candidate from looking like a sign change at the +/-90
degree wrap.

All hits are highlighted. The snap uses the first one.

## Highlight

Hits are drawn as 2 px polylines on the edit canvas, in the colour of
their own symbol, on top of the 1 px raster line. Only the matching
segment is thickened, not the whole polyline. The highlight is
re-projected on every render, so pan and zoom keep it on the line.

## Final placement

When the point is committed (a click while drawing, mouse release after
a drag) and a hit is present, the point value is recomputed instead of
taken from the cursor:

- The minute stays an integer minute, exactly as before. It is the
  cursor's column, and it is never changed by the snap.
- The value is solved so that the pixel slope of the active segment
  equals the pixel slope of the candidate: `y = anchorY + m * (x -
  anchorX)`, then y is converted back to a display value and to raw
  source points.

The solved value is a real number, so the fit is exact rather than
rounded to a whole storage point. The point moves at most one pixel from
where the cursor was, because the snap only fires inside the one pixel
bracket.

If the candidate segment is vertical on screen, or the moved point lands
in the same column as its anchor, there is nothing to solve and the
cursor value is kept.

## Storage

Line points now carry a `double` value in source points instead of an
`int`, and `drawing.json` stores it with 9 decimals
(`DrawingStore.ValueDecimals`), which is a ten-digit pip. Old files are
plain integers and load unchanged; note snapshots use the same format.
The whole point of the fractional value is the fit: at the deepest
vertical zoom one storage point is about 6 screen pixels, so an integer
value could not express a parallel slope.

Two side effects of the same change:

- Dragging a vertex or a line body no longer rounds the value to a whole
  display point, so the line follows the cursor smoothly at deep zoom.
- `Add point left` / `right` and `Clone 20 pips up` keep fractional
  values instead of rounding them.

## Known limits

- Only the anchor segment is fitted when a middle vertex of a polyline
  is dragged. The other adjacent segment is not checked.
- With `Flatten by line` on, angles are measured from the two segment
  ends, so a segment that crosses a flatten cut is treated as its chord.
