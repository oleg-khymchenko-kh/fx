# Wide spread minutes

Status: implemented, v1.

## Goal

The hours right after the American close are the worst part of the day:
the spread blows up to 5, 10, sometimes 40 pips, and the price prints
spikes that no one could trade. Those minutes still sit in the DB and
they stretch the price scale, spoil the high and the low of a selection
and add pips that never existed.

So every stored minute now carries one more flag - "wide spread" - and a
global setting decides whether such a minute is shown at all.

## The rule

A minute is wide spread when **both** are true:

- its stored spread is over 4 pips (`WideSpreadRule.MinTenths` = 41
  tenths, so 4.1 pips and up), and
- it falls between the American close and the Asian open.

A minute inside the window that has **no** stored spread is wide too,
but only from `WideSpreadRule.MeasuredFromUnix` (2026-01-01 UTC) on.
That date is where spread collection starts, so a later minute without a
spread is a hole in the collection, not old history: the live writer
missed it and the trendbar download filled the price alone
(`SpreadCodes.Keep` had nothing to keep). Such a minute carries the same
post-close spike as its measured neighbours, so it has to go with them.
Before that date nothing has a spread at all, so nothing is hidden and
years of history stay untouched.

This second branch is limited to the pairs that actually collect a
spread: `WideSpreadRule.SetMeasuredPairs` gets the `SymbolConfigs` names
at startup, the same way `AskViewRule.SetPairs` does. Without it every
computed symbol (a currency index, a price age row, the deals symbol)
would be flagged over its whole evening, because none of them ever
stores a spread.

The window is the real gap between the two sessions, so its length
follows the season. Tokyo has no daylight saving and always opens at
00:00 UTC, while the American close follows US DST exactly like the
session bands (docs/sessions.md): `SessionClock.AmericaCloseHourUtc`
gives 21:00 UTC in summer and 22:00 UTC in winter. So the window is

| Season | UTC window | Length |
|---|---|---|
| summer | 21:00 - 00:00 | 3 h |
| winter | 22:00 - 00:00 | 2 h |

It never crosses midnight, which is why the check is one comparison:
`utc.Hour >= AmericaCloseHourUtc(utc)`.

Weekdays are not checked, so the Sunday evening open falls inside the
window too. That is on purpose: the first minutes of the week carry the
widest spreads of all.

## Storage

No new bytes, no format change: the flag is bit 5 of the flags word at
offset 12 (`CandleYearFile.FlagWideSpread`), next to the spread bits.
See docs/spread.md for the whole word.

The flag is **derived, but stored**. Every write path recomputes it from
the spread it just wrote - or from the fact that it wrote none - and from
the minute's own timestamp:

- `CandleYearFile.Write` - CSV import, trendbar download, ask ticks, the
  live writer (including `SpreadCodes.Keep`, so a repaired minute keeps
  the flag its carried spread deserves);
- `CandleYearFile.WriteSpread` - the tick based spread backfill.

`WriteVolume` only touches the volume bits and leaves the flag alone.

Because it is recomputed on every write, the flag can never drift away
from the spread that is stored next to it.

## Backfill

Minutes written before the flag existed have it clear. The "Backfill
wide spread" button fixes them. It needs no broker connection: the
spread and the timestamp are already in the files.

The run walks every pair of `SymbolConfigs`, every year file, in blocks
of 4096 records (`CandleYearFile.RecomputeWideSpread`), sets or clears
bit 5 and writes a block back only when something changed. Indicator
symbols are not scanned, and they are not in the measured set either, so
they can have no wide minutes.

The log gets one line per pair:
`EURUSD wide spread: 12,345 of 3,000,000 minutes, 12,345 flags changed`,
and a total at the end. Re-running is cheap and safe: the second run
changes nothing.

Because the rule is recomputed, the same button also **repairs** flags
after a spread backfill fills in more minutes, and it clears flags that
should no longer be set.

"Verify DB" prints the share per pair:
`EURUSD wide spread: 12,345 of 3,000,000 minutes (0.41%), hidden`.

## Hiding

