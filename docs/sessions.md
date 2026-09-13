# Session highlight

Status: implemented, v1.

## Goal

A switch in the right toolbar (the "Sessions" icon) that paints the chart background
in six colours, so it is clear which part of the day a move belongs
to:

- normal (white) - Asia alone, everything outside the bands,
- Asia/Europe band (blue) - Europe is open while Tokyo is still open,
- Europe band (very light blue) - Europe alone, until the American
  open,
- overlap band (deeper amber) - Europe and America open at once,
- America band (light amber) - the rest of the American session,
- night band (light grey) - from the American close to the Asian open.

The night grey is much lighter than the weekend grey, so a night is
never mistaken for a Saturday.

Inside each pair the two colours belong together: the Asia/Europe band
is a shade darker than the Europe one, the Europe/America overlap is a
shade darker than the America one. The point is to see where the busiest
hours start and end, not to cut the day in pieces.

The bands are only a background. Nothing else changes: the grid, the
weekend shading, the lines and all indicators are drawn on top as
before.

## Session times

Times are stored in UTC, and Europe and America follow their own
daylight saving rules, so the bands move by one hour twice a year:

| Session | Local time           | UTC winter    | UTC summer    |
| ------- | -------------------- | ------------- | ------------- |
| Asia    | Tokyo 09:00-18:00    | 00:00 - 09:00 | 00:00 - 09:00 |
| Europe  | London 08:00-17:00   | 08:00 - 17:00 | 07:00 - 16:00 |
| America | New York 08:00-17:00 | 13:00 - 22:00 | 12:00 - 21:00 |

Japan has no daylight saving, so the Asia hours never move.

So the five bands are: the Asia/Europe overlap from the European open
to the Tokyo close (1 h in winter, 2 h in summer), Europe alone until
the American open, the 4 hour Europe/America overlap, then America alone
until the New York close, then the night until midnight UTC, where the
next Asian session starts. Asia before the European open stays white -
only the overlap is painted.

`SessionClock.At(unixSeconds)` returns `None` / `AsiaEurope` / `Europe`
/ `Overlap` / `America` / `Closed` for one moment. It also holds the DST rules (`EuSummer`, `UsSummer`) that
`WeekendCompressor` uses for the week open and close - they used to be a
private copy inside the compressor.

EU and US DST switch on different dates, so for 2-3 weeks a year one
band is one hour longer or shorter (the overlap becomes 3 h or 5 h). The
model follows that, it never inverts the order: the American open is
always after the European open and before the European close.

The America close (22:00 UTC winter, 21:00 UTC summer) is the same
moment as the weekly close, so on Friday the band ends exactly at the
end of the week and the night band covers the last two or three hours
of the week. Saturday and Sunday have no bands, so the Sunday evening
open keeps the weekend grey.

## Rendering

`ChartRasterizer.DrawGrid` fills whole columns before everything else,
using the column mid point (`columnEdges[x] + bucketSec / 2`) - the same
rule the weekend shading uses. The weekend fill runs after it, so a
weekend column stays grey.

The bands are drawn only when a column is at most one hour
(`SessionBandsVisible`). Wider than that the Asia/Europe band would be
gone and the chart would just look striped. At exactly one hour per
column the winter Asia/Europe band is a single column wide.

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

The colours live in `ChartPalette` (`SessionAsiaEurope`,
`SessionEurope`, `SessionOverlap`, `SessionAmerica`, `SessionClosed`,
`WeekendSession`), like the rest of the chart colours.

## State

`ChartViewState.SessionsVisible` is saved with the rest of the chart
state, so the switch is **per tab** (docs/tabs.md) and survives a
restart. The toolbar button is light when off and dark when on, exactly
like "No weekends".
