# Find similar (search for a Shift indicator)

Status: implemented, v2 (v1 + the ZigZag search type).

## Goal

Given a selected fragment of the chart, find the places in the past
where the source pair moved with a similar shape, and let the user
overlay any of them on the fragment with one click, using the Shift
indicator (docs/shifted-symbol.md).

## Flow

1. Select a time range on the chart (Shift+drag - the same range band
   the stats popup uses).
2. Right-click the Shift indicator row in the right bar, pick `Find`.
   The item is enabled only while a selection exists. A "Search
   parameters" dialog opens first (see below); Cancel there drops the
   search.
3. A popup shows the search progress (with Cancel). The search takes
   well under a second on 16 years of M1, so the bar mostly just flashes.
4. When done the same popup switches to the ranked result list, best
   match on top. The list is also saved to `data/NAME/find.json`, and can
   be reopened later with `Show results` in the same menu (enabled only
   when the file exists).
5. Selecting a row applies the result: the indicator's Target symbol is
   set to the pair of that row, Source time to the found window start,
   Chart time to the fragment start, the config is saved, and only this
   one series is rebuilt in memory (`Chart.ReplaceSeries`) - no full
   chart reload, so browsing results with the arrow keys is fast. The
   series' vertical offset is set so the found window's mean lines up
   with the fragment's mean.

## Search parameters

The dialog (`SearchParamsWindow`) holds four values. They are stored on
the indicator (`FindTargets`, `FindSmoothMinutes`, `FindZoomPercent`,
`FindStepMinutes`), so the next Find opens with the same values.

- **Target symbols** - multi-select over the traded pairs (indexes are
  not offered). This is where the search looks, and what is drawn once a
  row is picked. The template is still the indicator's Source: select a
  fragment of EURUSD, search in GBPUSD, and the GBPUSD window is drawn
  over the EURUSD fragment. Several pairs give one merged ranking with
  a `Pair` column.
- **Smoothing** - the full width of the centered SMA in minutes or
  hours (half-window = value / 2). Default 4 hours, which is what the
  old hardcoded `SmaHalfBuckets * bucketMinutes` gave for 15-min
  buckets. 0 turns smoothing off. The width no longer depends on the
  bucket size.
- **Max zoom, %** - P gives the clamp `1/(1+P/100) .. 1+P/100`, so 50 %
  means 0.67..1.50 (the old hardcoded clamp was 0.60..1.50). The dialog
  shows the resulting range.
- **Step, min** - the candidate step along the time axis, 1 hour by
  default. It sets how precisely a match can be placed; halving it
  doubles the search time. The bucket size used for comparing the
  curves stays automatic (15 / 5 / 1 min by fragment length).

## Cross-symbol search

Every target is laid out on the same virtual time axis and compared
against the same fragment values, so the ranking mixes pairs. Scoring is
scale free (re-centering plus the closed-form zoom), and display candles
are pip-scaled to a common unit, so EURUSD and USDJPY windows are
comparable.

Rules that stay per target:

- The candidate window must end at or before the fragment start, in
  every target, so a match is always something that already happened.
- Dedup by distance runs inside one target; the same time in two
  different pairs is two separate results. Top 100 per target, then the
  merged list is cut to 100.

Applying a cross-pair result rebuilds the copy from the target series
already in chart memory, and sets the series transform from the target
(mirror flag, mirror base, pip points), so the copy keeps the target's
price scale. With Flip on, the transform is the target's mirror flag
toggled: around the copy's own min+max for a plain pair, around the
target's mirror base for a mirrored pair (USDCHF, USDJPY, USDCAD).
Changing the target drops the stored mirror base of the indicator
(`AppConfig.MirrorBases`), which was computed for the old pair.

The found zoom is still only shown, not applied. Across pairs the
amplitude difference is bigger than within one pair, so an overlay can
look too large or too small even when the shape matches.

## Search algorithm

Adapted from the similar days / weeks research tools
(docs/similar-days.md, docs/similar-weeks.md, `src/Fx.SimilarDays`).
Differences, driven by arbitrary fragment length and the 1-hour
precision requirement:

- One flat series instead of per-week series. The whole source history
  is laid out on the virtual time axis of `WeekendCompressor` (weekends
  removed), so Friday..Monday windows glue exactly like the chart in
  "No weekends" mode. Session-open anchoring from the tools is not
  needed: virtual time already aligns sessions.
- The fragment is the selection, floored/ceiled to whole hours of
  virtual time. Minimum 2 hours of trading time.
- Scale-aware resolution: window >= 3 days - 15-min buckets, >= 12 h -
  5-min, shorter - 1-min. Smoothing is a user parameter now (see
  below), the same width in minutes at any bucket size.
