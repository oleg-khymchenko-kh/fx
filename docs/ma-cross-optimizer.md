# Moving-average crossover optimizer

Status: implemented, v1. Console tool `src/Fx.MaCross`.

## Strategy

Two simple moving averages of the same pair:

- `A1` - period of the first average in trading minutes
- `A2` - period of the second average in trading minutes
- `StopLossPips`
- `TakeProfitPips`
- `Risk` - size of one trade as a share of the current equity

Entry: the two averages cross. At that moment we open two opposite trades at
once - one BUY and one SELL. Both use the same size, the same stop loss and the
same take profit. Then we wait until both trades are closed. Only after that we
start looking for the next crossover. While any trade is open we never enter.

Because both directions are opened together, swapping A1 and A2 gives exactly
the same trades, so the sweep only tests pairs with `A1 < A2`.

Result of one entry, in pips:

| case                       | result        | when                       |
|----------------------------|---------------|----------------------------|
| both trades hit TP         | `+2 * TP`     | only possible if `TP < SL` |
| one hits TP, one hits SL   | `TP - SL`     | always possible            |
| both trades hit SL         | `-2 * SL`     | only possible if `TP > SL` |

With `TP <= SL` the "both lose" case cannot happen: price must pass `+TP` before
it reaches `+SL`, so the BUY is already closed in profit. That is why runs with
`TP <= SL` always report `dblLoss = 0`.

## Position size and the drawdown limit

Position size is a share of the current equity, not a fixed lot. `Risk` is that
share for **one** trade, so each entry puts `Risk` into the BUY and `Risk` into
the SELL. The lot is chosen so that hitting the stop loss costs exactly `Risk`
of the equity at entry:

    money result of one trade = equity_at_entry * Risk * pips / StopLossPips

Both trades are sized off the same equity, so inside one entry the two results
add up; between entries the equity multiplies. That is plain compounding: after
a good stretch the same `Risk` means a bigger lot.

The account has a high water mark - the highest equity ever reached, starting at
the deposit. A combination is **dropped** the moment equity falls more than
`--max-dd` (default 25%) below that mark. With a 100,000 deposit the fail line
starts at 75,000; after equity reaches 150,000 the line moves up to 112,500. The
run stops there and the combination never enters the top list - it is not
"stopped out and continued", it is thrown away.

The check runs at every realised equity change, and one entry has two of them:
when the first of the two trades closes, and when the second one closes. So an
entry that first takes the stop loss on one leg and only later takes the profit
on the other is measured at its low point too, not just at the net result.
Floating (unrealised) loss while a position is open is still not counted - a
live account would show a deeper dip between those points.

The deposit itself only scales the report. Everything is relative, so
`--deposit` never changes which combinations survive or how they rank.

## Data and units

Same store as the app: `data/<SYMBOL>/<year>.m1`, one record per minute, prices
in points (1/100000). The tool reads the files directly, it does not reference
FXViewer.

Only filled minutes are kept, packed into one flat array in time order. Index in
that array = trading minute, so an MA period of `1440` is one trading day and
`14400` is two trading weeks. Weekends are simply absent, exactly like the
`Average` indicator in the app (see `docs/moving-average.md`).

One pip = 10 points for the normal pairs, 1000 points for `*JPY*`
(`--pip-points` overrides it).

## Backtest rules

- Averages are count based, over full windows only. The sweep starts at bar
  `A2 - 1`, so no partial (expanding) window is ever used.
- `--entry-delay` shifts the entry that many bars after the signal bar, and
  applies to both strategies. It defaults to 1 because a signal computed from
  the `Avg` of a bar is only known once that bar has closed, so filling at that
  same `Avg` is not executable. It matters enormously for `--bands` and hardly
  at all here - see the band section for why, and for how to spot the difference.
- A crossover is a sign change of `sma(A1) - sma(A2)` between two neighbour
  bars. Bars where the difference is exactly zero are skipped and do not reset
  the sign. Compared with integers (`sum1 * A2` vs `sum2 * A1`), so there is no
  floating point noise near zero.
- Entry price = `Avg` of the crossing bar. Barriers are checked from the next
  bar on, so the crossing bar itself never closes the trade.
- A trade closes on the first bar whose `Max` reaches the upper barrier or whose
  `Min` reaches the lower barrier. If one bar touches both, it counts as a loss
  (we cannot tell the order inside a minute). Same rule as `src/Fx.Backtest`.
- The next entry needs a crossover strictly after the bar where the later of the
  two trades closed.
- If a trade cannot close before the end of history, the entry is dropped and
  the run stops there.
