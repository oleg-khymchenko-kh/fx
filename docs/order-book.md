# Order book indicators

Status: implemented, v1. Collector plus both indicators.

## Goal

Show, at every price level, how many pending orders (stop losses, take
profits, entries) and how many open positions other traders hold. Two
separate indicators over the same stored data: one for pending orders,
one for open positions. Both draw as a right-edge profile anchored at
the cursor, the same shape as Density.

## Where the data comes from

FXSSI publishes an order book at `c.fxssi.com/api/mini-oanda`. The
endpoint name is literal: the numbers are OANDA client data. OANDA
itself closed its own orderBook / positionBook API in September 2024
and its web tool needs an account, so FXSSI is the only reachable
channel.

The endpoint is open, needs no key, and returns about 490 KB of JSON
per pair: 30 days of hourly snapshots (`nanos`) plus 721 OHLC
candles. Anonymous access is limited to hourly grouping and the data
runs about 45 hours behind real time.

**Images live about a day, then disappear in one sweep.** The JSON
keeps 30 days of derived numbers, but the PNG that carries the
per-price profile is deleted after a while. Measured on 2026-08-17:
files sit on a 20 minute grid back to 08-16 22:00 and everything older
answers `404`. The boundary was the same at 13:30 and at 15:54 UTC, so
the server does not slide it minute by minute - it wipes in batches,
and how much history is reachable depends on when in that cycle you
ask. Of the 51 hourly stamps in the two days before 08-16 22:00, three
were still served; those strays are not something to count on.

So a restart can backfill roughly the last day, and a longer gap is
lost for good. That is why the collector runs on a timer and why the
raw PNGs are kept. `MaxBackfill` is 48 hours for that reason - far
enough to catch a wide window, and the misses cost one cheap `404`
each.

**The image name is the snapshot time**, `snapshot-{PAIR}-{unix}.png`,
on the 20 minute grid. The JSON field `imgTime` is filled in for the
newest nano only and points 20 minutes into the future, at a file that
is not published yet, so it answers `404` every time. The collector
asks for the nano's own `time` first and falls back to `imgTime`.

The in-between 20 minute images cannot be used: anonymous grouping is
hourly (`current_grouping_seconds` 3600), so only whole hours carry
`middle_price`, and without it the rows have no price to sit on.
`middle_price` is not derivable from `price` either - it is some slower
average, off by up to 15 pips - so hourly stays the cadence.

## What each source carries

Numbers, straight from the JSON, exact:

- `price`, `middle_price`, `step`, `time`, `imgTime`
- levels `sr`, `gr`, `mvo`, `mvp`, `ob`, `os`
- counts `oio` (orders) and `oip` (positions)

The per-price profile is **not** in the JSON. It exists only as a
rendered 520x338 PNG, which the collector decodes.

**Neither `price` nor `middle_price` is the market price at the snapshot
time.** The image draws its own price line and prints it in a green box;
that value matches our M1 candles, the JSON fields do not. Measured on
2026-08-17:

| hour UTC | our M1 avg | green box on the image | `middle_price` |
|---|---|---|---|
| 04:00 | 1.15831 | 1.1583 | 1.15800 |
| 08:00 | 1.16064 | 1.1607 | 1.15935 |
| 13:00 | 1.15973 | 1.1595 | 1.15940 |

`middle_price` is some slower average. It still anchors the picture -
the 338 rows are centred on it, which is what `RowPricePoints` uses and
what the axis labels confirm to within a row or two - but it is not
where price was. FXSSI also rewrites these numbers after the hour
closes: read live, the 08:00 nano carried `price` 1.16090, close to the
truth; read six hours later the same nano says 1.15935. Anything that
needs the price of a snapshot has to come from our own candles, never
from these fields.

## Image layout

Two panels side by side, each a two-sided histogram around its own
vertical axis:

| | Left panel | Right panel |
|---|---|---|
| Content | pending orders (SL / TP / entries) | open positions |
| Axis column | x = 130 | x = 390 |
| Half width | 79.5 px = 3.0% | 79.5 px = 3.0% |

Bars are flat `4,166,188` (teal) and `252,134,4` (orange). The current
price is a `98,171,0` line on row 151.

Vertical calibration starts from the site's own formula:

```
price(row) = middle_price - (row - 151) * step / 2
```

With `step = 0.00025` that is 1.25 pips per pixel and a 423 pip
window. One logical bucket is 2 rows, so about 150 real buckets of 2.5
pips each.

