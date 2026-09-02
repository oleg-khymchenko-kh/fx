# Measure distance

Status: implemented, v1.

## Goal

Right-click the chart -> `Measure distance`, then move the mouse. A
half-transparent rectangle is drawn between the point of the right-click
and the cursor, and a popup at a free edge of the chart says how big that
rectangle is: pips, time span, and which price its edges sit at for every
visible main pair.

It is a ruler, not an indicator: nothing is stored, nothing is saved into
the tab state.

## Drawing

`Q` does the same as the menu row, with the corner at the cursor. It always
starts a fresh measurement: a rectangle that is still being drawn or one
that is already finished is dropped first, so `Q` can be tapped again and
again without closing anything by hand.

1. Right-click on the chart (or press `Q`). That position is one corner of
   the rectangle, and it stays fixed.
2. Move the mouse. The opposite corner follows the cursor, so the
   rectangle grows in both directions - time to the left/right, price up
   and down.
3. Click a second time - left or right button. The rectangle stops
   following the mouse and stays on the chart together with its popup.
   `Escape` before that second click drops the measurement instead.

The corners are kept in chart coordinates, not in pixels: the anchor is a
column time plus a screen price. So panning, zooming and switching
`No weekends` keep the rectangle glued to the same candles. When it
scrolls out of the view, the rectangle and the popup are hidden and come
back when it is on screen again.

The fill and the border reuse the colors of the Shift+drag selection
(`RangeFillArgb` / `RangeEdgeArgb`).

## Closing

The rectangle and the popup live until one of:

- the `✕` button in the popup,
- `Escape`,
- a click on the chart outside the popup (left or right button).

A right-click outside the popup also opens the usual chart menu, so a new
measurement can be started right away. `Measure distance` is part of the
common block that every chart menu ends with, so it is there even when the
click lands on a pivot, a drawing line, a line point or a selection. `Q` replaces it with a new one
without closing it first. A click inside the popup does nothing to the
chart - the popup swallows it.

Switching a tab, reloading the series and `Escape` all drop the
measurement as well.

## The popup

It sits in one of six corners/edges of the chart and never overlaps the
rectangle. The candidates are tried in this order:

1. top centre,
2. top left,
3. top right,
4. bottom centre,
5. bottom left,
6. bottom right.

The first one that does not intersect the rectangle wins. If all six
intersect it (a rectangle that covers the whole chart), the one with the
smallest intersection area is used. The rectangle used for that test is the
part of it that is on screen, so a rectangle whose body is off to the left
does not push the popup away.

The choice is redone on every update, so while the second corner is being
dragged the popup jumps to whichever spot is free.

The zoom chip (`ZoomLevelView`) sits in the top left corner of the same
grid cell, above the chart, so it would show through the popup. While the
popup covers it the chip is hidden: `ChartView` raises
`MeasureLabelBoundsChanged` with the popup rectangle (or `null` when there
is no popup), and `MainWindow` sets the chip to `Hidden` when the two
rectangles intersect. `Hidden`, not `Collapsed`, so the chip keeps its
size and the next overlap test still works.

Its width is fixed, so it does not jump around while the digits change.
The width is computed once, on the first measurement, from the widest text
each line can ever hold (`MeasureLabelWidth`):

- pips line: `-99999.9 pips   9999:23:59` in the bold Consolas of that line,
- time line: `00-WWW-00 00:00  →  00-WWW-00 00:00` (`W` is wider than any
  month name or digit in the UI font),
- table: `WWWWWWWW` for the symbol plus two `00000.00000` price cells,
  each with the 14 dip gap a stats cell carries,

plus the border and padding (22 dip) and the close button with its gap
(32 dip). The widest of the three lines wins. Everything is measured with
`FormattedText` in the real fonts, so the number follows the system font
and DPI instead of being a guessed constant.

That widest-case number is then cut to 60% (`MeasureWidthScale`), because
the real texts are far shorter than the widest ones (`1.16234`, not
`00000.00000`). To keep the cut safe the popup never gets narrower than
what its current content needs: the width is
`max(widestWidth * 0.6, desiredWidth)`. The content is monospace, so
`desiredWidth` does not change while the digits change and the popup still
does not jitter.

Line 1 - the height of the rectangle and how long it lasts:

    +12.4 pips   0:03:25

The sign is the direction from the first corner to the second one: `+` when
the second click is higher on the screen. The value is a screen distance,
not a per-pair one: the chart draws every symbol on one shared axis where
one pip is always 10 display points, so a 12.4 pip rectangle is 12.4 pips
for every pair on it. For a mirrored pair (USDCHF, USDJPY, USDCAD) "up on
the screen" is a falling price, the number stays the same.

The duration after it is `D:HH:MM` - days, hours, minutes of the time span,
days always printed even when they are `0`.

Line 2 - the time span, without the duration:

    24-Aug-26 09:15  →  24-Aug-26 12:40

Then one row per **main pair** (the pairs of `SymbolConfigs` - EURUSD,
GBPUSD, EURGBP, USDCHF, USDJPY, AUDUSD, NZDUSD, USDCAD, GER40), skipping
hidden ones and bottom panels. Indicators, indexes, drawings and shifts are
not listed.

           min       max
    EURUSD  1.16234   1.16789
    GBPUSD  1.31234   1.31789

`min` and `max` are the prices of the **bottom and the top edge of the
rectangle** for that pair - where the edges are, not where the pair
actually traded. Every pair has its own vertical offset on the chart, so
the same two screen lines mean a different pair of prices for each of them.

    edgeDisplay = screenPrice - seriesOffset(symbol) - flattenShift(anchorTime)

`screenPrice` is the price the y pixel of the edge stands for
(`topPrice - y * pointsPerRow`), the same value the crosshair readout uses.
The result goes through the pair's transform (pip scale + mirror) and its
`PriceMul`, so the printed number is a real price of that pair.

For a mirrored pair the top edge is the lower price, so the two edge values
are sorted: `min` is always the smaller number, `max` the bigger one.
The flatten shift is taken at the time of the anchor corner, so with a
flattened chart the printed prices belong to that column.

Prices are printed like in the `Space` stats popup: the last digit smaller,
mirror and pip scale already applied.
