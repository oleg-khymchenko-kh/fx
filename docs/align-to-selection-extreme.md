# Align the symbols by the high or the low of a selection

Status: implemented, v1.

## Goal

Select a time range on the chart (Shift + drag), right-click inside it and
pick `Align to max` or `Align to min`. All symbols move vertically so that
their extreme inside the selection sits on one 100 pip grid line, close to
where the cursor was.

This is the range version of the double-click `Align to grid`: that one
matches the symbols at one column, this one matches them at their high (or
low) of the selected range.

## Which symbols move

Every series on the chart, except:

- moving averages,
- zigzags,
- hidden symbols,
- symbols with no candles inside the selection (drawings, an indicator
  whose data is not loaded yet).

Averages and zigzags are skipped on purpose. Their offset is stored
relative to the source symbol, so they follow the source and keep sitting
on top of it. Aligning them by their own extreme would tear them off.

## The extreme

Per symbol, over all minutes of the selection (stored plus live):

    extreme = max(candle.Max + flattenShift(t))     for Align to max
    extreme = min(candle.Min + flattenShift(t))     for Align to min

Values are display points (pip scale and mirror already applied), the same
space the chart draws in, so a mirrored pair is aligned by what is on the
screen, not by its raw price. The flatten shift is added for the same
reason - when the chart is bent by a line, the highest point on the screen
is what gets aligned.

## The target grid line

The grid step is 100 pips (`ChartRasterizer.GridPriceStepPoints`, 1000
points). Grid lines are at absolute prices, so panning the view moves the
data and the grid together - only the series offsets can put a candle on a
line.

`Align to max`, with `cursor` the price under the right-click:

1. `A` = the first grid line at or above `cursor`.
2. If `A` is inside the viewport - `A` is the answer, nothing else moves.
3. Otherwise (`A` is above the top edge) take `B = A - 100 pips`:
   - `B` above the middle of the viewport - `B` is the answer.
   - `B` below the middle - take whichever of `A` and `B` is closer to the
     cursor and pan the chart so that this line ends up at 20% of the
     viewport height from the top edge.

`Align to min` is mirrored: `A` is the first grid line at or below the
cursor, the check is against the bottom edge, and the pan puts the line at
20% of the height from the bottom edge.

## The offsets

The target price is the same for every moved symbol, so

    effectiveOffset(symbol) = target - extreme(symbol) - priceOffset

Offsets are stored relative to the source symbol, exactly as in
`AlignSeriesToGrid`: a symbol that has a source keeps
`stored = effective - effective(source)`, where the source's effective
value is the new one when the source moved too, and the current one when
it did not. So a shift symbol aligned together with its pair keeps a valid
relative offset.

The new offsets go into the chart state (`SeriesOffsetsChanged`), so they
survive a restart like any other offset.

## UI

The menu opens on right-click inside the selection band. Pivot points and
drawn lines still win the click - they are small targets and are checked
first, the range menu is the fallback. `Escape` clears the selection as
before.
