# Levels2 indicator

Type `Levels2`. Swing highs and lows of a pair as two-color dashes at the
right edge of the chart. The dash tells how strong the move into the level
was (left part) and how strong the reversal after it was (right part).

No storage: the levels are computed from the source pair's minutes on every
redraw of the right-edge overlay, like Density and ZigZag levels.

## Settings

- **Source**: a pair (or any symbol with candles), the same list as Density,
  plus every Shift indicator. On a Shift source the levels are found in the
  shifted copy: its candles already sit on the chart time axis and hold
  display values (flip included), so the lookback, the cursor anchor and
  the dash rows follow the copy, and they move with it when the copy is
  nudged (Alt + wheel, Alt + drag, a Find result). In the symbol bar such
  a Levels2 sits under its Shift, inside the pair's group, like ZigZag
  levels under their ZigZag.
- **Lookback**: a number plus Minutes / Hours / Days. The levels shown are
  the swings whose minute is inside `[anchor - lookback, anchor]`. The window
  is counted in trading minutes (weekends removed), the same as ZigZag
  levels. A selection on the chart replaces the window. Default 3 days
  (`DefaultLevel2Period`, separate from the 5 days of ZigZag levels).
- **Left table** and **Right table**: rows of `pips -> pixels`. Rows can be
  added and deleted freely, at least one row per table. Pips must be
  positive, pixels 0 to 500, both may have decimals (`12.5`, `12,5`). The
  rows are saved sorted by pips.
- **Price rose** color (`ColorArgb`, default green) and **Price fell** color
  (`SellColorArgb`, default orange).

Defaults:

| Left pips | px | | Right pips | px |
|---|---|---|---|---|
| 20 | 0 | | 30 | 10 |
| 40 | 10 | | 50 | 20 |
| 60 | 20 | | 100 | 30 |
| 100 | 30 | | | |

These are the values the user settled on for EURUSD and GBPUSD
(2026-10-04); before that the defaults were 10/20/50/100 pips at
0/5/10/15 px on the left and 20/50/100 at 5/10/15 on the right.

## What is a level

Every minute in the window is a candidate top (its high) and a candidate
bottom (its low). Values are display values, so on a mirrored pair a top
is a top of the drawn line. One pip is 10 display points.

For a top with high `H` at minute `k`:

- **Move into it** (approach): go back from `k - 1` until the first minute
  whose high is `>= H`. The approach is `H - lowest low` of the minutes
  passed on the way (minute `k` itself and the stopping minute are not
  counted). If no higher minute is found, the walk stops at the start of
  the data or at the lookback limit below.
- **Reversal after it**: go forward from `k + 1` until the first minute
  whose high is `> H`, but never past the anchor. The reversal is
  `H - lowest low` of the minutes passed.

A bottom is the mirror (lows and highs swapped). The `>=` going back and
`>` going forward make a double top with a small dip between count once:
the earlier minute gets the full reversal, the later one gets only the dip
as its approach and is dropped by the left minimum.

A candidate is a level when

- approach `>=` the smallest pips of the left table, and
- reversal `>=` the smallest pips of the right table.

So with the defaults a swing reached by less than 20 pips, or one the price
left by less than 30 pips (up to the cursor), is not a level. The right part
grows while the cursor moves forward, so a fresh swing appears once the
price has turned away from it far enough.

## Dash

- Left part length = left table at the approach pips, right part length =
  right table at the reversal pips.
- Between two rows the length is linear: with the defaults 80 pips gives
  25 px. Above the last row it stays at the last row's pixels (200 pips
  gives 30 px). The smallest row's pixels may be 0, then the left part is
  not drawn but the level still counts.
- Colors: a top (price rose into it) has the left part in the "price rose"
  color and the right part in the "price fell" color. A bottom is the
  other way round.
