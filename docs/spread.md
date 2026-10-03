# Spread in the candle record

Status: implemented, v1.

## Goal

Every stored minute keeps the spread of that minute, so backtests can
charge a real spread instead of one constant per pair. The value is the
**maximum** spread seen inside the minute, which is the worst case for an
entry.

## Storage

No new bytes: the record stays 16 bytes and the file format version stays
1. The spread lives in the flags word at offset 12.

| Bits | Meaning |
|---|---|
| 0 | `FlagFilled` |
| 1 | `FlagAvgApprox` |
| 2 | `FlagSpread` - the spread byte below is valid |
| 3 | `FlagVolume` - see docs/volume.md |
| 4 | `FlagProvisional` |
| 5 | `FlagWideSpread` - see docs/wide-spread.md |
| 8..15 | packed spread byte |
| 16..31 | volume word - see docs/volume.md |

Bit 2 is what separates "spread is 0.0 pips" from "spread unknown". Old
files have it clear everywhere, so they read back as unknown and stay
readable without any migration.

## Packing (`SpreadCodes`)

One byte covers 0 to 140 pips with two bands:

- `0..127` - tenths of a pip, so 0.0 to 12.7 pips with a 0.1 pip step.
- `128..255` - whole pips, `code - 115`, so 128 = 13 pips, 129 = 14 pips,
  255 = 140 pips.

Anything above 140 pips is stored as 140. A value between 12.7 and 13.0
pips rounds to 13 pips (code 128), because the fine band ends there.

Pips, not points: the conversion uses the symbol `PipPoints`, so 8 points
on EURUSD (`PipPoints` 10) is 0.8 pips, and 30 points on GER40
(`PipPoints` 100) is 0.3 pips.

## Writing

`CandleDatabase.WriteMinute` takes a `spreadCode`:

- a real code (0..255) - write it and set `FlagSpread`;
- `SpreadCodes.Unknown` - clear the spread bits (default, used by the
  computed indicator symbols where spread has no meaning);
- `SpreadCodes.Keep` - read the old record and carry its spread over.

`Keep` is what the base pair paths pass (CSV import, trendbar download,
ask tick download, live writer), so a repaired or re-downloaded minute
never loses a spread that was already measured.

`CandleDatabase.WriteSpread` sets only the spread bits of a minute that
already exists and leaves prices alone. It returns false for a minute
that is not filled, so a backfill can never invent candles.

## Where the spread comes from

**Live.** A spot event carries bid and ask. `TrackQuote` keeps the last
known bid and ask per broker symbol id, so events that carry only one
side still produce a spread. Each live tick converts `ask - bid` into
tenths of a pip and `FeedLive` keeps the maximum for the open minute.
When the minute closes, that maximum is written with the provisional row.

**CSV import.** If the header of a history CSV has a `spread` column, the
value is read as pips and stored. The FxPro export used so far has no
such column, and then the import keeps whatever was there.

**Backfill (`SpreadBackfill`).** For history there is no other source
than ticks: trendbars are bid only, and the stored bid bar plus ask ticks
cannot give a maximum, only an average. So the backfill downloads both
tick streams (`ProtoOAQuoteType.Bid` and `Ask`) for the same window,
merges them by timestamp keeping the last known price of each side, and
takes the maximum `ask - bid` per minute.

## Backfill behavior

The "Backfill spread" button runs over every base pair that is subscribed
(ask pairs are skipped), newest window first, in 24 h windows. The run
covers the last `SpreadBackfillDays` days (7), so the window start moves
with the clock. The broker keeps tick history for years, but downloading
all of it takes hours per pair, and the gaps worth repairing are the
fresh ones left by a dead live feed; older minutes keep whatever spread
they already have. A failure on one pair (like a server timeout) logs and
moves on to the next pair. Rules per window:

- a window whose minutes are all missing from the DB (weekend, gap) or
  already have a spread is skipped without any request, so a second run
  is cheap and the job is resumable;
- after 3 windows in a row where the server returns no ticks the pair
  stops, because that is where the broker tick history ends;
- each window logs tick counts, minutes written and the window maximum;
- the run is cancellable and shares `_historyCts` with the other
  downloads, so it cannot run next to a history download.

The window read passes `includeWide: true`, so with "Hide wide spread
minutes" on the backfill still reaches the minutes the flag hides. It
did not until 2026-09-17: a night the live writer missed was flagged
wide for having no spread, and the reader-level filter then kept those
minutes away from the very job meant to fill them.

## Automatic backfill after a reconnect

Every connect - the startup one and every reconnect after a drop -
writes some minutes from trendbars: the repair of the provisional range
and the tail download in `ConnectAndStreamAsync`. Those minutes have no
spread. Until 2026-09-17 they stayed that way until somebody pressed
"Backfill spread"; a PC that slept through the night left a hole of
hours in every pair (GBPUSD 2026-09-15 20:01..00:30 UTC was the case
that triggered this).

Both bar writers now report the earliest minute they wrote
(`NoteGapSpread`, called from `RepairProvisionalAsync` and from the
tail branch of `DownloadHistoryAsync`; the notes are cleared at the
start of every connect, so the per-minute repairs between two connects
never count). Once the live stream is up and the tail is merged,
`ConnectAndStreamAsync` starts `BackfillGapSpreadAsync` without
awaiting it. The run walks the base pairs in `SymbolConfigs` order:

- the range is `[earliest bar minute, now]`, floored at
  `SpreadBackfillDays` days like the button;
