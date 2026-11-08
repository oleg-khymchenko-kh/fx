# Retail sentiment history (Myfxbook)

Status: v2, collector tool plus the Sentiment indicator in FXViewer
(2026-10-25).

## Goal

Keep a history of how many retail traders are long and short on a
pair, as a series next to the price. Two things at once: the deepest
history that can be had for free, and a continuous minute series from
now on.

## Why Myfxbook

The percent shown in the FxPro web terminal ("42% of trading accounts
have open sell positions on this symbol") is cTrader's Market
Sentiment, cross-broker data of all Spotware servers. cTrader's
protocol has a history request (`ProtoCHMarketSentimentHistoryReq`),
but the servers answer `UNSUPPORTED_MESSAGE` to it, for a guest and
for a logged-in live account alike (checked 2026-10-16). So cTrader can
only give the current value.

Free history exists at Myfxbook Community Outlook. It is a different
crowd: accounts connected to Myfxbook, about 41 000 open EURUSD
positions on 2026-10-16. Myfxbook showed 81% long by lots and 69% long
by positions when cTrader showed 58% buyers, so the two series must
never be mixed. Minute-level history is sold only by FXCM Pro (SSI,
since 2009, price on request) and resold by TraderMade.

## Where the data comes from

`POST https://www.myfxbook.com/outlook-data.json?type=0&symbolOid=<oid>&timeFrame=<minutes>`
with the header `X-Requested-With: XMLHttpRequest`. No login. `type`
0 and 1 return the same rows. The answer is

    {"data": [[time ms, open, high, low, close, shortLots, longLots, shortPositions, longPositions], ...],
     "startDate": ..., "endDate": ...}

as strings; edge rows carry `"undefined"` and are skipped. The OHLC is
Myfxbook's own price feed and is not stored, our candles are the
price.

Symbol ids: EURUSD 1, GBPUSD 2, USDJPY 3, USDCAD 5, AUDUSD 11, EURGBP
17, NZDUSD 28, USDCHF 29, XAUUSD 51.

The depth is a fixed window of about 750-1000 rows per timeframe,
whatever dates are asked for (`startDate`, `from`, `start` are
ignored). Measured for EURUSD on 2026-10-16:

| timeFrame | first row | rows |
|---|---|---|
| 43200 (mn1) | 2017-02-01 | 107 |
| 10080 (w1) | 2018-10-28 | 411 |
| 1440 (d1) | 2024-01-22 | 808 |
| 240 (h4) | 2026-05-01 | 734 |
| 60 (h1) | 2026-09-03 | 726 |
| 30 (m30) | 2026-09-20 | 755 |
| 5 (m5) | 2026-10-12 | 987 |
| 1 (m1) | about 16 hours | ~1000 |

**Rate limit.** After roughly 15 requests in a few minutes the server
answers `429` with "Max requests reached. please purchase Outlook
Indicator to increase limits". The limit is per IP (a `curl` from the
shell got the same answer as the browser) and did not reset within 40
minutes, so it is a daily budget or close to it. The paid "Sentiment
Data" plan ($50 a month, $500 a year) lifts the window to 10 000 rows
per timeframe, adds an API with 2 880 requests a day and 15 second
updates.

The free budget decides the design: the minute series needs one m1
request per pair every 8-12 hours (the window is 16 hours), and the
old timeframes are fetched once, one missing file per run, so a
backfill of three pairs spreads over a few days by itself.

## Storage

Per symbol, next to the candles:

    <exe>/data/EURUSD/sentiment/myfxbook/m1.csv
    <exe>/data/EURUSD/sentiment/myfxbook/h1.csv ... mn1.csv
    <exe>/data/EURUSD/sentiment/myfxbook/raw/m1-1760590000.json.gz

One CSV per timeframe, header
`time,short_lots,long_lots,short_positions,long_positions`, `time` is
the bar start in unix seconds UTC, rows sorted, one row per bar. A new
fetch is merged by time: a row with the same time is overwritten (the
newest bar changes until it closes), nothing is ever deleted. The file
is written to `.tmp` and moved over the old one. Every raw answer is
kept gzipped under `raw/`, named by timeframe and fetch time, so the
series can be rebuilt if the parser changes.

