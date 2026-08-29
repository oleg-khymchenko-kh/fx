# Real volume in the candle record

Status: implemented, v5 (v1 read an external CSV; v2 moved the volume
into the pair's candles; v3 fills it live from Sierra Chart; v4 merged
the Volume profile indicator into Volume; v5 pinned the bar height so
zoom never changes it).

## Goal

Every stored minute of a base pair keeps the real traded volume of
that minute, so the Volume indicator reads it straight from the pair
like the spread. FxPro sends only tick counts, so the source is the
matching CME FX future (see "Filling" below).

## Storage

No new bytes: the record stays 16 bytes, the file format version
stays 1. The volume lives in the flags word at offset 12, next to the
spread:

| Bits | Meaning |
|---|---|
| 0 | `FlagFilled` |
| 1 | `FlagAvgApprox` |
| 2 | `FlagSpread` |
| 3 | `FlagVolume` - the volume word below is valid |
| 4 | `FlagProvisional` |
| 8..15 | packed spread byte |
| 16..31 | volume, 0..65535 contracts |

Values above 65535 are stored as 65535 (`VolumeCodes.Max`); the
observed EURUSD peak is ~6,500 contracts per minute, so saturation is
theoretical. Old files have bit 3 clear everywhere and read back as
"volume unknown" without any migration.

## Writing

`CandleDatabase.WriteMinute` takes a `volume` next to `spreadCode`:

- a real value (>= 0) - write it and set `FlagVolume`;
- `VolumeCodes.Unknown` - clear the volume bits (default, used by the
  computed indicator symbols);
- `VolumeCodes.Keep` - read the old record and carry its volume over.

`Keep` is what the base pair paths pass (trendbar download, ask tick
download, CSV import, live writer), so a repaired or re-downloaded
minute never loses a backfilled volume. A history CSV with a `volume`
column writes real values instead.

`CandleDatabase.WriteVolume` sets only the volume bits of a minute
that already exists and returns false for a minute that is not
filled, so a backfill can never invent candles.

## Filling (`Fx.VolumeFill`)

Real volume comes from CME FX futures via the Yahoo Finance chart
API. The console tool downloads 1m bars and writes them into the DB
by unix minute key:

```bash
dotnet run --project src/Fx.VolumeFill
```

Run it with the app CLOSED (the year files are locked while it runs).
Optional args: `--data <path>` for a non-default DB folder, or pair
names to fill only those.

| Pair | Future |
|---|---|
| EURUSD | 6E=F |
| GBPUSD | 6B=F |
| USDCHF | 6S=F |
| USDJPY | 6J=F |
| AUDUSD | 6A=F |
| NZDUSD | 6N=F |
| USDCAD | 6C=F |

EURGBP and GER40 have no Yahoo-served futures volume (CME EUR/GBP is
thin, FDAX is Eurex) and are skipped; GBPUSD-ASK is the ask side of
GBPUSD and gets no volume of its own. Volume is in contracts and the
contract size differs per future - compare volumes within one pair,
not across pairs.

Yahoo serves only the last ~30 days of 1m bars, so the tool fills the
recent month per run; re-running merges newer minutes over the old
ones. Minutes where CME is closed but FxPro trades (the 21:00-22:00
UTC maintenance break, holidays) simply stay without volume.

## Filling from a CSV (deep history)

For history older than Yahoo's month, buy or export the minute bars
somewhere else and feed the file to the same tool:

```bash
dotnet run --project src/Fx.VolumeFill -- --csv <file> --pair EURUSD
```

