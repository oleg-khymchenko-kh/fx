# USD index v2: consensus over intervals

Status: implemented 2026-10-17 as the index Method `Consensus` next to
Median and Average (see "In FXViewer" below). The algorithm was designed
and checked first in a Python prototype (src/usd-index-consensus); the
C# kernel lives in src/Fx.Consensus and reproduces the prototype to
0.00002 pips. The v1 index (per-minute median, docs/usd-index.md) stays
as it is. A fourth Method, `Consensus 2`, adds the EUR/GBP priority and
the return intervals of the EUR/GBP spread; see "Consensus 2" below.
Since 2026-10-20 both methods are computed causally, minute by minute
without looking ahead, so a value never changes once computed; see
"Causal algorithm" below. The sections between describe the first,
batch version (least squares over intervals), which looked ahead and is
no longer used by the app.

## Why v1 is not enough

v1 takes the six pair steps of one minute, picks the median and adds
the medians up. That is a consensus at one scale only: one minute. Over
weeks the small per-minute errors add up like a random walk, and the
index moves away from what the pairs show over the long interval.

Real example, moves in the "USD up" direction (USDCHF and USDCAD as
they are, the other four mirrored), pips, from 2026-08-21 09:10 to
2026-10-01 15:54:

    EURUSD +489   GBPUSD +489   USDCAD +513
    NZDUSD +387   USDCHF +323   AUDUSD +259

Three pairs agree at about +495. By the rule "an equal move of several
pairs is the dollar's move", the dollar went up about 490-500 pips over
this interval and EUR, GBP and CAD stood roughly still. The v1 median
index shows +417 over the same interval, the plain average +440.

## The rule

Orientation: for every pair take the move `s_i` in the USD up direction
(USDCHF, USDCAD, USDJPY as they are, EURUSD, GBPUSD, AUDUSD, NZDUSD with
the sign flipped). Then for any interval

    s_i = (USD index move) - (own move of currency i)

The split is not defined by the data alone; some rule has to fix it.
v1 and the classic indexes use "the own moves average out to zero".
v2 uses the user's rule:

- If several pairs show the same `s` over an interval, that common
  value is the USD move over the interval, and those currencies did
  not move. The pairs that show something else moved on their own and
  do not influence the USD move.
- The more pairs agree, the stronger the evidence. Pairs also have an
  importance: EURUSD, GBPUSD, USDCHF, USDCAD, NZDUSD, AUDUSD, in this
  order.
- The rule must hold for intervals of every length at once, from one
  minute to several months, not only for consecutive minutes.

In real data the rule contradicts itself: the pairs that agree over six
weeks do not agree over each of the weeks inside, and the sum of the
weekly consensus values is not the six week consensus. So no index can
satisfy the rule exactly for all intervals. v2 looks for the index that
satisfies it best in the least squares sense, with the strength of each
interval's consensus as the weight.

## Algorithm

Two stages. Stage A looks at the pairs only and finds, for many
intervals, the value the pairs agree on and how strong the agreement
is. Stage B builds one index curve whose moves over those intervals
match the agreed values as well as possible. Stage A never looks at the
index, so the index cannot "explain away" its own mistakes (an earlier
variant that re-weighted the pairs around the current index produced
single-minute spikes of 150 pips; that path is closed).

### Grid

The common minute grid: minutes where every selected pair has a filled
M1 candle, as in v1. Time below means grid index (trading minutes), so
weekends do not exist; one trading day is 1440 grid minutes, one week
about 7200.

### Stage A: the frame of intervals

Scales `s` in grid minutes: 1, 2, 4, 8, ... doubling while below one
day (1440), so up to 2048; from there the factor is 1.4142 (about
sqrt(2), rounded to whole minutes): 2896, 4096, 5793, 8192, ... up to
65536 (about 45 trading days, 9 calendar weeks). 22 scales in all. The
cap follows the user's rule that the moves of the last ten weeks are
what matters: intervals of half a year or a year are not checked at
all, so they cannot pull the recent part of the curve (the prototype
ran with 131072 first; see "Recency").

At each scale the intervals are `[a, a + s]` with the start `a` moving
in steps of `s / 4` below one day and `s / 16` from one day up (step at
least 1 minute). So every minute lies inside many intervals of every
scale, and any user-chosen interval has a frame interval of about its
length starting within a few percent of its length from it.

Endpoint averaging: from scale 256 up, the move of a pair over an
interval is measured between window averages, not single minutes:

    move = avg(x, b - h .. b + h) - avg(x, a - h .. a + h),   h = s / 256

One day: 5 minutes each side; one week: 40 minutes; six weeks: 2.5
hours; 18 weeks: 8.5 hours. Stage B measures the index over the same
windows. Without this the solver can satisfy a long interval by moving a
single minute at its end, and the long intervals turn into a staircase
of spikes.

### Stage A: tolerance, "the same distance"

Two pairs never move exactly the same. A pair counts as agreeing with
the candidate value `m` when it lies within

    c = max(floor(s), p * |m|)             p = 0.15

`p * |m|` is the user's picture: 489 and 513 are the same move, 489
and 323 are not. `floor(s)` handles small moves, where a percentage
means nothing: it is the typical spread of the pairs at that scale,

    floor(s, t) = max(0.5 pip, kappa * MAD(s, t))     kappa = 0.5

