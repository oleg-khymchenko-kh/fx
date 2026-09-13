# Zoom levels

Status: implemented, v1.

## Goal

The plain mouse wheel over the chart no longer zooms step by step. It
switches between a list of saved **zoom levels**. A level is one pair of
numbers:

- **horizontal** - pixels per day (can be fractional, e.g. `1.5`)
- **vertical** - pixels per 100 pips

Wheel up = next level (deeper zoom), wheel down = previous level. The
point under the mouse cursor stays in place, the same way the old zoom
worked.

`Shift + wheel` and `Ctrl + wheel` keep their old meaning and never
change the level:

- `Shift + wheel` - horizontal zoom only
- `Ctrl + wheel` - vertical zoom only

Everything else on the wheel is untouched: wheel over a volume band
still scales the volume bars, `Alt + wheel` over a volume band still
changes the volume group, and `Alt + wheel` with a tilted grid on still
rotates the grid.

## The widget

`ZoomLevelView` is a small chip in the very top left corner of the chart
(same grid cell as the chart, 2 px margin; `LoadIndicatorView` sits in
the top right). It hides while the `Q` measure popup covers that corner,
see `docs/measure-distance.md`. It shows

    Zoom 7/20

If the current zoom is not the one saved in level 7 - because the user
moved it with `Shift + wheel` or `Ctrl + wheel` - two small icons appear
next to the label:

- **save** (floppy) - writes the current horizontal and vertical zoom
  into level 7
- **revert** (`↺`) - throws the change away and puts the chart back on
  the zoom already saved in level 7

Both icons are shown only while the current zoom differs from the saved
one, and both disappear as soon as the two match again.

"Differs" is not a plain comparison against the stored numbers. The
chart clamps what a level asks for - `columnSeconds` can never be
coarser than the full history (`fit`), and `ApplyVerticalZoom` clamps
`pointsPerRow` to `0.05 .. (globalRange * 10 / height)`. A level that
asks for more than the chart can give would then never match its own
numbers, the icons would never go away and revert would look dead. So
the chart also remembers the zoom it **actually reached** the last time
a level was applied or saved (`MarkZoomApplied`), and counts as clean
when the current zoom matches either that snapshot or the stored
numbers. `RestoreState` and `SetZoomLevels` drop the snapshot
(`ForgetAppliedZoom`), so a fresh tab falls back to the plain
comparison.

All clickable parts of the widget and the popup are real WPF `Button`s
with a flat `ControlTemplate`. `Button` handles mouse down and up
itself, so a click on an icon can never leak through to the widget and
toggle the popup.

Clicking the label opens a popup with the whole list. It stays open
until the user clicks outside it or clicks the label again:

    #    px/day   px/100 pips
    1  [     1 ] [       28 ]  [+] [x]
    2  [   1.5 ] [       32 ]  [+] [x]
    ...
    [+  Add level at the end]
    Current: 96 px/day, 190 px/100 pips

- clicking the **number** applies that level and closes the popup
- both value cells are text boxes - type a new number and press `Enter`
  (or click away) to store it, `Esc` puts the old value back. A value
  that is empty, not a number or not positive is rejected and the old
  one comes back.
- a typed **px/day** is normalized through `Snap` before it is stored,
  so the box shows what the chart will really render (type `100`, get
  `102.86`). **px/100 pips** is stored as typed.
- `[+]` inserts a **new** level in front of that row, filled with the
  current chart zoom - so `[+]` on row 1 inserts at the start, on any
  other row in the middle
- `[x]` deletes the level; the last remaining level cannot be deleted
- the button under the list appends a level with the current zoom

Anything that changes the list is written to `config.json` right away.
Editing the values of the **active** level applies them to the chart
straight away, so what you type is what you see. Editing any other
level only stores the numbers.

The widget itself opens and closes the popup on mouse **up**, not down -
a popup opened on mouse down would be closed again by the release
(`StaysOpen` is false, so the popup owns the mouse capture while it is
open).

## Units and conversion

The chart keeps its zoom in two internal numbers:

- `_columnSeconds` - seconds of chart time per pixel column
- `_pointsPerRow` - price points per pixel row (1 pip = 10 points)

`ZoomLevel` converts between those and the display units:

    pixelsPerDay    = 86400 / columnSeconds
    columnSeconds   = Snap(round(86400 / pixelsPerDay))
    pixelsPer100Pips = 1000 / pointsPerRow
    pointsPerRow     = 1000 / pixelsPer100Pips