- `--spread <pips>` charges a fixed cost per trade, so an entry pays it twice.
  Default 0, which matches the plain strategy definition.

## Parameter grid

Periods: from `--min-period` (default 30) to `--max-period` (default 14400,
two trading weeks) with a growing step:

| range      | step   |
|------------|--------|
| up to 48 h | 30 min |
| above that | 4 h    |

That gives 144 periods, so 144 * 143 / 2 = 10,296 pairs. Stops: 20..100 step 5
for both SL and TP = 289 combinations. The lower bound matters: the lot is
`risk / StopLossPips`, so without it the optimiser always runs to the smallest
stop in the grid, where a single M1 candle can cover the whole stop and the
backtest cannot tell which barrier was hit first.

Risk per trade uses the same growing-step idea, from `--risk-min` (default
0.05%) to `--risk-max` (default 0.5%):

| range      | step  |
|------------|-------|
| up to 0.5% | 0.05% |
| up to 1%   | 0.1%  |
| up to 3%   | 0.25% |
| up to 5%   | 0.5%  |
| above      | 1%    |

With the default cap that is 10 values. Raising `--risk-max` widens the grid
with coarser steps.

## Trade count filters

A pair of very slow, very close averages crosses only a few hundred times in 15
years, so its "best" parameters rest on a handful of trades. Two filters cut
that off:

- `--min-crossovers` (default 2000): before the sweep the tool counts crossovers
  for every MA pair in one parallel pass, prints the distribution, and drops the
  pairs below the threshold. Skipping a pair here removes all of its stop and
  size combinations at once, so this is both a quality filter and a speed win.
- `--min-entries` (default 1000): applied per result, after the trade sequence
  is built. Entries are always fewer than crossovers, and the gap grows with the
  stops, because a wide SL/TP keeps the position open across many crossovers.

On the full EURUSD history the crossover counts run from 423 to 108,197 with a
median of 1,983, so the default keeps 5,139 of 10,296 pairs and leaves
14,851,710 combinations.

Note what the cap does and does not do. It caps the loss of one stop, not the
size of the position: the lot is `risk / StopLossPips`, so at `SL = 10` a 0.5%
risk still means a huge lot and a 190 pip winner pays +9.5% of equity in a
single trade. If you want to bound the position itself, raise `--sl-min`.

## How it stays fast

A naive implementation would rescan the whole history for every combination:
572,400 * 5.7M bars. Two precomputations remove almost all of that work.

**Prefix sums for the averages.** One `long[n + 1]` of cumulated `Avg`. Any
window sum is one subtraction, so finding all crossovers of one pair is a single
`O(n)` pass with two integer multiplications per bar - no division, no
materialised MA arrays. Done once per pair, then reused by all 400 stop
combinations.

**First-hit barrier tables.** For every distance `d` in the stop grid we
precompute, for every bar `i`:

    Up[d][i]   = first j > i with Max[j] >= Avg[i] + d
    Down[d][i] = first j > i with Min[j] <= Avg[i] - d

With these, one entry costs four array reads instead of a forward scan: the BUY
uses `Up[TP]` / `Down[SL]`, the SELL uses `Down[TP]` / `Up[SL]`, the winner is
whichever index is smaller, and the bar where both trades are closed is the
maximum of the two.

The tables are built with a monotonic stack walked from the end of the history
backwards. At bar `i` the stack holds the running maxima of `Max` over
`[i + 1, n)`, which is exactly the set of bars that can ever be a first hit;
its values are sorted, so each lookup is a binary search. Because the 20
thresholds of one bar are increasing, the answer position only moves down, and
each next search reuses the previous result as its upper bound.

Cost: `20 distances x 2 directions x n x log(stack)`, about half a second on 22
threads. Memory: `distances x 2 x n x 4` bytes, ~0.85 GB for 20 distances and
5.7M minutes. `--sl-step` / `--tp-step` / `--from` shrink it if needed.

**Trade sequence separated from money.** Which entries happen, and whether each
leg takes TP or SL, does not depend on the position size at all - size only
changes the money curve. So one pass over the crossovers builds a compact
sequence of two `short` values per entry (result of the leg that closed first,
and the net of both, in tenths of a pip), and then each of the 27 risk values is
just a sequential walk over that small array. The expensive part - four random
reads into the barrier tables per entry - is paid once per (pair, SL, TP)
instead of once per (pair, SL, TP, risk). Adding the whole size dimension costs
about 10% more wall clock.

Runs that breach the drawdown limit stop right there, which makes the losing
half of the grid cheaper than the surviving half.