where MAD(s, t) is the median, over the intervals of scale `s` near
time `t`, of the median absolute deviation of the six moves from their
median. "Near" means: the intervals of each scale are cut into blocks
of ten weeks (72000 grid minutes divided by the start step, at least 8
intervals per block, the remainder joins the last block), each block
gets its own median, and an interval takes the value interpolated
between the two nearest block centres. So the floor follows how far
the pairs are from each other in that period of the market; a volatile
year five years ago does not widen "the same distance" for today, and
a quiet year does not narrow it. Measured on 2026 data: 1 minute 0.5,
1 hour 1.7, 1 day 7, 1 week 23, 6 weeks 35 pips (with kappa applied).

The membership weight of a pair is `k(z)`, `z = (s_i - m) / c`:

    k(z) = 1                      for |z| <= 1
    k(z) = exp(-8 (|z| - 1)^2)    beyond (0.14 at |z| = 1.5, 0 at 2)

A flat top with a steep edge, so a pair inside the tolerance counts in
full and a pair at twice the tolerance does not count at all.

### Stage A: clusters and the consensus of one interval

Importance weights `w_i`: EURUSD 6, GBPUSD 5, USDCHF 4, USDCAD 3,
NZDUSD 2, AUDUSD 1, divided by 21. With these numbers EURUSD plus
GBPUSD (11) outweigh USDCHF plus USDCAD plus NZDUSD (9), and a single
EURUSD (6) equals USDCAD plus NZDUSD plus AUDUSD (6).

For each interval, start a mean shift from each pair's value as a seed:

    m <- sum_i w_i k(z_i) s_i / sum_i w_i k(z_i)      12 rounds

with `c` recomputed from the current `m` every round. Each seed ends in
a mode `m_j` with the weight `W_j = sum_i w_i k((s_i - m_j) / c)`, the
total importance of the pairs that sit in that cluster. The consensus of
the interval is the heaviest mode:

    m_1 = the mode with the largest W,    W_1 its weight
    W_2 = the largest weight among the modes farther than c from m_1
    H   = max(0, W_1 - W_2)                            the strength

So the strength is the margin of the winning cluster over the runner
up. All six agree: H = 1. Five agree and AUDUSD is apart: H = 0.90.
Five agree and EURUSD is apart: H = 0.43. EURUSD plus GBPUSD against
USDCHF plus USDCAD against two singles: the consensus is the EUR/GBP
value with H = 0.19. Two clusters of equal weight: H = 0, the interval
says nothing and the index there is shaped by the other scales.

The output of stage A is one (m, H) per frame interval.

### Recency

The user's priority: the moves of the last ten weeks must come out
with the best precision, far intervals may be sacrificed for that, and
moves from years ago must not distort today's index. Two things follow.

Recency weight: each interval's strength is multiplied by a weight
that depends on the age of its end (distance from the last minute of
the solved range):

    age <= 72000 minutes (10 weeks)            weight 1
    age from 10 weeks to 10 weeks + 65536      weight falls linearly 1 -> 0.1
    older                                      weight 0.1

Where the "old" and the "recent" agreements contradict each other
(around ten weeks back), the full weight of the recent intervals wins
and the older part of the curve takes the compromise.

The window: the index is never solved over its whole history at once.
A least squares over everything couples every node with every other
one, and the prototype measured it: adding January to May 2026 to a
June to October range changed the moves of the last ten weeks by up to
16 pips (mean 4.5). So the solve is a window:

    solved     the last 203072 minutes (28 weeks): 9 weeks of support,
               9 weeks of transition, 10 recent weeks
    written    the last 137536 minutes (19 weeks)
    kept       everything older stays exactly as stored

The written part is shifted as a whole so that its first minute keeps
the stored value there (the curve stays continuous); the 9 weeks of
support before it only give the first written weeks a full set of
intervals on both sides and are thrown away. So the shape of the last
19 weeks depends on the pairs' moves of the last 28 weeks and on
nothing else; older data sets only the vertical level, which the chart
places anyway. A minute leaves the window 19 weeks after it happened
and is never touched again. Measured: the last 19 weeks computed from a
January to October grid and from a grid of exactly the last 28 weeks
are the same curve to floating point precision.

A first build from scratch (Generate, Compute derived, a new index) uses
the same window stepping through time: the first window covers the
first 28 weeks (the 9 weeks of history read before the start minute
serve as its support), every next window ends 10 weeks later and
rewrites the last 19 weeks before its end, matching the level at its
first written minute. The result is what the hourly rebuild would have
produced had it run all along, with 10 week jumps instead of 1 hour
slides.

### Stage B: the index

Unknowns: the index value `u(t)` for every grid minute, `u = 0` at the
start minute. Minimize

    sum over frame intervals e of  H_e * (ubar(b_e) - ubar(a_e) - m_e)^2

where `ubar` is the same window average as in stage A (plain values
below scale 256). This is weighted least squares on differences: the
normal equations are a graph Laplacian (minutes are nodes, intervals
are springs with stiffness H and rest length m). Solved by conjugate
gradients with a Jacobi (diagonal) preconditioner, warm started from
the v1 median index, relative tolerance 1e-10. Every interval gets
1e-6 added to its H so the graph stays connected. With intervals at
all scales the graph has a small diameter and CG converges in a few
hundred steps.

