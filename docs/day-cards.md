# Day cards

Status: implemented, v1 (2026-10-26). Second tab `Days` in the puzzle app
`src/Fx.WeekPuzzle` (the week puzzle is the `Weeks` tab).

## Goal

A game about finding similar days. Every card is one trading day of
GBPUSD. The player (or Claude through the API) puts similar cards into
named groups. The grouping rules are given later, one request at a time.

## Card

- One card = one trading day, from the previous day's American close to
  this day's American close: 21:00 .. 21:00 UTC in US summer, 22:00 ..
  22:00 in winter (`WeekCards.DayEnd`). Monday starts on Sunday evening.
  Monday to Friday only; a day with less than 300 minutes of data is
  skipped (holidays).
- Card id = the date of the day, `yyyy-MM-dd`.
- Scale is fixed and the same on every card. It started with the
  proportions of the FXViewer chart the user showed (10 px per hour, 5 px
  per pip); on 2026-10-26 the user asked for cards half as tall, so now
  it is 10 px per hour and 2.5 px per pip, multiplied by 4
  (`DayCards.Scale`) so a card has enough pixels to zoom into: 40 px per
  hour (960 px per day), 10 px per pip. The card height follows the range
  of the day, so cards differ in height.
- Top strip (64 px) holds the day of week and the date, `Fri 2026-10-23`.
  The chart starts below it, so the text never covers the chart. 20 px
  margin above the high and below the low.
- Session backgrounds with the FXViewer colors (`docs/sessions.md`):
  Asia/Europe, Europe, Europe/America overlap, America. The hours before
  00:00 UTC (the evening of the previous day) use the night gray, also on
  Monday (Sunday evening).
- Grid like FXViewer: 100 pip lines solid `#C8C8C8`, 50 pip lines dotted
  `#C8C8C8`, 10 pip lines dotted `#E0E0E0`, hour lines dotted `#E0E0E0`,
  00:00 UTC solid `#E0E0E0`. Grid lines are 1 screen pixel at every zoom.
  10 and 50 pip lines and hour lines hide when they get closer than 6
  screen pixels.
- GBPUSD minutes as half transparent red bars, one bar per minute, the
  same color as the week puzzle. Wide spread minutes are skipped.
- On top the blue line of the Drawing indicator `GBP-Drawing-3` (same
  source and color as the week puzzle), clipped to the day window in real
  time. Its width stays about 1.5-1.8 screen pixels when zoomed in.
- No other text, no labels on the grid.

## Mirrored cards

A card can be flipped upside down (user request 2026-10-26): the high
becomes the low and back, time stays as it is. This makes days with a
low at some moment look like days with a high at that moment. Only the
chart part flips (grid, bars, line); the top strip stays on top.

A mirrored card is marked so it can not be missed at any zoom: the top
strip is purple (`#8E24AA`) with white bold text `Fri 2026-10-23   ↕
MIRRORED`, and the drawing line is green (`#1E8E3E`) instead of blue.
No extra border (the user asked to remove it, 2026-10-26).

Mirror a card with `M` while the pointer is over it, or `Mirror (M)` in
its right click menu. A group frame menu has `Mirror all cards` and
`Unmirror all cards`. The API returns the real prices also for a
mirrored card, plus `mirrored: true`.

## Table

