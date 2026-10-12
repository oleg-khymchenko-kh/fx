# Find similar (search for a Shift indicator)

Status: implemented, v3 (v1 + the ZigZag search type + the Levels2 search
type).

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
- **Levels2** - match the picture of a Levels2 indicator (the set of
  level dashes) instead of the curve, see "Levels2 search" below.

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

## Levels2 search

The third search type. Instead of a curve (Similarity) or a run of
zigzag points (ZigZag) it compares the PICTURE a Levels2 indicator
draws (docs/levels2.md): the set of dashes at the right edge, each at a
price row with a left part (approach) and a right part (reversal) in
two colors. The search looks for a past window whose Levels2 picture
looks like the picture of the selection.

### Parameters

- **Template** - a drop-down over all Levels2 indicators. The chosen
  one is the template: its levels inside the chart selection, with its
  own pips -> pixels tables, are the pattern. As the template a Levels2
  whose source is a Shift indicator uses the shifted copy's candles
  (chart time, flip included), exactly what the chart draws in the
  selection. The template does not have to be in the search list.
- **Search in** - a multi-select over the same Levels2 indicators,
  independent of the template. Every checked indicator is a search
  target: the search runs in the indicator's pair with the indicator's
  tables, so two Levels2 with different tables on the same pair are two
  different searches. A Levels2 whose source is a Shift indicator
  searches the Shift's target pair. A Levels2 whose source is not a
  traded pair is not offered. The `Pair` column of the results shows
  the pair a row belongs to, and that pair is what a clicked row
  overlays.
- **Max zoom, %** - the clamp `1/(1+P/100) .. 1+P/100` on the price
  zoom, see the algorithm. 0 means no zoom.
- **Step, min** - the candidate step along the time axis, shared with
  Similarity (1 hour by default).
- **Tolerance, pips** - how far a candidate level may sit from the
  template level and still count as the same level. It is the width
  (sigma) of the Gaussian used to blur both pictures; 5 pips by
  default, 1..100. Stored on the indicator as `FindLevelTolerancePips`.
- **Levels** - a table of template levels the search must respect.
  One row is:
  - `Level` - the price of the level, as the chart shows it for the
    template's series;
  - `Use` - `Required` (every result must contain the level),
    `Preferred` (results that contain more preferred levels rank
    higher) or `Off` (the row is kept but ignored);
  - `Pips up` / `Pips down` - the level is really the range from
    `Level - Pips down` to `Level + Pips up`; a candidate level must
    land inside it after the alignment. A new row gets the Tolerance
    value for both;
  - `Seq` - empty or a whole number 1, 2, 3... Empty means the level
    may sit anywhere in time inside the window. Numbered levels must
    come in time order: every level with number N comes after every
    level with a smaller number and before every level with a bigger
    one. Several rows with the same number may come in any order
    among themselves (gaps in the numbers do not matter). Two
    `Required` rows whose numbers differ by exactly 1 (say 2 and 3)
    also form a range: between the two matched levels the price must
    stay inside them, no minute high above the higher level and no
    minute low below the lower one (the minutes of the two levels
    themselves are not checked). The range is taken from the
    candidate's own two levels, not from the template;
  - `Min left` / `Min right` - the smallest approach and reversal in
    pips a candidate level must have to count as this level.

  Rows are editable, can be added and deleted; an empty table (or only
  `Off` rows) means no constraint.

  The table is generated from the template's levels in the selection
  by two thresholds, `Min left, pips` and `Min right, pips` above the
  table (50 / 50 by default, stored on the indicator as
  `FindLevels2MinLeftPips` / `FindLevels2MinRightPips`): every template
  level whose approach and reversal reach both thresholds becomes a
  `Required` row, with the thresholds as the row's minimums and `Seq`
  numbered 1, 2, 3... by the level's time. Rows are always in the
  order the levels appear in time (a level added from the chart is
  inserted at its time; a hand-typed row has no time and stays at the
  end). The
  `Generate` button rebuilds the table from the current thresholds
  (and drops hand edits). Changing the template also rebuilds it,
  unless the table has hand edits. The `Delete` button removes the
  selected rows (the Delete key on a selected row works too). Like the
  ZigZag points table it is not persisted: every Find rebuilds it from
  the fresh selection.

  The `Table` / `Chart` combo next to the buttons switches how the
  levels are picked. `Chart` replaces the table with a mini chart of
  the template series over the selection (`LevelsPickerView`): per
  pixel column the high-low bar in blue, and every level of the
  template as a circle at its minute and price: gray = not used (no
  row or an `Off` row), red = `Required`, light blue = `Preferred`.
  Next to every circle whose row has a `Seq` the number is written
  (above a top, below a bottom, on a white patch so the line does not
  hide it). A click on a circle moves it round: gray -> red -> blue -> gray (a
  gray circle without a row gets a new `Required` row; blue -> gray
  sets the row to `Off`, so its settings stay). Right click opens a
  menu with `Add level`: it takes the extremum nearest to the mouse
  (within 40 px; only extremums whose both arms are at least 4 px on
  the mini chart, so tiny wiggles are skipped), adds it as a circle
  and as a `Required` row. A new row gets min(threshold, the level's
  own arm) as its minimums, so the template itself always passes its
  own row. Adding a row renumbers `Seq` of all rows by time, until the
  user edits a `Seq` cell by hand; after that a new row gets an empty
  `Seq`. Hovering a circle shows the price, the side and the arms in
  pips. The table stays the single list behind both views. The last
  mode is stored on the indicator as `FindLevels2PickOnChart`.

  Note: the candidate side only knows the levels that pass the
  indicator's own pips tables (the first rows of its left and right
  tables), so a row whose minimums are below those never matches
  smaller swings than the indicator draws.

