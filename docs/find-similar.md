# Find similar (search for a Shift indicator)

Status: implemented, v3 (v1 + the ZigZag search type + the Levels2 search
type; since 2026-10-12 the Levels2 search works on pairs with its own
level tables and a window length grid).

## Goal

Given a selected fragment of the chart, find the places in the past
where the source pair moved with a similar shape, and let the user
overlay any of them on the fragment with one click, using the Shift
indicator (docs/shifted-symbol.md).

## Flow

1. Select a time range on the chart (Shift+drag - the same range band
   the stats popup uses).
2. Right-click the Shift indicator row in the right bar, pick `Find`.
   The item is enabled while a selection exists, or when the indicator
   has a last search (see "Last search" below), so a search can be
   reopened with no selection at all. A "Search parameters" dialog
   opens first (see below); Cancel there drops the search.
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

## Last search

Since 2026-10-14 (user's request) the dialog remembers the whole last
search per Shift indicator: the fragment range and everything the
dialog showed when `Search` was pressed - search type, all text boxes
and their units, the checked pairs and ZigZag indicators, the
template, the Level tables, the `Table` / `Chart` mode, the ZigZag
Points table and the Levels table with all hand edits (Use, Pips
up / down, Seq, Min left / right, rows added from the chart, deleted
rows). It is stored on the indicator in config.json as `FindLast`
(`FindDialogState`) and copied on Edit and Clone; renaming a ZigZag
indicator renames it inside `FindLast` too. Cancel does not change
it. Edit of the Shift indicator now keeps all `Find*` fields; before
2026-10-14 it kept only the four Similarity ones and dropped the
search type and the ZigZag / Levels2 settings.

- `Find` with a last search opens the dialog exactly as it was, on the
  saved range, also when another range is selected on the chart. The
  fragment line says `(last search)`; when the chart selection differs
  it also says which range `New search` would take.
- When the saved range is in years the chart has not loaded yet (lazy
  loading), those years are loaded for all pairs before the dialog
  opens, so the mini chart and the template levels are there.
- `Find` without a last search works as before: it needs a selection.
- The `New search` button (bottom left of the dialog) starts over,
  the same as `Find` did before 2026-10-14: the dialog is reopened on
  the current chart selection (or on the same range when nothing is
  selected), the fields are filled from the stored per-type values on
  the indicator (below) and the Points and Levels tables are generated
  from scratch.

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
min+max of the shifted series. The mirror base the copy ends up with
(the target's own base for a mirrored pair, the flip base for a flipped
copy) is stored in `AppConfig.MirrorBases` under the indicator's name,
and cleared when the copy is not mirrored, so a later rebuild of the
copy (tab switch, chart reload, start) uses the same base instead of
recomputing it from the candles that happen to be loaded. Applying a
result also re-syncs the series' source with the indicator's Source
(docs/tabs.md, per tab Shift) before the vertical offset is set over
the source. find.json carries a Version field
(currently 3); files of another version are treated as absent, so after
a format change the user just re-runs Find. Applying the zoom is still
a next step.

Renaming any indicator now also carries its per-series vertical offset
and hidden flag to the new name (chart state keys are renamed in the
saved state and in memory).

## Search types

The dialog starts with a `Search type` combo with three options:

- **Similarity** - everything described above, unchanged.
- **ZigZag** - match a sequence of ZigZag extremums instead of the
  smoothed curve.
- **Levels2** - match the picture of the levels of a pair (the set of
  level dashes a Levels2 indicator would draw) instead of the curve, see
  "Levels2 search" below.

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
  carry the `Zoom` mark, at different pip levels. The table is kept
  in the last search (see "Last search"); `New search` rebuilds it
  from the fresh selection.

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

## Levels2 search

The third search type. Since 2026-10-18 (user's rule) it checks one
thing: the price passed through a set of points, one after another. A
point is a turning point at a price level of the template. There is no
picture of dashes, no level tables, no time step, no tolerance and no
time stretch any more. Before that day the search compared blurred
pictures of Levels2 dashes over windows of a few fixed lengths; that text
is in the git history of this file.

The template and the searched symbols are pairs (since 2026-10-12), so no
Levels2 indicator has to exist on the chart.

### Parameters

- **Template** - a drop-down over the traded pairs. The pair whose levels
  inside the chart selection give the points. The default is the Shift's
  Source pair (or the stored last choice). It does not have to be in the
  search list.
- **Search in** - a multi-select over the traded pairs. The `Pair` column
  of the results shows the pair of a row; that pair is what a clicked row
  overlays.
- **Max zoom, %** - the clamp `1/(1+P/100) .. 1+P/100` on the price zoom
  of a match (see below). 0 means no zoom.
- **Min span** - a number plus Minutes / Hours / Days: the smallest
  trading time (weekends removed) from the first to the last matched
  point. 0 means no limit.
- **Points** - the table of points. One row is:
  - `Level` - the price, as the chart shows it for the template series;
  - `Use` - `Required` (every result has it), `Preferred` (results that
    have more of them rank higher) or `Off` (kept, not used);
  - `Pips up` / `Pips down` - the point is the range from
    `Level - Pips down` to `Level + Pips up`; 5 / 5 for a new row;
  - `Seq` - the order of the points. Points are taken in `Seq` order;
    equal or empty numbers go by template time, then by table order.
    Every point comes strictly after the one before it;
  - `Gap min` / `Gap max` (user's request 2026-10-18) - the shortest and
    the longest trading time (weekends removed) from the previous point
    to this one. Text like `30m`, `4h`, `2d`; a plain number is hours
    (`1.5` = 90 minutes). Empty = no limit on that side, so with both
    empty the previous point may come any time before. The previous point
    is the previous MATCHED one: when a `Preferred` point before this one
    is skipped, the gap runs from the point before it. The fields of the
    first point in the order do nothing (it has no previous point). The
    column headers have tooltips with the format. `Gap min` bigger than
    `Gap max` is refused.

  At least two points must be `Required`.

  `Generate` fills the table from the template's levels in the selection,
  found with the default Levels2 tables (docs/levels2.md): every level
  whose approach and reversal are both at least 50 pips becomes a
  `Required` row, numbered by time. The `Table` / `Chart` combo, the mini
  chart with the circles (gray / red / light blue, click to cycle, right
  click `Add level`), `Delete`, the hand edit flags and the last search
  snapshot work as before (see "Last search").

Removed on 2026-10-18 together with their dialog fields: `Step, min`
(Levels2 only; Similarity keeps it), `Tolerance, pips`, `Time stretch, %`,
`Level tables` (and the `LevelParamsWindow` popup), the `Min left` /
`Min right` thresholds and table columns. The indicator fields
`FindLevelTolerancePips`, `FindLevels2MinLeftPips`,
`FindLevels2MinRightPips`, `FindLevel2LeftSteps`, `FindLevel2RightSteps`,
`FindLevels2StretchPercent` and the matching fields of the stored dialog
state are gone; old configs still load, the values are ignored. Still
stored: `FindLevels2Template`, `FindLevels2Targets`, `FindZoomPercent`,
`FindLevels2MinSpanMinutes`, `FindLevels2PickOnChart`, `FindLast`.

### What a match is

The points in their order are `p1 .. pn`. Each has a level `L` (display
points), a range `L - down .. L + up` and a side, top or bottom: the side
of the template level the row came from; a hand-typed row gets it from its
neighbours (higher than both = top, lower than both = bottom, otherwise
top when the next point is lower).

A match in a target pair is one minute for every `Required` point and for
any number of `Preferred` points, in that order, each strictly later than
the one before, with:

1. **Value.** A top point uses the minute's high, a bottom point its low.
2. **Level.** One vertical offset `D` and one zoom `Z` for the whole
   match, with `Z * value + D` inside the point's range for every matched
   point. Mirrored (`M`): `-Z * value + D`, and tops and bottoms swap. `Z`
   is taken from a geometric grid inside the Max zoom clamp (steps of
   about 15 %: with 50 % it is 0.67, 0.76, 0.87, 1, 1.15, 1.31, 1.5).
3. **Corridor** (user's rule): between two neighbouring matched points no
   minute goes above the higher of their two values or below the lower
   one (the two minutes themselves are not checked). So a top is the
   highest price between its neighbours and a bottom the lowest: this is
   what makes every inner point a turning point. The first and the last
   point have no outer side to check.
4. **Gaps.** For every matched point after the first: the trading time
   from the previous matched point is inside its `Gap min .. Gap max`.
   The gaps are absolute times, they are not stretched with the time
   ranking below.
5. **Min span.** Trading time from the first to the last matched point.
6. **History.** All points lie before the selection start.

A skipped `Preferred` point is simply left out: the corridor then runs
between the matched points around it.

### Search (`Levels2Search.Search`)

Per target pair and direction the minutes get two arrays from one
monotonic-stack pass each: `NextHigher` (the next minute with a strictly
higher high) and `NextLower` (the next one with a strictly lower low).
Then, in parallel over the start minutes, a depth-first search tries both
orientations, every possible first point (the first `Required` one or a
`Preferred` one before it) and every zoom:

- the start minute fixes the first value and the range of allowed `D`;
- from a matched top at value `x`, the candidates for a next bottom below
  are the minutes that make a new low after it (the `NextLower` chain),
  in the next point's band, until the price first goes above `x`; from a
  bottom to a top above it is the same with `NextHigher`. Other steps (a
  next point on the same side, or a band around `x`) are scanned minute
  by minute until the price leaves the band's envelope, at most 50 000
  minutes;
- `Gap max` cuts the scan for the next point at the last minute it allows
  (binary search on the trading times), `Gap min` skips the candidates
  that come too early;
- each candidate narrows the allowed `D` range; an empty range drops it;
- a start minute is dropped at once when no next point can be reached for
  any zoom (one cheap chain walk), which is the common case;
- at most 20 000 search steps per start minute. Every start keeps its best
  complete match.

### Ranking

- **Pref** first: more matched `Preferred` points rank higher.
- **Sim**: how close the time between neighbouring points is to the
  template, allowing one common time stretch. For every pair of
  neighbouring matched points that both have a template time,
  `r = ln(found gap / template gap)`; with `s` the mean of `r`,
  `Sim = exp(-sqrt(mean((r - s)^2)))`. 1 means the same rhythm, at any
  speed. With only one gap `Sim` is 1.
- **Pace** (stored in `Score`): `exp(-|s|)`, 1 = the template's speed.
  Only breaks ties of `Sim`.
- A match whose span overlaps an already kept better one of the same
  direction by more than half of the shorter span is dropped. Top 100 per
  pair and direction, the merged list cut to 100.

### Results and applying

`FindResult` for a match: `SourceUnix` = the first matched minute,
`WindowMinutes` = the span, `Sim`, `Score` = pace, `Zoom` = `Z`,
`Mirrored`, `Overlap` = number of matched points (the `Levels` column),
`MeanA` = `D` (the middle of the allowed range), `MeanB` = 0, `Preferred`
/ `PreferredTotal` (the `Pref` column when the table has `Preferred`
rows), `AlignUnix` = the minute matched for the anchor point (the
`Required` point with the latest template time), `KeyPoints` = all
matched minutes, `Reversed`.

Applying a row works like before: the row's pair, Flip from `M`, the
Shift's zoom from `Zoom` and the offset `MeanA - MeanB * zoom` = `D`
(with flip `D - zoom * flipBase`), so the copy is drawn as
`Z * price + D` and every matched point sits inside its range on the
template level (docs/shifted-symbol.md, Zoom). The time alignment (user's
rule 2026-10-12) puts the anchor point on the template's anchor point:
Source time = the result's `AlignUnix`, Chart time = `FindData.AlignUnix`
(the template time of the anchor row). Without an anchor (no `Required`
row with a time) the first matched minute lands on the fragment start.

### Both time directions

Since 2026-10-18 (user's request) every pair is searched twice: forward,
and in the history read backwards in time. The second pass runs the same
code over a time-mirrored copy of the minutes:
`ShiftedSymbol.Shift(minutes, axis - 60, true)` with
`axis = vHistory + vStart` (virtual time, both on the hour grid), so the
mirrored history covers the same virtual range and "before the
selection" means the same minutes. Its results are mapped back to real
time (`Levels2Search.Unreverse`: minute `t -> axis - 60 - t`, window start
`axis - end`) and get `FindResult.Reversed = true`; both passes are ranked
separately and then sorted together.

The results window shows an `R` in the `Back` column (Levels2 only); the
`Source window` is always in real time. Applying an `R` row turns on
"Mirror in time" for the Shift (docs/shifted-symbol.md) so the found
sequence is drawn in the template's order: Source time = the result's
`AlignUnix`, Chart time = the template's `AlignUnix` plus one minute (a
mirrored copy puts a source minute on `[chart - 60, chart)`); without an
anchor Source time = the end of the found span and Chart time = the
fragment start. Applying a forward row turns "Mirror in time" off. No live
tail on a mirrored copy.

### Key points (circles on the found chart)

Since 2026-10-18 (user's request) a shown result marks the matched points
on the copy: one circle per matched point, centered on the copy's line at
that minute (the high for a top, the low for a bottom). Red = `Required`,
light blue = `Preferred`, the colors of the level picker. The circles lie
UNDER the chart lines and are semi-transparent (user's rule): 6 px
radius, alpha 140 of 255, no rim, drawn in the same under-the-lines pass
as the comment spots (`ChartRasterizer.Render`), and they are not part of
the comment hit list.

The points are the match itself, stored as `FindResult.KeyPoints`, a list
of `FindKeyPoint(Unix, Value, Top, Required, Sequence, Rule)`: the minute
and the value in the target pair's display points, the side, the row's
flags, its `Seq` and its row index. Results saved before 2026-10-18 have
no list; applying one shows no circles and the log says to run Find
again.

Applying a result copies the list to the Shift as
`IndicatorSymbol.FindKeyPoints` (`ShiftKeyPoints`: target pair, the Source
time it was applied with, the points). The circles show while the Shift
still uses that pair and that Source time, so they follow the copy when
it is moved with the time wheel or the Alt drag, zoomed or flipped, and
hide when a tab, a preset or the Edit dialog gives the copy another
Source time or pair (they come back with the old placement). Any other
Find result clears them. MainWindow maps each point through the Shift's
current shift (`ShiftedSymbol.MapMinute`, also for "Mirror in time") and
hands `KeyPointMark(Unix, Top, Required)` to `ChartView.SetKeyPoints`; the
chart reads the value from the copy's candle at that minute at render
time. A minute hidden as wide spread has no candle and no circle.

On / off: `Show key points of a Levels2 result` in the Shift params of the
Edit dialog (`IndicatorSymbol.ShowFindKeyPoints`, default on) and the
`Show key points` checkbox in the top right corner of the results window
(Levels2 only). Both write the same setting.

### Speed

EURUSD 2025-2026 (about 665 000 minutes), one pair, both directions:
0.4-0.6 s for four or five points, 0.6 s with Max zoom 50 % (scratch
harness, Debug build); forward only is about a third of that. The cost
grows with the history length, so 16 years of one pair take a few
seconds.

### Checks (2026-10-18, scratch harness, EURUSD 2025-2026)

Template 1-4 Sep 2026, points = the template's 30-pip zigzag turns (5
points, ranges 5 / 5). For four setups (all required; two of them
preferred; Max zoom 50 %; Min span 2 days) every result was checked by
brute force on the minutes of its own direction: one point per required
row and as many preferred points as `Pref`, every value the high / low of
its side, inside its range at the result's `Z` and `D`, the points in
order, no minute outside the corridor between neighbours, Min span kept,
`AlignUnix` on the anchor point, no overlap between kept results, the
list sorted: 0 violations. The fragment with 4 days around it, moved by
+37 pips, was planted into March 2025 forward and into June 2025 mirrored
in time: the two copies are the two best results, both with `Sim` 1.0000,
pace 1, offset -370 points and every point on the (mirrored) image of its
template point; a forward-only search finds only the forward copy. After
applying either one, every circle lands on the minute of its template
point.

Gaps (same day, same harness; template gaps 30.5 h, 33.8 h, 19.6 h): the
brute-force check also checks every gap against its row. With
`Gap min` / `Gap max` = 0.5x / 1.5x of the template gap on every point
(and a 30-day `Gap min` on the first point, which must do nothing): 17
results, 0 violations, and both planted copies are still found with
`Sim` 1. One `Gap max` only, and a `Preferred` point skipped with a
`Gap min` on the next one: 100 results each, 0 violations. `Gap max` = 1
minute between the first two points (a 58-pip move): 3 results, all real
one-minute spikes (11 Apr 2025 08:01, 6 Jan 2025 14:15, 11 Sep 2025
12:30 reversed), 0 violations.

### Storage

The same `data/NAME/find.json`, version 3: `FindData.SearchType` is
`Levels2`, `TargetIndicator` the template pair, `Targets` the searched
pairs, `ZoomPercent` the clamp, `MinSpanMinutes`, `AlignUnix` the template
time of the anchor point; `StepMinutes`, `TolerancePips` and
`StretchPercent` are 0 for new data. Every `FindResult` carries its own
`WindowMinutes`, `AlignUnix`, `KeyPoints` and `Reversed` as described
above. Older Levels2 files still load and show; their `Sim` is the old
picture cosine. The gaps are not stored in `find.json`; they live in the
rows of the last search (`FindLevelRow.MinGap` / `MaxGap`, the text as
typed, empty in older states).

## Not in v1 (next steps)

- Overlay preview thumbnails (like the research tools' HTML report).
- Searching in computed symbols (indexes, averages): the target list
  only offers traded pairs.
