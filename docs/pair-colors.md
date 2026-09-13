# Pair colors

Status: implemented, v1.

## Goal

A "Settings" button in the right toolbar (gear icon, after "Add symbol")
opens the app settings dialog. There the user picks a line color for
each main pair (the built-in `SymbolConfigs` list: EURUSD, GBPUSD,
EURGBP, USDCHF, USDJPY, AUDUSD, NZDUSD, USDCAD, GER40).

The colors are global. Tabs share one chart and one config, so a change
applies to every tab at once. Indicator symbols are not affected: they
keep their own color from the Add / Edit dialog.

## Dialog

`AppSettingsWindow`:

- left: one row per pair - a small swatch plus the pair name drawn in
  its current color; click a row to select the pair,
- no pair is selected when the dialog opens: the palette, the custom box
  and the Default button are disabled and drawn at 40% opacity until the
  user picks a pair,
- right: a palette of 64 predefined colors (`PairPalette.Colors`,
  8 x 8 grid) generated in HSL: 16 fully saturated hues (22.5 degree
  steps around the color wheel, S = 100%) x 4 lightness levels (0.25,
  0.35, 0.50, 0.65); L = 0.50 is the pure hue, so pure red `FF0000`,
  pure blue `0000FF` etc. are in the grid; click a swatch to assign it
  to the selected pair,
- right-click a palette swatch opens a popup with 16 brightness levels
  of that color (same hue and saturation, lightness 0.10 to 0.85 in HSL);
  click one to assign it, click outside to close,
- "Custom" - a hex box (`RRGGBB`, `#` allowed) with Apply (Enter works
  too) for any color outside the palette,
- "Default" - returns the selected pair to its built-in color,
- OK saves and applies, Cancel discards everything.

## Storage

`AppConfig.PairColors` is a `symbol -> ARGB` map. Only overrides are
stored: picking the built-in color removes the entry. Missing entry =
default color from `SymbolConfigs`.

`MainWindow.PairColorOf(symbol, defaultColor)` resolves the effective
color and `DisplayConfigs` uses it for the base pair series, so a
restart, a tab switch and every full chart reload pick the override up.

## Applying without a reload

On OK the changed pairs are recolored in place:

- `ChartView.SetSeriesColor(symbol, argb)` replaces the series record
  (`with { ColorArgb }`) and calls `Rebuild()`, which redraws the lines,
  the price labels and the shift lines with the new color,
- `SymbolBarView.SetSymbolColor(symbol, argb)` rebuilds the row brush in
  the right bar.

No candles are re-read from disk.

## Other settings in the same dialog

The colors are the main part, but the window is the app-wide Settings
window, so global switches live here too:

- **Hide wide spread minutes** (docs/wide-spread.md) and **Show ask
  instead of bid** (docs/ask-view.md). Unlike a color they cannot be
  applied in place - the minutes have to be re-read from disk - so
  switching either one reloads the chart.
- **Comment spot** - diameter, color and opacity of the comment spots
  (docs/comments.md). Applied in place: OK pushes the new style into
  `ChartView.SetCommentStyle` and the chart redraws.
