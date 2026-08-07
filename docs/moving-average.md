# Moving average (SMA) indicator

Status: implemented, v1.

## Goal

A new indicator type `Average` next to `ZigZag` and `Drawing`. It draws a
plain simple moving average (SMA) of the source pair as a normal line.
The user sets a period as a number plus a unit (minutes / hours / days)
and a direction (past / future):

- Past: the average of the candles behind each point (classic trailing
  MA).
- Future: the average of the candles ahead of each point (the window
  runs forward instead of backward).

"Plain" means simple average - each candle in the window has equal
weight (no EMA / weighting).

## Window size (count-based)

The source is M1 (one candle per minute). The window length is measured
in candles, not wall-clock time:

    windowBars = period * (minutes=1, hours=60, days=1440)

So "3 hours" = 180 candles, "1 day" = 1440 candles. On M1 this matches
minutes/hours/days of trading time. The window does not reset at week
boundaries: at Monday open the trailing window still reaches back into
last week's candles, so the line continues smoothly across the weekend
gap. Gaps in the data are ignored (the window is a count of existing
candles, not a time span). See MovingAverageSymbol.WindowBars.

At the very start (past) or very end (future) of the data the full window
does not fit; there the average uses whatever candles are available
(expanding / shrinking window), so every source minute gets a value and
the line is continuous. This makes a future MA converge to the last
candle at the right edge, and a past MA ramp up from the first candle.

## Data model and storage

Same per-minute candle store as the pairs. Each source minute gets one
target candle with Min = Max = Avg = the MA value (a flat line value).
Values are stored in raw source points (1/100000), before pip scaling
and mirror, exactly like the ZigZag point list;
the display transform (pip scale + mirror) is applied on load. Averaging
in raw space then transforming is the same as transforming then
averaging, because both scale and mirror are affine.

`MovingAverageSymbol.ComputeSma` uses a prefix-sum so the whole series is
O(n). `Generate` deletes the target folder and rewrites it, like
ZigZagSymbol.Generate. Progress is reported during the write loop.

## Creation and editing

The same Add/Edit dialog (SymbolEditorWindow). Type combo now has
`ZigZag`, `Average`, `Drawing`. For `Average` the pips limit field is
hidden and an "Average params" row shows instead: Period (number),
Unit combo (Minutes / Hours / Days), Direction combo (Past / Future).

Model: IndicatorSymbol gains `Period`, `Unit`, `FromFuture` next to the
existing `LimitPips`. IndicatorSymbol.SameData is type-aware: for Average
it compares Source, Period, Unit, FromFuture; only a change in one of
those (or Type / Source) recomputes the DB. A name-only change renames
the folder, a color-only change just saves config (same smart-edit rule
as ZigZag).

## Rendering

An Average symbol is NOT editable and has no points: DisplayConfigs marks
only ZigZag indicators as editable, so the MA loads as a plain candle
line series (SymbolSeries.Editable = false). It
still counts as an indicator in the symbol bar, so it keeps the
Edit / Delete / Align to source context menu, and its vertical offset is
stored relative to its source pair like any indicator. On a non-mirror
pair (EURUSD, GBPUSD) the MA overlays the source line directly.

Compute derived (button) and Apply (dialog) both branch on the type:
Drawing is skipped, Average calls MovingAverageSymbol.Generate, the rest
call ZigZagSymbol.Generate.

## Automatic refresh (v2)

Averages are the only indicator type that recomputes itself. Whenever new
broker candles land in the DB, `MainWindow.RefreshAveragesAsync` runs
`MovingAverageSymbol.Refresh` for every Average indicator. Trigger points:
after the once-a-minute repair pass described in docs/live-candles.md,
after the tail download at connect, and after the manual history download.

`Refresh` takes `redoFromUnix`. When it is greater than zero the method
behaves as if the target ended at `redoFromUnix - 60`, so rows that were
already written from provisional (tick derived) source values are
recomputed once the broker replaces those source minutes. With
`redoFromUnix = 0` it only extends past the last written row, which is
what the manual Refresh does.

An Average whose target has no data at all is skipped by the automatic
path, so it never kicks off a full `Generate` in the background. Use
Compute derived or the context menu Refresh for the first build.

`Refresh` flushes only the target symbol, not the whole DB, because it
runs once a minute.

## Context menu

Every indicator except ZigZag, Shift and Drawing has a `Refresh` item in
the symbol bar context menu, between Edit and Delete. It runs the same
code as the Refresh button in the editor dialog and reloads the chart when
it finishes. Refreshing a USD Index also refreshes every Currency Index
whose source is that index.

Next to it, every indicator that has storage (ZigZag included) has a
`Rebuild` item. It asks for confirmation first, then throws the stored
data away and generates the symbol from scratch, the same as the Compute
derived button does but for one indicator only. Use it when Refresh
cannot help: Refresh trusts the rows that are already there, so it cannot
repair data written by an older build whose meaning has changed.

While it runs it posts a job line into the same loading indicator the
chart load uses, so the spinner in the top left shows
`Name · rebuilding 42%`. The percentage comes from the same
`IProgress<double>` the editor dialog uses, so it only starts moving once
the generator reaches its write loop.

## Not in v1 (next steps)

- The line still does not follow live ticks between refreshes, and a
  refresh only updates the DB: the chart picks the new rows up on the next
  full chart load.
- Other average kinds (EMA / weighted), or time-based (wall-clock)
  windows.
