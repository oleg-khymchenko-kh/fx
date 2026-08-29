# Forecast layer

Status: v3, per-day files. (v1 was a user-created indicator type with a file picker;
v2 kept one flat `forecasts.json`; both are gone.)

## Goal

Show market forecasts on the chart as horizontal levels, so a forecast made in the
morning can be compared with what the price actually did.

It is **not** an indicator. There is nothing to add, no source to bind, no file to pick.
The forecasts live in the database and the chart always has a `Forecast` button in the toolbar
right of `Calendar`, that turns them on and off.

Every level carries the source it came from. A level without a source is a guess that
cannot be reviewed later, so the file format makes `Source` a first-class field and the
popup always shows it.

## Storage

A folder in the database folder, next to the `calendar` folder, one file per day:

    <exe>/data/forecast/2026-08-20.json
    <exe>/data/forecast/2026-08-21.json

Files are named `YYYY-MM-DD.json` after the day the forecasts were made. The loader
takes every `*.json` in the folder, reads the files in name order and merges their
lists; the name is a convention for humans, not a contract. A file that fails to
parse contributes nothing, the rest still load.

Read at startup and on demand. No candles, nothing to compute, nothing to recompute.

## One day at a time

The layer never draws the whole folder at once. It shows **one selected day** - the
records of one `YYYY-MM-DD.json` file. The default is the newest day in the folder,
so the app opens showing today's forecasts. A reload keeps the selected day if its
file still exists, otherwise falls back to the newest.

The day is picked from the chart. Any right click on the chart opens a context menu that
ends with a `Forecast YYYY-MM-DD` item for the day under the cursor. A click on a pivot, a
drawing line or inside a selection puts that object's own items on top and keeps the common
block below them, so the day can always be switched without moving the mouse first:

- the day has a forecast file - the item is enabled; clicking it switches the layer to
  that day and shows the layer if it was hidden. The item carries a check mark when that
  day is already the one on screen; clicking the checked item hides the layer, so the
  same menu pick works as an on/off toggle for the day under the cursor;
- no file for that day - the item is disabled and shows the date, so the reader still
  learns which day they pointed at.

The date under the cursor is the UTC date of the column time (`ColumnTime`), so with
hidden weekends the menu names the trading day the column actually belongs to.

The selected day is runtime state, global across tabs, and is not persisted;
`ChartViewState.ForecastHidden` (per tab) still only remembers whether the layer is on.

    {
      "Version": 1,
      "UpdatedAtUnix": 1787221800,
      "Forecasts": [
        {
          "Id": "2026-08-20-eurusd-day-point",
          "Pair": "EURUSD",
          "Kind": "Point",
          "Price": 1.1722,
          "MadeAtUnix": 1787221800,
          "UntilUnix": 1787259600,
          "Horizon": "Day",
          "Author": "Claude",
          "Title": "Point forecast for the day close",
          "Basis": "why this level, in words",
          "Source": "where the number came from",
          "SourceUrl": "https://...",
          "Probability": 55,
          "Note": "",
          "Outcome": ""
        }
      ]
    }

Fields:

- `Kind` is `Point` (one level, uses `Price`) or `Range` (a band, uses `Low` and `High`).
- `MadeAtUnix` is where the line starts, `UntilUnix` where it ends. Both unix seconds UTC.
  A record with `UntilUnix <= MadeAtUnix` is dropped.
- `Pair` must match a chart symbol by `IndicatorSymbol.NameKey` (letters and digits, upper
  case). A record for a pair that is not on the chart, or whose pair is hidden in the
  symbol bar, draws nothing. That is the whole per-pair filter - no extra UI.
- `Author` separates own forecasts from third-party ones. Publishing someone else's
  forecast next to your own is the point: after a few weeks the chart shows who was right.
- `Basis` is the reasoning, `Source` is the provenance of the number itself. They are
  different things and both matter - "momentum continues" is a basis, "spot 1.1706 from
  TradingEconomics at 10:30 UTC plus 16 pips" is a source.
