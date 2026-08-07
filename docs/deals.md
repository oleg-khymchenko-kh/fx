# Deals file and Deals indicator

Status: v1.

## Goal

Two parts:

1. Export the account's real trading history (cTrader Open API) into a
   JSON file.
2. A new indicator type `Deals`: the user picks such a file, binds it to
   a pair (Source), and sees every entry and exit on the chart.

## Deals file

Written by the "Export deals" button on the Connection tab (needs a
live connection). The app pages `ProtoOADealListReq` week by week (the
server caps one request at 7 days), joins deals into closed position
parts and writes `deals\deals-<accountId>.json` next to the exe:

    {
      "Account": 1234567,
      "ExportedAtUnix": 1785309000,
      "Deals": [
        {
          "PositionId": 987,
          "Symbol": "GBPUSD",
          "Side": "Buy",
          "Lots": 0.5,
          "OpenTimeUnix": 1784910240,
          "OpenPrice": 1.33823,
          "CloseTimeUnix": 1784939520,
          "ClosePrice": 1.35549,
          "Profit": 86.30,
          "Pips": 172.6
        }
      ]
    }

One record per closing deal (a partial close makes its own record):
side, lots and close price come from the closing deal, the entry price
from `ClosePositionDetail.entryPrice` (the position's price at that
moment), the entry time from the position's first opening deal.
`Profit` is net: grossProfit + swap + commission, scaled by moneyDigits.
`Pips` is the signed price move in pips of the pair (JPY pairs use
0.01). A position still open at export time gets a record with
`CloseTimeUnix = 0`, `ClosePrice = 0`, `Profit = 0` and open-side pips 0.

The scan starts 3 years back and stops at now. Times are unix seconds
UTC. The file is plain JSON, so records can also be added by hand.

## Generated deals files

The same format is written by the backtester, so a strategy run can be
looked at on the chart next to the real trades. `Fx.MaCross --bands
... --deals <name>` writes the winning (or pinned) combination's trades
there. A bare name lands in the app's own `deals` folder, so it shows
up in the Browse dialog right away:

    Fx.MaCross --bands --variant B --band-ma-min 210 --band-ma-max 210 \
      --band-min 0 --band-max 0 --dist-min 10 --dist-max 10 \
      --sl-min 15 --sl-max 15 --tp-min 45 --tp-max 45 \
      --risk-min 0.5 --risk-max 0.5 --from 2025 --deals test-b-3.5h

What the fields mean for a generated file:

- `PositionId` counts from 1 in time order, there is no real position.
- `OpenPrice` is the band level, which is where the pending order sits.
  `ClosePrice` is the exact stop or target level, not a bar price.
- `Lots` is what the risk rule implies: risk share of the running
  equity divided by the stop in pips times $10 per pip per lot.
- `Profit` is money on a 100,000 deposit with compounding, so later
  trades are bigger. `Pips` is the trade result, plus take profit or
  minus stop loss, spread included.
- `Account` is 0 and every record is closed, so nothing is drawn as an
  open position.

Only band mode writes deals. The crossover strategy opens two opposite
trades per entry and has no single-trade view to export.

## Deals indicator

Type `Deals` in the Add/Edit dialog. Fields: Name, Type, Source (a
pair or index symbol - the series the markers stick to), the deals
file path with a Browse button, and Color. No candle storage, nothing
to compute: like Drawing, the type is skipped by "Recompute
indicators" and has no Refresh.

On chart load the file is read, records are filtered by
`Symbol == NameKey(Source)` and attached to the series as marker data.
A missing file just logs and shows an empty row, like a missing
drawing.

## Rendering

A closed trade is drawn as three things, all in the color of the
result: green when `Profit >= 0`, red when it lost.

- Entry: a small filled triangle (7 px) centered on the entry price at
  the entry minute. Points up for Buy, down for Sell - the shape gives
  the direction.
- Exit: a small filled disc (7 px) at the close price and minute.
- Connector: a 1 px line from entry to exit, blended at ~55% into the
  background so the series lines under it stay readable. Skipped when
  entry and exit land within 2 px horizontally (zoomed far out - the
  two markers alone are enough).

Reading the chart: green rising line = winning buy, green falling
line = winning sell; red = the mirror cases. An open position draws
only the entry triangle in the indicator's own color (no connector,
no disc), so open trades are visually distinct.

Markers live in the coordinate space of the Source series: they apply
its pip scale, mirror flip, per-series offset chain and flatten shift,
so they stay glued to the line when the user drags, mirrors or
flattens it. X uses the same weekend-compressed column mapping as
everything else.

Draw order: after series lines and drawing polylines, before the
calendar dashed lines and entry panels. Hiding the symbol in the
symbol bar hides the markers (normal series visibility).

## Plumbing

- `IndicatorSymbol` gains `DealsFile` (string, persisted in
  config.json). `SameData` for Deals returns true like Drawing/Shift -
  changing the file or source never recomputes anything, OK just saves
  and reloads the chart, which re-reads the file.
- `IndicatorTypes`: `Deals` added to `All`; `HasStorage` false,
  `NeedsSource` true. Source combo offers pairs and Index symbols.
- `DisplayConfigs`: Deals series inherits Mirror and PipPoints from its
  source like a normal indicator, `Editable` false, loader skipped via
  empty `ReadSymbol` (the Drawing pattern), payload carried on
  `SymbolSeries.DealMarks`.
- `ChartRasterizer` gains blended primitives: `BlendSegment`,
  `FillTriangle`, `FillDisc`.

## Not in v1

- Hover tooltip with lots/profit details, click selection.
- Filtering by time range or magic/label.
- Live refresh of the file while the app runs (re-open the chart or
  toggle the symbol to re-read).