`SentimentPoint.LongShareByLots` and `LongShareByPositions` give the
two percentages; the store keeps the raw counts so either can be drawn.

## The tool

`src/Fx.Sentiment` is a console tool (net10, links
`FXViewer/Storage/SentimentStore.cs`).

    Fx.Sentiment fetch --tf m1[,h1,...] [--symbols EURUSD,GBPUSD,USDCHF] [--data <folder>] [--pause <sec>]
    Fx.Sentiment backfill [--symbols ...] [--force]
    Fx.Sentiment collect [--symbols ...]
    Fx.Sentiment status [--symbols ...]

- `fetch` asks for the given timeframes of the given symbols.
- `backfill` asks m1 for every symbol, then every timeframe whose
  file does not exist yet, in the order d1, w1, mn1, h4, h1, m30, m15,
  m5 (deepest history first). `--force` refetches existing files too.
- `collect` is the scheduled run: m1 for every symbol plus ONE missing
  backfill file, so a fresh install completes its backfill over the
  first days without ever touching the limit twice in a run.
- `status` prints rows, range, the last percentages and the gaps of
  every series.

Requests are 20 seconds apart (`--pause`). On `429` the run stops at
once, logs which jobs were not done and exits with code 3; the next
run continues from the same state because every job is "fetch and
merge". Exit codes: 0 ok, 1 error, 3 rate limited.

Every request is logged to `<exe>/data/sentiment-myfxbook.log`.

Default symbols are EURUSD, GBPUSD and USDCHF; the default data folder
is `FXViewer/bin/Debug/net10.0-windows/data`.

**Transport is curl.exe, not HttpClient.** Cloudflare in front of
Myfxbook answers `403` with a challenge page to .NET's `HttpClient`
whatever headers it sends (checked 2026-10-16 with plain headers, no
compression, HTTP/1.1), while `curl` with the same headers gets
through to the application. The difference is the TLS fingerprint, not
the headers. So the tool runs `C:\Windows\System32\curl.exe` (part of
Windows) with `-A`, `Accept`, `Accept-Language`, `X-Requested-With`,
`Origin`, `Referer` and an empty POST body, and reads the status from
`-w %{http_code}`. Node's `fetch` is blocked the same way.

## Scheduled run

A Windows scheduled task "FX Sentiment Myfxbook" runs `Fx.Sentiment
collect` every 8 hours (00:20, 08:20, 16:20 local, `StartWhenAvailable`
so a missed slot runs when the PC wakes). Three runs are 9 m1 fetches
plus up to 3 backfill files a day, 12 requests, under the burst that
triggered the limit. Two m1 fetches a day per pair would already cover
the 16 hour window; the third is margin for a missed run. If the log
shows `429` on the m1 jobs, lengthen the interval before anything else.

## Sentiment indicator in FXViewer

Indicator type `Sentiment`, source = a pair. It draws the Myfxbook long
share as a bottom panel, like Spread, and reads the CSV files of
`data/<SOURCE>/sentiment/myfxbook/` directly. No storage, no
parameters: `HasStorage` is false, so Recompute skips it, the Edit
dialog has no Refresh button, and delete removes nothing from disk. Two
colors: the main color is the long majority, the "Short color" (the
sell color slot of the indicator) is the short majority.

### One series from nine files

`SentimentSeries.Build` (`FXViewer/Chart/SentimentSeries.cs`) merges
the timeframes into one step function. Every row of every file becomes
a point with its bar length; a point is dropped when a point of a
finer timeframe starts inside its bar. So m1 wins where m1 exists, m5
and m15 fill the days before the m1 window, m30 and h1 the weeks
before that, h4 the months, then d1, w1 and mn1. A hole in a fine
series is filled by the coarser one automatically, because the coarse
point is dropped only if a finer point really starts inside it.

Each sample is in effect from its bar start until the next sample
starts, but at most `CarryBars` = 4 bar lengths: an m1 sample dies
after 4 minutes, an h1 sample after 4 hours, a d1 sample after 4 days.
So the weekend after the last Friday minute draws nothing, and a day
without data in the collector shows as a gap instead of a frozen value.