No Smoothing and no Points table. The chosen template is stored on the
indicator as `FindLevels2Template`, the search list as
`FindLevels2Targets`, the type as `FindSearchType = Levels2`. The next
Find opens with the same template and list; without a stored template
the drop-down starts on the first checked indicator. Renaming a Levels2
indicator renames it in both stored fields.

### Algorithm

1. **Template.** The selection is floored / ceiled to whole hours of
   trading time (the same window the Similarity search uses, at least
   2 hours). The template levels are found by the chart's own
   `Levels2Finder` over that window with the anchor at the window end,
   so they are the dashes the chart shows for the selection. At least
   two levels are required. Each level is one mark whose WEIGHT is the
   total dash length: left table pixels at the approach plus right
   table pixels at the reversal. Both arms count, so with the default
   tables a level with arms of 100+ / 100+ pips weighs the most (30 +
   30 px) and a level with arms of 20 / 30 pips the least (0 + 10 px).
   A mark lives in one of two channels, top or bottom (the two color
   orders of a dash). Rows are pips relative to the mean price of the
   template's levels.
2. **Candidates.** For every target, windows of the template's trading
   length ending at every Step on the hour grid, from the start of the
   history to a window that ends at or before the fragment start. The
   levels of a candidate window are the same levels the chart would
   draw with the selection set to that window: approach back past the
   window start, reversal only up to the window end.
3. **Score.** Both pictures are blurred with a Gaussian of width
   Tolerance per channel and compared with the cosine similarity, so
   `sim` is 1 for the same set of dashes and falls when a dash is
   missing, extra, at another row, of another total length or color.
   A strong level (long arms) weighs more than a weak one, so the
   picture's big features dominate, as they do for the eye; the split
   of a dash into its left and right part is not compared, only its
   total length. The comparison is
   done on the sparse marks against a dense blurred template, which
   makes a candidate cost a few thousand multiply-adds.
4. **Alignment.** The candidate's rows are measured from its own level
   mean, then shifted by -S..+S pips (S = half the template's price
   range, at least 20, at most 150 pips) in steps of half the
   tolerance with a 1-pip refinement around the best step. The best
   shift gives the vertical offset of the result (`MeanA`, `MeanB`), so
   applying a row puts the matched dashes on the template's rows.
5. **Zoom.** The candidate's rows are scaled by every zoom of a
   geometric grid inside the clamp (steps of about 15 %: with 50 %
   the grid is 0.67, 0.76, 0.87, 1, 1.15, 1.31, 1.5). The best zoom
   is reported in the `Zoom` column (template = zoom x candidate, the
   same meaning as in Similarity) and, like there, only shown, not
   applied. Zooming the price changes which swings pass the pips
   filters and how long their parts are, so a stretched copy of the
   template is not a perfect match even at the right zoom.
6. **Mirror.** Both orientations are checked: the mirrored candidate
   has negated rows and tops and bottoms swapped (so the colors flip
   too). A mirrored match carries the `M` flag and applies with Flip.
