# Play mode (trading game)

Status: implemented, v1 plus random day (2026-09-10), next day
(2026-09-11), game stats (2026-09-11), replay, your own time and the
Future button (2026-09-12).

## Goal

Trade the history by hand. One tab is put into **play** mode: it pretends
the current time is some minute in the past, hides everything after that
minute, and lets you buy, sell and place orders minute by minute. It is
the same chart with the same indicators - only the right side of the
world is missing.

- Play is **per tab**. One tab can play August 2019 while another shows
  the live chart.
- One game trades **every visible pair** of the tab at once. There is no
  "main" pair: switch a pair on in the symbol bar and it gets its own
  row with Buy and Sell in the popup.
- The play state (time, orders, positions, trade log) is stored in the
  tab and survives a restart.

## Switch it on

Right click the chart -> **Play**. The click column is the minute the
game starts from, snapped to the last candle at or before it (the latest
such candle over all visible pairs). The item is disabled when no base
pair is visible.

The item has a checkbox. While the game runs, the same item shows
**Play** checked - clicking it again stops the game. The `✕` in the
trading popup does the same.

**Play** always starts a **new** game at the minute under the cursor.
The previous game of the tab is dropped - its orders, positions and
closed trades are gone. Only the `SL` and `TP` boxes keep their last
values. There is no resume: to continue an old game do not stop it.

Right click -> **Play random day** starts a game for one whole trading
day picked by the app, see "Random day" below. While such a day game is
on, **Play next day** under it starts the trading day after the played
one, see "Next day" below, and **Replay day** plays the same day again
without counting it anywhere, see "Replay" below.

Right click -> **Game stats** opens a summary of all finished days, see
"Game stats" below.

## Moving in time

    P - one minute forward
    O - one minute back

The keys work while the chart has the keyboard focus (move the mouse
over it) and also while the popup itself is the active window, so a click
on **Buy** never takes the keys away. The popup has `◀` and `▶`
buttons that do the same.

A step goes to the **next candle that exists**, not to `+60` seconds, so
a Friday evening jumps straight to the Sunday opening and any gap in the
history is skipped in one press. With several pairs the step goes to the
nearest candle of **any** of them: the visible pairs plus every pair the
game has traded, so a hidden pair with an open position still moves.

When the play minute reaches the edge of the screen the chart scrolls so
that it sits at two thirds of the width again.

## What is hidden

Everything with a timestamp after the play minute:

- the candles of every series, at every zoom. A column that **contains**
  the play minute is recomputed from the minutes inside it, so a daily
  column at the cut shows the part of the day that already happened, not
  the whole day.
- the last price line of every series - it is drawn from the last candle
  at or before the play minute,
- the spread, volume, price age and entry point panels,
- the price density and order book profiles - their anchor is clamped to
  the play minute,
- computed pivot lines (ZigZag and friends). The last leg is only drawn
  up to its last pivot before the cut,
- the deal markers of the Deals indicator; a deal that closes after the
  cut is drawn as still open,
- the minutes in a selection: the `Space` statistics (min, max, pips,
  back) and **Align to max / min** only read the minutes up to the play
  minute. The time range in the statistics popup ends at the play
  minute, and an open popup is recomputed on every step.

What is **not** hidden, on purpose:

- your own drawings and forecast levels - you may want to draw into the
  future,
- calendar lines. The schedule of the news is known in advance in real
  life; the detail popup of a future event does show the released
  numbers, so do not hover it while playing,
- the last price of each pair in the right hand symbol bar - it is the
  price of the last loaded candle, taken at load time,
- a moving average built with "from future" - its value at the play
  minute uses candles after it.

### Look into the future

The **Future** button in the popup title switches the whole cut off and
back on. It is there in every game, a day game or a normal **Play**.

    Day 18-Aug-26 Tue  1:02:10   [Replay] [Future] [Notes] [✕]
    ◀ ▶  18-Aug-26 14:35  future

- With the future shown the chart is drawn as if no game was on: every
  series goes to the end of the loaded history, the panels, profiles,
  pivots, deal markers, selection statistics and the last price line all
  come back.
- The **game does not end** and nothing about it changes. `P` and `O`
  still step from the play minute, Buy, Sell and orders still fill at
  the play minute, and the day still finishes when a step reaches the
  end of the day.
