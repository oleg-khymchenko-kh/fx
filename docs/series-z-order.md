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

Nothing happens on a single click that lands farther than 2 pixels from
every line. A double click on that empty space clears the highlight (see
below).

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

## Highlight

One symbol at a time can be highlighted. While a symbol is highlighted every other real pair and every `Shift`
symbol (`BasePair` or `TimeShift`) is dimmed: its color is mixed with the
chart background at `SeriesDimmedAlpha` = 0.35, so the line looks 35%
opaque. The raster writes plain pixels with no alpha channel, so the
"transparency" is that mix, done once per frame in `DimmedColor`. Every
other indicator - Average, Drawing, ZigZag, index symbols, bottom panels,
deal marks - keeps its normal color.

Two things set the highlight:

- `_pressSymbol` - the line under the button while it is held down.
- `_latchSymbol` - the line of the last double click. It survives the
  mouse up.

The dimming follows `HighlightSymbol` = `_pressSymbol ?? _latchSymbol`, so
a press temporarily overrides the latched symbol and the latched one comes
back on release.

The extra width is separate. Only the pressed line, and only while the
left button is really down (`_pressHeld`), is drawn 2 pixels wide instead
of 1 - the same width the `Shift` symbol uses when Alt makes it the drag
target (`SeriesPressWidthPx` = `ShiftHotWidthPx` = 2). The extra pixel is
added below the line, `DrawLine` and `DrawLastPrice` both honour
`RenderLine.Width`. A latched line is 1 pixel wide like every other line -
it is told apart by the dimmed lines around it.

## Releasing and latching

Mouse up (or a lost mouse capture) clears `_pressHeld` at once, so the
line goes back to 1 pixel, but it does not drop `_pressSymbol`. It starts
`_pressReleaseTimer`, and only its tick clears the press and undims the
other lines. The interval is the Windows double click time
(`GetDoubleClickTime`, clamped to 200-1000 ms) plus 60 ms, so the gap
between the two clicks of a double click never flashes the undimmed state.
A new button down inside that gap stops the timer, so the press simply
continues.

A double click (`ClickCount > 1` on button down):

- on a line - that line becomes the latched symbol, so it stays thick and
  everything else stays dimmed after the button is released. Double
  clicking a dimmed pair moves the latch to it.
- on empty space - `ClearSeriesHighlight` drops both the press and the
  latch, and every line goes back to normal.

A single click on a dimmed pair raises it on top only for as long as it is
not dimmed. `BeginSeriesPress` saves its old place in the order
(`_orderRestoreSymbol`, `_orderRestoreIndex`). When the pair gets dimmed
again (the press ends while another line is latched, or another line is
pressed), `RestoreSeriesOrder` puts it back to that place and saves the
order. If the highlight is gone instead (double click on empty space,
Escape), or the pair itself gets latched, it stays on top.

Escape does the same as a double click on empty space. It sits at the end
of the Escape chain, after measure, popups, draw mode, the selected line
and the range, so it only fires when nothing else is open.

The latch is also dropped when the symbol disappears or is hidden (checked
at the start of every `Rebuild`), on a full series reload, and it follows a
rename. It is not stored in the config or in `ChartViewState` - it lives
only for the session.

The z-order change is never undone - it was applied on button down and it
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
