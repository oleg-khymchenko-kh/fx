# USD index and currency index

Status: implemented, v1.

## Goal

A synthetic "dollar index" symbol computed from the small (per-minute)
movements of the USD pairs. The idea: if only one pair moved, that was
the other currency moving and the dollar stood still; if all pairs moved
by the same percent in the dollar direction, that was the dollar moving.
The index extracts the common "dollar part" out of the movements.

The user picks which pairs build the index with checkboxes (all base
pairs are offered: EURUSD, GBPUSD, USDCHF, USDJPY, AUDUSD, NZDUSD,
USDCAD; the default selection is the six major non-JPY ones), sets a
start time (index = 1.0000 there) and an optional end time. With no end
time the index runs to the end of history and can be extended
incrementally as new history arrives.

The index is a standalone symbol, not an indicator of some pair: it has
no Source, it sits at the top level of the symbol bar next to the real
pairs, and other indicators (ZigZag, Average, Shift, Drawing) can be
built on top of it exactly like on a real pair.

## Algorithm

The computation is incremental, one M1 step at a time.

Each index has an Algorithm setting (default Percent). Percent is the
original algorithm: a step is a percent (log) return and the index is a
product of percent moves. Pips is an additive algorithm: a step is a
move in pips and the index is 1.0000 plus the accumulated pips. Both
share the same step merging, aggregation and storage; only the step
value and the accumulation differ.

### Percent

For each step take the log return of each selected pair between two
consecutive computed minutes:

    r = ln(price_now / price_prev)

Log returns are used instead of raw percent because they add up cleanly
over steps. Then flip the sign so that positive always means "dollar up":

    s = -r   when USD is the quote side (EURUSD, GBPUSD, AUDUSD, NZDUSD)
    s = +r   when USD is the base side  (USDCHF, USDCAD, USDJPY)

The side is read from the symbol name: a pair starting with "USD" has
the dollar as its base. Each `s` value is an estimate of the dollar move
against one currency: s_i = (USD move) - (currency i own move). The
dollar part is the common component of those values.

Aggregation, two methods (per-indicator setting, default Median):

- Median: sort the `s` values and take the middle one (the mean of the
  two middle ones when the count is even). If only one currency moved on
  its own, the median is unaffected and the index does not move. If all
  pairs moved the same percent toward the dollar, the median is exactly
  that percent.
- Average: plain mean of the values (a classic equal-weight index, kept
  for visual comparison with the median).

How many "own" currency moves the median can absorb depends on the pair
count: with N pairs it tolerates up to floor((N-1)/2) of them. Six pairs
tolerate two, three pairs tolerate one, so fewer pairs means a noisier
index. With a single pair the index is just that pair inverted.

The index accumulates in log space:

    logIndex += s_aggregated
    index = exp(logIndex)          (starts at 1.0 at the start minute)

Known limit: when three or more currencies move together for a non-USD
reason (e.g. risk-off hits AUD, NZD, CAD at once), part of that move is
attributed to the dollar. Six pairs cannot distinguish "USD up" from
"everything else down in sync"; the median is the best cheap
approximation.

### Pips

For each step take each pair's price change in pips instead of the log
return, with the same sign flip so that positive means "dollar up":

    d = (price_now - price_prev) / pip
    s = -d   when USD is the quote side
    s = +d   when USD is the base side

Pip size comes from the per-symbol config (10 raw points for the
5-digit pairs, 1000 for USDJPY), so a JPY pip counts as one pip like
any other. The `s` values go through the same Median / Average
aggregation, and the result is a pip move of the dollar itself. The
index accumulates additively:

    pips += s_aggregated
    index = 1.0000 + pips / 10000      (starts at 1.0 at the start minute)

Stored at the same raw scale of 100000, one pip of dollar movement is
10 raw points, so with the chart's fixed pip scale of 10 the line moves
1:1 in pips: a 40 pip dollar drop shows as the USD Index down 40 pips.

The point of the split: if EURUSD moved +50 pips and GBPUSD +70 pips,
and the aggregation puts the dollar part at -40 pips, then the EUR
index gets +10 and the GBP index +30, so in pips

    pair move = currency index move - USD index move