- a range whose minutes all have a spread already is skipped without a
  request and without a log line - a short drop repairs only live
  minutes, and those carry their own spread;
- otherwise it logs `GBPUSD: 258 minutes without spread in the reconnect
  gap 2026-09-15 20:01..2026-09-16 00:31 UTC, fetching ticks` and runs
  the same `SpreadBackfill.RunAsync` as the button, so the per window
  lines and the stop after three empty windows are the same;
- it does not take `_historyCts`: the live writer keeps writing while
  the ticks download, so a minute that closes during the download is not
  handed to the repair path without its spread. The run is skipped with
  one log line when a history download or a `_dbBusy` job is running at
  that moment;
- a drop cancels it (`_gapSpreadCts`), and `DisconnectAsync` waits for
  it the way it waits for the repair task, so a new connect never runs
  next to the old run.

After each pair the minutes of the range are read back
(`includeWide: true`) and patched into memory without a chart reload,
the way the volume collector does it (`PatchSpread`):

- the loaded base series is spliced through `PatchLoadedMinutes`
  (`WithReplacedRange` + `PatchSeriesHistory`, shared with the volume
  patch now);
- the live tail (`LiveState.Closed`) is patched in place through
  `LiveTailPatch.Apply` and pushed again. Minutes of the range that are
  in the DB but not in the tail - the wide-hidden ones the tail merge
  skipped - are inserted by `LiveTailPatch.InsertMissing`, restricted
  to minutes after the base series end and before the open minute, so
  `PushLiveTail` decides anew which of them stay hidden.

Limits: the loaded series is not patched in "Show ask" mode, because
there the prices themselves depend on the spread; a loaded minute whose
new spread crosses the wide threshold keeps its current visibility; and
a minute the per-minute repair fills from a bar in the middle of a
session (a post-close minute the live path never opened) still gets no
spread until "Backfill spread" is pressed. The next chart load settles
all three.

## Checking the result

"Verify DB" prints one line per pair:
`EURUSD spread: 12,345 of 3,000,000 minutes (0.4%), avg 0.8, max 24.0 pips`.

## Spread indicator

A `Spread` indicator type shows the stored spread on the chart as a
bottom panel, like Price age but thinner.

Model: no storage and no parameters, like Density. The panel reads the
source pair's already loaded display series - `HasSpread`/`SpreadCode`
survive `CandleTransforms.Transform` (`with` expressions), so pip
scaling and mirror do not touch them. `HasStorage` is false, so
"Recompute indicators" skips it, the Edit dialog has no Refresh button,
and delete removes nothing from disk.

Panel: 41 px of reserved height (bars + 1 px gray baseline), stacked with the
EntryPoints/PriceAge panels above the bottom margin, bars in the
indicator color, 1 px per column. A column aggregates its minutes by
taking the **maximum** spread; minutes without a spread are ignored, and
a column with no known spread draws no bar. The rollup levels
(`AggBlock`) carry `SpreadMaxTenths` (-1 = none), so zoomed-out views
fold blocks like the other panels instead of rescanning raw minutes.

Bar height follows the current price grid: a bar is as tall as the same
number of pips on the price scale, so a 30 pip spread is exactly as tall
as a 30 pip price move, and a 100 pip grid step is the ruler for it.
The height is `tenths / pointsPerRow` (display points are always 10 per
pip, so tenths of a pip and display points are the same unit), rounded,
minimum 1 px for a known spread so a known 0.0 still shows a tick.

Zooming the price scale therefore rescales the bars. When zoomed in far
enough a bar can be taller than the 41 px panel band and draws over the
chart; it is clipped at the top of the chart. The 41 px is only the space
reserved for stacking the panel, not a cap on the bars.

Cursor readout: the symbol bar row of a Spread indicator shows the
hovered column's spread in pips ("GBP Spread: 3.2") instead of a price;
"-" when the cursor is off the chart or the column has no known spread.
The values come from the columns built at render time
(`_renderedSpreadColumns` in ChartView), routed through the regular
`CursorPricesChanged` array - `ToTruePrices` skips spread rows and
`SymbolBarView` formats them as pips.

Wide spread minutes: when "Hide wide spread minutes" is on those minutes
are gone from the chart, but the panel still draws their spread - it
would be pointless to hide the widest spreads from the spread panel. The
values arrive as `CandleHistory.HiddenSpreads` / `LiveHiddenSpreads` and
`SpreadColumns` folds them on top of the columns. See
docs/wide-spread.md.

Live: the live tail carries spread now - `MakeLiveCandle` passes
`HasSpread`/`SpreadCode` through for closed live minutes and uses the
running `MaxSpreadTenths` for the open minute, and `SpreadColumns` folds
`History.Live` on top of the minute or block fill. Spot events that
carry only one side still widen the running maximum: `BumpLiveSpread`
applies the merged last-known bid/ask spread to the open minute when
`FeedLive` is not called for that side.

## Users of the stored spread

- "Hide wide spread minutes" drops the noisy post-close minutes, see
  docs/wide-spread.md;
- "Show ask instead of bid" draws every pair at bid plus this spread, see
  docs/ask-view.md.

## Not in v1

- `GBPUSD-ASK` minutes get no spread of their own from the backfill (the
  live path does fill them, with the same value as `GBPUSD`).
- No "load all history" hook like Density's key 0: the panel only covers
  the source years that are loaded for the view.
- If the source pair is toggled off in the symbol bar, its history is
  not loaded and the panel stays empty (same gap as Density). Re-enable
  the source to fill it.
