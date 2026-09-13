# Tilted grid

Status: implemented, v2.

## Goal

A second, tilted grid that overlays the chart, next to the plain
horizontal/vertical grid. The rising lines and the falling lines are two
independent families: each is switched on and off on its own, and each
has its own slope, its own anchor and its own lock.

The right bar carries two rows of seven small buttons right under the
symbol rows:

    [1][2][3][4][5][6][7]   rising lines
    [1][2][3][4][5][6][7]   falling lines

There are 7 grid slots (`ChartViewState.TiltedGridCount`). The top row
picks which slot supplies the rising lines, the bottom row which slot
supplies the falling lines - they do not have to be the same slot. There
is no "off" button: clicking the button that is already pressed releases
it and that family disappears. So both rows can be off, one on, or both
on at once.

To change the slot count, edit the const and the `GridNBtn` buttons in
`TiltedGridSettingsWindow` - the bar rows, state and rendering adapt.

While any tilted family is visible, the main grid's 50-pip sub-lines are
suppressed, so the chart is not overcrowded. The 100-pip lines and the
light 10-pip lines stay (the user asked to keep the 10-pip lines on
2026-09-10). The 50-pip lines are hidden because they have the same color
and dots as the tilted 50-pip sub-lines and would mix with them.

## Slope

The slope is **pips per day**: how far a tilted line travels in one day.
Rising lines are positive, falling lines are negative - a rising line of
+125 pips per day and a falling line of -125 pips per day are mirror
images. The sign is what makes a family rising or falling, and nothing
in the app ever flips it (see "Moving and changing the angle").

Internally the slope is kept as points per second,

    slope = pipsPerDay * 10 / 86400

(1 pip = 10 points, `ChartRasterizer.GridPriceStepPoints` = 1000 points =
100 pips). The magnitude is clamped to
`TiltedGridState.MinPipsPerDay` (0.001) .. `MaxPipsPerDay` (1 000 000).

Both families are drawn left to right and are infinite (they run the full
width of the view), so the slopes alone tile the whole visible price
range - there is no separate horizontal repeat, it falls out of the
vertical repeat plus the lines being unbounded.

The slope is applied as a straight line in whatever space column x
already lives in - virtual (weekend-compressed) time when "No weekends"
is on, plain real time when it is off. So "pips per day" counts trading
days only in "No weekends" mode; with weekends shown the same setting
draws a straight line over calendar days instead of skipping the two
weekend days each week. This is a deliberate simplification - the
alternative is a line that bends at every weekend gap, which is a lot of
extra complexity for a helper overlay.

## Placement model

Each family is stored as a free anchor point plus its slope:

    TiltedFamilySettings(Visible, AnchorSeconds, AnchorPoints, Slope, Nearest)

`AnchorSeconds` is a time on the chart's own axis (virtual seconds) and
`AnchorPoints` is a price in points. Every line of a family is

    price(t) = AnchorPoints + slope * (t - AnchorSeconds) + n * 1000

for all integers n, so the vertical repeat stays exactly 100 pips
whatever the angle. The default placement is anchor (0, 0), which
reproduces the original pattern anchored at bucket 0.

Because the anchor is stored in absolute chart units (not screen pixels),
the grid stays put when the chart is panned or zoomed.

"Reset placement" in the context menu puts the anchors of the visible
families back at (0, 0).

Each slot holds two independent halves - rising pips/day, anchor and
`UpLocked`; falling pips/day, anchor and `DownLocked`. So picking slot 2
for the rising lines and slot 4 for the falling lines uses slot 2's
rising half and slot 4's falling half; the halves that are not shown keep
their values untouched.

## 50-pip tilted sub-lines

When the tilted lines of one direction get far enough apart on screen,
that direction also gets 50-pip lines in between, so the mesh does not
become too sparse. The rule mirrors the main grid's: the gap is compared
against `TiltedSubLineMinSpacingPixels` (200 px), and each direction is
decided on its own (`TiltedStepPoints`).

The gap measured is the **perpendicular** distance between neighbouring
parallel lines of that family, not the vertical one:

    gap = (1000 / pointsPerRow) / sqrt(1 + screenSlope^2)

Worth knowing: that gap is always at most `1000 / pointsPerRow`, reached
only when the lines are horizontal, and tilting them makes it smaller. So
the threshold is driven mainly by the **vertical zoom**.

The intermediate lines are drawn dotted - every other pixel along the
line, `(x + row) % 2` so it works at any angle - matching how the main
grid draws its 50-pip lines, so the 100-pip lattice stays readable.