The header is sniffed, comma or semicolon separated. The time column
is the first of `ts_event`, `unix`, `time_utc`, `timestamp`, `time`,
`date`, `datetime` that is present; numbers are epoch seconds,
milliseconds, microseconds or nanoseconds (picked by magnitude, so
Databento's nanosecond `ts_event` works as is), text is parsed as UTC.
The volume column is the first of `volume`, `vol`, `size`, `qty`.
Timestamps must mark the bar OPEN, like everything else in the DB.

Vendors that stamp bars in exchange local time need `--tz`:

```bash
dotnet run --project src/Fx.VolumeFill -- --csv <file> --pair EURUSD --tz America/New_York
```

The zone is applied to TEXT timestamps only (epoch numbers are always
UTC) and follows that zone's DST rules, so a file spanning years
lands correctly on both sides of every transition. Known conventions:
FirstRate Data and Kibot stamp US Eastern (`America/New_York`),
Portara defaults to Chicago (`America/Chicago`), Sierra Chart exports
UTC (no `--tz`), Databento is UTC nanoseconds (no `--tz`).

Rows are floored to the minute and **summed** per minute, so a file
carrying several contract months for the same minute lands as the
total of that minute. One pair per run; the file range decides which
years are touched.

## Filling from Sierra Chart .scid files

Sierra Chart stores intraday data in a documented binary format, so
its files are read directly - no CSV export needed:

```bash
dotnet run --project src/Fx.VolumeFill -- --scid <file-or-folder> --pair EURUSD [--prefix 6E]
```

A `.scid` file is a 56 byte header (`SCID`, header size, record size)
followed by fixed records: `DateTime` (microseconds since 1899-12-30
UTC), OHLC floats, `NumTrades`, `TotalVolume`, `BidVolume`,
`AskVolume`. `TotalVolume` is summed per minute, so both 1-minute and
tick storage units work. Files are opened shared, so they can be read
while Sierra Chart is running (it flushes about every 5 seconds).

Point `--scid` at one file, or at the Data Files Folder with
`--prefix 6E` to take every contract month at once. With several
contracts the tool picks **one active contract per UTC day** - the
one with the highest volume that day - and takes only its minutes.
That is a volume-based roll and it avoids the double counting a plain
sum would produce during roll weeks.

### What was actually filled

2026-08-16, Sierra Chart Service Package 3 ($26/month, SC Data
historical service, no exchange fees, no broker account):

| Pair | Root | Minutes | Max per minute |
|---|---|---|---|
| EURUSD | 6E | 3,708,586 | 26,814 |
| USDJPY | 6J | 3,703,111 | 21,098 |
| AUDUSD | 6A | 3,640,139 | 10,575 |
| GBPUSD | 6B | 3,493,066 | 18,480 |
| USDCAD | 6C | 3,444,202 | 15,041 |
| NZDUSD | 6N | 3,216,725 | 5,244 |
| USDCHF | 6S | 3,049,764 | 4,285 |

All seven span 2015-11-26 to 2026-08-14 (10.7 years, 3,327 trading
days) and none reach the 65,535 ceiling.

How it was downloaded: symbol list of 308 quarterly contracts
(7 roots x H/M/U/Z x 2016-2026) imported into Chart >> Associated
Watch List, then Chart >> Start Scan. `Intraday Data Storage Time
Unit` was set to 1 Minute first. The default `Maximum Historical
Intraday Days to Download` of 120 is exactly right and must not be
raised: for a futures contract Sierra counts days back from the
**contract month end**, so 120 days covers each contract's
front-month life and consecutive quarterly contracts overlap by about
a month - which is what the per-day active-contract pick needs.

Quality check against the Yahoo month already in the DB: 95.3% of the
23,018 overlapping minutes match exactly, 96.1% within one contract,
median per-minute ratio 1.000. Sierra totals 6.5% lower, concentrated
in 1,089 minutes during 12:00-16:00 UTC - block and calendar-spread
trades, which CME counts in contract volume but which never enter the
intraday trade feed. Both numbers are right, they measure different
things; the intraday one is what a volume chart should show.

## Volume indicator (v4)

One `Volume` indicator type draws two things from the same source
pair: the bar panel at the bottom and the price profile at the right
edge. The separate "Volume profile" type is gone. A config that
still has one is fixed on startup: its lookback options move into the
pair's Volume indicator and the entry is dropped, or, when that pair
has no Volume indicator, the entry itself becomes a `Volume` one.

## Bottom panel

The panel reads the source pair's already loaded candles like the
Spread panel. 40 px band; columns with no known volume draw nothing.
The bottom panel stack starts at the last chart row, so the lowest
panel's bars sit flush with the bottom edge with no gap.

When the source has per-pip profiles (docs/volume-at-price.md) and
`Split ask / bid by color` is on, each bar keeps its full height but
carries the bid share on top in the indicator's second color. The
option is in the Add / Edit dialog; turning it off paints every bar in
one color. The rollup levels carry `VolumeSum` (-1 = none), so
zoomed-out views fold blocks instead of rescanning minutes. The
cursor readout shows the hovered column's contracts in the symbol
bar.

## Bar height (v5)

One bar is one group (see "Grouping"), and its height in pixels
depends only on how many contracts that group traded - never on the
view. Zooming or panning does not resize a bar.

The scale is `VolumeBarUnit`, the number of contracts that fills the
40 px band at bar scale 1. It is empty in a new config: the first
render that has volume fits the tallest visible bar into the band and
saves that number, and from then on the value is pinned and only the
wheel moves it.

Changing the group moves it too, but not one to one. A group that is
twice as long trades about twice the volume, so keeping the unit
proportional would leave the bars exactly as tall as before, and
keeping the unit fixed would double them. The rule in between is
**every doubling of the group makes a bar 1.5x taller**:

```
unit_new = unit_old * (group_new / group_old) ^ (1 - log2(1.5))
```

The exponent is 0.415, so the height follows `group ^ 0.585`. The
power law also covers the steps that are not doublings (3, 5, 10, 15
minutes ...), so every notch of the wheel changes the height by the
same smooth amount:

| Group | Height vs 1m |
|---|---|
| 1m | 1.00x |
| 2m | 1.50x |
| 3m | 1.90x |
| 5m | 2.56x |
| 10m | 3.85x |
| 15m | 4.87x |
| 30m | 7.31x |
| 60m | 11.0x |
| 240m | 24.7x |
| 1440m | 70.4x |

`IndicatorSymbol.ScaleVolumeBarUnit` is the one place that does it,
used both by the `Alt` wheel and by the Add / Edit dialog.

When one chart column covers several groups (zoomed out), the column
draws the **tallest** of them, not their sum - the peak group of that
column, at the same pixel height it has when zoomed in. With the
default 1-minute group a zoomed-out chart is therefore a "peak minute
per column" profile. The rollup levels carry that per-minute maximum
(`AggBlock.VolumeMax`), so the zoomed-out view folds blocks instead of
rescanning minutes.

The cursor readout and the ask/bid split follow the same rule: the
number in the symbol bar is the group the bar shows, and the split
uses the ask/bid share of the column.

## Grouping

`VolumeGroupMinutes` (default 1) sets the aggregation period: with 15
the minutes of each 15-minute block are summed and drawn as one bar,
as wide as the block. Blocks are aligned to the epoch, so they fall
on round clock times.

`VolumeGroupLocked` protects the value from the wheel (see below).
Both are set in the Add / Edit dialog.

## Wheel on the symbol bar row

Over the indicator's row in the symbol bar (the same place the wheel
moves a normal symbol vertically):

