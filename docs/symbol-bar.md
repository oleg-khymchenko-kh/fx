# Right bar

Status: implemented.

`SymbolBarView` is the strip on the right of the chart. It is one
`FrameworkElement` that draws everything itself in `OnRender` - there are
no child controls - so every row is placed by hand on a fixed grid: row
`n` sits at `PadTopDip + n * (textHeight + RowGapDip)` and `RowHit`
turns a y back into a row index.

Top to bottom:

1. one row per symbol, `SYMBOL: price` (the price follows the cursor when
   it is over the chart, otherwise it is the last price),
2. two rows of tilted-grid buttons (docs/tilted-grid.md),
3. "Unflatten" (only while a flatten is active, docs/flatten-by-line.md).

The chart-wide switches (Calendar, Forecast, No weekends, Sessions, Add)
and the connection status are not rows - they sit in the icon toolbar
above the bar, see below.

Clicking a symbol row hides/shows that line; right-clicking opens the
per-symbol menu (align, add, edit, delete, ...). The wheel over a symbol
label changes that symbol's price offset, and Alt + wheel over a Shift
symbol moves it in time.

## Toolbar

The switches that belong to the whole chart, not to one symbol, live in
`ChartToolBarView` - a second `FrameworkElement` pinned above the symbol
bar, outside the `ScrollViewer`, so it is always visible no matter how
long the symbol list gets.

It draws five square 22 dip buttons in one row, left to right:

1. Calendar (docs/economic-calendar.md),
2. Forecast (docs/forecast-indicator.md),
3. No weekends (docs/no-weekends.md),
4. Sessions (docs/sessions.md),
5. Add symbol (opens the same editor as the old "+ Add" button).

Each icon is vector-drawn in `OnRender`, there is no image file. A button
is dark with a white icon when its layer is on, light grey with a dark
icon when it is off, and light grey with a pale icon when it is not
usable at all (Calendar with no events loaded, Forecast with no files in
`data/forecast`) - those two ignore clicks in that state. Hovering fills
the button a shade darker and sets the tooltip.

Right-click still opens the same menus as the old rows: Calendar gives
"Settings..." and "Find...", Forecast gives "Reload forecasts".

The connection status (`SetConnStatus`) sits on the same line, right of
the buttons: one 16 dip coloured dot, no text. The text - `Online (n)` /
`Connecting` / `Downloading` / `Offline` - is the dot's tooltip, so the
state is one hover away and costs no width. It used to be pinned to the
bottom of the symbol bar, where a long symbol list pushed it off screen.

`MainWindow` keeps the old method names for the state
(`SetCalendarRow`, `SetForecastRow`, `SetWeekendsRow`,
`SetSessionsRow`), they just point at `ChartTools` instead of
`SymbolBar` now.

## Groups

An indicator row belongs to the symbol it is computed from
(`SymbolSeries.SourceSymbol`). Any symbol that is the source of at least
one other row gets a small `-` / `+` button at the right edge of its row
(`DrawGroupButton` / `GroupButtonBounds`, hit-tested before the row's own
click). `-` means expanded, `+` means collapsed - the same convention as
a file tree.

Collapsing hides the whole subtree from the bar: `_visibleRows` keeps the
indices of the entries that survive, `CollapsedAway` walks `_sourceOf` up
to the root and drops a row when any ancestor is collapsed, so an
indicator of an indicator disappears with its grandparent. Everything
that used to index `_entries` by row - `RowIndexAt`, `SymbolAt`,
`SymbolLabelAt`, `TiltedUpRowIndex` - goes through `_visibleRows`, while
the cursor-price array stays indexed by the original entry position.

**Collapsing alone does not touch the chart.** The lines keep being
drawn, they are only out of the way in the bar. But a group that is
collapsed **and** whose parent symbol is switched off hides its
indicators from the chart too - turning off the pair then buries the
whole family in one click, which is the point of collapsing it.

That rule lives in `ChartView`: `_collapsedSources` holds the collapsed
parents, `_hiddenSymbols` the symbols the user switched off, and
`RefreshHidden` folds both into `_effectiveHidden` - a symbol is in it if
the user hid it, or if any ancestor is collapsed and hidden.
`IsHidden` is what the renderer, the hover, the line hit-tests and the
stats use; `_hiddenSymbols` is left for the things that mean "what the
user pressed" (the row's own on/off state, `HiddenSymbols` in the saved
state, the data loader).

`_collapsedSources` is saved as `ChartViewState.CollapsedSymbols`, so the
collapsed groups are **per tab** (docs/tabs.md) and survive a restart.

## Scrolling

With many symbols the bar does not fit the window, so it lives in a
`ScrollViewer` (`VerticalScrollBarVisibility="Auto"`) in
`MainWindow.xaml`. For that `MeasureOverride` returns the real content
height (`ContentHeight` - down to the last row) instead of the 0 it used
to return. When the content is taller than the viewport the scrollbar
appears; the toolbar above stays put either way.

The wheel handler now only marks the event handled when the cursor is
over a symbol label (the offset/time-shift gestures). Anywhere else it
lets the event through so the `ScrollViewer` can scroll the list.