- While the future is shown a dashed vertical line marks the play
  minute, so it is clear where the game stands, and the word `future`
  sits next to the game clock. The button is bold.
- The state is in the game (`FutureOpen`), so it is per tab and survives
  a restart. A new game, a next day and a replay all start with the
  future hidden.
- Nothing is written to `game-log.jsonl` about it: a day played with the
  future open counts in **Game stats** like any other day. Only the app
  log keeps a line:

      Play: the history after 18-Aug-26 14:35 is shown, the game keeps running

## Trading

### The popup

One row per visible pair, the pair name in its own colour (the pair
colour from Settings), then two market buttons:

    EURUSD   [↑ Buy: 1.08460]   [↓ Sell: 1.08441]   sp 0.8
    USDCHF   [↑ Sell: 0.88012]  [↓ Buy: 0.88025]   sp 1.3

- **Buy** is green, **Sell** is orange.
- The arrow shows where the **line on the chart** goes when the trade
  wins. A normal pair: Buy `↑`, Sell `↓`. The USDXXX pairs (USDCHF,
  USDJPY, USDCAD) are drawn upside down (mirrored), so there Buy is `↓`
  and Sell is `↑`, and **Sell comes first** - the `↑` button is always
  the left one.
- The price on a button is the price a market order gets right now, the
  worst ask (Buy) or the worst bid (Sell) of the play minute - see
  Fills. `sp` is the spread of the minute in pips.
- A pair with no candle at or before the play minute has both buttons
  greyed out.

Under the pairs are the `SL` and `TP` boxes (in pips, default `20`,
`0` = none) used by the market buttons, then one line per open position
and per pending order - pair, `B`/`S`, price, SL/TP - with a `✕` to close
or cancel it. A pair that was traded and then hidden keeps its lines
here, so its positions can still be closed. The totals below sum all
pairs.

The popup is a **window of its own** (`GamePanelWindow`, borderless, owned by
the main window, sized to its content), so it can be dragged out of the
main window - onto a second monitor, for example. Drag it by any free
spot on it: everything except the buttons, the two boxes and the
scrollbar. Where it was left is stored in `AppConfig.GamePanelLeft` /
`GamePanelTop` and reused the next time a game starts; a position that
no longer fits any screen falls back to the top right corner of the main
window. The window never steals the focus when it opens, is not in the
taskbar, and minimises and closes with the main window.

Prices are the real prices of the pair, not the mirrored display value,
so a USDCHF game shows USDCHF quotes.

### Pending orders

The right click menu, while the game runs, has one game item besides
**Play**: **Order**. It opens the order dialog:

    Pair        Level
    (•) EURUSD  [1.08340]
    ( ) GBPUSD  [1.27100]
    ( ) USDCHF  [0.88000]
    Stop loss   [20] pips, 0 = none
    Take profit [20] pips, 0 = none
    [↑ Buy] [↓ Sell]            [Cancel]

- One row per visible pair. Each level box is **filled from the mouse
  position, for each pair separately**: the price of that pair at the
  height where you right clicked.
- The pair nearest to the click is selected. Pick another one with its
  radio button or by clicking into its level box.
- `SL` and `TP` start from the popup boxes and apply to this order only.
- **Buy** / **Sell** place the order and close the dialog. They have the
  same colours, arrows and order as the popup buttons of the selected
  pair. `Esc` or **Cancel** closes without an order.

### Fills

The DB keeps one candle per minute: low, high, average and the **maximum
spread** of that minute (docs/spread.md). Low and high are bid,
`ask = bid + spread`. Fills use the worst price inside the minute, so a
minute never gives a better entry than it could have:

| action | fill |
| --- | --- |
| buy at market | `high + spread` (the worst ask of the minute) |
| sell at market | `low` (the worst bid of the minute) |
| buy order at level `P` | `P + spread` |
| sell order at level `P` | `P` |
| close a long by hand | `low` |
| close a short by hand | `high + spread` |

A pending order triggers when the **bid** of a minute touches its level
(`low <= P <= high`) - the level you draw on the chart is a bid level,
because that is what the chart draws.

If a minute carries no spread, the last known spread is used (1 pip at
the very start of a game).

The game minute is shared by all pairs, but a quiet pair may have no
candle in that exact minute. A market order or a close on such a pair
uses its last candle before the play minute (the same worst price rule),
and its checks start with its next candle. A pending order placed then
waits for that next candle too. With "Show ask instead of bid" on
(docs/ask-view.md) the candles already hold ask prices; the game
subtracts the spread again so the fills stay the same.