**Loop order.** Outer loop over MA pairs (work-stealing over threads through one
atomic counter, because fast pairs produce far more crossovers than slow ones),
then TP, then SL, then risk. The crossover list of the pair is computed once and
reused 400 times.

Only the best surviving risk of each (pair, SL, TP) is offered to the top list.
Otherwise the top would fill up with the same parameters repeated at neighbour
sizes.

Full EURUSD history (5.7M minutes, 2011..2026), 22 threads with the defaults:
~0.5 s to build the tables, ~10 s to count crossovers, ~27 s for the sweep,
14.9M combinations, 2.4 billion simulated entries. Widening the grids scales
almost linearly in the number of trade sequences and barely at all in the number
of sizes.

`--bench <repeats>` times one combination on a single thread. For A1=660,
A2=1320, SL=20, TP=40 - 4,784 crossovers and 3,202 entries over 5.7M bars:

| stage                                       | per run | per entry |
|---------------------------------------------|---------|-----------|
| find crossovers (prefix sums, whole history) | 9.32 ms | -         |
| build the trade sequence (barrier tables)    | 0.09 ms | 30 ns     |
| the same by forward scanning (naive)         | 5.58 ms | 1744 ns   |
| money walk, one risk value                   | 0.01 ms | 2 ns      |

The barrier tables are worth 59x on the stage that dominates, the crossover pass
is paid once per pair and reused by all 289 stop combinations, and the money
walk is 12x cheaper than the sequence, which is why the whole risk dimension is
nearly free.

Those single-thread numbers are a best case: with 22 threads competing for a
776 MB table the real cost is closer to 200 ns per entry, so the sweep averages
about 40 microseconds of core time per combination, or 1.8 microseconds of wall
time. Keeping the same loop structure but scanning forward instead of using the
tables, and re-running the scan for every risk value, would take roughly two
hours instead of 27 seconds.

## Self-check

`--verify <n>` cross-checks the fast path against an independent naive
implementation before sweeping, and `--verify-only` stops right after it:

- `n` random `(bar, distance)` lookups against a plain forward scan
- 5 random full runs: crossover indices against a rolling-sum scan, and the
  whole run - entries, pips, wins, final equity, max drawdown and whether the
  drawdown limit was breached - against a scan-based simulation that keeps its
  own money loop

## Output

- console: top 10 (`--top`), sorted by final equity, then a per-year breakdown
  of the best combination - equity at the start and end of the year, return,
  max drawdown, pips, entries, win rate
- `reports/<symbol>-ma-cross-top.csv`: top 500 (`--top-keep`) with risk, final
  equity, return, max drawdown, entries, pips, win rate and the three outcome
  counts
- `reports/<symbol>-ma-cross-best-trades.csv`: every trade of the best
  combination - entry/exit time, minutes held, entry price, BUY and SELL result,
  pips, running pips, equity after the first leg and after the entry
- `reports/<symbol>-ma-cross-equity.html`: standalone report for the best
  combination - summary cards, equity curve, drawdown-from-peak panel and a
  per-year breakdown. Plain inline SVG, no scripts and no external files, same
  style as the `Fx.SimilarWeeks` report. The X axis is real calendar time, so
  flat stretches are periods without entries. The grey dashed line is the moving
  fail level (high water mark minus `--max-dd`), the red dashed vertical marks
  the bottom of the deepest drawdown. The curve has two points per entry, same
  as the sweep, so the reported drawdown matches the sweep exactly. Points are
  thinned per pixel column keeping the min and max of each column, so the curve
  stays exact at screen resolution even for combinations with a lot of trades.
  The same per-year table as the console one is at the bottom.

`--deals <name>` writes the winner's trades as a FXViewer deals file, so the
run can be opened on the chart with the Deals indicator and compared against
real trades. Band mode only. Format and field meanings: docs/deals.md.

With `--per-year` the last two files are not written and the console table
changes: one row per calendar year with that year's own winner. The top rows of
every year go to `reports/<symbol>-bands-per-year.csv`.

In the per-year table the drawdown is measured against the running high water
mark - the same one the fail rule uses - so it does not reset in January and the
largest value in the column equals the max drawdown of the whole run. Under
`--per-year` each year is a separate run, so there the drawdown does reset.

A year can show a negative return with zero pips. That is not a rounding bug:
position size follows equity, so the year is a product of multipliers, and
+1% followed by -1% ends at 0.9999. Flat in pips means slightly down in money.

## The control that undoes the rest: `--always`

`--always` throws the averages away. It enters on the first bar, and re-enters
on the bar after both trades close, forever. Everything else - barriers,
position sizing, the drawdown rule, the reports - is unchanged, so it is a clean
control for the question "does the crossover do anything at all?".