Why least squares and not the per-interval consensus directly: the
consensus values of overlapping intervals contradict each other, and
least squares spreads each contradiction over the interval in
proportion to the strengths. A long interval's correction is spread
over all its minutes (cost falls as 1 / length), so the long intervals
shape the slow part of the curve and the one-minute intervals keep the
fast part, with no scale weighting needed.

Everything after the index is unchanged from v1: storage as flat
candles at scale 100000, the Currency Index (`pair move = currency
move - USD move` in pips), the symbol bar, the chart.

## Units: pips or percent

The consensus is computed in the unit of the index (Algorithm setting
of v1, Pips or Percent). The unit decides what "the same distance"
means, and the two answers differ. The same example interval in
percent: EUR 4.26, GBP 3.64, CHF 3.96, CAD 3.67, AUD 3.68, NZD 6.69.
Here GBP, CAD and AUD agree at 3.66 and EUR is the odd one, because
GBPUSD at 1.36 and EURUSD at 1.17 turn equal pips into different
percents. AUDUSD and NZDUSD near 0.6-0.7 are the extreme case: their
pips are small in percent. The user works in pips and the chart grid
is in pips, so Pips is the default; Percent is the economically clean
variant, where a currency index is a product of returns.

## Prototype results

Prototype: src/usd-index-consensus (Python, numpy, scipy). Data:
2026-06-01 to 2026-10-17, 138,959 common minutes, 24 scales, 558,717
frame intervals. One run about two minutes (the CG solve in scipy).

Example interval 2026-08-21 09:10 to 2026-10-01 15:54:

    pairs       EUR +489  GBP +489  CAD +513  NZD +387  CHF +323  AUD +259
    mean        +440
    v1 median   +417
    consensus   +465

The remaining gap to 489 is real, not a defect. Week by week the
pairs do not confirm the end value: over the last three and a half days
(28 Sep to 1 Oct 15:54) EUR went +157 and GBP only +43 (CHF +14, CAD
+103, NZD +54, AUD +104), and in the final hour before 15:54 EUR made
+45 and GBP +26 while the other four made +2 to +20. The end point sits
on a spike of EUR and GBP that the other pairs did not follow, and the
equality 489 = 489 at that exact minute is partly a coincidence: at
15:00 the pairs read EUR +444 and GBP +463, at 17:00 EUR +490 and GBP
+489, at 20:00 EUR +468 and GBP +478. The index follows the whole
path, so it gives the spike minute +465 (v1: +417).

Splitting the example at 15 Sep 00:00:

    first half   pairs EUR +162 GBP +174 CHF +185 CAD +156 NZD +206 AUD +28
                 four pairs agree at about +169;  consensus +169, v1 +150
    second half  pairs EUR +327 GBP +315 CAD +357 CHF +138 NZD +182 AUD +231
                 three pairs agree at about +329; consensus +295, v1 +267

Behaviour per minute: standard deviation of a one-minute step 0.70
pips (v1 0.60, the pairs 0.7 to 1.0), no single-minute jumps above 6
pips while every pair moved 2 pips or less. The largest one-minute
moves of the index are news minutes (12:30 UTC US data, 18:00 FOMC)
where all six pairs moved 10 to 25 pips together.

The honest test of the rule: 20,000 random intervals with random
start and random length between one hour and six weeks, none aligned
to the frame. For each one the stage A consensus of the six pair moves
is computed, and the table shows the strength-weighted mean distance
between the index move and that consensus, in pips:

    length        1h-4h   4h-1d   1d-5d   5d-2w   2w-6w
    mean            1.7     4.1     9.3    18.5    29.0
    v1 median       2.4     5.8    14.5    27.4    36.7
    consensus       1.3     3.0     6.0    13.3    17.7

About 35 to 40 percent of random intervals have a clear consensus
(H > 0.3); in the rest the pairs do not agree enough, and no index can
be checked against them. The picture reports/usd-index-consensus.html
shows the three indexes over the six mirrored pairs from 10 Aug to 17
Oct, with the example interval marked.

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| Pairs, weights | EURUSD 6, GBPUSD 5, USDCHF 4, USDCAD 3, NZDUSD 2, AUDUSD 1 | importance, normalized to 1 |
| Max scale | 65536 min (about 9 weeks) | longest interval that is checked |
| Scale factor | 2 below one day, 1.4142 above | density of scales |
| Overlap | 4 below one day, 16 above | start step = scale / overlap |
| Endpoint window | scale / 256 each side, from scale 256 | averaging at the interval ends |
| p | 0.15 | relative tolerance, share of the move |
| kappa | 0.5 | floor tolerance, in units of the pairs' spread at the scale |
| Floor blocks | 10 weeks (72000 min), at least 8 intervals | time-local floor, interpolated |
| Kernel | flat to 1, exp(-8 (z - 1)^2) beyond | membership of a pair in a cluster |
| Strength | W_1 - W_2 | margin of the winning cluster over the runner up |
| Recency | 1 for ends in the last 10 weeks, then 1 -> 0.1 over 65536 min | priority of recent moves |
| Solved window | 203072 min (28 weeks) | the range one solve covers |
| Written window | 137536 min (19 weeks) | the part of a solve that is kept |
| Scale weight | 1 for every scale | no extra weighting by length |

The prototype runs variants of these from the command line
(`py run.py rel=0.1 weights=heavy`). Variants tried on the same data
(example interval, and the random interval error at 2 to 6 weeks, pips):

