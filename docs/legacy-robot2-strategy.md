# Legacy Robot2 strategy (from the 2015 FXViewer)

Status: implemented in this project as `Fx.MaCross --robot2` (Robot2Sweep.cs),
including the legacy quirks listed below. Differences: spread comes from
`--spread` (legacy hard coded 5.0 pips), data is this project's M1 store, and
the whole grid runs multi-threaded with the loss-filter early abort kept.

Reverse engineered from `job.exe` (FXViewer, Validio Ukraine, built
2015). Decompiled sources: `C:\Users\Oleg\Downloads\Job\job_decompiled`.
This document describes the trading rules and the parameter search that
the old optimizer ran, so the ideas can be reused here without reading
the old code again.

The binary contains two robots, `Robot1` and `Robot2`, but only
`Robot2` is reachable: `Job` accepts a single `batchID` value,
`Robot2_V1`, and throws for anything else. `Robot1` is dead code kept
from an earlier version.

## Summary

Breakout away from a simple moving average, entered on a small
pullback with a limit order. Price must first touch a near level around
the average (this arms the side), then reach a far level (this fires the
signal). The order is then placed a few pips back from the far level and
waits for the retracement. Fixed take profit and stop loss.

## Units and time

All prices are integer pips (1 unit = 0.0001). The sample data file
shipped with the binary is GBPUSD (first bars read 15510, so 1.5510 in
January 2012).

The bar index is trading time, not wall clock time. `FXTime` collapses
weekends: a week runs from Sunday 21:00 to Friday 21:00 and is exactly
7200 minutes long. Index 0 is the `start` date from the job file, and
index N is N trading minutes later. This matches the "no weekends" time
axis used in this project (see `no-weekends.md`).

## Bar format

One bar is 8 bytes, a little endian int64 packed as:

    flags (8 bits) | low (24 bits) | deltaMax (16 bits) | deltaAvg (16 bits)

    high = low + deltaMax
    avg  = low + deltaAvg

There is no open and no close. `_Open_Pips` in the old code is just an
alias for `low`, not a real open price. For downloaded data `avg` is
always the bar midpoint, `(high + low) / 2`.

A bar with `low == 0` means "no data for this minute" and is skipped
everywhere. Data files are named `15-<yyyy-MM-dd>-<barCount>.bin`.

## Indicator

A single simple moving average over the bar midpoints:

    avg(i) = mean of bar.Avg over the last avgPeriod minutes, ending at i

Empty bars are skipped: they do not add to the sum and do not count
toward the divisor, so the window is a count of existing bars, not a
time span. Until `avgPeriod` bars are available the average returns 0
and the robot does nothing for that minute.

`Average` keeps a running sum and requires the index to move forward,
so one instance can only be used for one sequential pass.

## Entry rules

Both sides run independently and can be armed at the same time.

    avg = SMA(index)
    if avg == 0: skip this minute

    if bar touches (avg + diffLevel1):
        armedTop = true
        pendingTop = null
    if bar touches (avg - diffLevel1):
        armedLow = true
        pendingLow = null

    top = avg + diffLevel2
    low = avg - diffLevel2

    if armedTop and bar touches top:
        armedTop = false
        pendingTop = BuyLimit(entry = top - orderStartDelta)

    if armedLow and bar touches low:
        armedLow = false
        pendingLow = SellLimit(entry = low + orderStartDelta)

"Touches" means the level is inside the bar range, or the level sits in
the gap between the previous bar and this one. This covers both a normal
bar and a jump over the level.

`diffLevel1` is searched from -50 to +50, so the arming level can sit on
the other side of the average. With a negative `diffLevel1` the rule
reads as: price must first drop below the average, and only then a rise
to `avg + diffLevel2` gives a buy signal. That is the "cross the average
first, then move away from it" filter.

Re-arming a side drops the pending order on that side without counting
it as cancelled.

## Order lifetime

A pending order is a limit order with a cancel level on the other side:

    buy:   entry = top - orderStartDelta,  cancel = entry + profLimit
    sell:  entry = low + orderStartDelta,  cancel = entry - profLimit

Whichever level the price reaches first wins. Reaching `entry` opens a
transaction, reaching `cancel` drops the order and increments
`CancelledOrders`. The meaning of the cancel level is "the move already
happened without us, do not chase it".

The order is checked on the same bar it was created on, so with
`orderStartDelta = 0` the entry fires immediately.

