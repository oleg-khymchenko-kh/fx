# Time zoom: seconds per pixel column

Status: implemented.

## What changed

The chart used to measure the horizontal scale in minutes per pixel
column, so 1 minute per pixel was the deepest zoom. Now the scale is
measured in **seconds per pixel column** (`columnSeconds`), so a minute
can take many pixels.

- Zoomed out: `columnSeconds` is a whole number of minutes (60, 120,
  180, ... snapped by the quantum bands, see docs/chart-line-rendering.md).
- Zoomed in: `columnSeconds` is one of 30, 20, 15, 12, 10, 6, 5, 4, 3,
  2, 1. Each of them divides 60, so a minute is always a whole number of
  pixels: 2, 3, 4, 5, 6, 10, 12, 15, 20, 30, 60 pixels per minute.
- The deepest zoom is 1 second per column, that is 60 pixels per minute.

Everything on the chart is placed by the same rule as before:

```
column x  ->  virtualSeconds = (startBucket + x) * columnSeconds
```

Only the unit of the factor changed, so the grid, the calendar lines,
the tilted grids, drawings, hit testing and the time axis need no
special case for the deep zoom.

## One minute fills its pixels with one value

Data is stored per minute, so a minute has nothing to show inside
itself. Below 1 minute per column the minute is drawn as a flat run:
every pixel of the minute gets the same value, and that value is the
minute's "main" one picked by the usual rule (min, max or avg, see
docs/chart-line-rendering.md).

To keep the pick stable, the work is done at minute level and then
copied to pixels:

1. `ChartColumns.BuildView` builds minute columns.
2. `LineDecimator.ChooseValues` picks min, max or avg per minute.
3. `ChartColumns.Expand` repeats each minute value over its pixels.

`ChartColumns.BuildLine` does all three in one call. Without step 2 on
minutes the decimator would see the copies as separate columns, decide
they repeat nothing new, and draw avg for all pixels but the first.

The bottom panels (spread, volume, price age, entry points) use the same
trick inside their own `Build`: build per minute, then `Expand`.

## No vertical zoom below a minute

Normal wheel zoom also scales the price axis (`PairedVerticalFactor`).
Below 1 minute per column that is off: the wheel changes time only, the
price scale stays as it is. The rule is in `ChartView.OnZoom`: if the
old or the new `columnSeconds` is 60 or less, the vertical part is
skipped. Ctrl + wheel still zooms the price axis alone at any zoom.

## Grid and labels

- Minute grid lines are drawn when a minute is at least 10 pixels wide
  (`columnSeconds <= 6`), dotted every 4th row.
- The time axis adds minute labels (1, 2, 5, 10, 15, 30 minutes) below
  1 minute per column. Hour and day labels are unchanged.

## Saved state

`ChartViewState.ColumnSeconds` holds the zoom. Old configs, tabs and
notes hold `MinutesPerColumn` instead; `RestoredColumnSeconds()` reads
whichever is present, so old saved views open at the same scale. New
saves write `ColumnSeconds` only.

## Edits stay on whole minutes

Candle timestamps are minute aligned, so anything that writes time back
rounds down to a minute: point drags, drawing clicks, and the Alt drag
of a Shift symbol (it only sends whole minutes, so a drag shorter than
one minute of chart time sends nothing).

## Arrow keys move the mouse cursor

The chart has no separate keyboard cursor: the arrows move the real
mouse pointer (`SetCursorPos`), so the crosshair, the readouts and the
hover all follow as if the mouse was moved by hand.

- Left / Right: one pixel column, so one step is `columnSeconds` of
  chart time.
- Up / Down: one pip. Pixels per pip come from the price scale
  (`PipPoints / _pointsPerRow`), so the step follows the vertical zoom.
  The target is snapped to the pip grid: the cursor lands on the next
  whole pip level, not on "current price + 1 pip". If a pip is thinner
  than a pixel at the current zoom, the step falls back to one pixel.

Both work only while the mouse is over the chart (`_cursorOnChart`).