**The formula alone is not enough.** FXSSI renders every image with
its own sub-pixel placement of the whole frame - bars, gridlines and
axis labels all shift together by up to 2.4 rows (3 pips) relative to
what the formula predicts. Measured over 88 stored snapshots on
2026-08-18: cross-correlating consecutive position profiles against the
`middle_price` prediction leaves a residual of 1.6-1.8 rows at the 90th
percentile, and that residual matches the per-image phase of the axis
labels almost exactly (subtracting it leaves 0.04 rows median). This
was found because the tallest positions bar visibly jumped 2-3 pips
between hourly snapshots while price never went there.

So the decoder measures the frame shift per image from the right-edge
axis labels: text blocks in the last 42 columns, luminance-weighted row
centroids, chained at the 20-row label spacing (labels are 25 pips
apart), median phase against the formula's prediction. Label text sits
`LabelRowBias = -1.33` rows above its gridline - a constant of FXSSI's
font rendering, measured against the drawn price line and our own M1
candles over 87 snapshots. The result is stored as `AnchorPoints`, the
calibrated price at row 151; `RowPricePoints` uses it instead of
`MiddlePoints`. When the labels cannot be read (fewer than 6 chained
blocks, or an offset over 6 rows) the anchor falls back to
`middle_price` and the collector logs it.

Verified effect on consecutive-snapshot stability (SSD cross-correlation
of position profiles, 44 hourly pairs per symbol):

| | vs `middle_price` | vs `AnchorPoints` |
|---|---|---|
| EURUSD median / p90 | 0.70 / 1.64 rows | 0.18 / 0.47 rows |
| GBPUSD median / p90 | 0.64 / 1.83 rows | 0.18 / 0.48 rows |

0.47 rows is 0.6 pips - the remaining wobble is FXSSI's own 2.5 pip
bucket quantization, not recoverable from the image.

Two known limits of the calibration, both measured by an independent
re-derivation (dotted-gridline regression plus OCR of the axis labels
on 3 images): the absolute placement carries about a 1 row constant
uncertainty (anchors sat 12-15 points above that method's estimate -
within the pixel-center convention ambiguity), and the true gridline
spacing is 19.86-19.93 rows per 25 pips rather than exactly 20, so the
linear formula drifts up to 1.5-3 pips at the very top and bottom of
the 423 pip window. Both are irrelevant for the snapshot-to-snapshot
stability the calibration was built for.

## What is confirmed and what is not

Confirmed:

- The decode. Two independent implementations (a PowerShell probe and
  `OrderBookDecoder`) produce identical totals to the second decimal:
  53.96% / 64.34% for the left panel, 114.75% / 33.28% for the right.
- The calibration. The formula holds only up to a per-image frame
  shift of up to 3 pips; the axis-label phase measures that shift and
  removes it - see the calibration section for the numbers.
- Snapshot times are UTC. Tested against our M1 candles over 87
  snapshots: the drawn price line matches the same-hour candle
  (median 1.0-1.4 pips), while +2h and +3h shifts are 5-7 pips off.
- Panel meaning. FXSSI documents the left histogram as pending orders
  including take profits and stop losses, and the right as currently
  open trades.

Not confirmed, deliberately left open:

- **What the two sides of each panel mean.** FXSSI describes the left
  panel as four quadrants by order type (Sell Limit, Buy Stop and so
  on) and the right panel as split into traders in profit and in loss
  - not buy versus sell. A buy/sell reading looked right on EURUSD
  (left side share 0.4561 against `prb` 0.4546) but failed on GBPUSD
  (0.3663 against 0.5117), so the match was a coincidence. The stored
  fields are therefore named by geometry - `PendingLeft`,
  `PendingRight`, `PositionsLeft`, `PositionsRight` - and carry no
  interpretation. Renaming later is free; storing a wrong meaning into
  data that cannot be refetched is not.
- **`mvo` and `mvp`.** Neither is the tallest bar nor the weighted
  median of its panel; gaps run from 2 to 97 pips depending on pair and
  method. They are stored as FXSSI publishes them and are not
  reproduced.
- **Colour split within one side.** Teal and orange bars overlap and
  the longer one hides the shorter, so counting pixels per colour loses
  mass. Only per-side totals are stored, which are unaffected.

## Storage

`data/{SYMBOL}/orderbook/{year}.obk`, append-only, fixed records, in
time order. Header is `FXOB` plus an int32 version, currently 2. Each
v2 record is 1416 bytes:

```
long  TimeUnix, ImageTimeUnix
int   PricePoints, MiddlePoints, StepPoints, AnchorPoints
int   MvoPoints, MvpPoints, SrPoints, GrPoints, ObPoints, OsPoints
int   OrdersCount, PositionsCount
byte  PendingLeft[338], PendingRight[338]
byte  PositionsLeft[338], PositionsRight[338]
```