- Cards lie where the player puts them (user request 2026-10-26: "двигать
  как угодно и ставить куда угодно"). Positions are saved and come back
  after a restart.
- A group is drawn as a rounded frame in the group color around its cards
  (bounding box plus padding), with the name and the card count in a
  strip above the cards. Frames of different groups may overlap when the
  cards are put that way.
- A frame follows its cards until the user resizes it. A resized
  ("sized") frame keeps its own rectangle: it does not shrink when cards
  leave, and it only grows past it while some card sticks out. It can
  never be smaller than its cards. `Fit frame to cards` (frame menu)
  makes it follow the cards again.
- The first start (no saved positions) lays the table out automatically:
  every group as a block, cards in rows, blocks on shelves left to right,
  then down, loose cards in date order in the last block without a
  frame. The number of columns in a block grows with the square root of
  its card count, so a block stays about square. After that nothing moves
  by itself, except the API changes below.
- `Arrange` (toolbar, asks first) lays the whole table out again the same
  way; the hand made positions and frame sizes are lost.
- Days that are new after `Reload` (no saved position) are put in rows
  below everything.
- Mouse wheel zooms around the pointer, drag on empty table pans, `Fit`
  (button or `F`) shows everything.

## Moving and grouping by hand

- Drag a card: it moves and stays where it is dropped. While it is
  dragged, its own group frame shrinks to the other cards and the frame
  under the pointer gets a thick border. On drop (by the pointer
  position):
  - inside the frame of a group: the card joins that group;
  - outside every frame: the card leaves its group, unless it is the only
    card of that group (then it carries the group with it, a sized frame
    moves by the same distance).
- Drag the empty area inside a frame (the title strip or a gap between
  cards, the pointer shows a move cross): the whole group moves anywhere,
  cards and frame together. Nothing joins or leaves the group.
- Drag a frame edge or corner (7 screen pixels around the border, the
  pointer shows a resize arrow, the bottom right corner has a grip): the
  frame gets that size. It stops at its cards; cards never move when a
  frame is resized.
- Right click on a card: `Mirror (M)`, `New group` (the card alone in a
  new group, in place), `Remove from group` (the card leaves and is put
  right of the frame).
- Right click on a group frame: `Rename group...`, `Arrange group` (pack
  its cards in rows at the frame's top left corner; a sized frame keeps
  its width and gets as many columns as fit in it), `Fit frame to cards`
  (sized frames only), `Mirror all cards`, `Unmirror all cards`, `Delete
  group` (its cards become loose and stay where they are).
- `Clear groups` removes all groups after a confirmation, cards stay.
- A group that loses its last card is removed.

## API

The app starts an HTTP server on `http://localhost:5288/` (6th command
line argument changes the port). `GET /api` lists the routes. All
answers are JSON except the images. Every change switches the window to
the Days tab and is saved.

| Route | Body / query | Does |
| --- | --- | --- |
| `GET /api/state` | | tab, card count, first and last id, groups with ids, loose ids, mirrored ids, weekFill (show, group, days) |
| `GET /api/cards` | `?detail=1`, `?group=A`, `?loose=1`, `?ids=a,b` | id, day, group, mirrored, start, end, open, high, low, close, rangePips, bodyPips, highTime, lowTime, x, y, width, height (real prices also on a mirrored card); detail adds `line` (polylines of `[minute from card start, price]`) and `hours` (`[hh:mm, o, h, l, c]`) |
| `GET /api/cards/{id}` | | one card with detail |
| `GET /api/groups` | | name, color, count, frame `[x, y, width, height]`, sized, cards, absent |
| `PUT /api/groups` | `{"groups":[{"name":"A","cards":[...]}]}` | replace all groups, lay out the whole table, fit the view; a card in two groups or an unknown id is an error and nothing changes |
| `POST /api/group` | `{"name":"A","cards":[...],"replace":false}` | create the group or add cards; cards leave their old group; `replace` sets the exact list. Every group whose cards changed is packed in rows at its frame's top left corner, a new group goes below everything |
| `POST /api/ungroup` | `{"cards":[...]}` | cards leave their groups and go in rows below everything, their old groups are packed |
| `POST /api/rename` | `{"name":"A","to":"B"}` | |
| `POST /api/delete` | `{"name":"A"}` | cards become loose and stay where they are |
| `POST /api/clear` | `{}` | remove all groups, cards stay |
| `POST /api/order` | `{"groups":["B","A"]}` | order of the blocks for `arrange` |
| `POST /api/arrange` | `{}` or `{"group":"A"}` | lay out the whole table (frame sizes reset), or pack one group in place |
| `POST /api/move` | `{"cards":[{"id":"2026-01-05","x":0,"y":0}]}` or `{"group":"A","x":0,"y":0}` | put cards (top left corner) or a whole group (frame top left corner, cards move with it) at world coordinates; with a group, `width` / `height` size the frame, `"fit":true` makes it follow the cards again, `x` / `y` may be left out |
| `POST /api/mirror` | `{"cards":[...]}` or `{"group":"A"}`, `"mirrored":true` (default) or `false` | flip cards upside down or back |
| `POST /api/highlight` | `{"cards":[...],"fit":true}` | cards blink three times (orange), `fit` zooms to them |
| `POST /api/fit` | `{}`, `{"group":"A"}`, `{"cards":[...]}` | |
| `POST /api/reload` | `{}` | read the data again (new days) |
| `POST /api/tab` | `{"tab":"days"}` / `{"tab":"weeks"}` | |
| `POST /api/weekfill` | `{"show":true,"group":"A"}`, both optional | pale green background and a red dot on the week cards over the days of one group (default the first group), shows the Weeks tab (`docs/week-puzzle.md`, Day fill) |
| `GET /api/weeks.png` | | PNG of the Weeks tab as it is on screen |
| `GET /api/image` | `?group=A` / `?ids=a,b` / `?loose=1`, `&cols=6`, `&scale=0.25` | PNG of the cards in a grid, scale 0.25 = 240 px per card (the size of the user's example) |
| `GET /api/view.png` | | PNG of the Days tab as it is on screen |

Prices are real prices (1.33961), pips are 0.0001. Errors come back as
`{"error": "..."}` with status 400 / 404 / 500.

Notes for clients:

- http.sys answers 411 to a POST without a body, so send `{}`.
- curl on Windows passes command line arguments in the ANSI code page, so
  a Cyrillic group name in `-d '...'` arrives broken. Put the body in a
  UTF-8 file and send it with `--data-binary @file.json`, or use Python
  (`py -I`, `urllib.request`). Percent-encode names in URLs.

## Saved state

`day-puzzle.json` next to the FXViewer `data` folder (the app folder,
like `notes.json` and `game-days.json`): last tab, Start and Days of the
Days tab, groups with their colors, card ids and the rectangle of a sized
frame (`frame`: `[x, y, width, height]`, missing = follows the cards),
the position of every card (`positions`: id -> `[x, y]`, world units,
kept also for cards that are not loaded now), the ids of mirrored cards
(`mirrored`, also kept for cards that are not loaded now). Written to a temp file
and then moved over the old one. Ids of cards that are not loaded now
(another Start date) stay in the group as `absent` and come back when the
cards are loaded again. A file that cannot be parsed is copied to
`day-puzzle.json.bad-<time>` and the app starts with no groups.

## Toolbar

`Reload` (read the cards again for `Start` and `Days`, keep the groups
and positions), `Start` (first day, default 2026-01-05), `Days` (empty =
every complete day up to the end of the data), `Fit`, `Arrange`, `Clear
groups`, status line with card / group / ungrouped / mirrored counts and
the API address.

## Code

`DayCards.cs` (card geometry from minutes and the drawing), `DayArt.cs`
(vector drawing of a card), `DayBoard.cs` (groups, frames, positions,
arrange / pack / place below, export / import), `DayView.cs` (table, drag and drop, menus, PNG rendering),
`DayTab.cs` (toolbar), `DayStore.cs` (saved state), `PuzzleApi.cs`
(`HttpListener` server, every request runs on the UI thread),
`NameDialog.cs`. `MainWindow` holds the two tabs; the week puzzle moved to
`WeekTab.cs` without changes in behavior.
