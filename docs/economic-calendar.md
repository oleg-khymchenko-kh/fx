# Economic calendar: event lines on the chart

Status: implemented (stages 1-4): data foundation, line rendering,
right-panel toggle, hover circle and popup. Stage 5 adds the gap
backfill from week pages, stage 6 the settings and find windows.

## Goal

Show economic calendar events on the chart as vertical lines. Line color
is the event currency (EUR = euro blue, USD = black, other tracked pairs
use their own color). Line style marks importance (high / medium / low).
The calendar can be turned on and off from the right panel like a normal
pair. Hovering within 2 px of a line shows a small circle at the bottom
of the line; a click on the circle opens a popup with the full event
info.

Only the light fields (date, importance, currency/color) live in memory.
The full event text is read from disk on demand when the popup opens.

## Data sources (free)

cTrader Open API (the rates feed) has no calendar, so the calendar comes
from Forex Factory, which is free:

- History (one time): a static Forex Factory dump on Hugging Face,
  dataset `Ehsanrs2/Forex_Factory_Calendar`, file
  `forex_factory_cache.csv` (~68 MB, 2007-01-01 to 2025-04-07, ~83k
  rows). Columns: `DateTime, Currency, Impact, Event, Actual, Forecast,
  Previous, Detail`. `DateTime` is ISO 8601 with a per-row timezone
  offset. The dataset is dead - its last update was 2025-04-14, so it
  never grows past 2025-04-07.
- Forward (each run): the official free weekly JSON, no API key:
  `ff_calendar_thisweek.json` on `nfs.faireconomy.media`. Fields:
  `title, country, date, impact, forecast, previous` and `actual` after
  the event. Same Forex Factory schema, so one parser handles both.
  The `lastweek` and `nextweek` files of that feed are gone (404 since
  at least 2026-08), only `thisweek` is still served.
- Gap filling: the normal week page `forexfactory.com/calendar?week=jul19.2026`.
  It carries the whole week as JSON inside the HTML, in a
  `window.calendarComponentStates[N] = { days: [...] }` assignment. Each
  event has `dateline` (unix UTC, matches the weekly JSON exactly),
  `currency`, `impactName`, `name`, `actual`, `forecast`, `previous`.
  No `detail` text there. The week key is `mmmD.yyyy`, lowercase month,
  no leading zero on the day, week starts on Sunday.

Because the CSV stops at 2025-04-07 and the weekly JSON only covers the
current week, everything in between has to be pulled week by week from
the week pages (`Backfill calendar gap`).

## Storage

Folder `data/calendar/`, one pair of files per year, the same way the
candle DB keeps `<SYMBOL>/<year>.m1`:

- `<year>.jsonl` - every event of that year, one compact JSON object per
  line, in time order. This is the full record for the popup. Read by
  byte offset and length, never fully loaded into memory for display.
  Fields are short: `T` unix, `C` currency, `I` impact, `E` event,
  `A` actual, `F` forecast, `P` previous, `D` detail.
- `<year>.idx` - the light in-memory index for that year. Fixed 23-byte
  records `[int64 unix][byte impact][byte currency][int64 detailOffset]
  [int32 detailLen][byte rateDecision]`, sorted by time. `detailOffset` is
  a byte offset inside that same year's `.jsonl`. Only the 8 tracked
  currencies are indexed (EUR, USD, GBP, JPY, CHF, AUD, NZD, CAD); other
  currencies stay in the `.jsonl` but are never loaded.

`Load` reads every `<year>.idx` in year order and concatenates them, so
the in-memory index stays sorted by time. `ReadDetail` derives the year
from the entry timestamp (UTC), so the entry does not need to store which
file it came from.

Impact byte: None=0, Low=1, Medium=2, High=3, Holiday=4, Highest=5.
Currency byte: 1..8 in the tracked order above, 0 = other.

`Highest` is the top level, above `High`: a USD event whose title starts
with one of

- `Federal Funds Rate`
- `Average Hourly Earnings` (the stored title is `... m/m`)
- `Non-Farm Employment Change`
- `Unemployment Rate`

