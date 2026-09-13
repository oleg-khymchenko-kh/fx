# Compact the pairs

Status: implemented, v1.

## Goal

Right-click the chart -> `Compact`. All visible main pairs move up or down
so that they sit as close to each other as possible, and every pair stays
on the 100 pip grid: a grid line always has a round price of each pair on
it (1.3100, not 1.3105).

What "close" is measured on:

- no selection - the minute under the cursor. When one column holds more
  than one minute (zoomed out), all minutes of that column are used;
- a selection (Shift + drag) - the whole selected period. Where the
  right-click lands does not matter: like the `Space` stats popup, the
  selection wins. `Escape` clears it to go back to the cursor minute.

`Compact` is in the common block that every chart menu ends with, next to
`Measure distance`.

## Which symbols move

Only the main pairs (`SymbolConfigs`: EURUSD, GBPUSD, EURGBP, USDCHF,
USDJPY, AUDUSD, NZDUSD, USDCAD, GER40) that are visible, are drawn as a
price line (not a bottom panel) and have at least one candle in the
window. The set is the same as for Play (`ChartView.VisiblePairs`).

Indicators are not moved by `Compact` itself. Their offset is stored
relative to the source pair, so they move together with it. Indexes and
indicators without a source stay where they are.

## What is made small

For every minute `t` of the window:

    distance(t) = highest pip of any pair at t - lowest pip of any pair at t

measured on the screen (display points, offsets applied). `Compact` makes
the largest `distance(t)` of the window as small as possible. For one
minute it is just the height of the bundle of candles in that minute.

## The grid rule

Grid lines are every 1000 display points (100 pips,
`ChartRasterizer.GridPriceStepPoints`). A pair is on the grid when
`seriesOffset + Transform.ToDisplay(0)` is a multiple of 1000 - the same
rule as `GridAlignedOffset` (docs/symbol-bar.md). It also works for the
mirrored pairs, whose `MirrorBase` is not round.

So a pair can only move in steps of 100 pips, and the task is to pick a
whole number of steps for every pair.

## The solver

`PairCompactor.Solve` is pure code without WPF.

Every candle is taken as `display - Transform.ToDisplay(0)`. With that
value a pair on the grid is drawn at `value + 1000 * k`, `k` a whole
number. Everything is in whole display points, so all numbers are
integers.

1. `reach[i][j]` = the largest `high_i - low_j` over the minutes where both
   pairs have a candle. `reach[i][i]` is the tallest candle of pair `i`. If
   two pairs have no common minute, the whole window is used instead
   (`max high_i - min low_j`).
2. For the steps `k` the largest distance of the window is the largest
   `reach[i][j] + 1000 * (k_i - k_j)` over all `i`, `j`. This is the same
   number as the per-minute definition above, only the two "largest of"
   are taken in the other order.
3. Binary search on a limit `L`: is there a `k` with largest distance
   `<= L`? Every `i`, `j` gives `k_i - k_j <= floor((L - reach[i][j]) / 1000)`.
   That is a system of difference constraints. Bellman-Ford finds a
   solution, or a negative cycle when there is none.
4. Often many `k` reach the smallest `L`: one pair that moved a lot during
   the period sets the limit and the other pairs have room. Then the
   solver takes the `k` with the smallest sum, over all couples of pairs,
   of the largest distance between just those two pairs
   (`max(reach[i][j] + d, reach[j][i] - d)`, `d = 1000 * (k_i - k_j)`).
   Search: move any group of pairs one step up or down, keep the move with
   the smallest sum while the largest distance stays `<= L`, repeat until
   no move helps. The sum is an L-convex function of `k`, so this local
   search ends at the best sum.

Checked against brute force on 1500 random cases with 2 to 5 pairs: the
same smallest distance and the same sum. On real data (six pairs of the
`All` tab, 2026-09-09) a round price of every pair, USDCHF and USDCAD
included, lands on a grid line. Speed: 9 pairs with one year of minutes
take about 0.1 s.

## Where the bundle goes

The solver only fixes the pairs against each other. The whole bundle is
then moved by whole 100 pip steps so that its middle (half way between the
highest and the lowest pip of the window, flatten shift included) is
closest to the price under the right-click. So it lands within 50 pips of
the cursor.

If after that the bundle is not fully inside the view (deep zoom, long
period), the view pans so that the middle of the bundle is under the
cursor. Otherwise the view does not move.

The new offsets go into the tab state (`SeriesOffsetsChanged`), like
`Align to grid`. The log gets a line like

    Compact: max distance 39.1 pips

## Notes

- In Play mode minutes after the play time are not used.
- Hidden wide spread minutes are not in the chart data, so they are not
  used either.
- Live minutes are used together with the stored ones.
