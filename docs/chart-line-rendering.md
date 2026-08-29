# Chart line rendering: candle decimation

Status: implemented for a static chart (FXViewer/Chart). Live appending is
not implemented yet.

## Scale epochs

The chart renders inside an "epoch": a fixed mapping that consists of

- `K` - seconds per pixel column, computed once per epoch and then frozen,
- bucket alignment to the unix epoch: `bucketIndex = unixSeconds / K`,
- a fixed price-to-Y scale.

`K` is a whole number of minutes while a column holds a minute or more.
Below that it is one of the divisors of 60, so one minute spreads over
several pixels; see docs/time-zoom.md.

Invariant 5 ("drawn pixels never change") applies inside one epoch. A new
epoch starts on: resize, DPI change, zoom, pan, the live price leaving the
Y scale range, or the line reaching the right edge of the bitmap. A new
epoch means one full redraw with a new mapping. Full redraw is cheap (it is
bounded by the viewport width, not by history size), so epochs are allowed,
they just must be rare in live mode. For that, the live Y scale must
reserve real headroom (several daily ranges), not cosmetic padding.

Buckets are aligned to the unix epoch, not to the start of the visible
range. This way the content of a column depends only on `K`, so a column
appended live is identical to the same column computed by a full reload,
and panning does not shift bucket boundaries.

The pixel writer must clamp Y values to the bitmap bounds unconditionally:
an out-of-scale value must produce a clamped pixel, never an out-of-bounds
write.

## Problem

One chart shows many symbols at the same time. To avoid visual mess, each
symbol is drawn as a line 1 pixel wide. One pixel column holds one candle or
a whole number of candles. So for each pixel column we must pick a single
value from the aggregated candle data: min, max, or avg.

## Invariants

1. An extremum that was not seen for a long time must be visible.
2. When there is no significant extremum, show avg.
3. The line is continuous while the data is continuous.
4. A data gap is drawn as empty columns. No connection across the gap.
5. New incoming data never changes pixels that are already drawn.
6. The line uses the minimum number of pixels: 1 px tall where price does
   not move. Columns taller than 1 px appear only where price moved or an
   extremum is shown.
7. The algorithm is single-pass: each candle is processed once, with a
   fixed amount of work, and its column is drawn immediately. No second
   pass over the data.

Invariant 5 is expensive: it forbids any rule that looks at candles to the
right of the current one. It exists so that a live chart never repaints
history and the pixel cache stays valid. Do not break it with a future
"improvement".

Invariants 5 and 7 support each other: because no rule looks to the right,
each candle can be finished the moment it arrives.

## Value selection

All terms below ("candle", "max", "min", "avg") refer to the aggregate of
all candles inside one pixel column.

For the current candle, search to the left only:

- `dMax` = distance (in candles) to the nearest left candle whose
  `max >= cur.max`. Not found = infinity.
- `dMin` = distance to the nearest left candle whose `min <= cur.min`.
  Not found = infinity.

The idea: the distance measures how significant the extremum is. A new high
over 500 candles matters more than a new high over 5 candles.

Search details:

- The lookback depth is bounded (for example, the visible width in pixels).
  Nothing found within the limit counts as infinity. The bound keeps the
  cost fixed and makes the result independent of how much history is loaded.
- The search goes across data gaps. Distance counts candles in sequence,
  not time. So after a weekend, Monday is compared with Friday.
- Both searches can be computed incrementally with a monotonic stack
  (the "previous greater element" / stock span pattern). This is what makes
  invariant 7 hold: two stacks are kept, one for max and one for min. When
  a candle arrives, pop stack entries it covers, read the distance from the
  new stack top, push the candle. Amortized O(1) per candle, O(n) total.
  Entries older than the lookback limit are dropped, so memory is bounded
  by the lookback depth.
- A naive backward scan per candle also works and is simpler. It is fine
  for a first version with a small lookback, but it is O(lookback) per
  candle, so the stacks are the target design.

Selection rules, in order:

1. If the nearest max-covering candle and the nearest min-covering candle
   are the same candle: show `avg`. The current candle is fully inside that
   old candle, it sets nothing new.
2. If `max(dMax, dMin) < D`: show `avg`. Both extremes were seen recently,
   this is noise. `D` is the noise threshold, start with 4-5 and tune on
   real data.
3. If `dMax > dMin`: show `max`.
4. If `dMax < dMin`: show `min`.
5. If `dMax == dMin` (including both infinite, i.e. an outside candle where
   both extremes are new records): show the side with the larger deviation
   from the previous candle. `upDev = cur.max - prev.max`,
   `downDev = prev.min - cur.min`. If `upDev >= downDev` show `max`, else
   show `min`. The chosen value carries only one extremum, but the other
   side is not lost: such a candle is marked full-range (see below) and
   the renderer draws its whole min..max range.

Rule 1 has a known accepted limitation: if the covering candle is far away,
the current candle dominates everything between them in both directions,
but we still show `avg`.

## Full-range columns: both extremes stay visible

