# Average max/min/avg indicator

Status: implemented, v3 (three modes).

## Goal

An indicator type `AverageBand`, shown in the Add/Edit dialog as
**Average max/min/avg**. It takes a set of simple moving averages of
the same pair and draws one value per minute out of that whole set:

    Max: max( SMA_1(t), SMA_2(t), ... SMA_N(t) )
    Min: min( SMA_1(t), SMA_2(t), ... SMA_N(t) )
    Avg: ( SMA_1(t) + SMA_2(t) + ... + SMA_N(t) ) / N

The default set is what the feature was asked for: 40 averages from
1 day to 40 days with a 1 day step. So at each minute the line shows
the top, the bottom, or the mean of all those averages.

The line lives in the same price space as its source pair, so it draws
right over the price, like a normal moving average.

## Parameters

The dialog row for this type has:

- **Count** - how many averages, 1 to 200 (default 40).
- **Max / Min / Avg** - what to take from the set (default Max).
- **Step** - period of the shortest average as a number plus a unit
  (Minutes / Hours / Days), default 1 Day.
- **Window by time** - a checkbox that measures every average in the set
  as a span of trading minutes instead of a count of candles, exactly
  like the plain average (docs/moving-average.md). On by default.

The averages are `step, 2*step, ... count*step`, so the defaults give
1, 2, 3 ... 40 days. The hint next to the Step field shows the range
(`= 1 .. 40 days`). The longest average must stay under 10M minutes.

Count = 1 makes it the same thing as a plain `Average` with that
period, in every mode.

The mode is stored as `BandMode` ("Max" / "Min" / "Avg"). The first
build of this indicator had a `BandMin` bool instead; `AppConfig.Load`
turns an old `BandMin: true` into `BandMode: Min` once and saves.

## Math

The same window rules as the plain average (see docs/moving-average.md):

- With "Window by time" on (the default), one day = 1440 trading minutes
  back, and a gap inside that span just means fewer candles. With it
  off, the window is a count of candles instead: one day = 1440 M1
  candles, and gaps in the data are ignored. No weekly reset either
  way.
- Minutes flagged as wide spread never go into any of the averages,
  whatever the global hide setting says.
- At the very start of the data the window is shorter (expanding), so
  every source minute gets a value and the line is continuous. At the
  first candle all the averages are equal, so max and min start from
  the price itself.
- Values are averages of the candle `Avg` in display space, rounded to
  the nearest point.

`Chart/AverageSeries.cs` holds the math for both types. `AverageSpec`
carries the window in bars, the direction, the volume-weighted flag,
the band count, the `BandPick` (Max / Min / Avg) and the `TimeWindow`
flag; `BandCount = 1` is the plain average, so both indicators share one
code path:

- `ComputeRange` runs one pass per average over shared prefix arrays
  into a `long` accumulator per minute: Max and Min keep the running
  extreme, Avg sums and divides by the count at the end. Cost is
  `count` passes over the range: about 170 ms for 40 averages over 1M
  minutes with "Window by time" on. Never on the UI thread - see the
  update points in docs/moving-average.md.
- `Diff` widens the changed range by the **longest** window, so a lazy
  year load patches only the part that can move. The widening follows
  the same window rule, so it counts trading minutes in time mode.
  A band is the expensive case of a year prepend: 6 years of GBPUSD is
  2.2M new minutes, and each of the three bands on that pair has to
  produce 40 values for every one of them.
- `LiveTail` recomputes the tail values the same way on every live
  flush (250 ms). Its prefix arrays cover the longest window and no
  more, so the default set costs a couple of ms.

Volume weighting is not offered here - every average in the set is a
plain SMA.

## Wiring

`AverageBand` is part of the "average family": `IndicatorTypes.IsAverage`
is true for it, so it reuses everything the plain average already has:

- no storage on disk (`HasStorage` is false), no Refresh / Rebuild,
  Compute derived skips it;
- computed in memory from the parent series, on a background thread
  during chart load and incrementally afterwards (`ChartView`
  `SetSeries` / `ReplaceSeries` / `PatchSeriesHistory` / `SetLiveTail`),
  and skipped entirely while the band is hidden;
- vertical placement is locked to the source pair, the align items are
  hidden in the label menu;
- if the parent pair is hidden but the indicator is visible, the parent
  still loads.

`IndicatorTypes.IsPlainAverage` marks the plain `Average` only, and is
used where the two must differ.

## Not in v3 (next steps)

- No wheel on the label: the plain average has Alt+wheel on its window,
  this one is edited in the dialog only. The label shows just the name
  and the price, with no window text.
- No Past / Future switch in the dialog (the math supports it, the
  dialog always saves Past).
- Only one line per indicator: to see both edges and the middle, add a
  Max, a Min and an Avg one.