For a driftless random walk the answer is known in advance. With TP 40 and SL 20
each leg wins one third of the time, so `2/3 * 20 - 1/3 * 40 = 0`: blind entry
is exactly break-even, and the break-even win rate is `SL / (SL + TP)` = 33.33%.
Any result above that is the market being more trending than a random walk, and
that is the only thing this strategy can be selling.

Measured on the full EURUSD history at SL 20 / TP 40, no spread:

| entry rule            | entries | pips   | win rate |
|-----------------------|---------|--------|----------|
| no averages at all    | 11,249  | -7,460 | 32.8%    |
| MA 30 / 60            | 8,559   | 0      | 33.3%    |
| MA 120 / 480          | 5,124   | -1,020 | 33.2%    |
| MA 240 / 1440         | 3,330   | +1,800 | 33.8%    |
| MA 300 / 900          | 4,005   | -1,260 | 33.1%    |
| MA 1440 / 4320        | 1,427   | +1,000 | 33.9%    |
| MA 2880 / 7200        | 844     | -40    | 33.3%    |
| MA 5760 / 10080       | 606     | -360   | 32.8%    |
| MA 660 / 1320 (fitted)| 3,202   | +5,540 | 34.8%    |

Seven MA pairs picked without looking at the results average 33.34% - the
break-even value to two decimal places. Blind entry is 32.8%, within noise of
the same thing. Only the pair the optimiser selected stands out, at 34.8%.

That is +1.46 points over a typical pair, or about 2.5 sigma on 6,404 trades -
less than the best of a few hundred independent draws would produce by chance,
and the sweep chose it from 2.98 million trade sequences. So the honest reading
is that the crossover contributes nothing, and the leader's advantage is
selection.

Spread finishes it. The cost is charged on both legs of every entry, so at one
pip the leader pays 6,404 pips against 5,540 of gross profit:

| variant                  | pips at 0 | at 0.5 pip | at 1.0 pip |
|--------------------------|-----------|------------|------------|
| MA 660 / 1320            | +5,540    | +2,338     | -864       |
| no averages at all       | -7,460    | -          | -29,958    |

Run `--always` before trusting any result from this tool. If a combination
cannot beat blind entry by more than the spread, it has not found anything.

## Band strategy (`--bands`)

A different entry rule on the same machinery: one SMA with bands at +-N pips,
one trade at a time. Grids: MA period 1 hour to 14 trading days on the same
growing step, band width 0..50 step 5, SL and TP 20..100 step 5, risk as usual.
Four variants of what to do when the price crosses a band:

| variant | crossing                     | side | idea                        |
|---------|------------------------------|------|-----------------------------|
| A       | lower band upward            | SELL | fade the return into the channel |
|         | upper band downward          | BUY  |                             |
| B       | upper band upward            | BUY  | follow the breakout         |
|         | lower band downward          | SELL |                             |
| C       | upper band upward            | SELL | fade the breakout           |
|         | lower band downward          | BUY  |                             |
| D       | lower band upward            | BUY  | follow the return into the channel |
|         | upper band downward          | SELL |                             |

A and D are opposite sides of the same events, B and C likewise. At `N = 0` the
bands coincide, so A becomes C and B becomes D.

### How the entry is modelled

The entry is a pending order sitting at the band level. The band for bar `i` is
built from the SMA of the bars up to `i - 1`, so the level is known before the
bar opens and the order can really be there. It fills when the bar reaches it -
`Hi >= level` for a touch from below, `Lo <= level` for a touch from above - and
the fill price is that level exactly, not the bar's average.

One extra condition is what makes the fill price honest: the previous bar must
lie entirely on the far side of the level, `Hi[i-1] < level` for a buy stop and
`Lo[i-1] > level` for a sell stop. Without it the band, which moves with the
average, can simply descend onto a standing price; the code would then record a
buy stop filled at a level that is already below the market, when a real order
there would have filled at market and worse. That is free money handed to
whichever variant trades with the direction of travel, and it produced the same
false winners as filling at `Avg` did.

This matters more than it looks. An earlier version detected the crossing on
`Avg` and also filled at `Avg`, which is not executable: the average of a minute
is only known once the minute is over, and by then the price has moved on. It
handed the backtest about a pip on every trade. Filling at the band level has no
such problem, because the price is fixed in advance rather than discovered from
the bar, so `--entry-delay` is not needed here and is not used in this mode.

