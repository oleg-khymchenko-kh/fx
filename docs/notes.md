# Notes: saved chart screens

Status: implemented, v1.

## Goal

A note is a saved screen of the chart: the same thing a tab keeps - zoom,
offsets, grids, which symbols are on - plus a **snapshot of the drawings**.
Open a note in a tab and you see exactly what you saw when you saved it,
even if the lines of the Drawing indicators were changed later in another
tab.

Notes are shared by all tabs. Deleting a tab does not delete notes.

## Model

`Note` (`Note.cs`):

    Id        - random id, the tab points to a note by it
    Name      - what the notes list shows
    StartUnix - first visible minute when the note was saved
    EndUnix   - last visible minute when the note was saved
    State     - the ChartViewState of the note (same content as a tab)
    Shifts    - a ShiftPlacement per Shift indicator (same as a tab)
    Drawings  - drawing symbol -> its polylines, in the drawing.json format

`ChartTab` gained `NoteId`: empty means the tab shows the live drawings,
otherwise the tab shows the snapshot of that note.

Storage: `notes.json` next to `config.json` (`AppConfig.Dir`), written
through a `.tmp` file, a broken file is moved to `.bad` and ignored.
`Start` / `End` come from `ChartView.VisibleRealRange()` - the real time of
the left and the right edge of the chart, so a note saved with "No weekends"
on still reports real timestamps.

## The dialog

Right-click a tab -> `Notes...`. The tab you right-clicked becomes active
first, so the dialog always works on the tab you meant.

The window is not modal - it stays open while you look at the chart. It
lists `Name`, `Start (UTC)`, `End (UTC)` and has four buttons:

- **Create from tab** - asks for a name (`Note 1`, `Note 2`, ... is
  suggested) and saves the current tab as a new note. The tab itself stays
  live: drawing after that still edits `drawing.json`.
- **Update from tab** - overwrites the selected note with the current tab
  (asks Yes/No first).
- **Rename...** / **Delete** - on the selected note.

A click on a row shows that note in the active tab. The selected row is
always the note the active tab currently shows, so an empty selection means
"this tab is live".

## Opening a note

`OpenNote` puts a **copy** of the note into the tab:

1. the pending view state of the tab is flushed,
2. `tab.NoteId`, `tab.State`, `tab.Shifts` are replaced by the note's,
3. the drawings of the note replace the ones in the chart,
4. `Chart.RestoreState` + symbol bar re-sync,
5. if the Shift placements really changed, a normal `LoadChartAsync()` runs
   (the same rule as switching tabs).

The copy means the note is **frozen**: panning, zooming, switching symbols
in that tab do not change the saved note. Use `Update from tab` to write
the current screen back into it.

## The drawing snapshot

There is only one `ChartView` and one series list, so the drawings of the
active tab are the ones in memory. Two things keep them in sync:

- `ApplyTabDrawings(tab)` runs on every tab switch and on opening a note.
  It builds symbol -> lines for every Drawing indicator (the note snapshot,
  or `drawing.json` when the tab is live) and swaps them all in one
  `ChartView.ReplaceDrawings` call - one rebuild, not one per symbol.
  `_appliedNoteId` remembers what is in the chart right now, so switching
  between two live tabs costs nothing.
- `LoadChartCoreAsync` reads the snapshot of the active tab's note instead
  of `drawing.json` when it builds the drawing series.

A Drawing indicator created **after** the note was saved has no entry in
the snapshot; it shows its live lines until it is edited in that tab.

## Editing lines inside a note

Drawing while a note is open writes into the note, not into
`data/<SYMBOL>/drawing.json`: `OnDrawingCommitted` and
`OnDrawingLinesChanged` ask `ActiveNote()` first and save `notes.json`
instead. So a note is a self-contained document - you can keep drawing on
it without touching the live picture, and the other way round.

## Renaming and deleting indicators

- Renaming an indicator rewrites its name in every note as well - in
  `State` (offsets, hidden, flatten), in `Shifts` and in the `Drawings`
  keys (`RenameChartStateKeys`).
- Deleting a Drawing indicator drops its snapshot from every note.
- Deleting a note clears `NoteId` on every tab that pointed to it, and the
  active tab goes back to the live drawings right away.

## Not in v1 (next steps)

- Open a note in a new tab instead of the current one.
- A comment / text field per note.
- Reordering the list, sorting by start time.