| Variant | Example | 2w-6w error | Note |
|---|---|---|---|
| default | +465 | 16.8 | |
| p = 0.10 | +465 | 17.6 | fewer intervals with a clear consensus (30%) |
| p = 0.20 | +470 | 15.6 | more intervals count as agreeing (41-50%) |
| weights 10/8/4/3/2/1 | +478 | 9.8 | follows EUR and GBP more; the error metric uses the same weights, so it is not comparable 1:1 |
| equal weights | +434 | 26.6 | barely better than v1 (34.0); the importance weights do most of the work |
| strength = W_1 (no margin) | +476 | 24.4 | ambiguous intervals count in full, so the error includes them |
| hard window kernel | +477 | 17.8 | like default, fewer clear intervals (26%) |
| no endpoint averaging | +468 | 17.2 | 8 single-minute jumps above 6 pips with quiet pairs appear |
| scales below 1 h weighted x4 | +466 | 16.8 | no effect |
| Percent unit | +410 (bp) | 16.3 | different clusters, see Units; v1 median gives +448 there |

There is a tension in the rule itself: "more pairs win" against "the
important pairs win". With 6/5/4/3/2/1, EURUSD plus GBPUSD (11) against
the other four together (10) is nearly a tie, so such an interval gets a
small strength and the other scales decide; with 10/8/4/3/2/1 EUR and
GBP win it clearly. Which one is wanted is a user choice, the weights
are a parameter.

## In FXViewer

Two algorithms run for a Consensus index, as the user asked for:

- Slow and exact, once an hour: the window of the last 28 weeks is
  solved again (stages A and B, CG to a relative residual of 1e-8) and
  the last 19 weeks are rewritten, everything older is kept. The hourly
  auto rebuild in `MainWindow.IndexAuto.cs` calls
  `DollarIndexSymbol.Compute` with the index name as `storedSymbol`;
  the compute reads the stored minutes before the window, checks that
  they cover the grid minute by minute, and returns them unchanged
  together with the new window (`ConsensusIndex.Continue`). If the
  stored index does not reach the window start (a young index, or the
  app was off for months) the whole index is built from scratch
  (`ConsensusIndex.Compute`, window stepping). `FlatSeriesSync` then
  diffs the result against the stored minutes and writes the changes:
  the 19 weeks move a little every hour, so the plan is the "replace
  whole" kind. `Generate` (dialog OK, Compute derived) and `Refresh`
  always build from scratch.
- Fast, every 10 seconds: `DollarIndexSymbol.Tail` is unchanged. It
  resumes from the last stored index value and appends one v1 median
  step per new minute (in memory, never written). The next hourly
  rebuild replaces those minutes with the exact values; measured
  difference after 16 days of tail: 0.4 pips.

Wide spread minutes (docs/wide-spread.md) take part in the index with
the same corrected price the moving averages use: `bid + (spread -
1 pip) / 2`. A flagged minute without a stored spread is skipped, which
drops that minute from the common grid (`DollarIndexSymbol.
TryEffectivePrice`). The pairs are read with `includeWide: true`, so
the global "Hide wide spread minutes" setting does not change the
index. The same rule now applies to the v1 Median and Average paths and
to the 10 second tail (the live minutes keep their wide flag instead of
being dropped), so a stored index and its tail see the same minutes.

Code:

- `src/Fx.Consensus/ConsensusIndex.cs` (namespace `Fx.Consensus`): the
  kernel. `BuildFrame`, stage A (`StageA`, `Cluster`), stage B
  (`StageB`, a preconditioned conjugate gradient with the matrix
  applied through `Apply`), `MedianIndex` for the warm start,
  `PairWeight` for the importance weights. All parameters are the
  constants at the top of the class. The library has
  `<Optimize>true</Optimize>` for every configuration on purpose: the
  app runs from `bin\Debug`, and a Debug assembly is JIT-compiled
  without optimizations; the same solver took 41 s in FXViewer.dll
  (Debug) and 11 s in this library, because the parallel loops only
  pay off with optimized code. FXViewer references the library with a
  ProjectReference, so `dotnet build FXViewer/FXViewer.csproj` builds
  both.
- `FXViewer/Compute/DollarIndexSymbol.Consensus.cs`: reads the pairs
  year by year from `start - max scale` (so the long intervals that end
  at the start minute exist; about 64 calendar days extra), merges
  the common minutes, builds the USD-up moves in the index unit (pips
  or basis points of log return), calls the kernel, shifts the result
  so that the start minute is 1.0000 and turns it into flat candles at
  scale 100000 like v1. `Generate`, `Refresh` and `Compute` in
  `DollarIndexSymbol.cs` branch on `IndexMethods.IsConsensus`.
- `IndexMethods.Consensus` in `IndicatorSymbol.cs`; the dialog's Method
  combo is filled from `IndexMethods.All`, so it just appears.
- Storage of the index, symbol bar, chart: unchanged. The Currency
  Index changed the same day, but independently of the method: it is
  computed in memory from the stored index and the pair and drawn in
  its natural orientation (docs/usd-index.md, "No storage since v2").

Matrix-vector product in stage B: for each interval the index move is
read from a prefix sum of the vector (window averages in O(1)), the
weighted residuals go into a per-interval array, and each block of
minutes gathers its own contributions through a difference array, so
the two passes are parallel with no shared writes. Stage A runs
parallel over the intervals of each scale.

