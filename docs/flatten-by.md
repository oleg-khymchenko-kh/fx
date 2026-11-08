# Flatten By indicator

Status: implemented, v1.

## Goal

A new indicator type that shows one series in the "frame" of another
series. You pick:

- **Source** - the pair or indicator to transform.
- **Base** - the pair or indicator that becomes a horizontal line.

The result is the source drawn as if the base were flat. The base itself
is shown by the indicator as a dashed horizontal line in the same color.

This is the same math as [flatten by avg](flatten-by-avg.md), but it is
a normal series in the right bar and changes only this one line, not the
whole chart.

## The math

All values are display points (10 points per pip, mirror applied), the
same space the chart draws in.

    out(t) = source(t) + round(L - base(t))

`L` is the level of the horizontal line. For every source minute the
base value at the same time is taken. If the base has no minute at that
time, the value is interpolated linearly between its two neighbor
minutes; before the first and after the last base minute the edge value
is kept. Min, max and avg of a candle all move by the same amount, so
candle sizes do not change. If source = base, the result is exactly the
flat line `L`.

If the base has no data at all, the indicator is an empty series.

### The level L

`L` is saved in the indicator config (`FlattenLevel`, display points):

- If another Flatten By indicator already uses the same base, its `L` is
  reused, so all of them share one horizontal line.
- Otherwise `L` = the base value at the time in the middle of the screen
  when the indicator is created. At that time the result goes through
  the source, so the new line appears right on top of the source.
- `L` is chosen again only when the source or the base is changed in the
  editor. Color or name changes keep it.

The line can be moved up and down with the wheel on its label like any
other indicator.

## UI

Add -> Type `Flatten By`. Source and Base lists hold all pairs and every
indicator that is a price line: USD Index, Currency Index, Average,
Average max/min/avg, Shift and other Flatten By indicators. When an
indicator is edited, the indicators built on it are not in the lists, so
a loop cannot be made.

In the right bar a Flatten By indicator sits under its source.

## Data flow

No storage, everything is in memory (`HasStorage` is false):

- Chart load: computed in a third pass after pairs and averages
  (`MainWindow.BuildFlattenBySlots`), in dependency order, so Flatten By
  of a Flatten By works. The series takes the transform and price
  format of its source.
- Updates: before each render `ChartView.RefreshFlattenBySeries` checks
  the source and base histories by reference. A new history (lazy load,
  minute repair, average recompute) starts a full recompute on a
  background thread. A new live tail only recomputes the live part (and
  the last tick) on the UI thread.
- If the source or base pair is hidden but the Flatten By is visible, the
  hidden pair is still loaded (same rule as for averages).
- Rename of the base renames `BaseSymbol` in all indicators. Deleting the
  base leaves the Flatten By as an empty series (the delete prompt counts
  it).

## Code

- `IndicatorSymbol.BaseSymbol`, `IndicatorSymbol.FlattenLevel`,
  `IndicatorTypes.FlattenBy`, `IndicatorTypes.HasPriceLine`.
- `Chart/FlattenBySeries.cs` - the math (`Build`, `UpdateLive`,
  `ValueAt`), `FlattenBySpec` (base name + level) on `SymbolSeries`.
- `ChartView`: `RefreshFlattenBySeries`, `ScheduleFlattenBy`,
  `ApplyFlattenBy`, `DrawFlattenBaseLine` (dashed line, 4 px on / 4 px
  off), `DisplayValueAt`.
- `MainWindow`: `DisplayConfigs` (children after their source),
  `BuildFlattenBySlots`, `FlattenLevelFor`, `FlattenInputsOf`.
- `SymbolEditorWindow`: `FlattenParams` panel with the Base list.

## Limits

- In game mode a Flatten By with a "Future" average as the base uses the
  real average values near the play time, which already see the future.
  Plain past averages and pairs are fine.
- The last price number in the right bar is set at load and is not
  updated live (same as for averages).