- `Probability` is optional, shown in the popup header when above 0.
- `Outcome` is filled in later, during the evening review.

Each file is plain JSON and is meant to be written by hand or by a script.
`ForecastStore.Save` writes through a temp file and moves it into place, so a crash never
truncates a day.

The authoring copies live in the repo at `market-review/forecast/`, one file per day
next to the written analysis that produced it, and are copied into the database folder
when they change.

## The toolbar button

`Forecast` is the second icon in the toolbar above the symbol bar, right after `Calendar`
(docs/symbol-bar.md). It is only clickable when the folder holds at least one usable
record, the same rule the `Calendar` button uses.

- Left click toggles the whole layer (the selected day's levels). Light button = off.
- Right click opens `Reload forecasts` - re-reads the folder without restarting the app.
  This is what makes the daily routine work: rewrite the JSON, right-click, reload.

The on/off state is per tab and is saved in `ChartViewState.ForecastHidden`. It is stored
inverted on purpose: the default value of a `bool` is `false`, so tabs saved before this
feature existed come back with the forecasts **visible**.

## Rendering

Each record is drawn in the coordinate space of **its own target pair**. The layer
resolves the target series at render time and borrows its transform, per-series offset,
pip scale, mirror flag and flatten shift. So a forecast line stays glued to the pair when
the user drags, mirrors or flattens it.

- Colour is the **target pair's series colour**, so a EURUSD forecast is EURUSD-blue and a
  GBPUSD forecast is GBPUSD-red.
- `Point`: a 1 px horizontal line at the level, from `MadeAtUnix` to `UntilUnix`.
- `Range` up to **40 pips** tall: two parallel lines at `Low` and `High` with the area
  between them filled at ~15% alpha, same colour. One start disc at the middle of the band.
- `Range` **wider than 40 pips**: no fill. The two edges are drawn as two independent
  lines, each with its own start disc. A wide band swamps the price line under a tint that
  carries no information, and at that height the two edges are what the reader actually
  uses, so they stand on their own.
- Start marker, 3 px half-size, with a white 1 px outline. The shape says whose call it is:
  a **right-pointing triangle** for our own forecasts (`Author` is `Claude`, see
  `ForecastAuthors.IsOwn`), a **disc** for everyone else's. The apex points right, into the
  direction the line runs. Own and third-party calls often land on the same price - on
  2026-08-20 our GBPUSD week line and RoboForex's were 75 pips apart while RoboForex and
  TradingEconomics were on the same price to the pip - so the shape is what lets the reader
  separate "what I said" from "what the market said" without opening a popup.
- End marker: a 7 px vertical tick where the line stops, one per edge.

The end tick is drawn **only when `UntilUnix` falls inside the viewport**. That is the
point of it: a line with a tick has ended, a line that runs to the right edge without one
continues past the view. Without it a short week forecast and a long quarter forecast look
identical - both just leave the screen - and the reader cannot tell which is which.

The 40-pip cut is measured in display points (`ForecastFillMaxPoints`, 10 points per pip).
Display points are already normalised by `PipPoints`, so the same threshold means 40 pips
on EURUSD, on USDJPY and on GER40 without any per-pair special case.

Both discs of a split range carry the same record, so clicking either opens the same popup.

## Hover

Moving the cursor over a start disc thickens that forecast's lines from 1 px to 2 px - both
edges of a range, or the single line of a point forecast. It is the cheapest way to answer
"which lines belong to this dot" when several forecasts overlap near the same price.

Hover uses the same multi-hit rule as the click: every forecast under the cursor thickens,
so two stacked discs light up both of their lines and the reader sees there are two.

The extra pixel grows **inward**: the top edge thickens downward, the bottom edge upward,
so the levels the band actually claims stay where they are. On a band too thin to hold the
extra pixels they are skipped rather than drawn on top of each other.

Hover is chart state, not an overlay, so a change re-rasterizes the chart. That is why it
is keyed to the disc and not to the whole line: the state only flips when the cursor enters
or leaves a 7 px circle, not on every mouse move. Rebuilds coalesce through the existing
`_computing` / `_pending` pair, so a fast sweep across several discs cannot queue up work.
Dragging, leaving the chart, toggling the layer and reloading the file all clear it.

Lines are drawn column by column rather than as one segment. That costs nothing at chart
widths and makes the band follow the flatten bend exactly, with no gap between the edges
and the fill.

Under mirror (USDCHF) the display value of the higher price is the smaller number, so the
two edges are ordered by display value, not by price, before drawing.

Draw order: after deal markers, before the calendar dashed lines and the bottom panels.

## The popup

A left click within 4 px of a start disc opens a popup anchored to it. It lists **every**
forecast whose disc is under the cursor, nearest first, separated by dividers - not just
the closest one. Two forecasts can sit at the same price and the same start minute (on
2026-08-20 the RoboForex and TradingEconomics calls for GBPUSD were both exactly 1.3600),
and then a single-hit popup silently hides one of them and attributes the wrong line to the
one it shows.

Each block carries:

- pair and level (or `low - high` for a range) in the pair colour,
- title, horizon, probability, and the `made - until` window in UTC,
- Author, Basis, Source, Source URL, Note, Outcome - empty fields are skipped.

Escape or the ✕ button closes it. So do a left click anywhere else on the chart
(including the click that starts a pan), a zoom, and a reload: the calendar popup and
this one both go through `CloseChartPopups`, so only one is ever open. Hover changes
and other re-rasterizations do **not** close it - the end-of-render cleanup touches only
the calendar popup (when the calendar layer is hidden or its lines are not drawn at the
current zoom), because a forecast popup that dies on the next rebuild is unusable:
moving the mouse off the start disc clears the hover, and that alone re-renders the chart.

The hit test runs before pivot and drawing-line selection but after the Alt and Shift
branches, so Alt-drag of a shifted series and Shift range-select keep working over a
forecast line.

## Plumbing

- `FXViewer/Storage/ForecastStore.cs` - records, per-day folder load (`LoadFolderByDay`,
  keyed by file name without extension), save.
- `ChartView` holds `_forecastMarks` (all days, each `ForecastMark` tagged with its `Day`),
  `_forecastDays`, `_forecastDay` and `_forecastHidden`; exposes `SetForecasts`,
  `HasForecasts`, `ForecastVisible`, `ToggleForecasts` - the same shape as the calendar's
  `SetCalendar` / `HasCalendar` / `CalendarVisible` / `ToggleCalendar` - plus
  `SelectForecastDay`, `ForecastDay` and the `ForecastDaySelected` event, which
  `MainWindow` uses to refresh the toolbar button after a menu pick. Rendering filters
  to the selected day, and the popup and hover hit only what was rendered.
- `ChartToolBarView` holds `_forecastPresent` / `_forecastEnabled`, `SetForecastRow` and
  the `ForecastClick` and `ForecastReloadRequested` events.
- `MainWindow.ForecastFolder` points at `<DbRoot>/forecast/`; `LoadForecastMarks` reads
  it and converts prices to raw points through `SymbolPriceDiv`.
- `ChartRasterizer` gains `BlendPixel`, `BlendColumn` and `StrokeDisc`.

Nothing touches `IndicatorSymbol`, `DisplayConfigs` or the Add/Edit dialog. Forecasts are
not indicators.

## Not in v1

- Editing a forecast from the chart. The file is written by hand or by script.
- Automatic scoring: writing the realised price back into `Outcome`.
- Vertical marker at the horizon end of the line.
- Forecast levels do not take part in the automatic price range, so the chart will not
  scroll to a level that sits far outside the visible prices.