- All dashes of one indicator share one split column at `area right -
  ceil(max pixels of the right table)`. Left parts grow left from it, right
  parts grow right from it, so the longest right part ends at the right
  edge of the indicator's area (see below).
- A fractional length draws the last pixel with that much opacity.
- One pixel tall, at the row of the level's price. Levels are drawn oldest
  first, so a newer level covers an older one on the same row.

## Several Levels2 at once

Every shown Levels2 indicator gets its own area, so two of them never
overlap. The area width is `ceil(max pixels of the left table) + ceil(max
pixels of the right table)`. The first Levels2 in the series order (the
order of the right bar) sits at the right edge of the chart, the next one
left of it with a 4 px gap, and so on. The area is kept even
when the indicator has no levels in the current window, so the others do
not jump while the cursor moves. A hidden indicator takes no area.

### Clean strip

The whole strip taken by the Levels2 areas (all areas plus their gaps, at
the right edge) is a clean background: white with the horizontal price
grid lines only, the same as on the rest of the chart. The vertical time
lines of the grid are not drawn there. Instead:

- every Levels2 area has a weak vertical line (`#EEEEEE`) through its
  split column, the column where the left color turns into the right one;
- a strong vertical line (the day grid color `#E0E0E0`) separates the
  indicators: one in the middle of the 4 px gap left of every area, so
  there is one between every two neighbour areas and one at the left edge
  of the whole Levels2 zone. There is none at the right edge of the chart.

Nothing else of the chart is drawn there: no session or weekend
backgrounds, no price lines of the series, no drawings, deals, calendar,
forecast, comments or bottom panels. In code the chart raster is finished
first and then `ChartRasterizer.RenderPlainGrid` repaints the strip; the
marker and edit canvases (pivot markers, selection band, draw preview) are
clipped at the left edge of the strip. The crosshair, the right-edge
overlay (Levels2, Density, Volume, order book, ZigZag levels) and the
labels at the top right still show there.

The areas count only Levels2 indicators. Density, Volume, the order book
and ZigZag levels still draw from the right edge of the chart as before.

## Speed

The finder (`Chart/Levels2Finder.cs`) uses monotonic stacks, so one redraw
is linear in the window size. Going back past the window start is needed
only for the tops that are higher than everything before them in the
window. That walk stops at the first higher minute, or as soon as the
approach reaches the last row of the left table (the length cannot grow
any more), and never goes more than 50 000 minutes back. Measured on
EURUSD 2025-2026 with the Debug build: about 1 ms per redraw for a 5 day
window, 3.5 ms for 20 days. The result was checked against a brute force
scan on 120 random windows of EURUSD and GBPUSD.

## Find by levels

The Find dialog of a Shift indicator (docs/find-similar.md, "Levels2
search") has a `Levels2` search type: it takes the dashes a Levels2
indicator shows for the chart selection as the pattern and looks for a
past window of the indicator's pair with a similar set of dashes.

## Code

- `IndicatorSymbol`: `Level2LeftSteps`, `Level2RightSteps` (`LevelStep` =
  Pips, Pixels), `Period` / `Unit` for the lookback.
- `MainWindow`: `DisplayConfig.IsLevels2`, series with `Levels2Panel`,
  `Level2Left` / `Level2Right` (`LevelSizeTable`).
- `ChartView.UpdateDensityOverlay` calls `DrawLevels2`
  (`Chart/ChartView.Levels2.cs`). The label at the top right shows the
  lookback. The labels of all right-edge indicators sit in one panel at
  the top right whose first letters start 5 device pixels below the top
  edge of the chart (the font's space above the capitals is taken off).
- `SymbolEditorWindow`: `Levels2Params` panel with the two tables. The
  Source list for this type is the pair list plus the Shift indicators
  (`IndicatorTypes.SourceMayBeShift`); `MainWindow.DisplayConfigs` emits
  a Levels2 whose source is an indicator right after that indicator, so
  it lands in the source pair's group of the symbol bar.