### Stop loss and take profit

Both are set at creation - from the popup `SL` and `TP` boxes for a
market order, from the order dialog for a pending order - and measured
in pips from the fill price:

- long: stop at `fill - SL`, take at `fill + TP`, both checked against
  the **bid** (`low`, `high`),
- short: stop at `fill + SL`, take at `fill - TP`, both checked against
  the **ask** (`low + spread`, `high + spread`).

So a long pays the spread on the way in and a short on the way out,
exactly like a real account.

Rules of the check:

- a position never closes in the minute it was opened - the checks start
  with the next minute. The fill is already the worst price of its own
  minute; taking the worst move of the same minute as well would be
  double counting.
- if the stop and the take are both inside one minute, the **stop**
  wins.

Profit is in pips: `close - fill` for a long, `fill - close` for a
short. The popup shows the closed total, the floating total of the open
positions and their sum. The floating profit uses the average price of
the play minute.

## On the chart

Every mark is drawn on the line of its own pair.

- open position: a solid line at the entry price from the entry minute
  to now, a red dotted line at the stop, a green dotted line at the
  take, and a triangle at the entry. The triangle points the same way as
  the arrow of the button: up = the line goes up when the trade wins,
  so a USDCHF buy has a triangle pointing down,
- pending order: the same line, dotted, at its level,
- blue = buy, purple = sell, the same two colours the popup uses for the
  `B` and `S` of each row,
- closed trade: entry triangle, exit disc and a connector between them,
  green when the trade won, red when it lost.

## Real time

The popup title shows how long the game runs in **real** time, next to
the word `Play` (or the day, see below): `4:35`, then `1:02:10` after an
hour. It counts from the moment the game started (wall clock, so a
closed app still counts) and stops when a random day is finished. A game
saved before this feature has no start time and shows nothing. How much
of it was really spent at the game is a second clock, see "Your own
time" below.

## Your own time

The real time clock above counts the wall clock: it keeps running while
you make coffee. Next to the game minute the panel shows **your own
time** - the time you really spent at the game:

    ◀ ▶  18-Aug-26 14:35   own 1:23:45

It runs from the moment the game panel opens until it closes, so one
number covers the game, its replays, the next days and everything in
between (reading the chart, drawing lines, writing the comment). It
stops while you are away.

### When it counts

Every second the app looks at two things:

- the last input on the **computer** (`GetLastInputInfo`: a mouse move or
  a key press in any program, not only in FXViewer),
- whether **FXViewer is the window in front** - the main window, the game
  panel or any of their dialogs.

Your own time only ever reaches the last moment when both were true. So
a minute of reading the chart without touching the mouse is counted as
soon as you move it again, but a minute that ends in nothing is not.

### Sleep

Time while the computer sleeps is never counted: played, closed the
laptop, opened it 8 hours later - your own time stops at the last action
before the lid was closed.

This laptop has no classic sleep (S3), only Modern Standby (S0 Low Power
Idle) and hibernate. In standby Windows freezes FXViewer: on 2026-09-13
the app log has no line at all from 10:51 to 12:30, the time between the
standby events 506 and 507 of `Kernel-Power`. The clock the counter uses
(`Environment.TickCount64`) keeps running through standby and hibernate,
so the first tick after waking sees that more than a minute passed since
the tick before.

That is the rule: **if FXViewer did not run for more than a minute**
(standby, hibernate, the app frozen), nothing of that time counts. The
clock stops at the last action before the gap and "Still here?" opens
with the reason "the computer was asleep, or FXViewer did not run, for
more than a minute".

The rule does not look at the input, and that matters. When the laptop
wakes, the key press or the touchpad move that woke it reaches FXViewer
before the first timer tick (Windows hands out `WM_TIMER` only when no
input is waiting). Without the gap check the counter saw "input right
now, FXViewer in front" and added the whole night; a test reproduced
exactly that (+28 900 s) before the fix.

A gap shorter than a minute is a normal tick: while you are active it is
counted like a minute of reading the chart.

### Still here?

After **one minute** without input, one minute with FXViewer in the
background, or a gap of more than a minute when FXViewer did not run
(see "Sleep"), the clock stops and a blocking popup opens:

    Still here?
    Your time at the game stopped because there was no mouse move or key
    press for a minute.

    Stopped at   14:32:07
    Paused for   3:42
    Your time    1:23:45

                                       [ I am back ]

