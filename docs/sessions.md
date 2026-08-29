# Session highlight

Status: implemented, v1.

## Goal

A switch in the right toolbar (the "Sessions" icon) that paints the chart background
in four colours, so it is clear which part of the day a move belongs
to:

- normal (white) - Asia and the night, everything outside the bands,
- Europe band (light blue) - from the European open to the American
  open,
- overlap band (deeper amber) - both sessions open at once,
- America band (light amber) - the rest of the American session.

The overlap colour is only a shade darker than the America one: the two
belong together, the point is to see where the busiest hours end, not to
cut the day in two.

The bands are only a background. Nothing else changes: the grid, the
weekend shading, the lines and all indicators are drawn on top as
before.

## Session times

Times are stored in UTC, and both sessions follow their own daylight
saving rules, so the bands move by one hour twice a year:

| Session | Local time           | UTC winter    | UTC summer    |
| ------- | -------------------- | ------------- | ------------- |
| Europe  | London 08:00-17:00   | 08:00 - 17:00 | 07:00 - 16:00 |
| America | New York 08:00-17:00 | 13:00 - 22:00 | 12:00 - 21:00 |

So the three bands are: Europe alone until the American open, the
4 hour overlap, then America alone until the New York close.

`SessionClock.At(unixSeconds)` returns `None` / `Europe` / `Overlap` /
`America` for one moment. It also holds the DST rules (`EuSummer`, `UsSummer`) that
`WeekendCompressor` uses for the week open and close - they used to be a
private copy inside the compressor.

EU and US DST switch on different dates, so for 2-3 weeks a year one
band is one hour longer or shorter (the overlap becomes 3 h or 5 h). The
model follows that, it never inverts the order: the American open is
always after the European open and before the European close.

The America close (22:00 UTC winter, 21:00 UTC summer) is the same
moment as the weekly close, so on Friday the band ends exactly at the
end of the week. Saturday and Sunday have no bands, so the Sunday
evening open stays white.

## Rendering

`ChartRasterizer.DrawGrid` fills whole columns before everything else,
using the column mid point (`columnEdges[x] + bucketSec / 2`) - the same
rule the weekend shading uses. The weekend fill runs after it, so a
weekend column stays grey.

The bands are drawn only when a column is at most one hour
(`SessionBandsVisible`). Wider than that the Europe band would be one
column and the chart would just look striped.

While the bands are drawn, the weekend uses a darker grey
(`ChartPalette.WeekendSession`) instead of the normal one. The normal
weekend grey is almost white and the band colours are just as light, so
next to them the weekend was hard to see. Without the bands the weekend
keeps its usual light grey.

That darker grey is darker than the grid colours, so the grid would
disappear inside the weekend. To avoid it, the weekend columns are kept
in a `bool[] weekendMask` and every grid line drawn after the fill (day
/ hour / minute, month, year, the price lines and the tilted grid) uses
its colour multiplied by `WeekendGridShade` on those columns. So the
grid stays as visible on the weekend as it is on a white background.

The colours live in `ChartPalette` (`SessionEurope`, `SessionOverlap`,
`SessionAmerica`, `WeekendSession`), like the rest of the chart colours.

## State

`ChartViewState.SessionsVisible` is saved with the rest of the chart
state, so the switch is **per tab** (docs/tabs.md) and survives a
restart. The toolbar button is light when off and dark when on, exactly
like "No weekends".