- Candidates step over the whole history strictly before the fragment
  start (~97k candidates for 16 years at the default 1-hour step).
  There is no inner +-12h shift loop: the candidate positions already
  cover every alignment at the step's precision.
- Scoring is identical to the tools: overlap of non-NaN points with a
  60% floor, re-centering by overlap means, closed-form vertical zoom
  `k = sxy/syy` clamped by the Max zoom parameter, mirror as a sign flip,
  `score = 1 - residual/sxx`, `sim = score * sqrt(overlap/len)`.
- Dedup: going down the ranking, a window closer than one window length
  to an already kept better one is dropped. Top 100 are saved.

## Speed

- The search reads no files: it runs on the source series already in
  chart memory. Display candles (pip scale, mirror) differ from raw by
  an affine map, which Pearson correlation and the score are invariant
  to, so the ranking is identical.
- Smoothing is prefix-sum (O(n)), candidates run under `Parallel.For`.
  ~97k candidates x 480 points for a week-long fragment is ~50M
  multiply-adds - far below a second.
- Applying a result rebuilds one `CandleHistory` (~0.3 s for 5.7M
  candles) instead of the full 10 s chart reload.

## Storage

`data/NAME/find.json`: the template symbol, fragment range (hour-aligned
real unix), search timestamp, window length in minutes, the searched
target list, the three search parameters, and the result list (source
window start unix, sim, score, rho, zoom, mirror flag, overlap points,
the two means, and the pair the window belongs to). The existing
indicator flows manage it: Delete removes the folder, a rename moves it,
and an Edit that changes Source or Type recomputes the symbol which
wipes the folder.

Guards against stale data:

- Any indicator mutation (Edit apply, Delete, Refresh, Recompute
  indicators) cancels a running search and closes the results window,
  so a search can never write into a renamed or deleted folder, and an
  open window can never apply results for an edited indicator.
- The stored source symbol is checked when the results are listed or
  applied; results recorded for another source are treated as absent.
- A search that finds zero matches does not overwrite the saved file.
- An empty search result, a search shorter than 2 hours of trading
  time, or a selection with under 60% data coverage reports an error
  instead of saving.

Applying a result also sets the indicator's Flip checkbox from the
result's mirror flag, so a row with the M flag overlays the vertically
flipped copy (and a non-mirrored row clears the flip). The stored
MeanA/MeanB give the vertical offset in both orientations: plain
`MeanA - MeanB`, flipped `MeanA + MeanB - flipBase` where flipBase is
min+max of the shifted series. find.json carries a Version field
(currently 3); files of another version are treated as absent, so after
a format change the user just re-runs Find. Applying the zoom is still
a next step.

Renaming any indicator now also carries its per-series vertical offset
and hidden flag to the new name (chart state keys are renamed in the
saved state and in memory).

## Search types

The dialog starts with a `Search type` combo with two options:

- **Similarity** - everything described above, unchanged.
- **ZigZag** - match a sequence of ZigZag extremums instead of the
  smoothed curve.

The chosen type is stored on the indicator (`FindSearchType`), so the
next Find opens in the same mode. The ZigZag mode also stores its
targets (`FindZigZagTargets`) and shares `FindZoomPercent`.

## ZigZag search

### Parameters

- **Target symbols** - a multi-select over ALL ZigZag indicators that
  have at least two points, whatever pair they are built on (traded
  pairs themselves are not offered). The search walks each selected
  target's point list (`data/NAME/zigzag.json`, in chart memory at
  runtime) and merges the results into one ranking; the `Pair` column
  shows which pair a row belongs to. What is drawn when a row is
  picked is NOT the ZigZag indicator but its source pair.
- **Max zoom, %** - the clamp on the COMPUTED zoom (see the
  algorithm): candidates whose zoom falls outside the range are
  rejected.
- There is no Smoothing and no Step: candidates are runs of zigzag
  points, not time-step windows, and no curve is built.
- **Points table** - the pattern, one row per zigzag point:
  - `Pips` - the value relative to the first pattern point (0 for the
    first row),
  - `Minutes` - trading minutes (weekends removed) relative to the
    first pattern point (0 for the first row),
  - `Pips up` / `Pips down` - how far above / below `Pips` a matched
    extremum may sit,
  - `Min left` / `Min right` - how far before / after `Minutes` it may
    sit.

  Every row describes a rectangle: the top-left corner is
  (`Minutes - Min left`, `Pips + Pips up`), the bottom-right corner is
  (`Minutes + Min right`, `Pips - Pips down`). A match must put its
  extremum inside the rectangle.

  The table is prefilled automatically from the points of the FIRST
  checked target that fall inside the chart selection, converted to
  pips with that source pair's pip size. When several zigzags are
  offered, the first one is checked by default (or the stored last
  selection). Default tolerances are 5 pips each way and 10% of the
  pattern's length (at least 30 minutes) per side. Changing which
  target is first refills the table, UNLESS the table has hand edits
  (edited cells, added or deleted rows) - an edited table is never
  replaced automatically. Rows are editable, and rows can be added
  or deleted. `Minutes` must not decrease down the table, all values
  must be plain finite numbers (cells reject text that does not
  parse), at least two rows are required, and exactly two rows must
  carry the `Zoom` mark, at different pip levels. The table is not
  persisted: every Find rebuilds it from the fresh selection.

  On Search the table is rebased to its first row: the first row's
  `Pips` and `Minutes` are subtracted from every row, so the pattern
  is always relative to its own first point and deleting head rows
  just works. Each prefilled row remembers which zigzag point it
  came from, and the anchor is the current FIRST row's point. A
  fully hand-typed pattern (no prefilled first row) anchors at the
  fragment start.

