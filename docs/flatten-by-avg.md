# Flatten the chart by an average

Status: implemented, v1.

## Goal

Same idea as [flatten by line](flatten-by-line.md), but the line that
becomes horizontal is an `Average` indicator (Avg). Every series is moved
vertically, so the average turns into one flat line and the pair shows
only its distance to the average (price - avg). Candle sizes do not change.

## UI

Right-click the Avg row in the right bar -> `Flatten by avg`. While this
average is active the same menu item reads `Unflatten`.

Turning it off works the same way as for flatten by line: the common
`Unflatten` row in the right bar (under the tilted grid rows) or the
`Unflatten` item in the menu. Only one flatten is active at a time:
flatten by line replaces flatten by avg and the other way round.

The item is shown only for plain averages (the rows that react to
Alt+wheel). `Average max/min` rows do not get it.

## The shift

The average values are already in display space (10 points per pip,
mirror applied). The anchor is a time `A`:

    level    = round(avg(A))
    shift(t) = level - avg(t)

So at the anchor time nothing moves. Between two average minutes the
value is interpolated linearly on the chart time axis (virtual time when
"No weekends" is on). Before the first average minute and after the last
one the edge value is kept, so there is no jump at the data edges.

The anchor is the time in the middle of the screen at the moment of the
click, clamped to the average data (and to the play cut in game mode).
It is saved in the chart state (`FlattenSymbol` + `FlattenAnchorUnix`),
so it survives a restart and tab switches. When the average window is
changed with Alt+wheel, the level is taken again at the same anchor time.

The average data used for the shift is exactly what the Avg line draws:
history minutes, the live tail, and the extension to the end of the day.
In game mode the play cut is applied the same way, so no future data is
used.

## Rendering

- Candle lines: one shift per column. For a column with average data it
  is `level - column mean of the average`, the same column value the Avg
  line has. Columns without average data use the interpolated shift.
- The Avg line itself is drawn as `level` in every column, so it is
  exactly flat at every zoom.
- The last price line of each series is moved by the shift of its last
  column (this also applies to flatten by line).
- Drawings, deals, comments, Trading Central levels and the cursor use
  the per-minute shift. Drawing segments are cut at every column border
  inside the screen, so a straight drawn line bends with the chart.
- The price under the cursor uses the same column shift as the candles
  under it, so it shows the true price.
- Vertical panning limits grow by the largest `|level - avg|`.

On `Flatten by avg` and on `Unflatten` of an average the chart is
centered vertically on the source pair of the average (zoom stays the
same), otherwise a part of the bent picture may end up off screen.

## Code

- `Chart/FlattenMap.cs` - abstract base: `ShiftAt`, `ColumnShifts`,
  `CutsBetween`, `MaxAbsShift`.
- `Chart/LineFlattenMap.cs` - flatten by line.
- `Chart/AverageFlattenMap.cs` - flatten by avg, reads the Avg
  `CandleHistory` (minutes, live, extension) without copying it.
- `ChartView.SetFlattenAverage` / `IsFlattenAverage`; `CurrentFlatten`
  rebuilds the map before each render when the average data, the live
  tail, the play cut, the weekend mode or the anchor changed.

## Limits

- The shift is one value per column. When one column holds many minutes
  (zoomed out), the candles in it keep their own range inside the column,
  only the column as a whole is moved.