`AnchorPoints` is the calibrated price at row 151 - see the
calibration section. v1 records (1412 bytes, no `AnchorPoints`) are
still readable; the reader substitutes `MiddlePoints`. At startup the
app upgrades any v1 file in place: every record is recalibrated from
its raw PNG (all of them succeeded on the 2026 archive), the old file
stays next to the new one as `{year}.obk.v1`, and a v1 file appended
to by an old build gets upgraded again on the next append. The upgrade
commits with `File.Replace`, so a crash cannot leave the year without
a main file.

The store repairs itself at startup and before every append, because a
broken year file cannot be refetched: a torn tail (partial record from
a cut-short write, or v1 records appended by an old build) is truncated
to the last whole record; a file with a broken header is moved aside to
`{year}.obk.bad` instead of being appended into; a missing main file
with a surviving `.v1` backup is restored from it; stale `.tmp` files
are deleted. Every repair is logged. Readers open with
`FileShare.ReadWrite` so a chart reload during a collector append gets
the whole-record prefix instead of a sharing violation.

Prices are integer points, `price * 100000`, the same scale the candle
files use for a 5 digit pair. Row values are raw pixel counts; `* 3.0
/ 79.5` turns them into percent, and the panel sums can be rescaled to
`OrdersCount` / `PositionsCount` for absolute figures. A year of hourly
snapshots is about 8.5 MB per pair.

Raw images go to `data/{SYMBOL}/orderbook/raw/{imgTime}.png`, roughly
15 KB each, about 130 MB a year per pair. They are the insurance: if
the decoder improves the whole archive can be rebuilt, and nothing
about a missed hour is recoverable from anywhere else.

## Collector

`OrderBookCollector` runs inside FXViewer on a `DispatcherTimer`,
started from `StartupAsync` with one immediate poll. Interval is 30
minutes, pairs are EURUSD and GBPUSD.

Each pass takes every hourly nano newer than what the store already
holds, oldest first, at most 48 of them, and for each one downloads the
PNG, decodes it, writes the raw file and appends the record. Snapshots
whose image has already expired are counted and reported in one line,
not one line each. The last stored time is seeded once per session from
the store, so a restart does not refetch, and it moves forward as
records are appended, so an expired hour is never asked for twice.

Steady state that means one new record an hour per pair; after a
restart it means whatever part of the last half day is still on the
server.

Panel centres are detected per image rather than assumed, and a move
away from 130 / 390 is logged as a warning - a silent layout change
would corrupt an archive that cannot be rebuilt. An empty panel is
logged the same way. A failure on one pair does not stop the other.

## Indicators

Two types, `PendingOrders` and `OpenPositions`, labelled "Pending
orders" and "Open positions" in the Add / Edit dialog. Both are
storage-less like Density, so Compute derived, Refresh and Rebuild
skip them and deleting one only drops the config entry. The only
parameters are the source pair and the colour; no param panel appears
because neither type matches one.

Rendering reuses the Density overlay end to end. `OrderBookProfile`
turns a snapshot into the same `DensityHistogram` the Density and
Volume indicators produce, and `ChartView.DrawDensity` draws
it unchanged - same 120 px right-edge bitmap, same translucent fill
with a solid edge, same alpha blending when several profiles are on.
No new drawing code.

Building the histogram:

- `OrderBookProfile.At` binary-searches for the last snapshot at or
  before the anchor minute, so the profile replays history as the
  cursor moves, the same way Density follows the cursor.
- Each of the 338 rows becomes a display pip through the source
  series' `SeriesTransform`, which keeps mirrored pairs and PipPoints
  scaling correct.
- Rows are 1.25 pips apart, so one row can straddle two pip levels.
  Each row fills the whole pip span between itself and the next row,
  taking the larger value, which leaves no gaps: a 423 pip window
  produces 422 pip levels with about 370 of them non-empty.
- Both sides of the panel are built, sharing one `LoPip` and one
  `MaxCount`, so their bar lengths are directly comparable.

## Two sides in one bar

`DrawOrderBook` puts both sides into a single bar growing left from
the right edge, so the profile keeps the Density footprint:

- the shorter side is drawn in full, nearest the edge;
- the longer side contributes only its excess, further out.

The two segments are `[width - large, width - small)` and
`[width - small, width)`, disjoint by construction, so no pixel ever
receives both colours and the translucent fills never mix with each
other. Each segment gets an opaque pixel at its outer end, so the
boundary reads as the shorter side's value and the total extent as the
longer one's. Which side is inner flips per row, whichever is smaller.