Measured on EURUSD on 2026-10-25: 10 875 samples, m1 for the last 8
days, then m15 4 days, m30 2 weeks, h1 3 weeks, h4 4 months, d1 from
2024-01, w1 from 2018-10, mn1 from 2017-02. The m5 file is fully
covered by m1 and contributes nothing yet; it will matter once the m1
window moves on.

### Panel

The one parameter is **Height, px** (`SentimentHeightPx`, default 41,
allowed 11 to 1001): the whole panel height, stacked with the other
bottom panels above the bottom margin. A gray line at 50% sits in the
middle, (height - 1) / 2 px above and below, so an even height loses
one pixel. A column shows the sample in effect at the end of the
column (the last sample that starts before the column's right edge). A
bar goes up from the middle when the long share by lots is above 50%,
in the long color, and down when below, in the short color; the half
height stands for 50 points of share, so with the default 41 px an 80%
long reading is a 12 px bar up and with 201 px it is 60 px. A 1 px
dark gray dot per column marks the long share by positions on the same
scale, so the gap between lots and positions is visible. A column with
no sample in effect draws only the middle line. A panel taller than
the chart is cut at the top.

Cursor readout: the symbol bar row of a Sentiment indicator shows
"EURUSD Sentiment: 84% / 75%", lots first, positions second, for the
hovered column; "-" on a column without data. When the cursor is off
the chart the row shows the newest sample. The values travel through
the regular `CursorPricesChanged` arrays like the Spread and Volume
rows; `ToTruePrices` skips sentiment rows.

### Reload

`MainWindow.Sentiment.cs` watches `data` recursively for `*.csv`
changes in a `sentiment` subfolder (the collector writes `.tmp` and
moves it over the CSV). Two seconds after the last event every
Sentiment indicator reloads its folder; `CachedStore` keyed by the
folder stamp returns the same series instance when nothing changed, so
an untouched symbol is not redrawn. The log line
`EURUSD Sentiment: 10,875 sentiment sample(s) for EURUSD (1m, 15m, ...) last ... UTC 84% long by lots, 75% by positions`
is written at chart load and `(reloaded)` after a file change.

## Big players indicator

Indicator type `BigPlayers` ("Big players" in the Add dialog), source =
a pair, data = the same Myfxbook CSV files. It shows when and where
large accounts opened or closed positions. Parameters: **Min lots**
(`BigMinLots`, default 10) and **Height, px** (shared `SentimentHeightPx`,
default 41). Colors: the main color is buying, the sell color is
selling. No storage.

### Why not the lots per position ratio

Two neighbouring minutes give four deltas: long lots, long positions,
short lots, short positions. A "few positions, many lots" minute in the
pure form does not exist in the data (0 of 8 156 GBPUSD minutes had
|d positions| <= 3 with |d lots| >= 20): a large account is always
mixed into the same minute with dozens of small opens and closes. So
the ratio of the deltas is useless, and the indicator measures the
**excess lots** instead, per side and per minute pair:

    typical = side lots / side positions of the previous minute (about 0.30 lots on GBPUSD, 0.49 on EURUSD)
    excess  = |d lots| - |d positions| * typical
    event   = excess, when excess >= Min lots; its sign is the sign of d lots

Lots that the position count cannot explain at the crowd's typical
size were moved by someone big. Two guards (`BigPlayersSeries`):

- a minute whose side changed by 100 positions or more
  (`MassPositionsPerMinute` times the step) is skipped: those are
  Myfxbook account syncs (712 EURUSD longs vanished at Monday 00:01),
  not trades;
- the magnitude is floored at zero and the sign comes from the lots,
  so a swarm of tiny grid positions closing (-267 positions, -30 lots)
  does not turn into a fake large buy.

Mapping to market flow: a large long opened or a large short closed is
**buying**; a large long closed or a large short opened is
**selling**. Each event keeps the four parts (buy open, buy close,
sell open, sell close).

Events come only from steps of 60 minutes or finer (`MaxStepMinutes`):
on 4-hour, daily and weekly bars the deltas are population drift
(10 000+ lots a day) and the method says nothing, so older history is
empty. On 2026-10-25 GBPUSD had 159 minute events at 10 lots or more
over 8 days, 20 a day; the biggest was a 125-lot long closed on
Tuesday 20 October at 12:43, the day the sentiment flipped.

### Panel

Same height rules as Sentiment, zero line in the middle. Per column:
bar up = lots bought by large accounts, bar down = lots sold, scaled so
the largest column in view fills the half height. The part next to the
zero line in the full color is positions **opened**, the lighter part
(55% toward white) is positions **closed**: fresh money and
profit-taking read differently. Columns are summed per pixel column
when zoomed out.

### Right profile

Like the order book profile: for every event in the visible time range
(or the selection, when one exists) the source pair's minute price at
the event's time (`OrderBookProfile.PricePointsAt`, at most one hour
old) puts its lots into a per-pip histogram, buys and sells
separately, drawn from the right edge with the smaller side over the
larger one, up to 120 px for the biggest level. The label above reads
"NAME: visible" or "NAME: selection". A play cut hides events after the
play time.