Because the fill is a real level rather than a bar statistic, barriers are
measured from the band level too, and the pre-built barrier tables of the
crossover strategy do not apply. Instead, per (period, band) the tool finds the
touch events and then, for each event, walks forward once per direction to
record the first hit of all 17 stop distances at the same time - the answers are
monotone in the distance, so one scan produces the whole row. A block index over
`Hi` and `Lo` (256 bars per block) lets the scan skip regions that cannot reach
the next level. All variants, distances, stops and sizes then read from that
compact per-unit matrix.

### Distance the price has to travel

`--dist-min/-max/-step` adds the requirement that the price arrived at the band
from far enough away. For a touch from below at level `L` with distance `D`:
take the last bar before the entry where the price was above `L`, and require
that between that bar and the entry the price fell to `L - D` or lower. Mirrored
for a touch from above. `D = 0` is no requirement at all, so the grid contains
its own baseline.

In other words the entry must sit at the top of a clean rise of at least `D`
pips - clean meaning the price never went above the entry level during it. The
same backward scan is accelerated by the same block index and stops early once
`D` is reached, so the cost does not grow with the distance.

233,592,920 combinations.

### Why `--entry-delay` exists and defaults to 1

Entering on the signal bar itself produced the best-looking result this tool has
ever printed: variant D, 1 hour MA, 5 pip band, SL 20, TP 20, 22,720 entries,
52.2% win rate on a symmetric barrier, 100,000 turning into 10.7 million.

It is an artifact. The signal is "`Avg` moved up through the band", and the entry
is at that same `Avg`. You cannot buy at a minute's average price after using
that average to decide - by the time the bar is closed and the average is known,
the price has moved on. The backtest was systematically buying below the market.

Moving the entry one bar later inverts the whole ranking:

| entry     | best variant | best result | A       | B       | C       | D       |
|-----------|--------------|-------------|---------|---------|---------|---------|
| signal bar| D            | +10,634%    | absent  | +4,074% | absent  | +10,634%|
| next bar  | C            | +2,074%     | +418%   | +288%   | +2,074% | +347%   |

At delay 0 the two variants that follow the crossing direction win and the two
that fade it do not even reach the kept top 500; at delay 1 it is exactly the
other way round. A one minute shift cannot flip a real market effect. It can
only flip an execution artifact, which is what this is.

The crossover strategy was re-checked the same way and barely moves, which is
the useful contrast. Its signal is the relation between two averages, not where
the price sits right now, so the entry price carries almost none of the signal:

| variant                    | delay 0        | delay 1        |
|----------------------------|----------------|----------------|
| MA 660/1320 SL 20 TP 40    | +5,540 pips, 34.8% | +5,980 pips, 34.9% |
| no averages at all         | -7,460 pips, 32.8% | -7,580 pips, 32.8% |
| best of the whole sweep    | +395%          | +334%          |
| band variant D, 1h, band 5 | +10,634%       | +347%          |

A result that survives the delay may still be selection bias, as the crossover
one turned out to be. A result that does not survive it was never real at all.

### What survives

Nothing worth trading. The remaining edge decays with the delay and dies on the
spread:

| entry delay | spread | best result |
|-------------|--------|-------------|
| 0           | 0      | +10,634%    |
| 1           | 0      | +2,074%     |
| 2           | 0      | +1,665%     |
| 5           | 0      | +705%       |
| 1           | 0.5    | +380%       |
| 1           | 1.0    | +232%       |

The delay-1 winner is variant C at SL 20 / TP 20 with a 51.6% win rate. On a
symmetric barrier the break-even is 50%, but with a one pip spread it becomes
`21 / 40` = 52.5%, so 51.6% loses. That is the whole story: there is a small
short-horizon mean reversion in the averaged price series, it is smaller than the
spread, and it fades within a few minutes.

## One winner per year (`--per-year`)

The default sweep looks for one combination that works over the whole history.
That is not the only question worth asking. A combination has a lifetime: it
starts working at some point and stops working at another. `--per-year` is the
first step towards measuring that - it picks the best combination for every
calendar year separately.

How the window works:

- The history is still loaded whole. Only the **entry window** is cut, so the
  moving average is already warm on 1 January and does not lose the first days
  of the year to warm-up.
- A trade that opens in December may close in January. It belongs to the year it
  was opened in.
- Every year starts again from the full deposit with a fresh high-water mark, so
  the drawdown rule is applied inside the year.
- The "one trade at a time" chain restarts at the year boundary. Against a
  single run over the whole history this shifts at most one entry per year.

The cost is almost free. Each window walks only its own events, so the total
sequence work over 16 years is about the same as one pass over the whole
history. 16 windows raise the combination count from 446 million to 7.1 billion
but the run time by only a few percent.

