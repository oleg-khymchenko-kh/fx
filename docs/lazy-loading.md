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
- Averages derived from the series are rebuilt on the worker too,
  before the commit. The worker asks the chart for the jobs
  (`AverageJobsOf`: symbol, spec, current history, cached parent array;
  hidden averages are left out), runs `AverageSeries.Recompute` against
  the spliced minutes and hands the ready histories to `ReplaceSeries`.
  Without this the whole recompute ran inside the commit: prepending
  2011-2023 to GBPUSD, which carries three 40-window bands, blocked the
  UI thread for about 17 of the 19.6 s the load reported.
- The splice is committed on the UI thread only if the series' history
  is still the same object that the chunk was computed against
  (optimistic retry, max 4 attempts). Each ready average is checked the
  same way and quietly dropped if its own history or cached parent
  moved on. Live tail and last tick are carried over to the new history
  object.
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

A job that has read its years and is rebuilding the averages shows
`reading averages`. The load line in the log names the same work:
`Loaded GBPUSD 2018-2023: 2,205,824 candles in 4210 ms, 3 average(s) in
2950 ms (scroll)`.

Sources of lines: startup reads (reason "startup") and loader jobs
(reasons "scroll", "fit", "toggle", "find"). The list refreshes as jobs
progress; the indicator hides when the queue is empty.

## What a reload costs, and what is cached

`LoadChartAsync` is not only the startup path. Editing an indicator, a
wide spread backfill, the ask toggle and a few other operations run it
again, and it rebuilds every series from scratch. Two things used to
dominate it.

**Reading the side stores.** Depth, volume profiles, the order book and
the deals file are read per symbol, and the depth store alone is tens of
MB (`data/EURUSD/depth/2026.dpt` was 75 MB). They are now cached in
`MainWindow` and only re-read when the files on disk actually changed:

- `_storeCache` keys volume profiles, order books and deals by a folder
  or file stamp (name + size + last write time). A stamp that cannot be
  read is empty, and an empty stamp means "do not cache", so an error
  never freezes a stale result.
- Depth has its own cache (`_depthCache`) because its files are
  append-only fixed-size records. It keeps the parsed snapshots per year
  plus the byte length already read, and `DepthStore.ReadYearFrom`
  continues from that offset. A minute added by the live collector costs
  one record, not the whole file. If the file shrank or the header does
  not parse, the year is read again from the start.

**Moving averages.** The 40 SMA passes of an `AverageBand` over 250k
candles take ~600 ms each, and a reload recomputed them even when the
parent data had not moved. `CachedAverage` keys the result by the
parent's fingerprint (length, first and last minute, the sum of the
average prices, a wide spread mask, the volume sum) plus the
`AverageSpec`. Equal fingerprint means the same input, so the previous
array is reused. The fingerprint is one pass over the parent, well under
a millisecond.

The indicator half of the load (drawings, ZigZag, deals, density,
spread, volume, order book, levels, averages) also runs in parallel now,
with the same degree of parallelism as the pair half. Nothing in it
reads another indicator's slot: every source is a pair or an index, and
those are filled by the first pass.

## Consistency with other operations

- Recompute indicators / edit indicator / delete indicator dispose the
  loader first (they delete or rename DB files), then reload the chart,
  which creates a fresh loader.
- Full history download keeps the loader running (writes only), the
  chart reloads afterwards.
- Live streaming is unaffected: the base info (last DB minute, mirror
  base, pip points) is read from the DB tail even when the tail year is
  not loaded, so the recent-tail merge stays small.