- plain wheel - rescale the bottom bars, 1.25 per notch, 0.25x to 200x.
  Bars are not clipped to the band, so scaled-up bars grow over the
  chart above. The new value is saved to the config right away
  (`VolumeBarScale`, default 1), so a restart opens with the same bar
  height. The Add / Edit dialog has no field for it, but it carries the
  value over, so editing the indicator does not reset the zoom.
- `Ctrl` + wheel - rescale the right-edge profile (see below).
- `Alt` + wheel - step the group through 1, 2, 3, 5, 10, 15, 30, 60,
  120, 240, 480, 720, 1440 minutes. The new value is saved to the
  config right away. Nothing happens when the indicator is locked.

The plain and the `Alt` wheel also work over the chart itself, in the
bottom 25 px of the panel's own band - from the row the bars stand on
up to 25 px above it. So the bars can be rescaled and the group
stepped without moving the mouse to the symbol bar. That strip wins
over the chart's own wheel meaning, including the `Alt` rotation of
the tilted grid; `Ctrl` and `Shift` there keep their chart meaning.

The row shows the current group and a `*` when locked, e.g.
`EU Volume 15m*: 12,345`, followed by the column's aggressor delta
(ask minus bid) when the profile covers it, e.g. `12,345  +1,230`.

The bar measures its width from a fixed-width placeholder value, not
from the number currently under the cursor, so the panel never
resizes while the mouse moves. Over the chart itself the wheel keeps its
usual zoom meaning.