holds exactly for every USD-quote pair (EURUSD, GBPUSD, AUDUSD,
NZDUSD). For USD-base pairs (USDCHF, USDCAD, USDJPY) the currency sits
on the other side, so the sign flips: pair move = USD index move -
currency index move. Both algorithms answer the same question ("how
much of this pair's move was the dollar"); Percent answers it in
percent, Pips answers it in pips. The known limit above applies the
same way.

Encoding limit of Pips: the stored raw value is 100000 + pips * 10, so
an index that drops 10000 pips below the baseline would store zero or
negative values. The compute layer treats those minutes as gaps, and
Refresh falls back to a full recompute instead of extending
incrementally (the result stays correct, just slower). In practice only
a JPY currency index anchored many years back gets anywhere near that
floor.

## Steps and gaps

A step is computed only on minutes where every selected pair has a
filled M1 candle (the candle Avg price is used). Each pair keeps its
price from the last computed step, and the next step's return is taken
from that price.
So movement that happens while some pair has a data gap is not lost - it
is included in the next full step. The sum of a pair's step returns
always telescopes to its total change since the start.

The first computed minute is the first minute at or after the start time
where all six pairs have data; the index value 1.0 is written there.
Minutes without a step stay empty (normal gaps, like weekends).

## Data model and storage

A new indicator type `Index` next to ZigZag / Average / Shift / Drawing,
shown in the Type combo as "USD Index" (the stored type string stays
`Index`), with storage: DollarIndexSymbol.Generate writes one flat candle
(Min = Max = Avg = index value) per computed minute through
CandleDatabase into data/SYMBOL/YYYY.m1, like Average does. The value is
stored in raw points: index * 100000 (so 1.0000 = 100000, same 5-digit
scale as the pairs). The accumulator stays a double for the whole run;
only the stored value is rounded.

Model: IndicatorSymbol gains `StartTimeUnix`, `EndTimeUnix` (0 = no end),
`IndexMethod` (Median / Average), `IndexAlgorithm` (Percent / Pips,
missing in older configs deserializes as Percent so old indexes keep
their meaning) and `IndexPairs` (the selected pairs; an empty list falls
back to the six defaults, which also migrates older config entries).
The Mirror checkbox reuses the existing `Flip` field (the same one the
Shift type uses). SameData for Index compares only the five data fields
(start, end, method, algorithm, pairs) and is checked before the shared
Source comparison, so a name, color or mirror change does not recompute
the data, and the pair list is compared as a set (reordering it changes
nothing).

Generate reads the selected pairs year by year (memory stays small) and
merges the per-year candle lists by minute. Refresh continues from the
target's last stored minute: it re-reads the pair prices and the index
value at that minute (turned back into the accumulator per the
algorithm: log of the level for Percent, pips from 1.0000 for Pips),
then extends forward. New steps are possible only
up to the earliest of the pairs' last filled minutes. Resuming
from the stored (rounded) index value can shift later values by at most
1 point; a full recompute (Compute derived or an Edit that changes
data params) removes the drift.

## Creation and editing

Same Add/Edit dialog, with the fields ordered Name, Type, Source. Type
is required and starts empty for a new symbol, so nothing is created by
accident. Picking "USD Index" hides the Source row (an index has no
source) and shows the index block: Start (date + HH:mm, required), End
(date + HH:mm, both empty = to the end of history), Method combo
(Median / Average), Algorithm combo (Percent / Pips), the Pairs
checkboxes and the Mirror checkbox. At least one pair must be checked. Mirror is display only: ticking or
unticking it just reloads the chart, it never recomputes the data.

For "Currency Index" the Source combo is refilled with the existing USD
Index symbols only (with a clear message if none exist yet) and a Pair
combo picks the pair to take the currency from.

Compute derived, dialog OK and dialog Refresh all branch on the type and
call DollarIndexSymbol / CurrencyIndexSymbol Generate / Refresh. Compute
derived runs Index symbols first, so currency indexes and other
indicators built on an index see fresh data in the same pass.

## Rendering and the symbol bar

Not editable, no pivots - loads as a plain line series like Average.
Lazy loading works unchanged because the symbol has normal per-year
files.

DisplayConfigs yields an Index as a top-level series after the real
pairs, with a fixed pip scale of 10, the mirror flag taken from its own
Mirror checkbox and no source link, followed by the indicators whose
Source is that index (those inherit pip scale 10 and the same mirror
flag from it). The fixed scale matters because the index is always
stored at scale 100000: inheriting USDJPY's pip scale of 1000 would draw
it 100 times flatter, and inheriting a mirrored pair would draw the
dollar upside down.

Mirror uses the same transform as the mirrored pairs (USDCHF, USDJPY,
USDCAD): the series is flipped around min+max of its own data, and that
base is stored in `MirrorBases` in the config so the line keeps its
place between runs. Everything built on the index (Currency Index,
Average, ZigZag, Drawing, Shift) inherits the flag, so those keep
drawing over the index in the same orientation; a Shift on a mirrored
index still XORs its own "Flip vertically" box on top.

In the symbol bar the index behaves like a real pair: no "Align to
source" and no "*" unaligned marker (it has no source), but it does get
"Add" so indicators can be built on it, plus "Edit" and "Delete" because
it is user-defined. Its vertical position is set by the per-series
offset (wheel over the symbol row), like any series.

Renaming an index rewrites the Source of the indicators built on it, and
deleting one warns how many indicators will lose their source. An
indicator whose source no longer exists, or whose data is missing on
disk (for example an Index whose range produced no minutes), still gets
an empty series row in the symbol bar, so Edit / Refresh / Delete stay
reachable instead of the symbol silently disappearing from the chart.

## Currency index

Type `Currency`, shown as "Currency Index". This one IS a normal
indicator: it has a Source, and that Source can only be a USD Index
symbol. The user also picks one USD pair; the index built is for the
OTHER currency of that pair (EURUSD gives EUR, USDCHF gives CHF), and
the dialog shows the derived currency next to the combo.

Once the dollar's own path is known, the rest of a pair's move belongs
to the other currency. In logs, for a pair with USD as the quote side
(EURUSD):

    ln(EUR) = ln(EURUSD) + ln(USD)     ->   step = r + u

and for a pair with USD as the base side (USDCHF):

    ln(CHF) = ln(USD) - ln(USDCHF)     ->   step = u - r

where r is the pair's log return and u is the USD index log return over
the same minute. Every currency starts at 1.0000 on the first minute of
its parent USD Index, so all currency indexes and the dollar index share
one baseline and can be compared directly on the chart.

The currency index has no algorithm setting of its own: it reads the
Algorithm of its source USD Index. With Pips the same two formulas run
on pip changes instead of log returns (r = the pair's pip change using
the pair's own pip size, u = the index's pip change), the accumulator
is a pip sum and the stored level is 1.0000 + pips / 10000, same as the
parent. Saving an index edit that recomputes its data (an algorithm
switch included) also regenerates the currency indexes built on it in
the same pass, so a stored currency index never continues old-algorithm
data with new-algorithm steps; Compute derived rebuilds everything as
well.

Steps are computed on the minutes where both the index and the pair have
a candle; each side carries its last price across gaps, so nothing is
lost. This makes the result exact in level terms:

    EUR_t / USD_t = EURUSD_t / EURUSD_start

which holds in the data to about 0.0005 percent (pure rounding). With
the Pips algorithm the exact identity is additive instead:

    EUR_pips - USD_pips = EURUSD move in pips since the start

Sanity check on 2011-2026 with the six-pair median index: USD 1.419,
EUR 1.185, CHF 1.655, with the franc's jump in January 2015 (the SNB
de-peg) clearly visible.

Storage, refresh and rendering work like the other stored indicators:
flat candles at scale 100000, incremental Refresh from the last stored
minute (limited by the earlier of the index's and the pair's last
minute), nested under its index in the symbol bar with pip scale 10 and
the index's mirror flag. Because a currency index and its parent dollar
index live on the same 1.0000 baseline, "Align to source" is meaningful
for it.
SameData compares the source index and the pair.

## Not in v1 (next steps)

- Live update from ticks; both index types extend only on Refresh /
  Compute derived.
- Weighting pairs by liquidity, or PCA / Kalman style factor extraction.