Cursor readout: "GBP-Big: +12 / -125" is lots bought / lots sold in
the hovered column, "-" where the column has no event.

## Paid plan (decided 2026-10-25)

The user buys the Myfxbook "Sentiment Data" plan
(`https://www.myfxbook.com/outlook-indicator`): $50 a month or $500 a
year, personal use, a Myfxbook login is required. Read from the page
on 2026-10-25:

- history window 10 000 points per timeframe instead of 1 000, which
  means about 7 days of m1, 35 days of m5, 104 days of m15, 208 days
  of m30, 14 months of h1, 4.5 years of h4 and all of d1;
- live updates every 15 seconds in the MT4/MT5 indicator;
- API access: up to 2 880 requests per 24 hours.

The official API (`/api`) has no history method. Its only sentiment
call is a snapshot of every symbol at once:

    GET https://www.myfxbook.com/api/login.json?email=...&password=...   -> session (IP-bound, one month)
    GET https://www.myfxbook.com/api/get-community-outlook.json?session=...

    {"symbols": [{"name": "EURUSD", "shortPercentage": 55, "longPercentage": 44,
                  "shortVolume": 1142.58, "longVolume": 905.47,
                  "longPositions": 2932, "shortPositions": 3888, "totalPositions": 2048,
                  "avgShortPrice": 1.3808, "avgLongPrice": 1.4097}, ...],
     "general": {"demoAccountsPercentage": 43, "realAccountsPercentage": 56, ...}}

So the plan for the collector after the purchase:

0. Measure how often the snapshot changes with
   `py src/Fx.Sentiment/probe-api-rate.py --interval 15 --count 90`
   (credentials in `MYFXBOOK_EMAIL` / `MYFXBOOK_PASSWORD` or a saved
   `MYFXBOOK_SESSION`, run during London or New York hours). The free
   budget of 100 requests a day is enough for one run. Myfxbook has no
   tick feed: the free history is 1-minute snapshots, the paid indicator
   updates every 15 seconds, and 2 880 API requests a day is one snapshot
   per 30 seconds, so the probe decides the polling interval.
1. Find out how the paid window is served: most likely the same
   `outlook-data.json` with the site login cookie (the 429 text names
   the Outlook Indicator purchase as the way to lift the limit). Add a
   cookie jar to the curl transport and a login step.
2. Run `backfill --force` once so every CSV gets the 10 000 point
   window; the deep h1 and h4 are the real gain, d1 extends to the
   start of the data.
3. Shorten the scheduled run to once an hour (or less); 3 symbols
   times 9 timeframes an hour is 648 requests a day.
4. Poll `get-community-outlook.json` once a minute into its own file
   per symbol (`sentiment/myfxbook/snapshot.csv`): this adds the
   average entry price of longs and shorts, which the history endpoint
   does not have, and it covers every symbol in one request.

The password must not live in the repo or in config.json. Keep it in
the user's environment or the Windows credential store; the tool keeps
only the session.

## Not done yet

- A live cTrader series as its own symbol (subscribe message 1152 on
  the trading-server websocket works without login).
- The average entry prices of longs and shorts from the paid snapshot
  API, drawn as levels on the pair.
- The exact reset time of the Myfxbook limit; read it from the log
  after a few days.
