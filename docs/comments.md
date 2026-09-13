# Comments

Status: implemented, v1.

## Goal

A comment is a note pinned to one point of the chart: **pair + minute +
level**. On the chart it is a round grey spot; the text lives in a small
dialog. Comments are global - they are not part of a tab, a note or a
game, so every tab shows the same comments.

## Model and storage

One comment is one file, plus one index file with the points:

    comments/index.json                { Id, Pair, MinuteUnix, Price } per comment
    comments/GBPUSD-20260910-1145.json { Pair, MinuteUnix, Price, Title, Description }

in the `comments` folder next to `config.json` (`AppConfig.Dir`).
`CommentPoint` is where the comment sits - the pair, the minute (UTC,
always a multiple of 60) and the level as a real price (1.16055, like the
forecast store). `ChartComment` is the same point plus `Title` (one line,
shown in the day game list) and `Description` (free text).

**The file name is the id**: `<pair>-yyyyMMdd-HHmm` in UTC, so a comment
can be found by hand. Two comments on the same pair and the same minute
get `-2`, `-3` and so on. Moving a comment to another pair or minute
renames the file - `CommentStore.Save` writes the new name and deletes
the old one; changing only the level or the text keeps the name, copy
number included. The `Id` inside a body file is ignored on read: the file
name always wins, so a file renamed by hand keeps working.

**Only the index is read at startup** - it is all the chart needs to draw
the spots. A body is read the first time something asks for it: opening
the dialog, or the day game list, which needs the titles. `CommentStore`
keeps the bodies it has read in memory for the rest of the session.

Saving writes the body file first, then rewrites the index (both through
a `.tmp` file). Deleting drops the index entry and then the body file.

The store repairs itself: no `index.json` (or a broken one, which is
moved to `.bad`) means the folder is scanned, every body file read and
the index written again. Points without a pair, a minute or a finite
price are dropped, duplicated ids are kept once.

The chart never sees `ChartComment`. `MainWindow` turns the index into
`CommentMark(Id, Pair, MinuteUnix, Value)` where `Value` is the price in
raw points (`price * 100000 / PriceDiv`), the same unit the candles and
the forecast marks use, and pushes it with `ChartView.SetComments`.

## The spot

Drawn inside `ChartRasterizer.Render`, right after the grid and **before
the pair lines**, so a comment never covers a pair or an indicator.
`ChartView.BuildCommentSpots` projects each mark the way forecasts are
projected: the pair has to be visible (hidden pair = no spot), the
weekend compressor gives the column, `Transform.ToDisplay` plus the
series offset and the flatten shift give the row. During a game a
comment after the play time is skipped, like the candles are.

The style is global (`AppConfig`, Settings dialog):

    CommentSpotDiameterPx      15 by default, 3..99
    CommentSpotColorArgb       808080 by default
    CommentSpotOpacityPercent  60 by default, 1..100

The diameter is in chart pixels (the render buffer), the same unit as
the other chart sizes. `CommentStyle.Of()` clamps the values and turns
the opacity into a 0..255 alpha for `ChartRasterizer.BlendDisc`.

While the comment dialog is open every **other** spot is drawn at half
its alpha, so the comment being edited stands out. `MainWindow` calls
`ChartView.SetCommentFocus(id)` before `ShowDialog` and
`SetCommentFocus(null)` after it; a new comment passes an empty id, so
all existing spots dim.

## Switching comments on and off

The right toolbar has a **Comments** button (speech bubble, after
Sessions). It is a per tab switch, stored as `ChartViewState.CommentsHidden`,
so one tab can show the spots while another hides them. Saving a comment
turns the layer back on in the active tab if it was off.

## The dialog

`CommentWindow` is opened by a **double click on a spot**, or from the
chart context menu:

- **Comment** - only when the right click landed on a spot; edits that
  comment. If several spots overlap the nearest one wins.
- **Add comment** - always there while at least one pair is visible.

The double click is checked in `OnDragStart` before the forecast markers,
only for `ClickCount >= 2`, so a single click still pans the chart and
raises the pair. It does not latch the pair highlight the way a double
click on a bare line does.

Fields, all editable:

    Pair         - a combo of the visible pairs
    Minute       - UTC, yyyy-MM-dd HH:mm
    Level        - pips (raw points / PipPoints), 11712.3 for EURUSD at 1.17123
    Title        - one line
    Description  - free text

Prefill for **Add comment**: the pair is the one whose line is closest to
the cursor (`PlayPairAt`, same rule as the game Order dialog), the minute
is the column under the cursor, the level is the cursor row read on that
pair. Prefill for **Comment**: the values of the comment itself.

Changing the pair in the combo replaces the level with the cursor level
of the new pair: the chart sends one `PlayLevel` per visible pair with
the request (`CommentEditRequest.Levels`), so the dialog can do it
without the chart. The minute and the text are left alone.

OK saves, Cancel drops everything, **Delete** (only when editing) asks
once and removes the comment. The title and the description come from the
body file, which is read when the dialog is opened, not at startup.

The level is shown in pips because that is how the app talks about
distance everywhere else. Conversion, both ways:

    pips  = price * 100000 / (PriceDiv * PipPoints)
    price = pips * PipPoints * PriceDiv / 100000

so EURUSD 1.16055 is 11605.5 pips, USDJPY 150.123 is 15012.3 and GER40
24000.5 is 24000.5 (one index point is one pip there).

## Day game panel

Under the "Comment of this day" box the day game panel lists the
comments of that day: **HH:mm, pair, title** (`GamePanelComment`). The
list sits inside the same block as the day comment, so "Day notes" hides
both - nothing about the day leaks while the day is being played.

Comments are picked by the UTC day of their minute, whatever pair they
belong to, and sorted by time.
