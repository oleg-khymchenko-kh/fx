# Entry points indicator

Status: implemented, v1.

## Goal

A new indicator type `EntryPoints` (label "Entry points" in the Add /
Edit dialog). For every minute of the source pair it answers two
questions:

- Would a **buy** opened at that minute win, with the given stop loss and
  take profit?
- Would a **sell** opened at that minute win, with the same stop loss and
  take profit?

The answers are drawn as three thin rows at the bottom of the chart, not
as a price line.

## Parameters

- Stop loss, pips - default 15
- Take profit, pips - default 45

Both are in pips of the source symbol. They are converted to raw points
with the source pip size (`SourcePipPoints`), so 45 pips = 450 points on
EURUSD and 4500 points on USDJPY.

## Trade rules

Entry price is the `Avg` of the minute (the same price the chart line
uses). The scan starts at the **next** minute, so the entry minute's own
high and low do not count.

- Buy: wins if some later minute's high reaches `entry + take` **before**
  any minute's low reaches `entry - stop`.
- Sell: wins if some later minute's low reaches `entry - take` **before**
  any minute's high reaches `entry + stop`.

If both barriers are touched inside the same minute candle, M1 data
cannot tell which came first, so the loss is assumed to come first (the
pessimistic rule).

A side is **decided** once one of its two barriers is hit. A trade that
is still open when the data ends is neither won nor lost - it is
undecided, and such a minute shows nothing at all (all three rows stay
background). "Both lost" therefore means both sides really hit their
stop, not "no winner yet". This is what keeps the right edge of the
chart clean.

Gaps (weekends, missing minutes) are skipped, exactly like
`Fx.MaCross`: the trade simply stays open across the gap.

Which of the three rows can light up depends on the ratio of the two
distances, see the derivation at the end:

- take profit larger than stop loss (the default 45 / 15): at most one
  side wins, so "both won" never happens and the black row is common.
- take profit smaller than stop loss: both sides can win for the same
  minute, and the black row is almost empty - it then needs one single
  minute whose range covers both stops.

## Algorithm

`EntryPointsSymbol.Evaluate` walks the candles backwards and keeps two
monotonic stacks - the prefix maxima of future highs and the prefix
minima of future lows. For each minute it binary searches each stack for
the first future candle that reaches a level, so all four barriers
(buy take, buy stop, sell take, sell stop) cost `O(log n)` per minute.
This is the same trick as `Fx.MaCross/BarrierTable`. About 200k minutes
per 30 ms, so a full EURUSD history is under a second; the DB write is
what takes the time.

## Storage

The result is a normal symbol in the candle DB, one record per source
minute, so lazy year loading, Delete and Rename all work unchanged. The
three channels of a candle record carry the state:

- `Max` = 1 when the buy wins, else 0
- `Min` = -1 when the sell wins, else 0
- `Avg` = 1 when both sides lost, else 0

An undecided minute is all zeros, which is also what an empty column
looks like, so both render as background.

`Min`/`Max` were picked because the rollup levels
(`CandleHistory.Levels`) keep min and max, so "any buy in this column"
and "any sell in this column" survive zooming out for free. `Avg` is
summed into `AggBlock.AvgSum`, so "any lost minute in this column" is
`AvgSum > 0` - also exact at every zoom level. Nothing is lost when one
screen column covers a month.

The values are flags, not prices, so the display transform must not
touch them: `IndicatorConfig` forces `mirror = false` and
`pipPoints = 10` for this type, which makes `CandleTransforms.Transform`
a copy.

## Rendering

`EntryPointsColumns.Build` turns the history into one byte per screen
column with three bits (buy / none / sell), using the same level choice
and column edges as `ChartColumns`, but with OR instead of averaging.

`ChartRasterizer.DrawEntryPanel` draws three 3 px rows one under
another, the lowest one 10 px above the bottom edge of the chart:

- top row: green (#2E7D32) when the buy wins, background when it loses
- middle row: black when neither side wins, background otherwise
- bottom row: dark orange (#E65100) when the sell wins, background when
  it loses

The band is opaque: every column of every row is painted, so the price
lines and the grid never show through it. "Background" is the chart
background colour (white), not a skipped pixel. Columns with no data,
and the undecided tail of the history, end up all background because
their state byte is 0.

Several entry point indicators stack upwards, 2 px apart. Hiding the
symbol in the symbol bar hides its rows.

Entry point series are skipped everywhere a price line is expected:
`RenderLine` building, the initial auto scale, the global price range
(pan limits), the range statistics popup and the align-to-source check.

## Creation, compute and refresh

Same Add / Edit dialog. Picking `Entry points` shows the Stop loss and
Take profit fields. `IndicatorSymbol.SameData` compares Source, stop and
take for this type, so a color or name only change does not recompute.

`Compute derived`, the dialog `Refresh` button and the context menu
`Refresh` all branch to `EntryPointsSymbol`. `Generate` rebuilds the
whole symbol. `Refresh` only rewrites the tail: it scans the stored
result backwards for the last decided minute (any record that is not all
zeros) and recomputes from the minute after it, because a decided minute
can never change when new candles arrive. The backward scan doubles its
window from one day up to 400 days.

Entry points are not part of the once-a-minute automatic refresh (only
Averages are), so live candles do not extend the rows until a manual
refresh.

Refresh keeps whatever is already stored, so it cannot repair rows
written by an older build - use the `Rebuild` context menu item for
that.

## Which rows can light up together

Let `bt` = first minute reaching `entry + take`, `bs` = first reaching
`entry - stop`, `st` = first reaching `entry - take`, `ss` = first
reaching `entry + stop`. Buy wins when `bt < bs`, sell wins when
`st < ss`.

With **take < stop**, a minute that reaches `entry - stop` also reaches
`entry - take`, so `st <= bs`; likewise `bt <= ss`. Both losing means
`bs <= bt` and `ss <= st`, which chains into
`st <= bs <= bt <= ss <= st`: all four are the same minute, one candle
that covers both stops. So the black row is almost always empty.

With **take > stop** it is mirrored: a minute that reaches `entry + take`
also reaches `entry + stop`, so `ss <= bt`, and `bs <= st`. Both winning
would mean `bt < bs <= st < ss <= bt`, which is impossible. So the green
and orange rows never light up in the same column, and the black row
marks every minute where price hit the small stop in both directions
before running 45 pips either way - which is most of them.

## Not in v1 (next steps)

- No live update between refreshes.
- The symbol bar row still prints the flag value as if it were a price.
- No per-column tooltip telling why a minute lost.
