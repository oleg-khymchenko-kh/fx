# Series z-order

Status: implemented, v1.

## Goal

Many pairs share one chart, and where two lines run over the same pixels
only the one drawn last is visible. This lets the user pick which one that
is: press the left mouse button on a line and it moves on top of every
other line, and it stays there.

## Picking a line

On left button down the chart looks for the nearest line under the mouse.
Only main lines take part - the ones built from candles
(`_renderedLines`, filled on every render). Bottom panels (spread, volume,
entry points, price age, density, order book) are not lines and are
skipped. Hidden symbols are not in the list either.

The distance is measured the same way as for an Alt + drag of a `Shift`
symbol (`LineDistancePx`): the columns from `x - 2` to `x + 2` are
checked, for each one the pixel run the line fills in that column, and the
distance is `sqrt(dx * dx + dy * dy)` to the closest point of that run.
A line farther than 2 pixels does not count. When two lines are inside the
2 pixel radius, the closest one wins; on a tie the one drawn first.

The search runs late in the button-down chain, so everything that was
already clickable keeps its priority: measure, draw mode, Alt + shift
drag, Alt + tilted grid, Shift + selection, a forecast marker, a calendar
circle, a pivot point, a vertex of the selected line, a drawing line.
Only when none of those is hit does the line search run. It does not
swallow the click - the usual left-drag panning starts right after it, so
grabbing the chart on a line still pans.

Nothing happens when the click lands farther than 2 pixels from every
line, and a double click keeps its old meaning (align the series to the
grid); the first click of that double click already raised the line.

## The order

`_seriesOrder` is a list of symbol names, bottom first. A raised symbol is
removed from the list and appended, so the last symbol pressed is the last
one drawn - on top of all the others.

Symbols that were never pressed are not in the list at all. They keep the
order they have in the series list and are drawn before every symbol that
is in `_seriesOrder`:

    rank(symbol) = 0                        when not in the list
                 = index in the list + 1    otherwise

`SeriesDrawOrder` sorts the visible series by that rank, ties broken by the
original index, and returns the indexes. The render loop walks the series
through that array instead of walking the list itself, so
`lineOffsets[si]` and everything else that is indexed by series position
stays correct. Vector lines (drawings, zigzags) are walked in the same
order.

Bottom panels are not reordered: they are stacked by `panelBottom` in a
separate loop that keeps the plain series order, so raising a line never
moves a panel.

## While the button is down

The pressed symbol is drawn 2 pixels wide instead of 1, the same width the
`Shift` symbol uses when Alt makes it the drag target
(`SeriesPressWidthPx` = `ShiftHotWidthPx` = 2). The extra pixel is added
below the line, `DrawLine` and `DrawLastPrice` both honour `RenderLine.Width`.

The width lives only as long as the button: mouse up, or a lost mouse
capture, drops `_pressSymbol` and redraws with the normal 1 pixel line.
The z-order change is not undone - it was applied on button down and it
stays.

Because the raised line is also the last one drawn, the thick line is
never covered by another pair.

## Storage

The order is global, not per tab: one list `AppConfig.SeriesOrder` in
`config.json`. All tabs share one chart and one series list, so they share
the order too - raising a line on one tab raises it everywhere.

It is not part of `ChartViewState`, so switching a tab does not touch it
and reloading the series list does not clear it. `MainWindow` pushes the
saved list into the chart once at startup (`SetSeriesOrder`) and writes it
back on `SeriesOrderChanged`, which fires only when a press actually moves
a symbol (pressing the line that is already on top changes nothing) and on
a rename. That save is immediate, not debounced - it happens once per
click, not once per frame.

Symbols that no longer exist are dropped before every save
(`PruneSeriesOrder`), so a deleted indicator does not stay in the file
forever. A rename rewrites the entry in place (`RenameSeriesKeys`).