- Nothing is counted while the popup is up, and the minute before it is
  **not** counted either: the clock only ever reached your last real
  action.
- The popup does not steal the focus. If you went to another program it
  waits until you come back to FXViewer.
- `Enter`, `Esc` or the button closes it and the clock starts again.
- While the clock is stopped the panel shows `own 1:23:45 paused`.
- If the popup cannot open (another dialog blocks it) the clock still
  stops, starts again by itself at your next action, and the app log gets
  one line about it.
- The app log gets one line when the clock stops and one when it goes on:

      Play: your time stopped at 10:43:29 because the computer was asleep,
      or FXViewer did not run, for more than a minute
      Play: your time goes on, away 1:47:16

The rules are in `GameTimeCounter` (`Game/GameTimeCounter.cs`), one small
class without any UI, so they can be tested with a fake clock;
`MainWindow.GameTime.cs` only feeds it the real numbers once a second and
opens the popup.

### Where it is kept

`game-time.jsonl` next to `config.json`, one line per session of the
panel, written when the panel closes (`✕`, a tab without a game, or the
app closing):

    StartedAt      real time the panel opened
    EndedAt        real time it closed
    Seconds        the time the panel was open: EndedAt - StartedAt minus
                   every gap of more than a minute when FXViewer did not
                   run (standby, hibernate), so a night in standby is not
                   in it
    ActiveSeconds  your own time
    Games          finished day games of this session (a replay is not one)
    Pauses         how many times "Still here?" opened
    Days           the days played in this session

Every finished day also gets an `ActiveSeconds` in `game-log.jsonl`, next
to the wall clock `Seconds`: your own time of that day alone. A replay
writes no line at all, as before. The day line is written the moment the
day ends, so the time you spend on the comment afterwards is in the
session total but not in that day's line.

A crash loses the running session - the line is only written at the end.

The older clocks still count sleep: the real time next to the title of
the popup and `Seconds` of a day in `game-log.jsonl` are wall clock from
the start of the game to its end, as described in "Real time" above.
Session lines written before 2026-09-13 also have wall clock `Seconds`.

### In Game stats

A **Your time** block: sessions, your own time total, average session,
the time the panel was open and what share of it is yours, how many away
pauses, and per finished game the average and median of your own time.
The **Real time** table gets a `Your min` column: your own time of the
sessions that started on that date, next to the wall clock minutes of the
games of that date.

## Random day

Right click the chart -> **Play random day**. The item is enabled when
at least one base pair is visible, and it works the same while another
game runs (that game is dropped, like with **Play**).

### Which days

- From **1 Jan 2026** to the **Friday of the previous week**. The week
  starts on Monday, so on Thursday 10 Sep 2026 the last day is Friday
  4 Sep.
- Monday to Friday only.
- A day needs at least 60 candles of one visible pair between 00:00 UTC
  and the end of the day. This drops holidays without data (1 Jan 2026).
- Before the pick the app loads the history of the visible pairs for
  these years (the loader job reason is `random day`), so a chart that
  shows 2019 still sees the 2026 days.

### Which day is picked

Every finished game of a day is one line in the game log (see below).
The pick counts the lines per day, takes the days with the **smallest**
count and picks one of them at random. So the app first goes through all
days that were never played, then through the days played once, and so
on. A game that was stopped before the end of the day is not in the log
and does not count.

The random number comes from the operating system generator
(`RandomNumberGenerator.GetInt32`), not from `Random.Shared`. On
2026-09-10 twelve picks in a row landed in January - April with
`Random.Shared`, while the same code on the same data outside the app gave
an even spread over all months. The cause was not found, so the pick now
uses the OS generator and writes every detail to the log:

    Play random day 17-Jun-26 Wed: EURUSD, GBPUSD; period 2026-01-01 ..
    2026-09-04, 176 trading days (2026-01-02 .. 2026-09-04), drawn #108
    of 0..169 among the days played 0 time(s)

`#108` is the position in the list of the least played days, sorted by
date. If the picks look bunched again, these numbers show whether the
list or the random number is wrong.

After the pick a small window shows what was picked; `Enter` or `Esc`
closes it:

    Period from    01-Jan-26 Thu
    Period to      04-Sep-26 Fri
    Trading days   176 in the period, 170 never played
    Picked day     17-Jun-26 Wed