Settings (the toolbar gear) has one checkbox, **Hide wide spread
minutes**, stored in `AppConfig.HideWideSpread` and mirrored into the
static `WideSpreadRule.Hide` at startup.

When it is on, a flagged minute is treated as **missing**, exactly like a
minute that was never downloaded:

- `CandleYearFile.ReadRange` and `TryGet` skip the record, so it never
  reaches a series. Every reader is covered by that one place - the
  chart, the lazy loader, the computed indicators, the find dialogs;
- the live tail skips it too (`PushLiveTail`), so the open minute
  disappears while its spread is wide and comes back if the spread
  narrows before the minute closes;
- highs, lows, the price scale, the pips of a selection and everything
  else that reads the loaded series follow automatically, because the
  minute simply is not there.

## The moving averages use a corrected price

The `Average` and `AverageBand` indicators never take the raw price of a
flagged minute, whatever this setting says, so their lines never carry a
post-close spike and look the same with the setting on or off:

- a plain average (no volume) uses the minute with a corrected bid,
  `real bid + (spread - 1 pip) / 2` (with the ask shown: that bid +
  1 pip). A flagged minute without a known spread is skipped;
- a volume weighted average skips the minute.

With the setting on, the hidden minutes still reach the averages: the
load paths keep them in `CandleHistory.HiddenMinutes` and the live tail
in `CandleHistory.LiveHidden`, and an average reads the merged
`AverageMinutes` / `AverageLive`. See docs/moving-average.md, "Wide
spread minutes".

## The Spread panel is the exception

Hiding the widest spreads from the Spread indicator would hide exactly
what that panel exists for, so the spread of a hidden minute is kept and
drawn (docs/spread.md).

The price of such a minute is dropped as everywhere else - only the
spread survives, as a `SpreadMark` (unix second + tenths of a pip):

- the chart load paths read with `includeWide: true`
  (`CandleDatabase.ReadRange`) and split the result in
  `SeriesDataLoader.SplitHidden`: the visible minutes go into the series,
  the hidden ones become marks in `CandleHistory.HiddenSpreads` (and,
  for the moving averages only, full candles in
  `CandleHistory.HiddenMinutes`, see above);
- the live tail does the same into `LiveHiddenSpreads` (and
  `LiveHidden`);
- `SpreadColumns.Build` folds both arrays over the columns it already
  built from the minutes or the rollup blocks, taking the maximum like
  everywhere else. So a column that has nothing but hidden minutes still
  draws its bar, and the cursor readout shows its value.

A minute hidden because it has no spread produces no mark at all - there
is nothing to draw - so the panel simply has a hole there, the same hole
it had before.

The marks carry no prices, so they cannot leak into a high, a low or a
mirror base by accident. `HiddenMinutes` do carry prices, but only the
moving averages read them. `WithReplacedRange` carries both over, which
is what keeps them alive through a volume patch or an average rebuild.

When the setting is off nothing is split: the minutes stay in the series
and the panel reads them the old way.

Toggling the checkbox reloads the chart, so the change is visible at
once.

What the setting does **not** touch: the raw file itself (the record
stays, only the reader skips it), `CountFilled` / `FirstFilledMinute` /
`LastFilledMinute` / `ReadSpreadStats`, which describe the DB and are
used by the download bookkeeping, and the volume at price store
(docs/volume-at-price.md), which has its own per-tick data.

## Not in v1

- The threshold and the window are constants in `WideSpreadRule`, not
  settings.
- The spread era floor (2026-01-01) is a constant too. Minutes older
  than it have no spread at all (see docs/spread.md), so they can never
  be flagged.
- A whole night that the live writer missed is hidden completely once
  the trendbar download fills it, because every one of its minutes lands
  inside the window without a spread. The tick backfill that runs after
  a reconnect (docs/spread.md) brings the narrow minutes back, and so
  does "Backfill spread" for older days; both read the hidden minutes
  through `includeWide: true`.
- An ask pair (docs/ask-symbol.md) is not in the measured set, so its
  own file only gets the flag from a stored spread.
- No visual marker for a wide minute while the setting is off.