Measured (22 logical cores, Debug FXViewer.dll plus the optimized
library):

| Run | Grid minutes | Blocks | Intervals | CG steps | Total |
|---|---|---|---|---|---|
| one block, 2026-06-01 .. 2026-10-17, no pre-start | 138,959 | 1 | 558,708 | 837 | 1.9 s |
| the user's index from scratch, start 2025-08-26, with pre-start | 478,950 | 5 | 4,082,915 | 4718 | 9.9 s |
| the same index, hourly rebuild (window only) | 203,072 solved, 137,536 written | 1 | 816,583 | 960 | 2.4 s |

The v1 median index over the same range takes 0.5 s. Memory for the
user's index: the pair moves (6 x 3.8 MB) plus one block at a time
(816k intervals, two floats each, seven working vectors of 203k), well
under 100 MB; a build from 2011 stays at that level too, because only
one block is in memory at a time, and its time grows linearly with the
length (about 2 s per 10 weeks of history).

Consistency checks (harness in the scratchpad, Release build against
the Debug FXViewer.dll): a single block equals the Python prototype to
0.00002 pips; the hourly window result has the same shape as a build
from scratch over the same minutes (max 0.1 pip, mean 0.04) and leaves
every stored minute before the window untouched; the last 19 weeks
computed from a January to October grid and from a grid of exactly the
last 28 weeks differ by 0.00000 pips; the 10 second tail resumed from a
stored Consensus index steps like the v1 median (16,422 minutes in 48
ms) and sat 5 pips off the exact value after 16 days without a rebuild.

The code in bin is updated only when the app is closed and rebuilt into
bin; a temp OutDir build does not change what the running app does.

## Consensus 2: EUR/GBP priority and return intervals

Status: implemented 2026-10-17 as the index Method `Consensus 2`, next
to `Consensus`. Both methods can live on one chart as two index
symbols, so the curves can be compared minute by minute. The storage,
the Currency Index, the hourly window rebuild and the 10 second tail
are the same as for Consensus; only two things in the kernel differ.

### Where it comes from