### The game

- The game starts at **23:59 UTC of the day before**: the day itself is
  hidden, the days before it are visible.
- The day ends at the **last minute of the American session**: 20:59
  UTC in US summer time, 21:59 UTC in winter (docs/sessions.md). The
  step that reaches or passes this minute ends the day. The step stops
  exactly at the last minute even when the next candle is later.
- At the end every open position of every pair is closed at that minute
  with the normal close rule (a long at the low, a short at the high +
  spread), and every pending order is cancelled. The close reason of
  these trades is `day end`. In the model this is one `dayend` action per
  pair that still had something open.
- After that the game is **finished**: `P`, `O`, `◀`, `▶`, the trade
  buttons and **Order** do nothing (they are greyed out or gone). The
  chart keeps the cut at the end of the day. `✕` (or **Play**) closes
  the game as usual, and **Play random day** or **Play next day**
  starts the next one.
- The popup title shows the day: `Day 18-Aug-26 Tue`.

### Next day

While a day game is on in the tab - still running or already finished -
the right click menu has **Play next day** right under **Play random
day**. It starts a day game for the first trading day **after** the
played one, with the same rules as a random day: weekends are skipped,
and so is a day with less than 60 candles of every visible pair. Friday
goes to Monday, a holiday without data (like 1 Jan) is skipped.

- The day is not random, so there is no pick window. The log line is:

      Play next day 07-Sep-26 Mon after 04-Sep-26 Fri: EURUSD, GBPUSD

- The game itself is a normal day game: it starts at 23:59 UTC of the
  day before, the draft of the new day is cleared, the comment and the
  day drawing are hidden until the end. A running game is dropped (not
  logged), like with **Play random day**.
- A finished next day game is a normal line in the game log, so it
  counts for the random pick as well.
- The next day must be inside the same period as the random days, up to
  the Friday of the previous week. When the played day is that Friday,
  the item is greyed out; it comes back on Monday, when the period
  grows by one week.
- The item is not in the menu in a normal **Play** game and after the
  game was closed with `✕`.

`GameDayPicker.NextDay` walks the days after the played one up to the
end of the period and returns the first weekday with candles (the same
test as `Candidates`). `MainWindow.RefreshGame` passes two flags to
`ChartView.SetPlay`: a day game is on, and the played day is before the
end of the period (`GameDayPicker.HasNextDay`). The click loads the
history of the visible pairs first (loader reason `next day`), like the
random pick does.

### Replay

A day can be played again as if that run never happened: the **Replay**
button in the popup title, or right click -> **Replay day**. Both are
there for a day game only, running or already finished.

    Day 18-Aug-26 Tue  1:02:10   [Replay] [Future] [Notes] [✕]
    ◀ ▶  18-Aug-26 14:35  replay

- The day starts over from 23:59 UTC of the day before with the same
  pairs. The trades, orders and positions of the run before are gone,
  like with **Play** - only `SL` and `TP` keep their values.
- The replay is **not** written to `game-log.jsonl`. So it does not
  count for the random pick (the day keeps the play count it had), it is
  not in **Game stats**, and its real time is counted nowhere. The end
  of the day says so in the app log:

      Replay: day 18-Aug-26 Tue finished at 18-Aug-26 20:59, +12.0 pips
      in 3 trade(s), real time 41:12, not in the game log

- The **draft** drawing of the day is kept, unlike a fresh start of the
  day which deletes it, so the lines of the run before are still there.
  The comment and the day drawing are untouched too, and hidden again
  until the end of the day (or **Notes**).
- The word `replay` next to the game clock shows that this run does not
  count. The real time clock starts from zero again.
- A replay of a replay is a replay again. **Play next day** started from
  a replay is a normal game and is logged.
