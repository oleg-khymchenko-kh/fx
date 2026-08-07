# Similar weeks search

Status: implemented, v1 (research console tool `src/Fx.SimilarWeeks`).

## Goal

Given a target trading week of a pair, rank all other weeks in history by
how similar their price shape is. Similarity ignores absolute price level
and absolute amplitude (within limits): what matters is the picture, not
the pips.

## Week series

- A trading week runs from the Sunday evening open (~21:00/22:00 UTC
  depending on DST) to the Friday close. Minutes are grouped into weeks
  by UTC date, Sunday belongs to the next Monday's week. Minutes before
  Sunday 20:00 UTC or after Saturday 00:00 UTC are dropped.
- Weeks are aligned by their session open: point 0 is the first filled
  minute of the week when it falls into the normal Sunday-evening open
  window (up to Sunday 22:05 UTC). This keeps the session grid aligned
  across DST regimes. Weeks that open late (Christmas / New Year, ~5
  weeks in history, up to 35 hours late) are anchored to the expected
  open instead: Monday 00:00 UTC plus the open offset of the last normal
  week. Without this the whole session grid of such a week is displaced
  and the +-24h shift window cannot bridge it.
- The M1 avg series (raw points, 1/100000) is smoothed with a centered
  4-hour SMA (window +-120 minutes, at least 121 samples, edges use the
  truncated window).
- The smoothed curve is downsampled to 15-minute buckets: 480 points per
  week (5 x 1440 / 15). After 4h smoothing the M1 resolution carries no
  extra information, this is a ~15x speedup. Empty buckets are NaN.
- Weeks with fewer than 288 valid points (60%) are skipped (holiday
  weeks, partial history edges).

## Similarity of a pair of weeks

For shift in -24..+24 hours (1-hour step, 49 positions), candidate week B
is moved right by `shift` relative to target week A (positive shift: B
starts later). Only overlapping non-NaN points take part; overlaps
shorter than 288 points are skipped. On the overlap:

- Both series are re-centered by their overlap means (the zero line is
  recomputed per shift, otherwise a vertical bias appears).
- The optimal vertical zoom of B is closed-form least squares
  `k = sxy / syy`, clamped to [0.6, 1.5]. No grid search over zooms:
  this is exactly the best zoom the old 10-step grid was approximating.
- Pearson correlation `rho = sxy / sqrt(sxx * syy)` is reported.
- Fit quality: `score = 1 - residual / sxx` where
  `residual = sxx - 2k*sxy + k^2*syy`. With unclamped k this equals
  rho^2. Anti-correlated weeks get a negative score.
- Overlap penalty: `sim = score * sqrt(overlap / 480)`, so a match found
  on a short overlap ranks below an equal match on the full week.
- Mirror: each shift is also tried with B flipped vertically around its
  overlap mean. In centered form the flip is just a sign change of B, so
  `sxy` flips sign and the same closed-form zoom applies (`k = -sxy/syy`
  clamped to the same [0.6, 1.5]). The reported rho is the one of the
  winning orientation, so a mirrored match shows a positive rho plus a
  mirror flag.

The best of 49 shifts x 2 orientations by `sim` wins; the tool reports
sim, score, rho, zoom, shift, mirror and overlap for it. Comparing a week with itself gives sim = 1 at
shift 0, zoom 1 (used as a self-test on every run).

## Tool

    dotnet run --project src/Fx.SimilarWeeks [dataRoot] [symbol] [targetMonday] [topN]

Defaults: FXViewer debug data folder, EURUSD, 2026-07-20, top 20.

Output:

- Console: parameters, self-test, top N table.
- `reports/<symbol>-similar-weeks-<targetMonday>.csv`: all ranked weeks,
  `weekMonday;sim;score;rho;zoom;shiftHours;mirror;overlapPoints;weekPoints`.
- `reports/<symbol>-similar-weeks-<targetMonday>.html`: standalone SVG
  overlay charts of the top 5 matches (target in blue, candidate shifted,
  re-centered, zoomed and mirror-flipped when flagged in orange, both in
  pips relative to the overlap mean).

## Not in v1 (next steps)

- DTW with a +-24h band for "same shape but locally stretched in time".
- Chart integration in FXViewer (show the matched week under the live
  chart).
- GBPUSD and cross-symbol search.