## Transaction and exit

Fixed brackets around the entry price:

    buy:   takeProfit = entry + profLimit,  stopLoss = entry - lossLimit
    sell:  takeProfit = entry - profLimit,  stopLoss = entry + lossLimit

The transaction closes as soon as a bar reaches one of the two levels.
Profit is `(exitLevel - entry) * amount` where amount is +1 or -1.

Spread is a hard coded constant, charged once per closed transaction:

    profitWithSpread = profit - 5

Several transactions can be open at once. They are held in a linked
list and each one is advanced on every bar. One filter limits this: a
new transaction is dropped if an open transaction with the same
direction already exists within 50 pips of the new entry price. Dropped
ones are counted in `DuplicateTransactions`.

## Result metrics

Collected over the whole pass:

- `Profit`, `ProfitWithSpread` - sum over closed transactions.
- `WorstLossSum` - the deepest drawdown. A running sum of
  `profitWithSpread` is kept, clamped to 0 whenever it turns positive,
  and its minimum is the result.
- `MaxLossCount` - longest run of consecutive losing transactions.
- `ProfCount`, `LossCount`, `CancelledOrders`, `DuplicateTransactions`.

The pass aborts early as soon as `WorstLossSum` drops below the
`lossFilter` job parameter. This is the main speed-up of the search:
hopeless combinations die on the first bars instead of running through
the whole history.

A combination is saved only if both hold:

    ProfitWithSpread >= profFilter
    WorstLossSum     >= lossFilter

The default job template uses `profFilter = 3000` and
`lossFilter = -500`.

The GUI has one more ranking formula that the worker never uses:

    score = min(5000, profitWithSpread) + 2 * worstLossSum

So profit above 5000 gives no advantage and drawdown counts double.

## Parameter grid

Ranges come from the `.rob` job file. The default template is written by
`Processor.CreateJobFile`:

| Parameter | Start | End | Step | Values |
|---|---|---|---|---|
| `avgPeriod` | 1440 | 14400 | 1440 | 10 (1 to 10 days) |
| `diffLevel1` | -50 | 50 | 10 | 11 |
| `diffLevel2` | 0 | 100 | 10 | 11 |
| `lossLimit` | 20 | 100 | 10 | 9 |
| `profLimit` | 50 | 150 | 10 | 11 |
| `orderStartDelta` | 0 | 50 | 10 | 6 |

That is 718740 combinations, and every one is a full pass over the whole
history (1120500 bars in the shipped file). `Range.List` walks the grid
like an odometer: the last range moves fastest, the first one slowest.

## How the search was distributed

`job.exe` is not the chart viewer. `Program.Main` has a hard coded
`if (true)` that makes the WinForms path unreachable and always runs
`Processor.ForegroundMain()`, a console worker:

1. Look for a `.rob` file next to the exe. If there is none, ask the
   SOAP service `JobService.asmx` for one (`/server:<url>` overrides the
   endpoint, machine name is the client id).
2. Read the `.bin` data file, downloading and unzipping it from
   `<server>/Data/<name>.zip` if it is missing.
3. Walk the parameter grid. Every matching combination is written to
   `Result\<batchID>-<paramIds>.rob`.
4. A background thread uploads results and the current grid position
   every 60 seconds. The current position is also written back into the
   local `.rob` file, so a restart continues where it stopped.
5. The server can answer the status upload with anything other than
   `InProgress` to stop and delete the job.

## Known problems

Worth knowing before reusing any of these results.

**Longs are scored optimistically, shorts pessimistically.** The exit
check tests the high level first and the low level only in `else`. For a
buy the high level is the take profit, for a sell it is the stop loss.
So a bar that covers both brackets is always counted as a win for buys
and a loss for sells. With `lossLimit` starting at 20 pips such bars are
common on M1, so the long and short results are not comparable.

**Intrabar order is unknown anyway.** Bars store only low, high and
midpoint. There is no way to tell which bracket was hit first inside a
minute, so any resolution is a guess, not data.

**The progress counter is wrong by about half.** `Range.Count` is
`(End - Start) / Step` with no `+1`, while the loop runs to `End`
inclusive. The reported total is 360000 against the real 718740, so the
"N of M" log passes M and keeps going.

**Spread is fixed at 5 pips.** No session widening, no news widening,
and it is charged once per transaction rather than on entry and exit.