Since the panel reads the source series, it is empty where the source
history is not loaded or the source is toggled off (same gap as the
Spread panel).

## Right-edge profile

The right edge uses the same 120 px overlay the Density indicator
draws in, weighted by volume: how much volume changed hands at each
pip level. Two profiles sit on top of each other:

- the lookback profile, translucent with a solid edge, over the
  window picked by keys 1-9 (0 = all history) - this is what the old
  "Volume profile" indicator drew;
- the candle profile, opaque, over the group under the cursor only.

Both use the same scale, so the candle profile is nested inside the
lookback one - a mini profile inside the profile. A level with volume
is at least 1 px wide, so a 15-minute group against a 5-day window
still shows as a thin sliver marking where that candle traded.

That scale starts as "the lookback maximum fills 120 px" and keeps
following the data until the first `Ctrl` + wheel over the indicator's
row in the symbol bar. From then on it is pinned: the same number of
contracts is the same bar length whatever the window or the cursor
does, and only the wheel moves it (1.25 per notch, no practical
limit). The pinned value is not printed on the chart; it is saved to
the config (`DensityScalePerPixel`, the same field the Density
indicator uses, see "Scale" in docs/price-density.md) and shown in the
`Scale` box of the Add / Edit dialog.

The profile label shows the window and the group instead, e.g.
`EUR-Volume 3: 5d | 15m`, with a `*` after the group when it is
locked - the same text the symbol bar row uses.
Bars are not clipped to the 120 px band - they grow left across the
chart, like the bottom bars grow up.

### The candle

The candle is the group (`VolumeGroupMinutes`, epoch aligned) under
the cursor while it is wider than one chart column, and the column's
own range once the chart is zoomed out past the group. In that
zoomed-out case it covers more than the bottom bar does, because the
bar shows only the tallest group of the column (see "Bar height"). Its height is the min..max of the prices
in that block, and each pip level inside is one horizontal rectangle
pushed against the right edge, as wide as the volume that traded
there.

Per-pip volume is split the same way in both profiles
(`DensityProfile.Weighted`): a minute's volume is spread evenly over
the pip levels between its Min and Max, in doubles. A minute with no
known volume adds nothing, so a group outside the filled coverage
draws nothing. Minutes covered by the volume-at-price store
(docs/volume-at-price.md) use their real per-pip distribution instead
of the even spread, in both profiles.

### Windows

The nine lookback options are the shared `DensityPeriods` /
`DensityUnits` / `DensitySelected` fields, edited in the same block of
the Add / Edit dialog as the group. Keys 1-9/0 switch the window for
every Density and Volume indicator at once and the choice is saved to
the config.

