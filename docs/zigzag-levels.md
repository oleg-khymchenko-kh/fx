# ZigZag levels indicator

Status: implemented, v1.

## Goal

A new indicator type `Levels` (label "ZigZag levels" in the Add / Edit
dialog). Its source is a ZigZag indicator, not a pair. It answers one
question: at what prices did the zigzag turn recently?

Every zigzag point inside a lookback window that ends at the cursor is
drawn as a short horizontal dash at the right edge of the chart, at the
height of that point's price. The color says which side the price came
from, and the distance from the right edge says which trend the point
belongs to: one offset per trend, the newest trend flush right, each
older trend a fixed number of pixels further left.

## Parameters

- Source: a ZigZag indicator. The dropdown lists only ZigZag
  indicators; if there are none, the dialog says so.
- Lookback: a period plus a unit (Minutes / Hours / Days), default
  5 Days. It counts back from the cursor.
- Length: dash length in pixels, default 4.
- Step: how far left each older trend moves, in pixels, default 2.
- Two colors: "From below" for a level the price rose into (a top) and
  "From above" for one it fell into (a bottom). They are the usual
  `ColorArgb` / `SellColorArgb` pair, the same fields the order book
  and Volume use for their two colors.

Length and Step are 1 to 500 px. The lookback must be a positive whole
number and stay under 10M minutes.

## Definition

- The anchor is the cursor time: the last minute of the hovered column.
  With the mouse off the chart the anchor is the right edge of the
  view. In play mode the anchor never goes past the play time.
- The window is `[anchor - lookback, anchor]` in **trading** minutes:
  weekends are removed the same way `Limit2Delay` of ZigZag removes
  them (`WeekendCompressor`), so a 5 day window is 5 trading days
  whatever the `No weekends` setting is. Holidays and data holes are
  not removed.
- Points are taken from the source ZigZag in time order, newest first.
  Both ends of the window are included.
- A point that is off the top or the bottom of the view still takes its
  place in the staircase, it is just not drawn. The staircase therefore
  does not shift when the chart is panned vertically.

## Two kinds of level

A zigzag point is a **from below** level when the price rose into it,
which makes it a top, and a **from above** level when the price fell
into it, a bottom. The kind is read off the drawn line: point `i` is
"from below" when its display value is above the previous point's. The
points strictly alternate, so the kinds alternate too (checked on the
full GBPUSD zigzag: 12 589 points, zero neighbours of the same kind).

Everything below works on display values, the same numbers the zigzag
line is drawn from. On a mirrored pair the picture stays consistent
with what is on screen: a top of the drawn line is a "from below"
level.

## Trends

A level continues the current trend, or it starts a new one. For a
level `L` let `prevTop` be the nearest earlier "from below" level and
`prevBottom` the nearest earlier "from above" level.

An **up** trend accepts:

- a "from below" level when `prevTop < L` (a higher high) and
  `prevBottom < prevTop`,
- a "from above" level when `prevBottom < L` (a higher low) and
  `prevTop > L`.

A **down** trend is the mirror: a higher high becomes a lower high and
a higher low becomes a lower low. The two extra conditions
(`prevBottom < prevTop`, `prevTop > L`) are kept as they are in both
directions, because they only say the zigzag is well formed - a bottom
sits below the tops around it. Mirroring them literally would ask for a
bottom above a top, which never happens. On the full GBPUSD zigzag they
never fire once both kinds have been seen (0 of 12 589 points), so in
practice the direction is decided by the first line alone.

A level that the current trend does not accept starts a new trend, and
the new trend's direction is whatever that level fits (up, down, or
still undecided when there is not enough history). The walk starts at
the very first point of the zigzag, not at the window, so the first
levels in the window land in the trend they really belong to. On the
full GBPUSD zigzag this gives 3 891 trends over 12 589 points, 3.2
points per trend.

## Layout

All levels of one trend sit at the same distance from the right edge.
Trend `i` counts from 0 for the trend of the newest point in the
window: the right edge of its dashes is `width - i * Step` (exclusive)
and the left edge is `right - Length`. So the newest trend is flush
with the right edge of the chart and every older one steps `Step` px
further left. Pixels are device pixels, the same as everywhere else in
the chart bitmap. A dash is one pixel tall.

