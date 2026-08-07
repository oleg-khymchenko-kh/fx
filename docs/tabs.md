# Chart tabs

Status: implemented, v1.

## Goal

Many chart tabs instead of one. Every tab shows the same chart - the same
series, the same DB, the same colors - but keeps its own view settings, so
one tab can sit on a 5 minute zoom of last week while another looks at the
whole 2011..2026 history with a different set of symbols switched on.

- Tabs are added, renamed, duplicated and deleted at runtime.
- `Connection` is always the last real tab; new tabs go before it.
- The last active tab is stored and re-activated on the next start.

## What is per tab

Everything in `ChartViewState` plus the placement of the Shift indicators:

- horizontal zoom (`MinutesPerColumn`) and vertical zoom (`PointsPerRow`)
- horizontal offset (`ViewStartBucket`) and vertical offset (`TopPrice`,
  `PriceOffsetPoints`)
- which symbols are on (`HiddenSymbols`), calendar on/off
  (`CalendarVisible`), tilted grid off / 1 / 2 / 3 (`TiltedGridIndex`)
- the vertical offset of every single symbol (`SymbolOffsetPoints`)
- flatten / unflatten (`FlattenSymbol`, `FlattenLine`)
- No weekends (`WeekendsHidden`)
- all three tilted grids - slopes, placement, lock - and which one is on
  (`TiltedGrids`, `TiltedGridIndex`)
- all Shift settings: Source, Source time, Chart time, Flip, Target

## What is shared

Everything that is data, not view:

- the list of indicators, their type, name and **color**
- the DB, the drawings, the find results, the deals file
- mirror bases, calendar entries, the live connection

So changing a symbol color changes it in every tab, and a new indicator
appears in every tab.

## Model

`ChartTab` (`ChartTab.cs`) is one tab:

    Name     - what the tab header shows
    State    - the ChartViewState of that tab
    Shifts   - a ShiftPlacement per Shift indicator

`ShiftPlacement` is the part of `IndicatorSymbol` that a tab overrides:
`Source`, `TargetSymbol`, `SourceTimeUnix`, `ChartTimeUnix`, `Flip`.

`AppConfig` gained `Tabs` and `ActiveTab` (the index in `Tabs`).
`ChartState` is kept only to read an older config: `EnsureTabs` moves it
into a first tab named "Chart" and then writes `null` there. A config from
before this change opens with exactly the chart it had, as a single tab.

`ActiveTab` follows chart tabs only. Selecting `Connection` does not change
it, so closing the app on the Connection tab still starts on the chart tab
that was used last.

## One chart, many tabs

There is only one `ChartView` (and one `SymbolBarView`, `TimeAxisView`,
`LoadIndicatorView`) - a second one would mean a second copy of ~6 M
candles per series in memory.

The whole chart panel lives in XAML inside a collapsed `ChartPanelHost`.
`BuildTabs` takes it out of that host and puts it into the `TabItem` of the
active tab. Switching tabs moves it: `oldItem.Content = null` first, then
`newItem.Content = ChartPanel` (a WPF element has one parent). This is the
same detach / re-attach the app already did when switching between Chart
and Connection.

Tab items are plain `TabItem`s created in code, with the `ChartTab` in
`Tag` and a context menu (Rename / Duplicate / Delete). The `+` at the end
is a `TabItem` too, but its `PreviewMouseLeftButtonDown` is handled, so it
never really gets selected - it just adds a tab.

## Switching

`ActivateTab`:

1. flush the pending state of the old tab (`SaveChartState`),
2. move the chart panel to the new `TabItem`,
3. apply the tab's Shift placements onto `_config.Indicators`,
4. `Chart.RestoreState(tab.State)`,
5. re-sync the symbol bar rows (symbol on/off, calendar, weekends, tilted
   grid, flatten),
6. save the config with the new `ActiveTab`.

Steps 1..6 are in-memory only, so a switch is instant. Lazy loading takes
care of itself: the rebuild fires `ViewChanged`, which asks
`SeriesDataLoader` for the years the new view needs, including symbols that
were hidden in the old tab.

`RestoreState` now also handles `MinutesPerColumn <= 0` - it puts the chart
back into fit view instead of leaving the previous zoom, so a tab saved in
fit view opens in fit view.

## Per tab Shift

A Shift series **is** its data: the candles of the target pair with moved
timestamps. So when the new tab wants a different Source / Target / times /
Flip, the series has to be built again.

`ApplyShiftPlacements(tab)` writes the tab's placements into the live
`IndicatorSymbol` objects and reports whether anything changed. If it did,
`ActivateTab` runs a normal `LoadChartAsync()` - the same path used after
editing an indicator, so the loader, the mirror bases and the live tails
end up consistent. Tabs whose Shift settings are equal (the usual case)
switch without any reload.

If a DB operation or a history download is running, the reload is skipped
with a log line; the placements are already in the config and show up on
the next load.

The other direction: every place that changes a Shift - the Add/Edit
dialog (`ApplyIndicatorAsync`), a find result (`ApplyFindResultAsync`) and
the Alt+wheel nudge (`NudgeShiftTimeAsync`) - calls
`RecordShiftPlacements()`, which copies the current Shift settings into the
active tab. So editing a Shift only moves it in the tab you are on.

A tab that has no placement for an indicator (a new indicator, or a tab
made before it existed) adopts the current values on its next activation.
Placements of deleted indicators are dropped there as well.
`_config.Indicators` always holds the active tab's Shift values - that is
what a fresh tab and every non-Shift code path reads.

## Tab operations

- **+** (or `Duplicate`) makes a copy of the tab: same view state, same
  Shift placements, name `Chart 2`, `Chart 3`, ... (the first free one).
  `+` appends at the end, `Duplicate` inserts right after its tab. The new
  tab is selected right away.
- **Rename...** opens a one-line dialog (`TabNameWindow`).
- **Delete** asks for confirmation. The last chart tab cannot be deleted.
  Deleting the active tab first selects a neighbour.

Renaming an **indicator** rewrites its name in every tab - in
`SymbolOffsetPoints`, `HiddenSymbols`, `FlattenSymbol` and in the shift
placements (`RenameChartStateKeys`).

## Not in v1 (next steps)

- Reordering tabs by dragging.
- Per tab Shift without a chart reload (re-shift the series in memory from
  the target that is already loaded, as `ApplyFindResultAsync` does).
- Per tab drawings and per tab indicator set.
