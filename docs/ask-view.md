# Ask instead of bid

Status: implemented, v1.

## Goal

The DB stores bid prices. A buy is filled at the ask, so a chart drawn
from bid is always on the wrong side of the spread for a long entry. One
global setting draws every pair at its ask price instead, using the
spread that each minute already carries (docs/spread.md).

## The rule

    ask = bid + spread of that minute

- The stored spread is the **maximum** inside the minute, so the shift is
  that minute's worst case, not a per tick value. Low, high and average
  move by the same amount, so the candle keeps its shape.
- A minute with **no stored spread counts as missing**, exactly like a
  minute that was never downloaded. No spread means "unknown", and an
  unknown ask cannot be drawn.
- The spread byte is in tenths of a pip, so the shift in stored points is
  `tenths * PipPoints / 10`. It follows the pair scale: on EURUSD one
  tenth is 1 point, on USDJPY 100 points, on GER40 10 points.

What to expect: history older than the spread data is **empty** in this
mode. The backfill covers only a recent window (docs/spread.md), so with
the setting on the chart shows just the minutes with a measured spread.

## Where it applies

Only base pairs are converted - the list in `MainWindow.SymbolConfigs`,
registered once at startup by `AskViewRule.SetPairs`. Everything else
(derived symbols, indexes, drawings, panels) is not in the list and is
read unchanged.

The conversion sits on the two chart read paths and on the live tail:

- the startup load and `SeriesDataLoader.LoadPartAsync` call
  `AskViewRule.ToAsk` right after the DB read, before the wide spread
  split and before the pip / mirror / shift transform;
- `PushLiveTail` moves each live minute by its own max spread and drops a
  minute that has no spread at all;
- the last tick line uses the spread of the current tick and falls back
  to the running maximum of the open minute; with no spread known at all
  it stays at bid.

Everything downstream follows, because it all reads the loaded series:
highs and lows, the price scale, selection pips, moving averages,
density, find dialogs.

## What it does not touch

The file on disk is always bid. The setting is a view, so:

- every write stays bid: CSV import, trendbar download, live writer,
  spread backfill, volume fill;
- `SpreadBackfill` and `VolumeCollector` read the DB directly and still
  see every minute, so a minute without a spread is still fillable -
  which is what keeps the setting from freezing its own data source;
- indicators with storage (ZigZag, Entry points, Price age, Currency
  index) are computed from bid and stay bid, even when recomputed while
  the setting is on. They are drawn over an ask series, so they sit below
  the price by the spread of the minute - under a pip in normal hours;
- drawings, notes and zigzag edits keep their stored prices and do not
  move with the setting;
- the volume at price store (docs/volume-at-price.md) and the order book
  levels (docs/order-book.md) are bid based as well;
- a minute dropped for having no spread takes its volume with it, so the
  Volume panel has holes in this mode.

## The setting

Settings (the toolbar gear) has one checkbox, **Show ask instead of
bid**, stored in `AppConfig.ShowAsk` and mirrored into the static
`AskViewRule.Show` at startup. Toggling it reloads the chart at once, and
the window title becomes `FXViewer - ASK`, so the mode is still visible
after a restart.

It stacks with "Hide wide spread minutes" (docs/wide-spread.md): a wide
minute is hidden first, and its spread still reaches the Spread panel as
a mark.

## Not in v1

- Global only, no per pair switch.
- One side at a time: no mid line and no bid/ask band.
- No half spread (mid) option.
- Nothing writes ask candles; the DB keeps one price series.