The match is on the start of the title, not anywhere inside it, so
`ADP Non-Farm Employment Change` stays a normal high event. The level is
derived from the currency and the title, not from the source impact
field, so `CalendarDetail.ImpactLevel` decides it and the index stores
the result. The CSV writes holidays as `Non-Economic`, which maps to
`Holiday`.

The `rateDecision` byte marks the `Federal Funds Rate` events alone. The
titles are not in the index, so without this flag the renderer cannot
tell a rate decision from the other three `Highest` titles. It is set the
same way, from the currency and the title, when the index is written.

The index header carries a version (`CAL4`). `UpgradeOutdatedIndexes`
rewrites any `.idx` still on the old version straight from the year's
`.jsonl` - it only touches the index, never the data. That is what pulls
the `Highest` level into stores written before it existed, what picked up
the three new titles when the level grew past rate decisions, and what
fills the `rateDecision` byte. Records grew by that byte in `CAL4`, so
`LoadYear` reads the older 22-byte layout without it.

`WriteYear` dedups by (unix, currency, event), keeping the copy that has
an `actual` value and filling its empty fields from the other copy, sorts,
then writes both files through `.tmp` + move. Field filling matters
because the week pages and the weekly JSON carry no `detail` text, so a
re-download must not wipe the description that came from the CSV.

`Merge` is the only write path. It touches only the years present in the
fresh batch and always reads the existing year first, so nothing already
on disk is lost. It is idempotent.

There used to be a `Rebuild` that dropped year files missing from the
fresh batch; the CSV import used it, so every `Import calendar history`
silently deleted everything newer than 2025-04-07. It is gone - the CSV
import merges like everything else.

A store in the old single-file layout (`details.jsonl` + `index.bin`) is
migrated to per-year files once, on the next load, then the old files are
deleted.

## Currency colors

EUR = `0xFF3366DD` (same blue as EURUSD), USD = `0xFF000000` (black),
GBP = `0xFFD32F2F`, JPY = `0xFFB8860B`, CHF = `0xFF7B1FA2`,
AUD = `0xFF1B5E20`, NZD = `0xFF66BB6A`, CAD = `0xFF795548`, other =
`0xFF808080` gray. These match the pair colors in `MainWindow`.

## UI (stage 1)

Connection tab:

- `Import calendar history` - if `calendar-import/forex_factory_cache.csv`
  is missing it downloads it from Hugging Face first, then merges the CSV
  into the store.
- `Download calendar` - fetches the weekly JSON and merges it.
- `Backfill calendar gap` - asks for a date range first, then walks the
  week pages of that range, 5 s between requests, merging every 500
  events. The range is proposed from the widest hole between two stored
  events (10 days or more), so it points at the real gap rather than at
  the end of the store - the current week is already there from the
  weekly feed, and taking the last event would skip everything before it.
  With no such hole it proposes last event .. today. Both ends are widened
  to whole weeks, weeks start on Sunday.

  Forex Factory answers the first one or two requests with 403 until its
  cookie is set, so each page is retried up to 4 times with 10 / 20 / 40 s
  pauses. While it runs the button turns into `Stop backfill`; stopping
  keeps everything fetched so far.
- `Verify DB` also prints the calendar range and high/medium counts.

The light index is loaded once at startup and after each import/download.
Startup also runs `Download calendar` in the background, so the current
week appears without pressing anything.

## Rendering (stage 2)

Vertical lines are drawn into the chart raster after the candle series,
so they sit on top. x is the event unix through the same
`unix / (K*60) - startBucket` mapping used for candles, full height.
Color is the currency, dash marks importance: high solid, medium dashed
(3 on / 3 off), low dotted (1 on / 3 off). Holidays and None are not
drawn. Within one column the levels are drawn low, then medium, then
high, so a high event wins the shared pixels.

Highest lines are solid and 1 px wide like the rest. Draw order is
holiday, low, medium, high, highest, so the strongest level wins the
shared pixels.

A rate decision line - and only that one, not the rest of `Highest` -
also carries a small black triangle pointing down: 7 px wide, 4 px high,
its base on the very top row of the chart, and the line runs through the
middle of it. Width and height are set apart from each other, so the
shape is not tied to a 45 degree slope. All sizes are device pixels, like
the rest of the raster. The triangle comes from the entry's
`rateDecision` flag and is drawn with the line, so hiding the `Highest`
level hides it too.

