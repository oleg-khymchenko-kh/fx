# Live candles in the database

## Problem

Live spot prices from cTrader were kept only in memory (`MainWindow.LiveState.Closed`) and drawn
through `Chart.SetLiveTail`. Nothing wrote them to the candle DB. Computed symbols (USD index,
currency index, moving average, zigzag) read only the DB, so every minute that arrived over the live
stream was missing from them. After the app had been running for hours, "Compute derived" still built
the index from data as old as the moment of connect.

## Solution

Every closed live minute is written to the DB right away and marked as low quality. Once a minute the
app downloads the proper M1 bars from the broker for the range that is still low quality and
overwrites those rows with the real ones.

## Storage

`CandleYearFile` record stays 16 bytes. A new flag bit is used:

```
FlagFilled       = 1 << 0
FlagAvgApprox    = 1 << 1
(bits 2 and 3 unused, they held the old ZigZag pivot flags)
FlagProvisional  = 1 << 4   new
```

Old files open unchanged and read bit 4 as `0`, which means "final". No migration, no format version
bump. The four offline tools under `src/` mask only `FlagFilled`, so they keep working.

`CandleYearFile.Write` rebuilds the whole flags word and writes the whole record blind at the minute
position. That means a normal history write (which leaves `provisional` at its default `false`) is
also the clearing path. There is no separate "clear the bit" step.

## Watermark

Per symbol, two minute-aligned unix values in `data/<SYMBOL>/live.json`:

- `ConfirmedToUnix` - every minute at or before this one has been covered by a broker fetch. Only
  grows. `0` means the symbol has never had an open range.
- `LastWrittenUnix` - the newest minute this app recorded from ticks.

The open range is `(ConfirmedToUnix, LastWrittenUnix]`. It is non empty exactly when
`LastWrittenUnix > ConfirmedToUnix`.

The truth of record is the `FlagProvisional` bit on the candles. The side-car is only a fast cursor.
If it is missing or broken, `LiveDbWriter.ReadMark` rebuilds the pair by scanning the flag bit over
the last 14 days.

The side-car is written with the same tmp file plus atomic move as `FindStore`, so a kill cannot
leave a half written file.

## Writing a live minute

`FeedLive` closes a minute when the first tick of a later minute arrives. At that moment:

- `avg` is `(open + high + low + close) / 4`, the same formula `HistoryDownloader` uses. Before this
  feature the live candle put the close in the `Avg` slot, which would have made a visible step at
  the seam between live rows and broker rows.
- Values are raw broker points, not scaled and not mirrored. `TransformLivePoint` is display only.
- The minute is dropped if it is at or before `ConfirmedToUnix`. In a quiet market a minute can be
  closed many minutes late, by which time the repair job may already have replaced it. Without this
  guard a late tick would downgrade a good broker row back to provisional.
- The DB write is skipped while `_dbBusy` is set or a history download is running, but the watermark
  is extended anyway. The repair job then downloads the real bar for that minute, so nothing is lost.
  Extending the watermark is required: a skipped minute that sits before `LastFilledMinuteUtc` would
  otherwise never be requested again by any existing download path.

## Repair job

A `DispatcherTimer` fires every 60 seconds and runs `RepairProvisionalAsync`. It also runs once
inside `ConnectAndStreamAsync`, before the tail download, so the range left by the previous session is
closed before `DownloadTailToDbAsync` picks its start from `LastFilledMinuteUtc`.

For each symbol with an open range:

```
nowMinute = now rounded down to a minute
from = ConfirmedToUnix + 60
floor = nowMinute - 14 days
if from < floor: log once, from = floor, ConfirmedToUnix = floor - 60
to = min(LastWrittenUnix + 60, nowMinute - 2 min)
to = min(to, from + 1440 min)
if to <= from: skip this symbol
```

`to` is exclusive. `HistoryDownloader.DownloadRangeToDbAsync` fetches `[from, to)` through the
existing `DownloadRangeAsync`, which already skips the still open minute and already overwrites rows
blind through `db.WriteMinute`. It flushes the symbol before returning.

On success `ConfirmedToUnix` moves to `to - 60`, always, no matter how many bars came back. That is
what makes a stuck cursor impossible. After 10 failures in a row for one symbol the cursor is moved
anyway and the rows stay provisional, so a permanently broken symbol cannot grow its request range
forever.

Cost in the steady state: one request per symbol per minute, 7 requests a minute, roughly 0.12 req/s
against a 5 req/s limit. Over a weekend it is zero requests, because `LastWrittenUnix` stops moving
and the range closes.

## Edge cases

- **No bar returned for a minute.** Not an error. cTrader creates a trendbar only when ticks arrive,
  so a tickless minute genuinely has no bar. The row keeps its provisional bit, which is the honest
  record: derived from our ticks, never confirmed by the broker.
- **Weekend.** No ticks, so no new provisional minutes, so no requests at all.
- **Restart with provisional rows.** `live.json` survives. The connect path repairs before the tail
  download.
- **Reconnect after a drop.** The watermarks live on `LiveDbWriter`, not on `LiveState`, so
  `StopLive` clearing `_live` does not lose them.
- **Range crossing a year boundary.** Nothing special. Both values are plain unix seconds, and every
  read and write path already picks the year file from the timestamp.
- **Offline for weeks.** The open range is clamped to 14 days and catches up at one day per pass.
  Anything older keeps its provisional bit and needs "Download history 2010+".

## Computed symbols

Computed symbols do read provisional minutes. That is the point of the feature.

Averages have no storage anymore: the line is computed in memory from the parent series
(see docs/moving-average.md), so it follows the live tail and needs no repair pass.
Everything else (USD index, currency index, zigzag) still needs a manual recompute.

The one real danger is the cumulative index level. `DollarIndexSymbol.Refresh` and
`CurrencyIndexSymbol.Refresh` read their running state back out of the DB at the last written minute.
If that minute's source value is later replaced by the broker bar, the level is off by that log
return forever and only a full `Generate` fixes it. So the Refresh button clamps its end to the
smallest `ConfirmedToUnix` among the source pairs that have an open range.
ZigZag has no Refresh at all, only a full rebuild.

## Not done

- The chart is not spliced when a range is repaired. `CandleHistory.Minutes` is a one time snapshot
  and the lazy loader never re-reads a year it already covers, so corrected highs and lows appear
  only after the next full chart load. Until then the newest candles show the tick derived, slightly
  narrower high and low.
- Only Averages follow the data automatically (they are computed in memory from the parent).
  Zigzag must stay manual because its only update path is a full rebuild, which would wipe hand
  edited points. The two index types must stay manual because a full rebuild is expensive and their
  Refresh is already clamped to the confirmed watermark.
- The Verify DB button does not report provisional counts.
