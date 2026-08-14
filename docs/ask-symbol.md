# GBPUSD-ASK (ask side of a broker pair)

Status: implemented, v1.

## Goal

A new base pair `GBPUSD-ASK` in the fixed pair list. It shows the ASK
price of GBPUSD, while the normal `GBPUSD` pair keeps showing BID like
every other pair. The two lines together show the spread directly on the
chart.

## Why it is not a normal pair

cTrader has no separate "GBPUSD ask" symbol and `ProtoOAGetTrendbarsReq`
has no bid/ask choice - trendbars are always BID. Historical ASK exists
only as tick data (`ProtoOAGetTickDataReq` with type `ASK`). So the pair
needs its own data path.

## Model

- `SymbolConfigs` gets `("GBPUSD-ASK", green, mirror=false,
  PipPoints=10, PriceDiv=1)` right after GBPUSD.
- `AskSources` maps the app symbol to the broker pair it reads:
  `GBPUSD-ASK -> GBPUSD`. `IsAskSymbol` checks membership. Adding
  another ask pair later is one config line plus one map entry.
- At connect the symbolId is resolved through the broker name from
  `AskSources`, so `_symbolIds["GBPUSD-ASK"]` holds the id of broker
  GBPUSD. Spot subscription sends distinct ids only.
- `_idToSymbol` keeps id -> bid pair; a second map `_idToAskSymbol`
  keeps id -> ask pair. One spot event can feed both: bid goes to
  GBPUSD, ask goes to GBPUSD-ASK.
- The DB folder is `data/GBPUSDASK` (the dash is dropped by the usual
  symbol sanitizer). Prices are stored in the same 1e-5 points as every
  other pair.

## History from ticks

`AskHistoryDownloader` replaces `HistoryDownloader` for ask symbols:

- It pages `GetTickData` (newest first; the first entry carries absolute
  timestamp and price, later entries carry deltas for both fields),
  collects the ticks of a 24 h window, then aggregates them into M1
  rows: min, max, avg = (o+h+l+c)/4 rounded, `avgApprox` set. The
  current minute is never written.
- Windows are minute-aligned, so no minute is split across two windows.
- Rate limit errors are retried with a growing wait, like the deals
  export.
- "Download recent": from the last filled minute, capped at 30 days
  back (`MaxTailDays`). If the DB is empty it seeds the last 14 days
  (`SeedDays`).
- "Download history 2010+": same tail behavior, with a log line saying
  the server has no ask trendbars. There is no deep ask backfill in v1 -
  a full tick download of 15 years is not realistic over the Open API.
- Live repair of provisional minutes uses the same tick range download
  instead of trendbars, so ask minutes are never overwritten with bid
  bars.

## Live minutes

Spot events already carry ask. `OnSpot` feeds the ask points into the
same `FeedLive` path the bid pairs use, so provisional minutes, the live
DB writer, the tail merge and the symbol bar price all work unchanged.

First run bootstrap: a pair with no data on disk gets no series and no
`_baseInfo` entry, so live points would be dropped. Two small fixes make
the seed flow work without a restart:

- "Download recent" also reloads the chart when a symbol that was not in
  `_baseInfo` before got new rows.
- `ReconcileLive` adds `_live` entries for subscribed base symbols that
  appeared in `_baseInfo` after a reload, instead of only updating the
  existing ones.

So the flow is: Connect, then Download recent - the seed arrives, the
chart reloads, GBPUSD-ASK appears in the symbol bar and streams live
from then on.

## Everything else is free

The pair is a normal base symbol, so lazy loading, tabs, notes,
drawings, indicators on top of it (Average, Shift, Density, PriceAge),
align to grid and the symbol bar all work without special cases.

## Not in v1 (next steps)

- Deep ask history backfill from ticks (needs a long paged download with
  its own progress and resume).
- More ask pairs (EURUSD-ASK etc.) - one line in `SymbolConfigs` plus
  one entry in `AskSources` each.
- A derived "spread" series (ask minus bid) as an indicator.
