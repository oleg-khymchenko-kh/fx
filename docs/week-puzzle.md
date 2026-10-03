# Week puzzle

Status: implemented, v1 plus the hint menu and the Sort button (separate
WPF app `src/Fx.WeekPuzzle`, 2026-09-26), 100 pip grid (2026-09-27), lines
from a Drawing indicator instead of the ZigZag (2026-09-30).

## Goal

A game that trains the memory of the market. Every piece is a week card
of one pair, the same picture as the "week picture" (blue ZigZag line with
the inserted closes, day lines, red min-max bars, gray ticks at the edges).
The cards are scattered on a table in random order. The player drags cards
together so that they form the real sequence of weeks: the right edge of
one week (its close) meets the left edge of the next week (its previous
week close). The table zooms in and out as a whole.

## Cards

- One card = one trading week, Sunday 21:00 UTC .. Friday 21:00 UTC (US
  summer, one hour later in winter, `SessionClock.AmericaCloseHourUtc`).
- The blue line on a card is the user's own drawing: the polylines of a
  Drawing indicator (default `GBP-Drawing-3`, source GBPUSD) read from
  `data/<INDICATOR>/drawing.json`. Every polyline is clipped to the card's
  time window (previous week close .. Friday close, weekend-free time), a
  line that crosses the window edge is cut at the edge, levels are drawn
  like any other line. A week with nothing drawn shows only candles, day
  lines, grid and ticks. Points of the drawn lines take part in the card's
  price range, so a line drawn above the week's high makes the card
  taller.
- The 5th command line argument picks another Drawing indicator, or
  `zigzag` for the computed line of the week picture: ZigZag 50/20/90 over
  the minutes (wide-spread minutes skipped, as with `HideWideSpread` on),
  plus the previous week close, every day close, every day high and low and
  the opposite extreme between the later extreme and the day close. The
  window title names the line source.
- Same vertical scale on every card: 10 px per pip (1000 px = 100 pips,
  the week picture uses 5), 30 px margins, 2000 px wide. Card height
  follows the range of the week, so cards differ in height.
- The gray tick on the left edge marks the previous week close, the tick on
  the right edge marks the week close. When two cards are joined correctly
  the ticks meet at the same height and the line continues across the
  edge.
- Cards are drawn as vectors (one filled polygon for the bars, one polyline
  for the ZigZag), so they stay crisp at every zoom. Line widths keep a
  minimum size in screen pixels when zoomed out; the bar color gets more
  opaque below zoom 0.5 so the band stays visible on a small card.
- Light gray horizontal lines at every round 100 pip price (1.3400,
  1.3500, ...) run across the whole card width, no labels. Two cards joined
  correctly continue each other's grid lines across the edge.
- No text on the cards, no dates: the player recognizes the weeks by their
  shape, by the levels at the edges and by where the grid lines fall.

## Data

- Minutes come straight from the `.m1` files of the FXViewer database
  (`data/<SYMBOL>/<year>.m1`, read-only with shared access, so the running
  app is not disturbed). The ZigZag code is the app's own
  `ZigZagSymbol.BuildPoints`, compiled into the game from the shared source
  file (`Compute/ZigZagSymbol.cs`, together with `Storage/Candle.cs`,
  `Storage/SpreadCodes.cs`, `Chart/WeekendCompressor.cs`,
  `Chart/SessionClock.cs`).
- The lines of the indicator come from two places, merged: its
  `data/<INDICATOR>/drawing.json` and the snapshots of that indicator in
  every note of `notes.json` in the app folder (FXViewer saves a line drawn
  while a note tab is open into the note, not into drawing.json). Indicator
  names are matched like the app does (letters and digits only, case
  ignored). Exact duplicate lines are dropped; a line that was edited in a
  note while an older copy stays in drawing.json is drawn twice.
- Both files are read with shared access and parsed with the app's own
  `DrawingStore.FromRaw`; the game never renames a broken file (the app's
  `DrawingStore.Load` does), so the live data of FXViewer is safe. An
  indicator found in neither place is reported in a message box.
- In `zigzag` mode the ZigZag is built from 60 days before the first card,
  so its state at the first card is the same as in the app.
- Weeks with less than 600 minutes of data are skipped.

## Table

- Cards are laid out on a grid in random order with random offsets inside
  the cells, so nothing overlaps. `Fit` (button or `F`) shows all cards.
- Mouse wheel zooms around the pointer (0.02 .. 4). Left or middle drag
  on the table pans.
- Left drag on a card moves the card together with everything it is
  connected to. The dragged card gets a blue frame and its chain comes to
  the top.
- Ctrl + left drag takes one card out of its chain (the neighbours stay
  where they are, the chain splits).

## Connecting

- On drop, the dragged chain looks for a free edge of another chain:
  its head's left edge against a free right edge, its tail's right edge
  against a free left edge. The candidate with the smallest distance
  between the edges and the ticks wins, if that distance is within 30
  screen pixels. The chain then jumps into place: edges touch, ticks at the
  same height.
- Any two cards can be connected, right or wrong. The game does not stop a
  wrong link, that is the puzzle.
- `Check` (button or `C`) paints every junction: green when the right card
  is really the next week, red otherwise. The marks stay until a card is
  picked up again. The status line counts links, right links and how many
  checks were used.
- Solved = all cards in one chain in the right order. The status line then
  shows the time since the game started.

## Hint

Right click on a card opens a menu with one item, `Hint`. The card that
belongs to the free side of the clicked card blinks three times with an
orange frame and fill (about 1.3 s) and comes to the top: the next week
when the right side of the clicked card is free, otherwise the previous
week when the left side is free. A card with both sides taken gives no
hint. The blinking card is not scrolled into view; press `F` when it is
off screen. Hints are counted in the status line, like checks.

## Sort

`Sort` (button or `S`) lays the loose cards out in three blocks, one under
the other, and leaves every connected card where it is. A card counts as
loose when it has no neighbour on either side. The blocks start below the
connected cards (or at the origin when nothing is connected), rows of at
most 8 cards, 150 px between cards, a wider gap between blocks; the view
fits everything afterwards.

- Rise: week close at least 60 pips above the week open (open = first
  minute of the week, Sunday evening). Strongest rise first.
- Flat: close within 60 pips of the open. Sorted by the size of the weekly
  candle, high minus low, biggest first.
- Fall: close at least 60 pips below the open. Strongest fall first.

The 60 pip border is `PuzzleView.BigWeekPips`; with the 2026 GBPUSD weeks
it gives roughly a third of the cards in each block.

## Options

    Fx.WeekPuzzle.exe [dataRoot] [symbol] [firstMonday] [weeks] [drawing|zigzag]

Defaults: FXViewer debug data folder, `GBPUSD`, `2026-01-05`, all complete
weeks up to the last one in the data, lines from `GBP-Drawing-3`. The toolbar has the same `Start`
date and `Weeks` count (empty = all); `New game` reloads the cards for
them and scatters them again. A start date that is not a Monday is moved
back to its Monday.

## Not in v1

- Random start week from the whole history, several pairs at once.
- Hints (show the answer, highlight the next card), scores and a log of
  games.
- Saving a game in progress.
