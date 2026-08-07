# No weekends mode

Status: implemented, v1.

## Goal

A switch in the right bar ("No weekends") that cuts the weekend gaps out
of the chart and glues the trading weeks together, so the price line is
continuous: Friday close is immediately followed by the Sunday evening
open of the next week.

Everything else (zoom, drag, indicators, drawings, pivots, calendar
lines, price scale) keeps working in this mode.

## Time model

The chart works in two time lines:

- real time - unix seconds as stored in the DB (UTC), used for all data
  lookups, labels and edits.
- virtual time - real time with every weekend gap removed. Column x of
  the chart covers a fixed virtual span (`k` minutes), not a fixed real
  span.

`WeekendCompressor` maps between them. It is a static table of weekend
gaps `[close, open)` built once for 2007..2100 (about 4900 weeks, ~100
KB), plus a binary search:

- `ToVirtual(real)` - real to virtual; times inside a gap collapse to the
  glue point.
- `ToReal(virtual)` - virtual to real; the glue point maps to the open of
  the new week.
- `InGap(real)` - true for times inside a removed weekend.

Both directions are monotone, so all the existing "unix / bucketSec"
column math keeps working, it is just fed virtual time.

## Session boundaries

Checked against the whole EURUSD M1 history (2011..2026, 5.7 M minutes):

- Week open (gap end) follows EU DST (server time EET/EEST, open =
  Monday 00:00 server time): Sunday 21:00 UTC in EU summer, 22:00 UTC in
  EU winter. EU DST runs from the last Sunday of March 01:00 UTC to the
  last Sunday of October 01:00 UTC.
- Week close (gap start) follows US DST (New York 17:00): Friday 21:00
  UTC in US summer, 22:00 UTC in US winter. US DST runs from the second
  Sunday of March to the first Sunday of November.

So a normal week is 120 hours: Sunday 3 h + Mon..Thu 96 h + Friday 21 h
in summer, Sunday 2 h + 96 h + Friday 22 h in winter. In the 2-3 weeks
per year when the two DST regimes disagree the week is 119 h or 121 h,
which the model follows exactly - no empty hours at the end of the week
and no cut at the week start.

Verified on the real data: the modelled gaps contain 134 filled minutes
out of 5.7 M (single stray minutes at the close, and up to 57 minutes on
four Sundays in October when the broker opened an hour early). Those
minutes are not dropped, they end up in the column that spans the glue
point. Holiday closures (Christmas / New Year) are not weekends and stay
visible as gaps.

## Rendering

- `ChartColumns.ColumnEdges` builds the real time of every column edge
  (`width + 1` values). A column that spans the glue point simply covers
  the whole gap in real time - there is no data in it anyway.
- `ChartColumns.BuildView(..., map)` aggregates candles by a sweep over
  those edges instead of dividing timestamps by the bucket size.
- Rollup levels: the shift between real and virtual time is always a
  whole number of hours, so only the 15 min and 60 min levels stay
  aligned with the column grid. In this mode the 4 h and 1 day levels are
  not used (`LevelFor`), the cost is a longer scan at full zoom out.
- `ChartRasterizer` takes the column edges array and derives the hour /
  day / month / year grid and the weekend shading from it, in both
  modes. The weekend band is decided by `edge + bucketSec / 2`, so the
  Sunday evening part of the week is shaded when zoomed in and
  disappears when a column is wider than the session start.
- `TimeAxisView` generates its labels in real time over the visible real
  span and drops the ones that fall inside a gap.

## State

`ChartViewState.WeekendsHidden` is saved in the app config together with
the rest of the chart state. Toggling keeps the left edge of the view on
the same candle.