`NearestTiltedLineIsUp` uses the same `TiltedStepPoints`, so when the
sub-lines are on, hovering one of them counts as hovering that family.
Pinning still uses the 100-pip lattice only.

## Moving and changing the angle

The Fn key cannot be used: it is handled inside the keyboard controller
and never reaches Windows as a key event, so no application can bind it.
Alt is used instead - it was the only free modifier on the chart surface
(Shift = range select, Ctrl = vertical zoom) and it already means "adjust
this thing" in the symbol bar (Alt + wheel = time shift).

- **Alt alone** darkens the single line nearest the cursor
  (`palette.GridTiltedNear`, 0x8C8C8C instead of 0xC8C8C8), so it is
  clear which family the wheel will move. The line is the one
  `NearestTiltedLine` returns - the same `round(residual / step)` index
  the gap measurement already computes - of the nearer family.
  `ChartView` recomputes family and line index on Alt down/up and on
  every mouse move while Alt is held, and only re-renders when either
  changes.
- **Alt + left drag** moves the visible families. `BeginTiltedDrag`
  stores their anchors at press time and every move sets
  `anchor = start + delta`, converting pixels with the rendered column
  size and `pointsPerRow`, so there is no drift over a long drag. Both
  families move together, keeping their crossings.
- **Alt + wheel** changes the angle by 1 degree per notch.
  **Alt + Ctrl + wheel** uses 0.1 degree. Only the family nearest the
  cursor is touched - the same one that is darkened.

A Shift indicator line under the cursor wins over the grid: while Alt is
held and such a line is within 2 pixels, no grid line is darkened and
Alt + left drag moves that indicator instead of the lattice. See
docs/shifted-symbol.md. The wheel is not affected - Alt + wheel always
goes to the grid.

This is not a real rotation - a wheel notch only makes one family's slope
steeper or shallower. The reachable range is a bit above 0 up to a bit
below 90 degrees (`TiltedMinAngleDegrees` 0.1 ..
`TiltedMaxAngleDegrees` 89.9, clamped), and because only the **magnitude**
of pips per day is ever written back (`TiltedGridState.SignedPips` puts
the sign back per direction), a rising family can never turn into a
falling one or the other way round.

"Nearest line" is measured as perpendicular screen distance. For each
family, the vertical gap from the cursor to the closest line of that
family is `residual - round(residual / step) * step` points, converted to
pixels and multiplied by `cos(angle)` of that family - without the cosine
a steep family would look closer than it is. When only one family is
visible it is always the nearest one.

The angle only means something at a given zoom, so it is computed from
the slope together with both zoom factors - the column size in seconds
and `pointsPerRow`:

    angle = atan(|slope| * bucketSeconds / pointsPerRow)
    slope = tan(angle) * pointsPerRow / bucketSeconds

`RotateTiltedGrid` reads the current angle, adds the step, clamps it,
converts back to a slope, rounds that to 3 decimals of pips per day and
writes the result back. Each step recomputes from the stored value, so it
does not drift.

**Pinning.** Before the angle changes, the anchor of the family being
changed is moved onto the point that should stay put
(`SnapTiltedAnchor`). Re-anchoring on a point that already lies on one of
the family's lines leaves the family itself untouched - substituting it
as the new anchor reproduces the same set of lines, the line indices just
shift by an integer. Once the anchor is that point it is fixed by
construction, because the family passes through its anchor for any slope.

- Both families visible: the anchor goes to the nearest **intersection**
  of the two, so the crossing under the cursor stays exactly where it was
  and the family fans around it. With slopes `sA` (the family being
  changed) and `sB` (the other one) and their anchors `(Ta, Pa)`,
  `(Tb, Pb)`, intersections sit at

        spacing = 1000 / (sA - sB)
        t       = (Pb - Pa + sA * Ta - sB * Tb) / (sA - sB) + k * spacing
        price   = Pa + sA * (t - Ta) + n * 1000

  The nearest one is picked by screen distance over the two candidate
  `k` values, each with its best `n`.
- Only one family visible: the anchor goes to the point of the nearest
  line at the cursor's time, so that line stays under the cursor.

`ChartRasterizer` refuses to draw a family that would need more than
`MaxTiltedLinesPerFamily` (2000) lines, as a backstop against a
near-vertical slope trying to fill the screen.

## Rendering

