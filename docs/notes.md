# Notes: saved chart screens

Status: implemented, v2 (2026-09-30: notes no longer store drawings).

## Goal

A note is a saved screen of the chart: the same thing a tab keeps - zoom,
offsets, grids, which symbols are on. Open a note in a tab and you get the
same view you had when you saved it.

A note has **no indicator data of its own**. Drawing lines always come from
the main folder, `data/<SYMBOL>/drawing.json`, in every tab, with or
without a note. So a line you draw is the same line everywhere.

Notes are shared by all tabs. Deleting a tab does not delete notes.

## Model

`Note` (`Note.cs`):

    Id        - random id, the tab points to a note by it
    Name      - what the notes list shows
    StartUnix - first visible minute when the note was saved
    EndUnix   - last visible minute when the note was saved
    State     - the ChartViewState of the note (same content as a tab)
    Shifts    - a ShiftPlacement per Shift indicator (same as a tab)

`ChartTab` has `NoteId`: the note that was opened in the tab last, empty
means none. It only selects the row in the notes list.

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
  suggested) and saves the current tab as a new note.
- **Update from tab** - overwrites the selected note with the current tab
  (asks Yes/No first).
- **Rename...** / **Delete** - on the selected note.

A click on a row shows that note in the active tab. The selected row is
always the note that was opened in the active tab last, so an empty
selection means "no note was opened here".

## Opening a note

`OpenNote` puts a **copy** of the note into the tab:

1. the pending view state of the tab is flushed,
2. `tab.NoteId`, `tab.State`, `tab.Shifts` are replaced by the note's,
3. `Chart.RestoreState` + symbol bar re-sync,
4. if the Shift placements really changed, a normal `LoadChartAsync()` runs
   (the same rule as switching tabs).

The copy means the note is **frozen**: panning, zooming, switching symbols
in that tab do not change the saved note. Use `Update from tab` to write
the current screen back into it.

## Drawings

Up to 2026-09-30 a note kept a snapshot of all Drawing indicators
(`Note.Drawings`), and drawing in a note tab was saved into `notes.json`.
That second storage is gone. `OnDrawingCommitted` and
`OnDrawingLinesChanged` always write `data/<SYMBOL>/drawing.json`, and the
chart load always reads it.

An old `notes.json` may still have a `Drawings` field. It is ignored on
load and is dropped the next time the notes are saved.

## Renaming and deleting indicators

- Renaming an indicator rewrites its name in every note as well - in
  `State` (offsets, hidden, flatten) and in `Shifts`
  (`RenameChartStateKeys`).
- Deleting a note clears `NoteId` on every tab that pointed to it.

## Not done (next steps)

- Open a note in a new tab instead of the current one.
- A comment / text field per note.
- Reordering the list, sorting by start time.
