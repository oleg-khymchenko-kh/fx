# Similar days search

Status: implemented, v1 (research console tool `src/Fx.SimilarDays`).

## Goal

Given a start day, take that trading day plus the next 2 (a 3-day
window, e.g. Mon-Wed) and rank all windows of 3 consecutive trading days
in the past by shape similarity. Same idea as the similar weeks search
(docs/similar-weeks.md), different window and candidate enumeration.

## Differences from the week search

- Window: 3 trading days = 288 points (15-minute, 4h-SMA smoothed, built
  from the same per-week series as the week tool, 96 points per day).
- Candidates slide by one trading day over the whole history strictly
  before the target start day: every trading day that has 2 following
  trading days starts a window (~4000 windows). Consecutive means
  consecutive in trading time: Friday is followed by Monday, the weekend
  gap is glued exactly like the chart displays it. Windows never span a
  hole in the data: weeks are concatenated into continuous runs and a
  week absent from the data breaks the run. Unlike the week tool there
  is no 60% week-coverage filter here - a sparse (holiday) week stays in
  the run with NaN points, and the per-window overlap floor (172 of 288
  points) decides whether a window over it is comparable.
- Shift range +-12 hours (1-hour step). Unlike the week tool the
  candidate window is sliced with +-12h of real context around it, so a
  shifted comparison still uses all 288 points (no overlap loss at the
  window edges; a day-window plus context exists in history unless it
  hits a run edge or the target itself). Candidate context never reads
  the target's own days.
- Mirror comparison: same as the week tool (sign flip of centered B,
  same closed-form zoom clamped to [0.6, 1.5]).
- Because shift +-12h tiles the 24h day step, the same episode is seen
  by two adjacent windows. The console/HTML top list collapses these
  duplicates (windows closer than 3 trading days to a better-ranked one
  are skipped); the CSV keeps every window.

Everything else (centering per overlap, zoom formula, score, sim with
sqrt(overlap/288) penalty, session-open week anchoring with the
late-open re-anchor) matches docs/similar-weeks.md.

## Tool

    dotnet run --project src/Fx.SimilarDays [dataRoot] [symbol] [startDay] [topN]

Defaults: FXViewer debug data folder, EURUSD, 2026-07-20, top 20.

Output:

- Console: parameters, self-test, top N table (deduplicated).
- `reports/<symbol>-similar-days-<startDay>.csv`: all windows,
  `startDay;endDay;sim;score;rho;zoom;shiftHours;mirror;overlapPoints`.
- `reports/<symbol>-similar-days-<startDay>.html`: standalone SVG
  overlays of the top 5 (target blue, candidate orange, transformed as
  reported).