One value per column hides one side of a column that sets two significant
extremes at once. Real case, GBPUSD 28 Aug 2026 14:00 UTC: a news minute
spiked to 1.35983 (a retest of the previous day high) and then the market
fell to a multi-day low. Zoomed out, one column holds both the spike and
the start of the fall: `dMin` is huge (fresh low), `dMax` is about one
day back (the level was touched the day before), so rule 4 picks `min`
and the retest high is never drawn. It survived on deep zoom only because
the lookback there was shorter than the distance to the previous day
high. Any retest of an old level loses this way: a retest has a finite
`dMax` by definition, and the reversal from the level puts a fresh
opposite extreme into the same column.

So the decimator marks a column as full-range when both sides are
significant: rules 1 and 2 did not fire, and both `dMax >= D` and
`dMin >= D`. A not-found distance (infinity) counts as significant. The
chosen value still follows rules 3-5 and stays the line's main value
(connectors, hover readout). The renderer additionally draws the whole
min..max range of a marked column as a vertical run, like a 1 px candle
wick.

Measured on GBPUSD August 2026 with `D = 4`: 1-3% of columns get marked
depending on zoom, the median marked range is 3-17 pips, so the chart
stays a thin line everywhere except real events.

## Rendering

Each column draws a vertical run of pixels that connects the previous
chosen value to the current one:

```
curY   = chosen value of the current candle, mapped to pixels
prevY  = chosen value of the previous candle, mapped to pixels

start = prevY + sign(curY - prevY)
draw pixels from start to curY inclusive
```

Why this formula:

- Flat line (`curY == prevY`): `sign` is 0, one pixel is drawn. No holes.
- Price moved: the run starts one pixel away from `prevY`, so the corner
  pixel of the previous column is not duplicated. Adjacent columns connect
  diagonally, which looks fine for a 1 px line (same as Bresenham).
- No framebuffer reads are needed. The decision uses only `prevY` and
  `curY`. Checking "is there a pixel to the left" would break when many
  symbols draw into the same pixels.

Run clipping (no parallel columns):

The connector run must not retrace rows already drawn by the previous
column, otherwise a reversal draws two touching parallel columns. Track
the drawn row range `[prevLo, prevHi]` of the previous data column.
Compute the run `[start..curY]` as above, then:

- If the run lies fully inside `[prevLo, prevHi]` (a reversal back into
  the previous swing): draw only the single pixel at `curY`.
- Otherwise clip the run to the side of `[prevLo, prevHi]` that contains
  `curY`: going down the run starts no higher than `prevHi + 1`, going up
  it starts no lower than `prevLo - 1`.

Result: two adjacent columns never share more than one row. A spike
renders as one tall column, its neighbors connect diagonally to the ends
of that column, and a reversal shows the swing column plus a single
pixel. A flat line still shares exactly one row between neighbors, which
is what a 1 px horizontal line is.

Full-range columns:

For a column marked full-range the run is the union of the connector run
and the column's own min..max range. The one-sided clip against
`[prevLo, prevHi]` is skipped for it: cutting the run to one side of the
previous column is exactly what would hide the weaker extreme again.
Only the fully-inside rule stays: if the whole run lies inside
`[prevLo, prevHi]`, a single pixel at the chosen value is drawn, the
column adds nothing new. Two adjacent marked columns may overlap a few
rows; that is accepted, the price really traded there in both columns.

Thicker lines:

`RenderLine.Width` (1 by default) makes a line thicker without touching
any of the rules above: the run and the `[prevLo, prevHi]` bookkeeping are
computed exactly as for a 1 px line, and only the fill goes `Width - 1`
rows further down, clamped to the bitmap. Today the only user is the
Shift indicator picked with Alt (docs/shifted-symbol.md), drawn 2 px wide.

Special cases:

- The first candle has no `prevY`: draw a single pixel at `curY`.
- A candle right after a data gap is drawn the same way: a single pixel,
  no connector back across the gap.
- A data gap is drawn as empty columns. The width of the empty area is
  proportional to the gap duration.

Side effects that fall out naturally:

- A spike after a quiet period renders as a vertical run up or down, like
  a wick. With threshold `D`, tall columns appear only on significant
  moves.
- A sharp reversal (max then min right after) gives one tall column over
  the full swing.

## Aggregation levels

Aggregating pixel columns straight from M1 makes a redraw cost grow with
the visible time range, which gets too slow past roughly a year on
screen. So each symbol precomputes coarser levels once at load: 15m, 1h,
4h, 1d. A level block stores min, max, the exact sum of minute avg
values, and the minute count. Any column assembled from whole blocks
gets exactly the same min/max/avg as if it were computed from M1, so
the rendered pixels do not depend on which level was used.

To keep columns made of whole blocks, `K` is quantized on wide zooms.
In minutes: below 60 any integer, then a multiple of 15 up to 240, of 60
up to 960, of 240 up to 5760, and of 1440 beyond. Each threshold is a
multiple of the next quantum, and a quantum is at most 25% of `K` in its
band, so the 1.25x zoom ladder keeps working. `BuildView` picks the
largest level whose size divides `K`; a `K` that divides no level (small
zooms, old saved states) falls back to the M1 scan. Below one minute per
column there is nothing to aggregate, so `BuildView` builds minute
columns and copies each one over its pixels.

## Tuning parameters

- `D` - noise threshold in candles. Below it the extremum is ignored and
  `avg` is shown. Start with 4-5.
- Lookback limit - how far the left search goes. Start with the visible
  width in pixels.

Both should be tuned by eye on real EURUSD data.