The user proposed a geometric construction over EURUSD and GBPUSD:
align the two pairs vertically at some minute, take every later minute
where the two lines cross again as an index point (there both pairs
moved the same distance, so the whole move is the dollar's), call that
chain a sequence, start the next sequence at the next free minute and
slide it vertically inside the channel between the two aligned lines of
the earlier sequences, and so on. The goal was an index where, over any
interval on which EURUSD and GBPUSD moved the same number of pips, the
EUR index and the GBP index did not move at all; other pairs would be
used only where a sequence kept too much vertical slack.

Analysis of that construction (2026-10-17):

- The crossings after an alignment at minute t0 are exactly the minutes
  where the spread S = EURUSD - GBPUSD (pips, same orientation) is back
  at S(t0). A sequence is one level of the spread, and inside a
  sequence the EUR index (EURUSD plus USD index) is constant.
- "Stay inside the channel" of another sequence reduces to one
  inequality between the two levels, 0 <= F(s1) - F(s0) <= s1 - s0,
  where F(s) is the EUR index at spread level s. So the whole index is
  one function F of the spread level, monotone, slope between 0 and 1
  (the share of a spread move given to EUR). Nothing in two pairs tells
  one such F from another: the vertical slack of every sequence is the
  whole channel, and the choice "middle of the channel" gives
  F(s) = s / 2, which is the plain two-pair Average index.
- The target property "over any interval" is wrong for long intervals.
  Day 1: EURUSD +50 on EUR news, GBPUSD 0, USDCHF 0; day 2: EURUSD 0,
  GBPUSD +50 on GBP news, USDCHF 0. Over the two days both pairs made
  +50, so the property says the dollar fell 50 pips while USDCHF shows
  it stood still. An index that is a function of the spread level has
  no memory, but whose move it was depends on when it happened.
- Measured on 2025-01-01 .. 2026-10-17 (651k common minutes): for each
  minute take the next return of the spread to the same level. 85% of
  the returns are shorter than an hour. Of the returns longer than a
  day, USDCHF and USDCAD both confirm the EUR/GBP move in under 10% of
  the cases and none of the other four pairs confirms it in 36-41%; the
  median of the other four sits 24-86 pips away from the EUR/GBP move.
- Adding a third pair "only where the slack is large" does not work in
  the strict form: with EUR/GBP exact, the EUR/CHF condition can only be
  met by a constant F. The pairs can only be combined with weights,
  which is what v2 does.

What survives from the idea is realized inside v2: the two heavy pairs
get a clear majority, and the exact return intervals of the EUR/GBP
spread join the frame as intervals of their own, so the index passes
through the points where EURUSD and GBPUSD moved the same distance
whenever the other pairs do not contradict it.

### The two differences

Importance weights 10/8/4/3/2/1 (EURUSD 10, GBPUSD 8, USDCHF 4, USDCAD
3, NZDUSD 2, AUDUSD 1, normalized), `PairWeight(pair, heavy: true)`.
EURUSD plus GBPUSD (18 of 28) outweigh the other four together (10), so
an interval where those two agree gets a strength of at least 0.29 even
when the other four agree among themselves on something else; with
6/5/4/3/2/1 the same interval was nearly a tie (11 against 10).

Return intervals. In every solve block, after the regular frame
(`AddReturnIntervals`): the spread between the two heaviest pairs,
S(t) = x_first(t) - x_second(t) in the index unit; for every minute t
the return minute r(t) is the first later minute where S crosses S(t)
again (if S(t + 1) > S(t), the first minute with S <= S(t); if below,
the first with S >= S(t); if equal, t + 1), found with two monotonic
stacks in O(n). The interval [t, r(t)] joins the frame when
r(t) - t <= 65536 and its end windows fit into the block. Its end
window half follows the frame rule (length / 256 from length 256), its
tolerance floor comes from the regular scale nearest in log length,
interpolated at its start like a frame interval of that scale. Stage A
treats it like any interval (six pairs, weights, margin strength,
recency) and multiplies the strength by `Options.ReturnStrength`
(1.0 in Consensus 2; 0 reproduces Consensus exactly, checked). Stage B
sees it as one more spring; the matrix-vector product handles these
irregular intervals with their own parallel loop and a binary search
over the intervals sorted by start and by end. About one return
interval per minute, 85% of them shorter than an hour.

### Measured

Harness in the scratchpad (a console project on the kernel, reads the
.m1 files directly, 2026-06-01 .. 2026-10-17, 138,959 minutes, one
block, no pre-start; the random intervals use the kernel's floors and
the 6/5/4/3/2/1 weights for the metric):

| | Consensus | heavy weights only | returns only | Consensus 2 |
|---|---|---|---|---|
| example interval (pairs +489/+489) | +464 | +478 | +473 | +480 |
| random 1h-4h / 4h-1d / 1d-5d | 1.3 / 3.2 / 5.5 | 1.1 / 2.7 / 5.5 | 1.2 / 2.8 / 5.7 | 1.1 / 2.6 / 5.6 |
| random 5d-2w / 2w-6w | 12.3 / 17.7 | 12.8 / 16.0 | 12.0 / 16.8 | 12.8 / 15.8 |
| EUR/GBP returns 1d-5d / 5d-2w / 2w-6w | 4.2 / 6.0 / 12.7 | 2.3 / 3.1 / 5.6 | 2.1 / 2.7 / 9.0 | 1.2 / 1.5 / 5.3 |
| one-minute step std, jumps > 6 with quiet pairs | 0.70, 0 | 0.72, 0 | 0.71, 1 | 0.72, 0 |
| solve time | 1.7 s | 1.4 s | 1.3 s | 1.3 s |

Random intervals: strength-weighted mean |index move - six-pair
consensus| in pips, intervals ending in the last 10 weeks. EUR/GBP
returns: mean |index move - common EUR/GBP move| over the 2,616 return
intervals of a day or longer (1,854 / 499 / 263 per bucket), plain
minute values. Consensus 2 passes 3-4 times closer through the points
where EURUSD and GBPUSD moved the same distance, and the random
interval test does not get worse (16.0-17.7 at 2-6 weeks, within the
noise of the test). The regular Consensus path is unchanged: the
kernel built with these additions and the kernel the app ran before
give the same curve to the last bit on the same data.

In FXViewer: `IndexMethods.Consensus2` ("Consensus 2" in the Method
combo; `IsConsensus` is true for both methods, `IsConsensus2` picks the
weights and `Options.Anchored` in `DollarIndexSymbol.ComputeConsensus`).
The two index symbols are independent: each has its own storage,
hourly rebuild and tail.

## History end

The index dialog has an optional "History end" (date + HH:mm, UTC; the
label was "End" until 2026-10-20, stored as `EndTimeUnix`). Minutes of
the pairs after that time do not exist for the index: `ComputeConsensus`
reads the pairs only up to it, so the grid, the frame, the floors, the
recency weights and the solve windows all end there, exactly as if the
history stopped at that minute. Empty = no limit. Every path passes it:
OK in the dialog (Generate), Refresh, the hourly rebuild (`Compute`
with the history end clamped to the confirmed live minutes) and the
10 second tail (stops at the history end).

This mattered for the batch version of Consensus and Consensus 2: in a
least squares solve every minute depends on the minutes after it, so
an index with an empty history end kept changing its recent past as
new minutes arrived. Since 2026-10-20 both methods are causal (see
"Causal algorithm"), and a history end only cuts the index, as for v1.
The measurements below are of the batch version.

Checked 2026-10-20 with a harness on the FXViewer code (copies of the
2025 and 2026 files of the six pairs, start 2025-10-17, history end
2026-09-15 12:00). Copy B had every minute after the history end
changed: EURUSD +30 pips, USDCHF -20 pips, every third GBPUSD minute
removed. With the history end set, A and B give the same 328,248
candles to the last bit, for both methods. With the history end empty,
the index before 2026-09-15 differs between A and B by up to 14.6 pips
(Consensus) and 6.6 pips (Consensus 2).

The same run compared the index with the history end against the index
without it on the real data, by trading minutes before the history end,
pips (mean / max):

| Zone | Consensus | Consensus 2 |
|---|---|---|
| last hour | 3.5 / 4.2 | 3.9 / 4.8 |
| 1 hour .. 1 day | 4.8 / 9.5 | 2.9 / 4.9 |
| 1 day .. 1 week | 3.0 / 8.2 | 3.4 / 5.2 |
| 1 .. 4 weeks | 2.2 / 6.2 | 4.8 / 11.6 |
| 4 weeks .. 19 weeks (the written window) | 2.8 / 17.3 | 4.0 / 11.2 |
| older | 0.0 / 0.0 | 0.0 / 0.0 |

So the index at the right edge moves by a few pips on average (up to
10-17) during the 19 weeks after it is first computed, and is final
after that, as the window design says.

## Causal algorithm (both methods since 2026-10-20)

Status: since 2026-10-20 the app computes Consensus and Consensus 2
with this algorithm only. Consensus uses the weights 6/5/4/3/2/1 and
no return intervals; Consensus 2 uses 10/8/4/3/2/1 and the EUR/GBP
return intervals. The batch least squares described in the sections
above is the first version; the app no longer uses it, the kernel
library keeps it (`ConsensusIndex.Compute`) for measurements. For one
day (2026-10-20) the causal algorithm was a separate method named
"Consensus 3"; the user then asked to drop it and make the two
existing methods causal instead.

The user's rule: once the index has a value for a minute, that value
must stay when new candles arrive. The batch version cannot do this:
in a least squares solve every minute depends on the minutes after it,
and the right edge moved by 3-5 pips on average (up to 17) during the
19 weeks after a minute was first computed (see "History end"). The
causal algorithm computes a minute once, from the pair minutes up to
itself and the index values before it, and never touches it again.

### Algorithm

Kernel: `Fx.Consensus.CausalIndex` (src/Fx.Consensus/CausalIndex.cs).
It walks the grid minute by minute from the start minute (value
1.00000) and keeps the index as the stored integers (scale 100000, so
0.1 pip in Pips).

For minute t and each of the 22 scales s of the batch version (1, 2,
4, ... 2048, 2896, ... 65533 grid minutes):

- The interval goes from an anchor window around a = t - s with half
  width s / 4 (so from t - 1.25 s to t - 0.75 s) to the single minute
  t. A pair's move is its price at t minus its mean price over the
  window (pips, or basis points of log return in Percent), in the USD
  up orientation.
- Stage A is the one of the batch version: the six moves are
  clustered with the importance weights of the method, the tolerance
  max(floor, 0.15 |m|), and the strength H is the margin of the
  winning cluster.
- The scale predicts the index at t as (mean index over the same
  window) + m. A scale whose window reaches before the start minute is
  skipped, so during the first 11 weeks after the start only the
  shorter scales vote.

Consensus 2 only: return intervals of the EUR/GBP spread take part as
one more scale. Every interval [t', t] with t the first return of the
spread to its level at t' (length up to 65536, start window
length / 256) predicts the index at t the same way; their
strength-weighted mean prediction enters with the mean strength as its
weight.

The value at t:

    L = sum H_s p_s / sum H_s          the level all scales agree on
    b = u(t - 1) + m_1                 one-minute step: previous value
                                       plus the one-minute consensus
    u(t) = b + lambda (L - b)          lambda = 0.1

So a minute's step follows the one-minute consensus of the pairs, and
the index is pulled toward the multi-scale level with a time constant
of about 10 minutes. With lambda = 1 (u = L) there were 307 jumps of
more than 3 pips in minutes where every pair moved 2 pips or less
(January-October 2026): the weak scales (strength about 0.07, often
the "cluster" of one heavy pair) give predictions tens of pips away,
and as their strength flickers between 0 and 0.13 the weighted mean
jumps. lambda = 0.1 removed all of them and did not change the random
interval error. For Consensus 2, EUR/GBP returns 1d-5d / 5d-2w /
2w-6w: lambda = 1 gave 3.2 / 5.8 / 8.0 (with the 307 jumps), 0.1 gives
3.7 / 6.3 / 8.2, 1/30 gave 4.1 / 6.8 / 8.7, 1/120 gave 5.1 / 8.3 /
11.1.

Tolerance floors, time-local but causal: a week is the calendar week
starting Sunday 12:00 UTC. For every completed week and every scale,
the weekly value is the median, over the minutes of the week with
unix minute % 8 = 0, of the median absolute deviation of the six
moves. The floor of week W is max(0.5 pip, 0.5 x median of the weekly
values of weeks W-10 .. W-1). A minute's tolerance therefore depends
only on completed past weeks.

Exactness: pair prices are whole or half points and the index values
are integers, so every window sum is exact in double precision. The
same minute gets the same value whether the index is built from
scratch or appended to the stored values, wherever the loaded data
starts. This is what lets the app append instead of recompute.

### In FXViewer

Glue: `FXViewer/Compute/DollarIndexSymbol.Consensus.cs`. Pips and
Percent both work.

- Generate (dialog OK, Compute derived, Rebuild): from scratch, up to
  the last confirmed live minute (`ConfirmedEndUnix`), so provisional
  live minutes are never fixed. It reads the pairs from 164 days
  before the start (10 weeks of floors plus the 94 day lookback).
- Hourly auto step and Refresh: append only. The stored index is read,
  only minutes after its last minute are computed and written; stored
  minutes are never rewritten.
- 10 second tail: the same algorithm in memory for the minutes after
  the last stored one, including the live minute. The data window up
  to the last stored minute is cached until the next hourly append,
  and the weekly floors are cached per data setup (pairs, start,
  unit), so a tail call reads only the new minutes.
- A continuation reads 81917 grid minutes (about 94 calendar days)
  before the first new minute, plus 10 weeks more when the floor
  cache does not hold the 10 previous weeks (after an app start).
- Marker file `consensus.key` in the index folder (data/NAME/):
  `causal v<CausalIndex.Version>|<ranked or heavy+returns>|<pairs>|
  <start>|<unit>`. Generate, Refresh and the hourly step write it after
  the values are written (`DollarIndexSymbol.MarkStored`). When it is
  missing or different (an index stored by the batch version, another
  method or setup, a new kernel version), the next hourly step or
  Refresh rebuilds the index from scratch once, and the tail stays
  empty until then. So the indexes stored before 2026-10-20 are
  rebuilt by the first hourly step after the app start.

### Measured

Harness on the kernel (six pairs, index start 2025-10-17, grid
2025-07-20 .. 2026-10-20, metrics over 2026-01-05 .. 2026-10-20;
random intervals and returns as in "Consensus 2", pips). "batch" is
the least squares version that looks ahead:

| | v1 median | batch Consensus | Consensus | batch Consensus 2 | Consensus 2 |
|---|---|---|---|---|---|
| example interval (pairs +489/+489) | +417 | +468 | +484 | +480 | +485 |
| one-minute step std | 0.72 | 0.87 | 0.86 | 0.91 | 0.88 |
| 99.9% of the step's distance from the median pair step | 0.0 | 2.7 | 2.5 | 2.7 | 2.6 |
| jumps > 3 pips while every pair moved <= 2 | 0 | 4 | 0 | 5 | 0 |
| random 1h-4h / 4h-1d / 1d-5d | 2.7 / 6.7 / 15.2 | 1.6 / 3.6 / 6.8 | 2.1 / 4.2 / 8.3 | 1.3 / 2.9 / 6.9 | 1.7 / 3.4 / 7.6 |
| random 5d-2w / 2w-6w | 28.5 / 47.0 | 14.5 / 22.0 | 14.7 / 21.7 | 15.1 / 18.8 | 14.8 / 19.5 |
| EUR/GBP returns 1d-5d / 5d-2w / 2w-6w | 18.1 / 37.9 / 66.3 | 4.8 / 14.8 / 22.8 | 6.6 / 12.1 / 17.5 | 1.3 / 2.5 / 5.9 | 3.7 / 6.3 / 8.2 |

The causal versions lose 10-30% on intervals up to a few days, are
equal at 1-6 weeks, and have no jumps in quiet minutes.

Checks:

- Causality: the index with the history ending 2026-09-15 12:00 and
  the index over all data agree on all 328,884 minutes up to that time.
  For the user's USD setup (start 2025-08-26), "history ends 2026-10-16
  20:00" and "no history end" agree on every minute; the batch version
  differed by -13 .. +8 pips there.
- Continuation from three points (a Monday 00:00 with a month to go, a
  Friday with the last weekend, a December week) equals the full
  computation bit for bit, with a fresh floor cache.
- The FXViewer path on a copy of the data, both methods: an index
  stored without a marker (standing for the batch values) is rebuilt
  once by the first hourly step; then four append steps (one hour, over
  a weekend, two weeks after a simulated app start with no caches, to
  the end of the data) change no stored minute, and the result equals a
  Generate over everything on every minute (415,319 minutes for
  Consensus, 362,377 for Consensus 2). Percent too. The 10 second tail
  equals the from-scratch values, and is empty while the marker is
  missing.
- Timings (Debug FXViewer.dll, optimized kernel): Generate for a year
  3-8 s, hourly append 0.2-0.6 s, tail 22 ms (330 ms before the window
  cache).

### Limits

- The first 11 weeks after the start have fewer scales (their anchor
  windows would reach before the start). Put the start about three
  months before the period you want to read.
- The tail computes the live minute and the minutes since the last
  hourly append from live data. If a broker bar later differs from the
  live minute, that minute can change once, when the hourly step
  writes it. Stored minutes never change.
- When pair history in the past is repaired, the stored index keeps its
  values; only Generate (dialog OK, Compute derived, Rebuild) recomputes
  it.

## Known limits

- Contradictions between intervals are resolved by least squares; a
  user-chosen interval whose end sits on a spike of two pairs will not
  show the exact agreed value (the example: 465 for 489).
- Pips and percent give different consensus values (see Units).
- "Everything else moved together" (risk-off hitting AUD, NZD, CAD at
  once) still looks like a dollar move when the heavy pairs agree. Six
  pairs cannot tell the two apart; the importance weights decide.
- The frame approximates "every interval"; an interval that falls
  between two frame intervals is enforced through its neighbours.

## Ideas not in v2

- Swing intervals: add the intervals between ZigZag points of the
  pairs (several thresholds) to the frame with extra weight. The user
  reads moves from swing to swing, and such intervals are where the
  rule matters most. Consensus 2 does this for one kind of interval,
  the returns of the EUR/GBP spread; ZigZag swings are still open.
- A tolerance per pair (AUD and NZD are noisier in pips).
- USDJPY and other pairs with the same weights scheme (`PairWeight`
  gives 1 to any pair not in its list).
- Parameters in the dialog (weights, p, kappa) if tuning is wanted.
- A coarse-grid preconditioner for the CG loop if a block solve ever
  needs to be faster than about 2 s.