- The time of a replay is in **your own time** of the session (see "Your
  own time" above); only the game log ignores a replay.

### Game log

`game-log.jsonl` next to `config.json` (`AppConfig.Dir`), one JSON
object per line, one line per finished day. A line is appended when the
day ends; a broken line (a crash in the middle of a write) is skipped on
read and the next write starts on a new line. A replay writes no line at
all.

    Day        the played day, 2026-08-18
    StartedAt  real time the game started, local time with offset
    EndedAt    real time the day ended
    Seconds    EndedAt - StartedAt
    FromUtc    game start, "2026-08-17 23:59"
    ToUtc      game end, "2026-08-18 20:59"
    Pairs      the pairs of the game (visible and traded)
    Pips       sum of the closed trades
    Wins, Losses
    Trades     Id, Symbol, Side, OpenUtc, OpenPrice, CloseUtc, ClosePrice,
               Reason (stop / take / manual / day end), Pips
    Actions    every user action: TimeUtc, Kind (market / order / cancel /
               close / dayend), Symbol, Side, Price (order level),
               StopPips, TakePips, Id, Target

Zero and empty values are left out of the JSON. Prices are real prices
with 5 decimals. An order that was filled became the trade with the same
`Id`.

### Comment of the day

Every day has **one** comment, shared by all games of that day (a day
played 10 times still has one comment).

- While the day is played the comment is **hidden**.
- It opens by itself when the day ends, and the **Notes** button in the
  popup title shows or hides it at any time. The button is only there in
  a random day game, and it is bold while the notes are open.
- It is a normal text box at the bottom of the popup: type freely, it
  is saved a moment after the last key and when the box loses the focus.

Under the box the popup lists the **chart comments** of that day
(docs/comments.md): `HH:mm`, pair, title, sorted by time. It is the same
block as the comment of the day, so **Notes** hides both and nothing
about the day leaks while it is being played.

### The two drawings of the day

Every day has two drawings, both stored per pair:

- **draft** - for the current game. It starts empty: every start of a
  game of this day deletes its lines - except a **Replay**, which keeps
  them. It is always visible during the game.
- **day** - kept forever for this day, like the comment but as lines.
  It is shown together with the comment: hidden while you play, visible
  after the end of the day or after **Notes**.

Drawing works like a Drawing indicator (docs/drawing-symbols.md): right
click near a pair -> `Draw line - EURUSD · draft`, `Draw level - EURUSD ·
draft`, and while the notes are open also `Draw line - EURUSD · day` and
`Draw level - EURUSD · day`. Only the pair nearest to the click gets these
items, so the menu stays short. Select, move, delete, clone and flatten
work as for any line. Draft lines have the pair colour, day lines a
darker shade of it. A hidden pair hides its game lines too.

Storage: `game-days.json` next to `config.json`:

    { "2026-08-18": { "Comment": "...",
                      "Drawing": { "GBPUSD": [[[unix, value, 1], ...]] },
                      "Draft":   { "EURUSD": [[[unix, value], ...]] } } }

The line format is the drawing.json format. Written through a `.tmp`
file; a broken file is moved to `.bad`.

In the chart the game drawings are extra drawing series (`SymbolSeries`
with `GameDrawing` = `draft` / `day`, symbol `EURUSD · draft`) that
`ChartView.SetGameDrawings` appends to the series list. They are not in
the symbol bar and not in the indicator list. `MainWindow.RefreshGame`
rebuilds them for the visible pairs; the call does nothing when nothing
changed, so a step does not drop the selected line. `OnDrawingCommitted`
and `OnDrawingLinesChanged` send their lines to `game-days.json` instead
of `drawing.json`.

### Model

`GameState` got `Day` (empty for a normal game), `EndUnix`, `StartedAt`,
`EndedAt`, `Finished`, `NotesOpen`, `FutureOpen`, `Replay` and
`ActiveSeconds` (your own time of this game). They are saved with the tab,
so a random day survives a restart, finished or not, and a replay stays a
replay.

`ChartView` hides the future through one place, `PlayCutUnix`, built from
`PlayLimitUnix` - the play minute, or `0` while `FutureOpen` is on. So
one flag turns off the candle cut, the clamped panels and profiles and
the clamped selection statistics at once.

## Game stats

Right click the chart -> **Game stats**, the last of the Play items. It
is enabled when a base pair is visible, like **Play random day**. The
window shows a summary of `game-log.jsonl`. It is not modal, so it can
stay open while you play, and it is refreshed when a day game ends.
`Esc` closes it.

**Days** - the same period and the same trading days as the random pick
(the history of the visible pairs is loaded first, loader reason
`game stats`):

- trading days in the period, how many of them were played,
- how many days were played 0 times (never), 1 time, 2 times, ... up to
  the most played day,
- one row per month: trading days, played days, games, and a bar with
  the share of played days. It also shows at a glance whether the
  random pick spreads over the months.

**Real time** - from `Seconds` of the log lines. It is wall clock time,
so a break in the middle of a game is counted too; the median shows the
typical game without such breaks.

- finished games, total minutes, average and median minutes per game,
  the fastest and the slowest game,
- rest of the days: never played days x the average game,
- one row per real date, newest first: games, minutes, pips.

**Trades** - from `Trades` of the log lines:

- trades and trades per game, won trades (pips > 0) and their share,
- pips total, per game and per trade, games in profit and in loss,
- the best and the worst game,
- hold time: entry to exit in game time, average and median,
- one table with trades, win %, pips and pips per trade, split by close
  reason (take / stop / manual / day end), side, pair, entry session and
  play of the day.

The entry session uses the session bands of the chart
(docs/sessions.md): Asia (before the European open), Asia + Europe,
Europe, Europe + America, America, Night (after the American close).

Play of the day: `first play` is the first finished game of a day in
the log, `replay` is every later game of the same day. A replay can be
better only because the day is remembered, so the table keeps the two
apart. A game started with the **Replay** button is not in the log at
all, so it is in neither row.

A game stopped before the end of its day is not in the log, so neither
its trades nor its time are counted.

`GameStats.Build` (`Game/GameStats.cs`) computes everything from the log
entries and the list of trading days, `GameStatsWindow` only draws it.
`MainWindow.ShowGameStatsAsync` builds the day list with
`GameDayPicker.Candidates`, the same call the random pick uses.

## Model

`GameState` (`Game/GameState.cs`) is stored in `ChartTab.Game` and holds
the start minute, the current minute, the default SL/TP and a list of
**actions** - what the user did, on which pair and when:

    market  pair, buy/sell, SL, TP
    order   pair, buy/sell, price, SL, TP
    cancel  pair, order id
    close   pair, position id

Ids come from one counter per game, so they are unique over all pairs.

Positions, orders and the trade log are **not** stored. `GameSim.Run`
(`Game/GameSim.cs`) replays the actions of one pair over the candles of
that pair from the start minute to the current minute and returns the
state at that moment. `MainWindow.GameBooks` runs it once per pair: the
visible pairs plus every pair that has an action. So

- stepping back with `O` really goes back: the actions of the minutes
  after it are kept in the list but not applied yet, and they come back
  when you step forward again,
- the same actions over the same candles always give the same result;
  there is no state that can drift.

A cancel or a close whose target no longer exists is ignored, which is
what happens when an order was filled during the minute you cancelled
it - the fill happens first, and the popup already shows it as a
position.

The replay walks the minutes between the start and now on every change,
for every pair. That is a few thousand candles per pair, far below one
frame.

A game saved before the multi pair version has actions without a pair;
they are ignored. Starting a new game drops them anyway.

## The cut in the chart code

`ChartView.PlayCutUnix` is `play minute + 60` - the first second that
must not be visible - and `long.MaxValue` when the game is off. It is
passed as `maxUnix` into `ChartColumns.BuildView` / `BuildLine` and into
the four panel builders.

`ChartColumns.ColumnEdges` clamps every column edge to `maxUnix`, so a
column that is completely in the future gets an empty time range and
stays empty, and the column holding the cut only reads the part before
it. When the columns come from an aggregated level (15 min, 1 h, 4 h,
1 day) the block holding the cut would still carry future minutes, so
`FixCutColumn` recomputes that single column from the raw minutes.

`MergeLive` drops live candles at or after the cut.

The selection code reads candles straight from the history, so
`ComputeRangeStats` (the `Space` popup) and `RangeExtreme` (Align to
max / min) clamp their end to `PlayCutUnix - 1`, and `PlayClampEnd`
clamps the time range shown in the popup.

Nothing else in the chart knows about the game, and with the game off
every path is byte for byte the one that was there before.

## Not in v1 (next steps)

- Editing the SL and TP of a position that is already open.
- Position size and money. Everything is in pips, all trades are the
  same size.
- A trade list with the closed trades in the popup (only the counters
  are there now).
- A faster step: `Shift+P` for an hour, a play button that runs by
  itself.
- A window with the played days (log lines, comments, day drawings)
  outside a game. Now the comment and the day drawing of a day are only
  reachable in a game of that day.
- Your own Drawing indicators stay visible in a random day. Lines drawn
  later can show where the price went: switch them off in the symbol bar
  before you play.