`--min-entries` changes meaning here: it is read as "entries per full year" and
scaled by the length of each window, so a partial first or last year is judged
by the same entries-per-month bar. The default is 200.

What comes out is a table of 16 winners, not one winner. Read it as a map of
what each year rewarded, not as a strategy - picking the year's winner needs
that year to be over already. The chained number printed under the table is
there for scale only; it is not reachable.

## Longest win chain (`--chain`)

A different question: not "which pair earns the most" but "which pair once
produced the longest streak of winning entries". SL and TP are fixed at
`--sl-min` and `--tp-min`.

First every minute of the history gets a label from the barrier tables. Entry
is the bar's average price; the first touch of a barrier decides the outcome,
a bar touching both barriers counts as a loss:

- **some side wins** - a buy reaches +TP before -SL, or a sell reaches -TP
  before +SL. The two cannot both happen, so the label also knows which side.
- **both sides lose** - price hits the SL barrier on each side first.
- **still open** - the data ends before the outcome is known (only the last
  hours of the history).

Then every MA pair is scanned. Its crossover entry bars (signal bar plus
`--entry-delay`) pick labels out of that per-minute array, and the longest
chain in that label sequence is found. Chain rule: wins extend the chain; a
single loss stays inside it only when at least 2 straight wins come right
before it and at least 2 straight wins right after. Two losses in a row, or a
loss without that support, ends the chain. Entries are labels, not simulated
positions: overlapping trades are allowed and no equity is tracked.

    Fx.MaCross --chain --sl-min 15 --tp-min 45

`--strict` switches the forgiveness off: every loss ends the chain, so the
chain is simply the longest run of straight wins. The CSV then goes to
`<symbol>-chain-strict-top.csv`.

`--one-trade` deduplicates the decisions: while a trade is open, crossovers
are skipped, and the next entry is allowed only after the exit bar. The exit
is when the whole decision is resolved: the winning side's TP bar on a win,
the later of the two SL bars on a both-lose. Without this rule neighbouring
crossings enter at almost the same price and share the outcome, so one move
is counted as a whole block of wins. With it the outcomes use disjoint parts
of the price path and the run-length distribution of a random walk becomes
the correct yardstick. The CSV gets a `-seq` suffix.

Output: top pairs ranked by chain length (trades in the chain, wins inside,
losses forgiven, the chain's dates, the pair's total crossings and win rate),
plus `<symbol>-chain-top.csv` with the kept rows.

Read the ranking with care: fast neighbouring pairs (0.5h/1h) cross every few
minutes, and close crossings share almost the same entry price, so one 45-pip
move stamps a whole block of them as wins at once. A long chain there is one
good week counted many times, not many independent wins. The crossings column
tells how often a pair fires; the share column shows how small the chain is
against the pair's whole history.

## Comeback (`--comeback`)

No moving averages at all. An anchor sits at a bar's average price. The setup
completes when the price touches both anchor+X and anchor-X (either order)
while never leaving anchor+-Y, with X < Y. When a later bar then comes back
through the anchor price, a BUY and a SELL open there at once, each with the
same SL and TP measured from the anchor. When both legs close, the exit bar
becomes the next anchor and the search starts again.

    Fx.MaCross --comeback --from 2015 --x-min 10 --x-max 100 --x-step 5 \
        --y-min 10 --y-max 100 --y-step 5 --sl-min 10 --sl-max 100 --sl-step 5 \
        --tp-min 10 --tp-max 100 --tp-step 5 --spread 0.7

Conventions on one-minute bars, where the order inside a bar is unknown:

- a touch is `high >= anchor+X` or `low <= anchor-X`; leaving the band is
  `high > anchor+Y` or `low < anchor-Y` (touching exactly Y is still inside);
- a bar that leaves the band moves the anchor to that bar's average price and
  clears both touches; its own high/low are not tested against the new levels;
- the entry bar must contain the anchor price and both touches must already
  be set on EARLIER bars; a bar that completes the second touch and comes
  back to the anchor in the same minute does not enter;
- when the entry bar both contains the anchor and leaves the band, the entry
  wins; leg barriers are scanned from the bar after the entry bar.

`--x-*` and `--y-*` set the X and Y grids; only pairs with X < Y run. SL and
TP use the usual `--sl-*`/`--tp-*` grids. Default `--min-entries` is 300.
Output: `<symbol>-comeback-top.csv`, columns as in the reversal top plus the
Y column. With TP > SL a double win is impossible, so the entry outcomes are
one leg wins (+TP-SL) or both lose (-2*SL), spread charged per leg.