`ChartRasterizer.DrawTiltedGrid` (called from `DrawGrid` when
`TiltedGridSettings.Visible`, i.e. at least one family is on) draws each
visible family with `DrawTiltedFamily` / `DrawTiltedLine`: for every
screen column it computes the price at the column's left and right edge
and fills the pixel rows in between, so the line has no gaps even at a
steep slope. In a family marked `Nearest`, the one line whose index
equals `NearestLine` is drawn with `palette.GridTiltedNear`; that family
is drawn last so the darkened line stays on top at crossings.

`ChartView` keeps the seven slots in `_tiltedGrids` and which slot feeds
each direction in `_tiltedUpIndex` / `_tiltedDownIndex` (0 = off). Both
indexes and all seven slots are persisted on `ChartViewState`
(`TiltedUpGridIndex`, `TiltedDownGridIndex`, `TiltedGrids`), the same way
`WeekendsHidden` is saved, so - like the rest of the view - the grid is
**per tab** (docs/tabs.md).

## Reading old configs

Two older formats are read once and then written back in the new shape
(`TiltedGridState.Migrate`, called from
`ChartViewState.EnsureTiltedGrids`):

- The v1 slots stored Days / Hours / Minutes to cross 100 pips plus one
  shared anchor. The triple becomes `100 * 86400 / cycleSeconds` pips per
  day (negated for the falling family), and the shared anchor is copied
  into both per-direction anchors. A falling triple of 0 - from the even
  older config where both directions shared one triple - copies the
  rising one first. The shared `Locked` flag sets both `UpLocked` and
  `DownLocked`.
- The oldest format had one flat set of tilted fields on
  `ChartViewState`; it still becomes slot 1 plus default slots up to
  `TiltedGridCount`. A config saved with fewer slots (e.g. the five of
  v2) is padded with default slots the same way.

`TiltedGridIndex` (one slot, both directions on) becomes the same slot in
both `TiltedUpGridIndex` and `TiltedDownGridIndex`. All legacy fields are
marked `JsonIgnore(WhenWritingDefault)` and are zeroed by the migration,
so they disappear from the config on the first save.

## Right bar rows

`SymbolBarView` gained two rows right under the symbol rows
(`TiltedUpRowIndex = _visibleRows.Count`, `TiltedDownRowIndex` right
below, "Unflatten" shifts down accordingly):

- Instead of a text label each row holds seven small square buttons
  `1`..`7`. The selected one is filled dark, the rest are light; at most
  one per row is selected and clicking it again clears the row. A click
  sends `TiltedGridSelected(up, slot)`, which `MainWindow` passes to
  `ChartView.ToggleTiltedGrid`. The buttons are drawn by
  `DrawTiltedButtons` and hit-tested by `TiltedButtonAt` (row by
  `RowHit`, column by x), so the rows keep their place in the bar layout.
- Right-click on either row opens a context menu with "Settings"
  (`TiltedGridSettingsRequested`), handled in `MainWindow` by opening
  `TiltedGridSettingsWindow` and applying the result via
  `ChartView.SetTiltedGrids`, and "Reset placement"
  (`TiltedGridResetRequested` -> `ChartView.ResetTiltedGridPlacement`),
  which undoes any Alt-drag / Alt-wheel placement.
- The dialog edits all seven slots: a row of small toggle buttons
  `1`..`7` at the top picks which one is shown, plus a pips-per-day box
  and a Lock
  checkbox per direction. The values of the slot being left are
  read back into memory first, so a switch keeps them (if they are
  invalid the switch is refused and the boxes are put back). It opens on
  the slot feeding the rising lines, or the falling one if only that is
  on. A typed value may carry either sign or none - the dialog forces the
  sign that matches the direction.
- **Lock is per line**, one checkbox next to each pips box
  (`UpLocked` / `DownLocked`). It blocks Alt + wheel for that one
  direction: `RotateTiltedGrid` returns right away when the family it
  would change is locked, and says so once in the log
  (`ReportTiltedLocked`, repeated only after the slot, the direction or
  the settings change) - otherwise a locked line looks broken. So the
  rising line can be pinned while the falling one is still tuned with the
  wheel. Moving the lattice with Alt + drag still works - the lock is
  only about the angles, which are easy to change by accident while
  scrolling.
- Mouse wheel over the rows does nothing, on purpose. The bar's wheel
  handler only reacts when the cursor is over a symbol label (symbol
  offset, or Alt time shift for a Shift symbol); over the toggle rows
  and over empty bar space the wheel is swallowed. The slope is changed
  with Alt + wheel over the chart or in the Settings dialog.