While a chart selection is active (Shift + drag), the lookback
profile is built from the selected minutes instead (see "Selection
mode" in docs/price-density.md) and the candle profile is skipped -
the selection already is the window.

## Live filling inside the app (v3)

The console tool cannot run while FXViewer is open: it writes the year
files directly and the app holds them with `FileShare.Read`. So live
volume moved into the app, where the writes go through the handles
`CandleDatabase` already owns and nothing is locked.

`ScidReader` is the shared `.scid` parser. `ReadMinuteVolumes` starts
at a byte offset instead of the header, so each pass reads only the
records Sierra appended since the last one. It returns the new offset,
and restarts from the header when the file got shorter or the offset
no longer lands on a record boundary.

`VolumeCollector` polls every 30 seconds:

- the front contract comes from the calendar - the quarterly month
  (H/M/U/Z) whose roll date, 8 days before the third Wednesday, is
  still ahead. On 2026-08-18 that is `6EU26-CME.scid`. A switch is
  logged, and it resets the offset;
- records are accumulated per minute in memory, because the newest
  minute is still growing. Every poll rewrites the minutes it holds
  and only drops a minute once it is 3 minutes behind the newest one,
  so a partial minute is corrected rather than left short;
- a minute whose candle is not in the DB yet is KEPT and retried on
  every poll until it lands, or until it is 48 hours behind the newest
  minute (v4). This is what a sleeping machine needs: on wake the
  collector reads the whole gap out of the `.scid` seconds after the
  reconnect, while the missing bars are still being downloaded from
  the broker, and `WriteVolume` refuses a minute that has no candle.
  Before v4 those minutes were dropped after one try, so a night of
  volume was lost until the next app start. A pass that writes nothing
  but is still waiting logs the count at most once per 5 minutes;
- a minute is written only when its value actually changed, so a quiet
  market costs nothing;
- the first pass reads the whole file but writes only the last 48
  hours. Deeper history stays the console tool's job.

Writes go through `CandleDatabase.WriteVolume`, which still refuses a
minute with no candle. The same `!_dbBusy && _historyCts == null`
guard the live writer uses is checked before every write, and a poll
that hits it keeps its accumulator so the next one retries.

`MainWindow.ApplyVolumeWrites` folds the written minutes back into the
loaded series: the affected range is rebuilt through
`CandleHistory.WithReplacedRange`, which recomputes `VolumeSum` on all
four rollup levels, then `ChartView.PatchSeriesHistory` swaps it in.
That method exists because `ReplaceSeries` cancels a pivot drag and
hides the hover readout, which is wrong to do every 30 seconds; a
volume-only change touches no price, so it only rebuilds the raster.

The Sierra data folder is `SierraDataFolder` in the config. Empty
means read `C:\SierraChart\DataFilesFolder.txt`, and if that is
missing, `C:\SierraChart\Data`.

## Not in v3

- Sierra must be running, otherwise nothing is appended to read. Same
  dependency the order book collector has on its own poll. It must
  also hold an open chart for that contract: Sierra only keeps the
  `.scid` of a symbol it is charting up to date. On 2026-08-19 only
  6E and 6B had one, so the other five pairs stopped getting volume
  on 2026-08-14 while their candles kept arriving. The collector logs
  no error for this - the file exists, it just never grows.
- The front contract is picked by the calendar, not by comparing
  volume between contracts the way the historical fill does. During a
  roll week the log line shows which one was chosen.
- A redownload that rewrites a `.scid` in place without changing its
  length is not detected and would double count. A restart clears it.
- Deeper history than 48 hours still comes from the console tool.

## Not in v5

- The right-edge candle profile still covers the whole column when the
  chart is zoomed out past the group, while the bottom bar shows only
  the peak group of that column.
- `VolumeBarUnit` is pinned from the first render that has data, so
  the very first look at a brand new indicator still fits the window.

## Not in v4

- The candle profile has no numeric readout, only bar lengths.
- Volume is split evenly over a minute's pip range where the
  volume-at-price store has no data (docs/volume-at-price.md); a 1m
  OHLCV file has no distribution inside the minute.
- Pips of the candle range whose minutes carry no volume stay empty,
  so a candle can look shorter than the price bar next to it.