## Swing (`--swing`)

No moving averages. An anchor sits at a bar's average price. The price must
run at least X but at most Y pips to one side of the anchor; the largest
excursion so far is P. The entry waits at the mirror level: anchor-P after a
move up, anchor+P after a move down (the "turn and run 2*P from the extreme"
level). The first later bar that touches it enters ONE trade at that exact
price, in the direction of the turn, SL and TP measured from the entry level.

    Fx.MaCross --swing --from 2015 --x-min 10 --x-max 100 --x-step 5 \
        --y-min 10 --y-max 100 --y-step 5 --sl-min 10 --sl-max 100 --sl-step 5 \
        --tp-min 10 --tp-max 100 --tp-step 5 --spread 0.7

Bar conventions, same spirit as `--comeback`: extremes for the trigger come
from EARLIER bars only; the mirror level moves while the extreme grows; a bar
beyond anchor+-Y re-anchors at that bar and clears both extremes; a bar that
would trigger both sides at once re-anchors instead; leg barriers are scanned
from the bar after the entry bar; the exit bar becomes the next anchor.

The report counts direct wins (the taken trade reaches TP), mirror wins (the
taken trade lost but the OPPOSITE trade at the same level would have won) and
fails (both lose). The console prints the winner's by-year table. Output:
`<symbol>-swing-top.csv`.

## Reading the results

The winners sit right on the cliff. The optimiser keeps the largest size that
survives, so almost every top row lands at 22-25% drawdown - one more bad trade
and it would have been thrown away. Those parameters are fitted to the exact
sequence of losses in this history, and nothing says the next one is as kind.
Treat the risk column as an upper bound that already failed to leave a margin,
not as a recommendation. `--max-dd 15` gives a more honest picture.

The optimiser always runs to the smallest stop the grid allows, because the lot
is `risk / StopLossPips` and a smaller stop buys a bigger position for the same
risk. So `--sl-min` is the real handle on position size, not `--risk-max`, and
the results are only as trustworthy as that lower bound. Below ~20 pips a stop
is inside the range of a single M1 candle, where the "both barriers in one bar
counts as a loss" rule is a guess rather than a measurement.

Two more things to check before trusting a row:

- **Spread.** It is charged twice per entry, and a tight stop feels it most.
  Always rerun the winners with `--spread`.
- **Where the drawdown sits.** A row at 24% under a 25% limit survived by luck
  and would be thrown away if two losses had swapped places. A row well below
  the limit is the more honest find - sort the CSV by
  `returnPercent / maxDrawdownPercent` to see them.
- **The neighbourhood.** This is the cheapest and most useful check, and the
  CSV already contains what is needed. Filter the top rows to a fixed SL and TP
  and look at what the neighbouring periods give. A real effect is a ridge: one
  step of the grid should barely move the result. A single point standing 40%
  above everything one step away is noise that got selected, and refining the
  grid produces more of those, not fewer, because it enlarges the pool the
  maximum is drawn from. On the default grid 2,975,544 trade sequences compete,
  so the winner is the maximum of about three million noisy estimates.

## Picking only one leg (tested, no edge found)

The tool prints a `LEG CHOICE` table for the winning combination: what would
happen if each entry opened only one trade instead of two, and the direction was
picked by some rule.

Only the entries where exactly one leg wins can be affected - on the rest both
legs lose and the choice changes nothing. For the current leader that is 2,227
of 3,202 entries, and the rule has to beat 52.07% to match simply taking both
legs. Below that, one leg is worse; the coin-flip baseline of 50% gives exactly
half the pips of two legs.

Three families were tested, six parameter values each, plus the direction of the
crossover itself, split 2011-2019 / 2020-2026:

- direction of the crossover (fast MA above slow) - **49.89%**
- price above / below SMA(T), T from 1 day to 4 weeks - 48.41% to 51.10%
- sign of the price change over the last k minutes, k from 30m to 1 week -
  49.21% to 52.27%
- position of the entry price inside the high/low range of the last N bars -
  48.77% to 50.25%

The best of the 19 was momentum over 1 day at 52.27%, which is 2.1 sigma from a
coin flip. Testing 19 rules two-sided, the chance of seeing something that good
by pure luck is around one in two, so it is not evidence of anything.

### Third moving average

A fourth family was tested separately: a third SMA whose position decides the
direction. Grid from 30 minutes to 30 trading days - 30 min steps to 48 h, 4 h
steps to 10 trading days, 1 day steps above that, 164 values - against two
reference levels, and both polarities. `--pin a1,a2,sl,tp,risk` skips the sweep
and runs this analysis for one chosen combination; the full curve goes to
`reports/<symbol>-ma-cross-third-ma.csv`.

