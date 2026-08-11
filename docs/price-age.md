# Price age indicator

Status: implemented, v1.

## Goal

A new indicator type `PriceAge` (label "Price age" in the Add / Edit
dialog). For every minute of the source pair it answers one question:

- How many trading minutes ago was the same price last seen?

A minute candle usually covers several pips. The age is computed for
every pip level inside the candle range and the largest age wins. A pip
level that was never seen before in the loaded history has an infinite
age.

The value is signed. It is positive when the price went up into the old
levels and negative when the price went down into them.

The answer is drawn as thin vertical bars around a zero line at the
bottom of the chart, not as a price line: positive values grow up from
the zero line, negative values grow down.

## Parameters

None. Only the source symbol and the color. The pip size comes from the
source symbol config (`SourcePipPoints`): 10 raw points on EURUSD, 1000
on USDJPY, 100 on GER40 (1 index point).

## Definition

- The pip levels of a candle are `round(Min / pip)` to
  `round(Max / pip)`, both ends included. The same rounding is used when
  the level is looked up and when it is recorded, so a level "was seen"
  means an earlier candle range covered the same rounded pip.
- Age is the distance in bars of the dense minute array, so it counts
  trading minutes only. Weekends and data gaps do not add to the age,
  the same convention as everywhere else in the app.
- Only earlier candles count. The current minute never sees itself, so
  the smallest possible age is 1.
- If any pip level of the candle was never seen before, the whole minute
  is infinite. This includes the very first candles of the history and
  every new all-time high or low.
- The sign compares the two sides of the previous minute's average
  price: the best age among the candle's levels at or above it against
  the best age among the levels below it. The bigger side wins and gives
  the sign - positive when the price rose into the old levels, negative
  when it fell into them. The magnitude is always the overall maximum.
  When both sides tie (for example a quiet minute where every level has
  age 1), the direction of the average price against the previous minute
  decides. Any level inside the previous candle's range has age 1, so a
  large age always comes from a level the price just moved to. The first
  minute of the history is positive.
- The stored sign follows the raw price. Pairs that are drawn mirrored
  (`USDCHF`, `USDJPY`, `USDCAD`, and any index with Mirror on) show the
  price line flipped, so for them the sign is flipped once more when the
  panel is drawn. The bar direction always matches the direction of the
  line the user sees.

## Algorithm

`PriceAgeSymbol.Evaluate` is one forward pass. A dictionary maps a pip
level to the index of the last bar whose range covered it. For each bar
it reads the ages of all its levels, takes the maximum, then stamps all
its levels with its own index. Cost is the total number of pips over all
candles, about 20M dictionary hits for a full EURUSD history - a few
seconds. The dictionary stays small (thousands of levels).

## Storage

The result is a normal symbol in the candle DB, one record per source
minute. The signed age is written into all three channels (`Min`, `Max`,
`Avg`); infinity is stored as `int.MaxValue`
(`PriceAgeSymbol.NeverSeen`) with the sign applied, so a falling minute
that reached a never-seen level stores `-int.MaxValue` (never
`int.MinValue`).

The rollup levels (`CandleHistory.Levels`) keep min and max per block:
`Max` carries the largest positive age of the column and `Min` the
largest negative one, both exact at every zoom level for free - the same
trick as entry points. A zoomed-out column can therefore show an up bar
and a down bar at the same time.

The values are minute counts, not prices, so the display transform must
not touch them: `IndicatorConfig` forces `mirror = false`,
`pipPoints = 10` and `PriceDiv = 1` for this type, which makes
`CandleTransforms.Transform` a copy.

## Rendering

`PriceAgeColumns.Build` turns the history into one `AgeColumn` per
screen column - the largest positive value (`Up`, from the `Max`
channel) and the largest negative one (`Down`, from the `Min` channel),
using the same level choice and column edges as `ChartColumns`. When the
source pair is drawn mirrored, `Up` and `Down` are swapped here, so the
stored values stay raw and only the picture follows the mirrored line.
The flag comes from the source symbol config through
`DisplayConfig.AgeMirror` and `SymbolSeries.AgeMirror`.

`ChartRasterizer.DrawAgePanel` draws a light gray zero line across the
panel and, per column, a 1 px wide bar up from the zero line for `Up`
and one down from it for `Down`. Each direction can take up to 50 px
(`AgeBarMaxPx`), so the panel is 101 px tall (`AgePanelHeightPx`,
50 + zero line + 50). The bar height follows a fixed anchor scale in
trading days (a trading day is 1440 trading minutes):

- 1 day = 5 px, 2 days = 10 px, 5 days (a trading week) = 15 px,
  10 days = 20 px, 20 days = 30 px, 40 days = 40 px
- 80 days and more, and "never seen before", fill the whole 50 px
- between anchors the height is interpolated linearly in log(age)
- inside the first day it grows as `5 * ln(1 + age) / ln(1 + 1440)`,
  with a minimum of 1 px for any finite age

Only the zero line and the bar pixels are painted, bars in the indicator
color; empty columns and the space beyond a bar keep whatever the chart
drew there. Live candles are not part of the columns, exactly like entry
points.

Price age panels share the bottom band with entry point panels. Visible
panels stack upwards in series order, 2 px apart, the lowest one 10 px
above the bottom edge. Each panel advances the stack by its own height
(101 px for price age, 9 px for entry points). Hiding the symbol in the
symbol bar hides its bars.

Price age series are skipped everywhere a price line is expected, via
the shared `SymbolSeries.BottomPanel` flag: `RenderLine` building, the
initial auto scale, the global price range (pan limits), the range
statistics popup, centering, auto align and the align-to-source check.
They are also in the align-to-selection excluded set.

## Creation, compute and refresh

Same Add / Edit dialog. Picking `Price age` shows no extra fields.
`IndicatorSymbol.SameData` compares only the source for this type, so a
color or name only change does not recompute.

`Compute derived`, the dialog `Refresh` button and the context menu
`Refresh` / `Rebuild` all branch to `PriceAgeSymbol`. `Generate`
rebuilds the whole symbol. `Refresh` still reads and evaluates the whole
source history (the last-seen table needs the full past), but writes
only the minutes after the last stored one, so the DB work is small.

Ages only look backwards, so a stored value never changes once written -
as long as the source history behind it does not change. Two source
changes are handled:

- **Provisional live minutes.** Live candles are tick-built and slightly
  narrower than the broker bars that replace them once a minute
  (docs/live-candles.md), so an age computed from them could be wrong
  and would then be frozen. Both `Generate` and `Refresh` therefore stop
  at the confirmed boundary (`ConfirmedEndUnix` of the source, the same
  clamp the index refresh uses) and never read the provisional tail. The
  skipped minutes are picked up by the next refresh after the repair.
- **Backfilled history.** Downloading an earlier year changes the
  correct value of every already stored minute (new pip levels, longer
  distances). `Refresh` detects that the source now starts in an earlier
  year than the target and falls back to a full `Generate`. Filling a
  gap inside a year that the target already covers is not detected - run
  `Rebuild` by hand after such a download.

Price age is not part of the once-a-minute automatic refresh (only
Averages are), so live candles do not extend the bars until a manual
refresh.

## Not in v1 (next steps)

- No live update between refreshes.
- The symbol bar row still prints the age value as if it were a price,
  and `int.MaxValue` looks like a huge number there.
- No per-column tooltip with the exact age.
