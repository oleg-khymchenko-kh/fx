# Performance counters in the log

Status: implemented, v1.

## Goal

When the chart feels slow (zoom or scroll does not follow the mouse),
tell from `fxviewer.log` **which part** is slow, without a profiler.

## What is measured

`Perf` (FXViewer/Perf.cs) is a static counter table. Every measured
piece of code adds one sample: a name, the elapsed ms. Two kinds of
output go to the log:

1. **Slow frame lines** - printed right away when one user action takes
   longer than `Perf.SlowFrameMs` (50 ms by default):

   ```
   PERF slow chart.rebuild 180 ms | chart.raster 90 | panel.volume.build 40 | present.density 25 x2
   ```

   `x2` means the step ran twice inside that frame.

2. **A summary every 10 s**, if anything was measured in that window:

   ```
   PERF 10s: chart.rebuild x118 tot 4120 max 95 | chart.raster x118 tot 1830 max 40 | ...
   ```

   `x118` = samples, `tot` = total ms in the window, `max` = worst
   single sample. Sorted by total time, so the first name is where the
   time goes. Counters reset after every summary.

The summary is skipped when the only counter is `log.append`, so an
idle app does not print a line every 10 seconds.

## Counter names

Frames (one user action, these can print a slow line):

| Name | What |
|---|---|
| `chart.rebuild` | one full chart render, from request to painted bitmap |
| `input.crosshair` | mouse move: crosshair, cursor prices, right-edge profile |
| `input.drag` | mouse move while panning / dragging a shift or a grid |
| `input.zoom` | one wheel notch |
| `input.edithover` | mouse move over editable points / lines |

Inside `chart.rebuild`, background thread:

| Name | What |
|---|---|
| `chart.buildlines` | candles -> columns for every visible series |
| `chart.fitscale` | auto price range on the first render |
| `chart.raster` | grid, session bands, tilted grid, price lines |
| `chart.vectors` | drawings, zigzag polylines |
| `chart.overlays` | deals, forecast, calendar |
| `panel.spread` | spread panel |
| `panel.volume.build` | volume columns from candles + `.vap` profiles |
| `panel.volume.draw` | volume bars |
| `panel.age` | price age panel |
| `panel.entry` | entry points panel |

Inside `chart.rebuild`, UI thread:

| Name | What |
|---|---|
| `chart.publish` | copying the render result into the view state |
| `chart.present` | everything in `Present` (see below) |
| `chart.viewchanged` | time axis update + lazy loader range request |
| `chart.statechanged` | building the state object handed to MainWindow |

Inside `chart.present`:

| Name | What |
|---|---|
| `present.blit` | `WriteableBitmap.WritePixels` of the chart |
| `present.range` | selection band |
| `present.markers` | editable point circles |
| `present.density` | right-edge profile (see below) |
| `present.rest` | draw preview, selection circles |

Inside `present.density` (the right-edge profile of Density, Volume and
order book indicators):

| Name | What |
|---|---|
| `density.histogram` | scanning the lookback window into a per-pip histogram |
| `density.draw` | painting the histogram rows |
| `density.book.build` / `density.book.draw` | same for order book / depth |
| `density.blit` | `WritePixels` of the profile strip |
| `density.labels` | the text labels next to the profile |

Other:

| Name | What |
|---|---|
| `config.save` | writing `config.json` |
| `view.loader` | asking the lazy loader to cover the visible range |
| `timeaxis.update` | time axis invalidate |
| `symbolbar.cursor` | writing cursor prices into the symbol bar |
| `log.append` | the logging itself (TextBox + file append) |
| `rebuild.coalesced` | a render was requested while one was running, so it was dropped and re-run once (count only, 0 ms) |

A high `rebuild.coalesced` count next to a high `chart.rebuild` max
means the render cannot keep up with the input rate.

## Log hygiene

Two things made the log itself a slowdown, both fixed:

- **Repeat collapsing.** A line identical to the previous one is not
  written again. When a different line arrives (or on the 10 s timer)
  the app writes `... previous line repeated N more time(s)`. Before
  this, a failing render could write 90 identical lines per second,
  each one an append to the file and to the on-screen TextBox.
- **The Log box is trimmed** to the last 2000 lines. The file keeps
  everything. A WPF TextBox with hundreds of thousands of lines is slow
  to append to and slow to scroll.

Also, `Chart failed:` now logs the whole exception with its stack, not
just the message, so a broken render can be located from one log line.

## Turning it off

`Perf.Enabled = false` makes every call a no-op. It is on by default;
one sample is two `Stopwatch.GetTimestamp()` calls and a dictionary
update, which is far below the cost of anything it measures.