Both colours are picked in the editor. For an order book type the
existing swatch grid is labelled "Buy color" and a second grid, "Sell
color", appears below it; other types are untouched and still show one
"Color". They live on `IndicatorSymbol.ColorArgb` and
`SellColorArgb`, so an indicator saved before the field existed falls
back to the property default rather than to black. Two order book
indicators still blend with each other in the shared bitmap, the same
as two Density indicators do.

## Price line

After the bars, `DrawBookPriceLine` puts one red row across the whole
120 px overlay at the price the pair traded at when the snapshot was
taken, so it is visible which side of the book price sat on.

The price comes from our own M1 candles - `OrderBookProfile.PricePointsAt`
takes the last minute at or before `TimeUnix` and uses its average -
not from the JSON, for the reasons in "What each source carries". A
candle older than an hour is treated as no price and no line is drawn,
which is what happens over a weekend. The value goes through the same
`SeriesTransform` and the same `YToDisplay` rows as the bars, so a
mirrored or shifted source moves the line with the profile.

The line is opaque, so it covers the bars in its row rather than
blending with them. Two order book indicators on the same pair draw the
same line twice in the same place.

Which axis side is buy and which is sell is **not yet established** -
see "What is confirmed and what is not". The whole mapping is the
single constant `OrderBookProfile.LeftSideIsBuy`; `BuildSides` orders
its result by it and everything downstream speaks only of buy and
sell. When the price-crossing test settles the question, flipping that
one boolean is the entire change - no stored data and no editor field
has to move.

Before the first stored snapshot the profile is empty and nothing is
drawn, which is most of the chart until the archive grows. Past the
last snapshot the newest one stays on screen. The label at the top
right shows which snapshot is being used, as `NAME: MM-dd HH:mm`, or
`NAME: no book here` when the anchor predates the archive - so a book
that is two days stale always says so.

Keys 1-9 and 0 do not apply. They stay bound to Density and Volume
profile, whose windows these indicators do not have.

## Not in v1 (next steps)

- The two sides of each panel are summed rather than drawn apart,
  because what they mean is unsettled - see above.
- The whole `.obk` archive is read into memory when the chart loads
  (`OrderBookStore.ReadAll`). At 8.5 MB per pair-year that is fine for
  now; it is not wired into the lazy year loader.
- No symbol bar readout, no tooltip with the value under the cursor,
  no numeric scale - the same gaps Density has.
- Selection mode (Shift + drag) anchors the book at the end of the
  selection instead of aggregating over it.
- Hourly resolution only, and roughly 45 hours behind. A paid FXSSI
  plan is what unlocks 20 minute grouping and removes the delay; it may
  also serve historical images, which is untested.
- Snapshots older than the image retention window - about a day - are
  lost while the app is closed. If gaps become a problem the collector
  moves to a console tool under Task Scheduler.
- The newest nano is stored even when its hour is still running, so its
  `oio` / `oip` counts can be up to 20 minutes fresher than the image
  they are stored with. The rows themselves are exact.

## Market depth import

Sierra records CME depth to `MarketDepthData\<contract>.<date>.depth`, one file per symbol
per UTC day, and the app stores it as `data/SYMBOL/depth/<year>.dpt`.

`DepthCollector` keeps the store current by itself: every 30 s it tails the current UTC day's
file of the front contract (the one `VolumeCollector` settled on from the `.scid` volumes, see
docs/volume.md; the calendar month until it has decided) from the byte offset it left off at,
replays the add / modify /
delete commands into a book, and appends one record per minute that closed. Sierra sends a
ClearBook roughly every 10 minutes and re-adds the whole book, so a reader that starts in the
middle of a file has a complete book again within 10 minutes - that is what makes tailing
possible without replaying the day from byte 0.

Guards, mirroring the volume collector: the newest stored minute is read once per session and
minutes at or before it are never re-appended, so restarts cannot duplicate; a file that got
shorter or an offset that no longer lands on a record boundary restarts the read and resets
the book; a day rollover switches files and resets; writes are skipped while the DB is busy.
Appended minutes are merged into the loaded series through `ChartView.MergeDepthSnapshots`, so
the profile follows without a chart reload.

`src/Fx.DepthFill` stays for deep history - days older than today, or a first fill. It now
shares the parser (`FXViewer/OrderBook/DepthReader.cs`) with the app and skips minutes already
in the store, so re-running it over days that are already imported writes nothing and cannot
create duplicates or out-of-order records.

Depth recording is per symbol in Sierra: Global Settings >> Symbol Settings, pick the symbol,
tick `Record Market Depth Data`. Only symbols with that option produce `.depth` files, which
is why a pair can have live depth on screen (the cTrader `DepthProbe` stream) and still have
no history to import.