Against the entry price the third MA is useless: 48.3% to 52.0%, best money
result below the both-legs baseline.

Against the level where MA1 and MA2 cross it does better, and this is the only
signal found so far that behaves like a real one rather than a lucky draw. The
rule "MA3 above the crossing level -> BUY" - which means buying when price sits
below its own two to three week average, so a mean reversion rule - peaks at
52.72% for an 18 day MA3. The reversed polarity is the losing side at 47.3%.
What makes it credible is the shape: the hit rate curve rises smoothly from
about 50% at short periods to a broad plateau over 14 to 21 days and declines
slowly after, and the peak holds in both halves of the data (53.10% up to 2019,
52.12% from 2020). It is not a single spike between two bad neighbours.

It still does not pay. At the same 0.5% risk the 18 day rule turns 100,000 into
455,513 against 369,943 for both legs, but the drawdown grows from 12.4% to
22.7%. Matched on drawdown instead of on risk, the single leg is clearly behind:

| variant                     | risk  | final   | max drawdown |
|-----------------------------|-------|---------|--------------|
| both legs                   | 0.50% | 369,943 | 12.4%        |
| single leg, MA3 17d         | 0.35% | 291,083 | 13.1%        |
| single leg, MA3 17d         | 0.30% | 251,135 | 11.3%        |
| single leg, MA3 18d         | 0.25% | 217,935 | 11.9%        |

Return per unit of drawdown is about 22 for both legs and about 15 for every
single-leg variant at every risk level. A 2.7 point edge raises the average but
does not pay for replacing a certain +20 pips with a coin flip between +40 and
-20, and the extra variance costs more than the edge is worth under compounding.

### Filtering entries instead of choosing a leg (tested, no edge found)

If the direction cannot be predicted, maybe the bad entries can be. 975 of the
3,202 entries end with both stops hit and cost 40 pips each; skipping them would
be worth more than any direction rule. `EntryFilter` sorts the entries by a
predictor, splits them into deciles and prints the share of double-stop outcomes
in each, then the money result of keeping only the deciles above or below each
cut. The decile table is the honest view: a real predictor makes the share fall
steadily across the buckets, one odd bucket is noise.

Five predictors were tried - the size of the third MA's deviation from the
crossing level, mean bar range over 1 hour and over 1 day, and the high-low
range over 1 day and over 1 week. Every one of them is flat. The double-stop
share stays between 26% and 36% in every decile of every predictor, around a
30.4% baseline, with no trend; the spread matches the +-2.6 point standard error
of a 320-entry bucket.

No filter beat the baseline, and the way they fail is informative. Dropping the
worst decile leaves 2,882 entries and lands between 284,000 and 342,000 against
369,943 unfiltered. Removing 10% of entries *at random* would give
`100,000 * exp(0.9 * ln 3.699) = 324,600`, which is where the filters actually
land. They are removing entries without removing any risk.

This one is predictable from theory. For a driftless random walk the chance of
hitting the far barrier before the near opposite one depends only on the ratio
of the two distances, 40 to 20 here, and not on volatility at all. Every
predictor tried is a volatility measure in some form, so none of them can move
the double-stop rate. The `pips/entry` column carries no extra information
either - it is exactly `20 - 60 * share`.

A filter can only pay if it predicts the deviation from random-walk behaviour,
that is whether the next few hours will trend or chop, and none of these do.

The reason is structural, and it is worth understanding before trying more
rules. For a driftless random walk with a take profit of 40 and a stop of 20,
each leg wins one third of the time and both lose one third. This strategy gets
a winner on 69.55% of entries instead of 66.67% - that 2.9 point gap is the
whole profit. But the split between BUY and SELL wins is 1,107 to 1,120, dead
even. The edge is "the far barrier is reached before the near opposite one more
often than chance", which is a statement about trendiness, not about direction.
Choosing one leg converts a non-directional edge into a directional bet and
throws away the part that works.

## Not in v1

- No margin, no leverage cap and no minimum lot step: the position is whatever
  size the risk formula asks for, and it can be arbitrarily small or large.
- Entry at the `Avg` of a bar, not at an open or a limit fill, and no slippage.
  `--spread` is only a flat cost, it does not move the barriers.
- Floating drawdown inside an open position is not measured, only the two
  realised points of each entry.
- The sweep optimises in-sample over the whole history. Use `--from` / `--to` to
  split the data and check the winners out of sample.
