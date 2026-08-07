# Lazy data loading and the loading indicator

Status: implemented, v1.

## Goal

Before this change the app read the full M1 history of every symbol
(all years, all symbols, including hidden ones) into memory at startup.
That is 1+ GB of RAM and a slow start. Now the app keeps in memory only
what the chart shows, and loads the rest in the background on demand.

## What loads at startup

- Only symbols that are enabled (not in `ChartState.HiddenSymbols`).
- Only the year files that overlap the saved view range plus one screen
  width on each side. The range comes from the saved `ChartState`
  (`ViewStartBucket`, `MinutesPerColumn`, `WeekendsHidden`) and the
  current chart width in pixels (see `MainWindow.StartupRealRange`).
- If there is no saved view state, the full history of enabled symbols
  is read (same as the old behavior, usually only the first run).
- If the saved view does not overlap a symbol's data at all, the nearest
  year is loaded so the user still has something to navigate from.
- Hidden symbols get a series with an empty history. They stay in the
  symbol bar (the last price is read cheaply from the DB tail) and can
  be toggled on later.

## Background loader (SeriesDataLoader)

One worker thread with a job queue. Granularity is the DB year file.
Per series it tracks a contiguous loaded span `[LoadedLo..LoadedHi]`
(years) and extends it by prepending/appending year chunks.

Triggers:

- Scroll / zoom: every `ChartView.ViewChanged` maps the rendered range
  (plus one screen back, two ahead) to real time and asks the loader to
  cover it (`EnsureVisibleRange`).
- Zoom out to fit (`MinutesPerColumn <= 0`): full history of all
  enabled symbols is requested, because fit means "show everything".
- Enabling a symbol in the symbol bar: same as scroll for that view.
- Find on a shift symbol: the full source history is loaded first
  (`EnsureFullAsync`), then the search runs.

Chunk splicing:

- A chunk is read from the DB on the worker, transformed
  (`CandleTransforms.Transform`), and spliced into the existing
  `CandleHistory` with `WithReplacedRange` (prepend or append). The agg
  levels are patched incrementally, not rebuilt.
- The splice is committed on the UI thread only if the series' history
  is still the same object that the chunk was computed against
  (optimistic retry, max 4 attempts). Live tail and last tick are
  carried over to the new history object.
- Overlap is removed by filtering the chunk to strictly before/after
  the loaded minutes.

ZigZag, Drawing and Deals symbols have no candles at all. Their whole
vector data is read once at chart load and never touched by the loader.

Shift symbols:

- Read the source symbol's years, then `ShiftedSymbol.Shift` by the
  virtual delta. The view range is mapped back to source time
  (`ChartToSource`) to decide which years are needed. After a find
  result is applied the loader state is reset to "fully loaded" with
  the new delta (`MarkShiftApplied`).

## Mirror base persistence

Mirrored symbols (USDCHF, USDJPY, USDCAD) used `min+max` of the full
history as the mirror base. With partial loading that value would drift
as more data arrives, shifting the series vertically. Now the base is
computed once from the first loaded chunk, stored in
`AppConfig.MirrorBases`, and reused forever (chunks are transformed
with the fixed base). One-time effect: the very first start after this
change computes the base from the visible range, which may differ from
the old full-history base, so mirrored symbols may need a one-time
re-align.

## Loading indicator

`LoadIndicatorView` sits in the top-right corner of the chart. It is
visible whenever anything is loading: a spinner plus "Loading (N)".
Clicking it toggles a popup listing every job:

    EURUSD 2018-2020 · reading 2019 (scroll)
    GBPUSD 2011-2026 · queued (find)

Sources of lines: startup reads (reason "startup") and loader jobs
(reasons "scroll", "fit", "toggle", "find"). The list refreshes as jobs
progress; the indicator hides when the queue is empty.

## Consistency with other operations

- Recompute indicators / edit indicator / delete indicator dispose the
  loader first (they delete or rename DB files), then reload the chart,
  which creates a fresh loader.
- Full history download keeps the loader running (writes only), the
  chart reloads afterwards.
- Live streaming is unaffected: the base info (last DB minute, mirror
  base, pip points) is read from the DB tail even when the tail year is
  not loaded, so the recent-tail merge stays small.