### Algorithm

A candidate is any run of N consecutive points of a target's list
(N = pattern rows) whose last point is before the fragment start, so
the pattern occurrence itself never matches. Stages, per target:

1. **Duration.** A candidate whose span (trading minutes from its
   first to its last point) is more than twice the pattern's span is
   rejected.
2. **Pips, computed zoom.** Two pattern rows are marked as the Zoom
   points (the `Zoom` checkbox column in the table; the prefill
   marks the pattern's min and max rows, the user may move the marks
   to any two rows at different pip levels). The zoom is not
   searched, it is computed so that the candidate's points at those
   two POSITIONS land exactly on the pattern's:
   `z = (c_B - c_A) / (Pips_B - Pips_A)` over the point deltas
   `c_i = (value_i - value_0) / pipPoints`, and the mapping
   `t_i = z * Pips_i + d` with `d = c_A - z * Pips_A` puts both zoom
   points pip to pip, no tolerance involved. `z` must lie inside the
   Max zoom clamp `1/(1+P/100) .. 1+P/100`, otherwise the candidate
   is rejected. Every other point must satisfy
   `t_i - Down_i <= c_i <= t_i + Up_i`; the tolerances (5 pips by
   default) only matter for the non-zoom points. Time is ignored
   here.

   Both orientations are always checked: the mirrored variant maps
   the negated pattern the same way, with the up/down tolerances
   swapping sides. Mirroring flips the sign of the computed `z`, so
   a candidate can match in at most one orientation. A mirrored
   match carries the `M` flag in the results.
3. **Minutes.** Candidate minute deltas `m_i` (trading minutes from
   the candidate's first point) are checked against
   `[Minutes_i - Left_i, Minutes_i + Right_i]`. All inside - a full
   time match, `fit = 1`. Otherwise each point's overshoot is taken
   relative to the tolerance on the violated side,
   `e_i = overshoot / max(1, tolerance)`, and
   `fit = 1 / (1 + mean(e_i))`.
4. **Similarity.** Each surviving candidate is scored with the
   Similarity machinery (same buckets, re-centering, closed-form
   zoom, the orientation fixed to the one that matched in stage 2)
   by aligning the candidate's first point with the pattern's first
   point (the anchor keeps its offset from the fragment start) and
   comparing the source pair curve over one fragment length. The `Zoom` column shows the computed pip zoom
   from stage 2, not the curve-fit zoom. Buckets at or after the fragment start are
   excluded from the score, so a window that reaches into the
   selection cannot inflate its sim by matching the fragment against
   itself. A window with too little data keeps sim 0 but stays in the
   list - the zigzag match itself is the primary signal. A candidate
   whose aligned window would start before the loaded history is
   dropped. A selection with under 60% data coverage reports an error
   instead of searching, same as Similarity mode.

Results are sorted by `fit` first (full time matches on top), then by
`sim`, and cut to 100. No distance dedup: two candidates are different
point runs by construction.

### Applying a result

The same flow as Similarity: the row's `Symbol` is the ZigZag
indicator's SOURCE pair, so clicking a row overlays the pair itself,
never the zigzag line. `SourceUnix` is the aligned window start (the
candidate's first point minus the anchor offset), which lines the
matched extremums up with the pattern's on the chart. A mirrored row
applies with Flip set, exactly like a mirrored Similarity result.

### Storage

The same `data/NAME/find.json`, version still 3: `FindResult` grows an
optional `TimeFit` (0 for Similarity results), `FindData` grows
`SearchType` (defaults to `Similarity`) and `TargetIndicator` (the
ZigZag indicator searched). Old files load unchanged; the results
window shows a `Fit` column only for ZigZag data.

## Not in v1 (next steps)

- Overlay preview thumbnails (like the research tools' HTML report).
- Applying the found zoom to the drawn overlay (the time shift, the
  vertical offset and the mirror are applied; zoom is only a column in
  the list).
- Searching in computed symbols (indexes, averages): the target list
  only offers traded pairs.