Two dashes at the same pixel row overlap when the left one would run
under the right one. Then the left dash is cut on the right: its
rightmost pixel goes 1 pixel left of the leftmost pixel of the right
dash. Its left edge stays where the formula put it, so the dash gets
shorter instead of moving. With the default 4 px length and 2 px step
this means a row hit by several trends draws one continuous line whose
length tells how many trends share that price.

The rule uses the leftmost pixel of the dash drawn last on that row,
which is always the leftmost one drawn so far, because the left edge
falls by `Step` per trend. Two levels of the SAME trend on one row
would land on the same pixels, so the second one draws nothing.

A dash whose computed left edge falls off the left side of the chart is
clipped to the left edge; once a whole trend is off the chart the walk
stops.

## Rendering

The dashes go into the same right-edge overlay bitmap as Density,
Volume and the order book (`ChartView._densityImage`), so a cursor move
redraws them without re-rasterizing the chart:

- `OnCrosshairMove` redraws when the hovered column changes,
- `Present` redraws after every chart rebuild (pan, zoom, resize, data
  load, tab switch),
- `MouseLeave` redraws with the right edge as the anchor.

`DrawLevels` reads the source ZigZag's display values straight from
`ChartView._pivots`, which is the same array the zigzag line is drawn
from, so an edited point moves its dash on the next commit. The value
is turned into a row with `DisplayToY` using the **source** offset
chain, so the dashes always line up with the zigzag line. The row's own
drag offset is ignored, exactly like the Density profile.

`BuildLevelTrends` runs the trend walk over the whole point list up to
the anchor on every redraw and writes one trend number per point into a
reused array. A full pair is around 12k points of plain integer work,
tens of microseconds, so nothing is cached or invalidated.

The overlay reports how far left it drew, so the cleared band grows
with the trends instead of being cut at the 120 px density band.

A label per visible indicator sits at the top right, in the indicator
color: `Name: 5d`, or `Name: selection`.

## Selection mode

While a time range is selected on the chart (Shift + drag) the window
is the selection itself, `[start, end of the last selected column]`,
and the lookback is ignored. Escape returns to the cursor window. Same
as Density and Volume.

## Storage

None. `IndicatorTypes.HasStorage` is false for the type, so Compute
derived, Refresh and Rebuild skip it and Delete only drops the config
entry. Everything is read from the source ZigZag at draw time.

## Wiring

- `IndicatorTypes.Levels` is in `All`, `HasStorage` is false,
  `SourceIsZigZag` is true, `Label` is "ZigZag levels".
- `IndicatorSymbol` keeps `Period` / `Unit` (shared with Average) plus
  `LevelLengthPx` and `LevelStepPx`. `SameData` returns true, so OK
  never computes anything: it saves the config and reloads the chart.
- `DisplayConfigs` emits a levels config right after its ZigZag parent,
  so `SourceSymbol` is the zigzag name and the symbol bar puts the row
  in the zigzag's group. Collapsing the pair hides the zigzag and the
  levels row with it.
- The series is an empty `CandleHistory` with `LevelsPanel = true`,
  which joins the shared `BottomPanel` flag, so every price-line code
  path skips it. The name is in the align-to-selection excluded set.
- Renaming a ZigZag re-points the `Source` of every indicator built on
  it, the same way renaming a USD Index already did.

## Known limits

- The dash is always 1 pixel tall. Only the length and the step are
  parameters.
- The window counts trading minutes, so holidays and data holes are
  counted as trading time.
- Nothing marks how many points share one row: overlapping dashes merge
  into one line, and the length is the only hint.
- Two levels of one trend at the same pixel row draw once. Zoom in on
  the price axis to split them.
- A trend is only a grouping for the offset. Its direction is not shown
  anywhere, and the trend the newest point is in may still be
  unfinished.
- Levels of a hidden ZigZag are still drawn; hiding the levels row
  itself is what turns them off.