7. **Levels table.** Every used row is a template row `R` (its price
   in pips from the template's level mean, rounded) with a range
   `R - down .. R + up`, two arm minimums and an optional `Seq`. A
   candidate level matches a row at an alignment (zoom, mirror, shift)
   when its approach and reversal reach the minimums (the candidate's
   own pips, not scaled by the zoom; the side is not checked) and its
   aligned row falls inside the range.
   - Per zoom and mirror every qualifying level stamps the interval of
     shifts where it lands inside its row's range, once per row. A
     shift is allowed when every `Required` row stamped it; the count
     of `Preferred` rows that stamped it is an upper bound of the
     preferred matches there.
   - Without `Seq` numbers that count is exact. With numbers, an
     allowed shift is checked exactly: rows are processed group by
     group in number order, each `Required` row of a group takes its
     earliest matching level after the previous group, a `Preferred`
     row is included when that does not block the following groups (a
     small dynamic program over "the time the group ends", keeping only
     states that end earlier with more preferred matches). A shift with
     no valid order is dropped; otherwise it gets the exact number of
     matched preferred rows. Rows without a number take part in the
     count but not in the order.
   - Range pairs (two `Required` rows with numbers N and N+1): the
     required numbered rows are pinned by a small search over their
     matching levels in number order (a level must come after every
     pinned level with a smaller number, and for a pair the minutes
     between the two levels must stay inside them, checked with a
     per-target block max/min index, 64 minutes per block); every full
     pinning runs the same group DP with the required rows fixed to the
     pinned times, the best preferred count wins. The search stops as
     soon as the upper bound is reached or after 4096 full pinnings.
   - The best alignment of a candidate is the one with the most
     preferred matches, then the highest `sim`. Shifts are scanned from
     the highest preferred count down and the scan stops as soon as a
     lower count cannot win. Small shift sets (up to 48 shifts) are
     scanned shift by shift, big ones (no rows) in steps of half the
     tolerance with a 1-pip refinement as before.
   - A candidate that lacks a qualifying level for some `Required` row
     is dropped before any scoring; a candidate without an allowed
     alignment gets `sim` 0 and is dropped.

   Checked on EURUSD 2020-2026 (template week of 2026-09-14, 5 rows at
   30 / 30, tolerance 5, step 1 h) with a brute-force checker of every
   result: 0 required or order violations, the preferred count equal to
   the brute-force best at the result's alignment, the list correctly
   sorted, in every mix (all required, required + preferred, paired
   numbers, preferred only, 8 up / 2 down ranges). Time 1.1 .. 2.7 s
   against 1.5 s without rows. With range pairs (all 5 rows required,
   numbered 1..5) the brute-force checker also scans every minute
   between the paired levels: 0 violations, 78 results instead of 100,
   2.8 s.
8. **Ranking.** Candidates with `sim > 0` are sorted by the number of
   matched preferred rows, then by `sim`; a window
   closer than one window length to an already kept better one is
   dropped (the same dedup as Similarity); top 100 per target, the
   merged list cut to 100.

### Results and applying

The results window hides the `Rho` and `Fit` columns for this type
(when the table has `Preferred` rows, the `Fit` place shows `Pref` as
`matched/total`, stored as `FindResult.Preferred` /
`PreferredTotal`) and
shows the number of levels of the candidate window in the last column
(`Levels` instead of `Overlap`). `Sim` and `Score` carry the same
cosine. Applying a row works like the other types: the row's `Symbol`
is the pair, `SourceUnix` the window start, the vertical offset comes
from `MeanA` / `MeanB`, Flip from the `M` flag.

### Speed

Finding the levels of 97k candidate windows with the chart's finder
takes about 9 s on 16 years of EURUSD M1, too slow for a search. The
search builds a `Levels2Index` (`Chart/Levels2Index.cs`) per target
instead: one forward and one backward monotonic-stack pass over the
whole history (about 0.8 s for 5.7M minutes) store, per minute and per
side, the previous higher-or-equal minute, the approach and the final
reversal (up to the next strictly higher minute), plus the sorted list
of minutes that pass both pips filters with their final reversal. The
levels of a window are then the passing minutes inside it, except the
window's suffix maxima (minutes not exceeded before the window end),
whose reversal is cut by the window end: those are found by walking the
previous-higher chain back from the anchor and tracking the running
low, and their reversal is replaced by the running one. This matched
the finder on 400 random windows (5530 levels, no differences). The
approach is not capped at 50 000 minutes as in the drawn version, which
only matters for a swing higher than everything in the previous 35
days with an approach below the left table's last row.

The whole search takes about 2 s for one pair at a 1-hour step (index
0.8 s, scan 1-1.5 s on 8 threads); 15-minute steps take 4x longer.

Checked on EURUSD with planted copies of the selection (the fragment
and 7 days before it copied into March 2025): a copy moved by +37 pips
is found first with `sim` 1.000 and the offset -370 points, a mirrored
copy moved by -20 pips is found first as `M`, a copy stretched 1.3x
(searched with tables stretched 1.3x so the dashes stay the same) is
found first with `Zoom` 0.76, and with Max zoom 0 the same stretched
copy drops to rank 61.

### Storage

The same `data/NAME/find.json`, version still 3: `FindData.SearchType`
is `Levels2`, `TargetIndicator` the template Levels2 indicator,
`Targets` the searched Levels2 indicators, `StepMinutes` the step,
`ZoomPercent` the clamp, and a new optional `TolerancePips` (0 in older
files and for the other types).

## Not in v1 (next steps)

- Overlay preview thumbnails (like the research tools' HTML report).
- Applying the found zoom to the drawn overlay (the time shift, the
  vertical offset and the mirror are applied; zoom is only a column in
  the list).
- Searching in computed symbols (indexes, averages): the target list
  only offers traded pairs.
