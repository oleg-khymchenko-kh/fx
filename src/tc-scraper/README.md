# tc-scraper

Downloads the Trading Central signals that FxPro Direct keeps (about the
last 45 days) by walking the article ids one by one.

The API `client-api-global.fxpro.technology` needs no login. Node
`fetch` with browser-like headers gets 200 (PowerShell
`Invoke-WebRequest` gets a Cloudflare 403). No packages are needed.

## Run

    cd src/tc-scraper
    node scrape.mjs

Daily run, new ids only, and import of the new chart pairs into the
FXViewer base:

    node scrape.mjs --import

Every id that answered once is in `index.jsonl` and is never asked
again. The run starts at `state.next` minus `--recheck` ids (default
300), because ids at the edge can still be empty (404) at the time of a
run and get a signal later; only the empty ones are asked again. The run
ends at the newest id of the list API.

`--import` takes the new Intraday signals of EURUSD, GBPUSD, USDCHF,
USDJPY, USDCAD, AUDUSD, NZDUSD, EURGBP, writes them to
`trading-central/mail/incoming-signals-scrape-<stamp>.json` with
`readAt` = publication time and runs `Fx.TradingCentral signal` on it.
Signals already in the base are skipped by the tool.

Options:

- `--from <id>` first id, default 15270000 (about 2026-08-16) or the
  saved progress minus `--recheck`.
- `--to <id>` last id (not included), default the newest id + 1.
- `--recheck <n>` ids before the saved progress to ask again if they
  were empty, default 300.
- `--import` import the new signals of the chart pairs.
- `--concurrency <n>` parallel requests, default 4.
- `--batch <n>` ids per batch, default 100.
- `--pause <ms>` pause between batches, default 300.
- `--out <dir>` output folder.

A stopped run continues from `state.json` when started again.

## Output

Folder `FXViewer/bin/Debug/net10.0-windows/data/trading-central/api-history`:

- `forex.jsonl` - one full signal per line (levels, preference,
  alternative scenario, comment, chart links), Forex only.
- `index.jsonl` - every id that answered: id, symbol, category, term,
  language, time.
- `state.json` - the next id to read.

When the same ids keep failing (403, 429, network) after 6 tries, the
tool saves the progress and stops with exit code 2.