`ChartColumns.Snap` is what makes `columnSeconds` a value the chart can
actually render: above one minute it snaps to the 15 min / 1 h / 4 h /
1 day bands, below one minute to the fixed sub-minute steps (see
[time-zoom.md](time-zoom.md)). Because saving always stores
`86400 / columnSeconds`, applying a saved level gives back exactly the
same `columnSeconds`.

The dirty check normalizes both sides through `ToColumnSeconds`, so a
level counts as saved when the snapped column seconds match and the
points per row match within a tiny tolerance.

## Limits

A level means exactly what it says: `ApplyZoomLevel` does **not** clamp
`columnSeconds` to the fit width. If a level asks for fewer pixels per
day than the whole history needs, the history simply occupies part of
the window and the rest stays empty - `ClampViewStart` already allows a
view narrower than the window. Clamping to `fit` was the first version
and it was wrong: every level below the fit scale collapsed into the
same picture, so two different levels looked identical and revert
looked dead.

The popup footer prints the fit scale (`ChartView.FitPixelsPerDay`) so
it is clear where that boundary is:

    All history fills the width at 0.27 px/day - below that the chart
    leaves empty space

Because a level may sit below the fit scale, the code that re-anchors
the view must read the **current** `_columnSeconds`, never `fit`. An
earlier version wrote

    long cs = _columnSeconds <= 0 || _columnSeconds > fit ? fit : _columnSeconds;

which mixed two scales: `startBucket` stays measured in
`_columnSeconds`, so `(startBucket + xm) * fit` gave an anchor time that
was `fit / _columnSeconds` of the real one - years off. The view then
hit the left `ClampViewStart` limit and the time under the cursor jumped
into the empty space before the first candle. `fit` is now used only
when there is no explicit zoom yet (`_columnSeconds <= 0`).

`Shift + wheel` shares that code. Its "never zoom out past fit" branch
now fires only while the chart is at or below the fit scale, so a level
that is coarser than fit can still be zoomed with `Shift + wheel`
instead of collapsing to the fit view.

The vertical still has a ceiling, but a wide one. `DrawPriceLines`
walks the whole visible price range in 100-pip steps, so a very large
`pointsPerRow` means a very long loop per frame.
`ApplyLevelPointsPerRow` allows the larger of

- `globalRange * 10 / height` (what `Ctrl + wheel` allows), and
- `MaxLevelGridLines (512) * 1000 / height`

which keeps the grid loop bounded while letting a level zoom out well
past the data range. `Ctrl + wheel` keeps its own old limit -
`ApplyVerticalZoom` is untouched.

## Custom zoom per tab

A tab can multiply the vertical part of every level by its own factor -
see the **Tab properties** section of [tabs.md](tabs.md).
`ApplyZoomLevel` divides the level's points per row by it,
`SaveCurrentZoomToLevel` and `InsertZoomLevel` divide the current
px/100 pips by it before storing, and the dirty check uses the scaled
target. With the factor at `1` (the default) nothing changes.

## Storage

The list is global, not per tab - `AppConfig.ZoomLevels`:

```json
"ZoomLevels": [
  { "PixelsPerDay": 1, "PixelsPer100Pips": 28 },
  { "PixelsPerDay": 1.5, "PixelsPer100Pips": 32 }
]
```

If the list is missing or empty on load, `ZoomLevel.Defaults()` fills it
with 20 levels, from 1 px/day (whole history) to 86400 px/day (1 second
per pixel). Invalid entries are dropped. The cap is
`ZoomLevel.MaxCount` = 60.

Which level is active **is** per tab: `ChartViewState.ZoomLevelIndex`,
default `-1` (no level yet, the label shows `Zoom -`).

## Files

| File | What it does |
| --- | --- |
| `Chart/ZoomLevel.cs` | the level record, unit conversion, defaults |
| `Chart/ZoomLevelView.cs` | the corner label, save icon and the popup |
| `Chart/ChartView.cs` | `OnZoom` split, `ApplyZoomLevel`, dirty check, list edits |
| `Chart/ChartViewState.cs` | `ZoomLevelIndex` |
| `ChartTab.cs` | `CustomZoom` |
| `TabPropertiesView.cs` | the tab properties popup |
| `AppConfig.cs` | `ZoomLevels` + `EnsureZoomLevels` |
| `MainWindow.xaml(.cs)` | places the widget, wires save / insert / delete |