Zoom gate: with `Show at any zoom` off, lines are drawn only when the
hourly grid is visible, that is `ChartRasterizer.HourGridVisible(k)`
(k <= 6 minutes per column), so zoomed out wider the calendar hides to
avoid a wall of lines. The setting is on by default, which draws the
lines at every zoom. Only the visible time window is scanned each render
(binary search by unix).

## Right panel (stage 3)

A `Calendar` button is the first icon in the toolbar above the symbol bar
(docs/symbol-bar.md). Click toggles the lines like a pair. The button is
dark when on, light when off, and pale (dead) while the store has no
events. The on/off flag is saved in
`ChartViewState.CalendarVisible`, so it is per tab.

Right-click on the button opens its own menu with `Settings...` and
`Find...`. The bar marks right-click handled on the rows it owns,
otherwise the click reaches the tab item behind the chart and its
Rename / Duplicate / Delete menu opens instead.

`Settings...` picks which impact levels are drawn (highest, high, medium,
low, holidays) and whether the lines show at any zoom. Unlike the on/off
flag these live in `AppConfig.Calendar` - one setting for all tabs.
Hidden levels are skipped by hover and by the popup too, not only by the
renderer.

The same menu starts with three levels, one of them checked:

- `High` - highest and high.
- `Medium` - highest, high and medium.
- `Low` - all levels.

A level writes the same flags as `Settings...` (holidays and zoom stay as
they are) and turns the calendar on. Clicking the checked level turns the
calendar off. The check shows only while the calendar is on and the
flags match one of the three levels.

Every checkbox applies at once: each click raises `SettingsChanged`, and
`MainWindow` saves the config and pushes the new settings into the chart
right away, so the lines change behind the still-open window. The window
has one `Close` button and no OK / Cancel - there is nothing left to
confirm or undo.

## Find window (stage 6)

`Find...` opens a non-modal window listing every stored event, newest
first: time (UTC), currency, impact, title, actual / forecast / previous.
A text box filters by title or currency, and two checkboxes narrow it to
the highest level or to high-and-above. At most 3000 rows are rendered;
the counter says how many matched in total when the list is cut.

The list is built from `CalendarStore.LoadSummaries`, which reads every
year's `.jsonl` with `Utf8JsonReader` and skips the `D` field, so the
heavy description text is never allocated. ~83k events load in under a
second. The result is cached in `MainWindow` and dropped whenever the
calendar is reloaded.

Clicking a row calls `ChartView.ShowTimeCentered`: it puts that minute in
the middle of the chart and moves the view vertically so the pair sits in
the middle of the screen. The pair is EURUSD when it is visible, else the
first visible series. Vertical zoom is left alone, except when the chart
was in fit view - then the zoom is set to 5 minutes per column and the
vertical scale is recomputed for the new window.

## Hover circle and popup (stage 4)

Hover detection is by x only (the line is vertical): the cursor within
`LineHitRadiusPx` (2 px) of a line column shows a small filled circle at
the bottom of the chart, on the line, `CalCircleBottomMarginPx` above the
bottom edge. The circle is inside the chart plot (an `Ellipse` on the
edit canvas), not on the time axis. Its fill is the highest-importance
currency in that column.

The circle is drawn by WPF, but the edit canvas is not hit-test visible,
so the click is caught in `OnDragStart` by distance to the stored circle
center. A click reads the full detail(s) for that column from
`details.jsonl` through `CalendarDetailLookup` (wired in `MainWindow` to
`CalendarStore.ReadDetail`) and opens a `Popup` above the circle. The
popup lists every event in that minute: time (UTC), currency, impact,
title, actual/forecast/previous, and the detail text.

The popup stays open while the mouse moves or the chart is used
(`StaysOpen = true`). It closes only on its `✕` button, when another
circle is clicked, on zoom-out past the hour grid, or when the calendar
is turned off.

`Forecast` is the expected value and `Actual` is the value published
after the release; both come from Forex Factory and are stored per event.
Historical rows already carry `actual`. For a fresh event the weekly JSON
has an empty `actual` until the release happens, so running
`Download calendar` again after the release fills it in: the dedup keeps
the copy that has an `actual`.

Only pivot hover wins over calendar hover; otherwise the two never show
at once.
