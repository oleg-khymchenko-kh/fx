using System.Globalization;
using System.Text;

namespace Fx.MaCross;

internal static class EquityChart
{
    private const int Width = 1180;
    private const int EquityHeight = 400;
    private const int DrawdownHeight = 150;
    private const int PadLeft = 72;
    private const int PadRight = 24;
    private const int PadTop = 18;
    private const int PadBottom = 26;

    public static void Write(string path, string symbol, Options opt, History h, Result best,
        List<Sweep.Trade> trades)
    {
        if (trades.Count == 0) return;

        int points = trades.Count * 2 + 1;
        var times = new long[points];
        var equity = new double[points];
        var limit = new double[points];
        times[0] = h.Ts[trades[0].EntryIndex];
        equity[0] = opt.Deposit;
        for (int i = 0; i < trades.Count; i++)
        {
            times[i * 2 + 1] = h.Ts[trades[i].FirstExitIndex];
            equity[i * 2 + 1] = opt.Deposit * trades[i].FirstEquity;
            times[i * 2 + 2] = h.Ts[trades[i].ExitIndex];
            equity[i * 2 + 2] = opt.Deposit * trades[i].Equity;
        }

        double keep = 1 - opt.MaxDdPercent / 100;
        var drawdown = new double[equity.Length];
        double peak = opt.Deposit;
        double maxDd = 0;
        int maxDdAt = 0;
        for (int i = 0; i < equity.Length; i++)
        {
            if (equity[i] > peak) peak = equity[i];
            limit[i] = peak * keep;
            drawdown[i] = (1 - equity[i] / peak) * 100;
            if (drawdown[i] > maxDd)
            {
                maxDd = drawdown[i];
                maxDdAt = i;
            }
        }

        long tMin = times[0];
        long tMax = times[^1];
        if (tMax <= tMin) tMax = tMin + 1;
        double eMin = Math.Min(equity.Min(), limit.Min());
        double eMax = equity.Max();
        if (eMax <= eMin) eMax = eMin + 1;

        double PlotWidth = Width - PadLeft - PadRight;
        double X(long t) => PadLeft + (double)(t - tMin) / (tMax - tMin) * PlotWidth;
        double YEquity(double v) => PadTop + (eMax - v) / (eMax - eMin) * (EquityHeight - PadTop - PadBottom);
        double YDrawdown(double v) => PadTop + v / Math.Max(1, opt.MaxDdPercent) * (DrawdownHeight - PadTop - PadBottom);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{symbol} MA cross equity - A1={best.W1} A2={best.W2} SL={best.SlPips} TP={best.TpPips}</title>");
        sb.AppendLine("""
            <style>
            body{font-family:Segoe UI,sans-serif;background:#fafafa;color:#222;margin:24px}
            h1{font-size:20px;margin:0 0 4px}
            h2{font-size:15px;margin:26px 0 6px}
            .meta{color:#666;font-size:13px;margin:0 0 14px}
            svg{background:#fff;border:1px solid #ddd;display:block;max-width:100%;height:auto}
            table{border-collapse:collapse;font-size:13px;margin-top:6px}
            th,td{border:1px solid #ddd;padding:4px 10px;text-align:right}
            th{background:#f0f0f0;font-weight:600}
            td.k{text-align:left}
            .neg{color:#c62828}
            .pos{color:#2e7d32}
            .cards{display:flex;flex-wrap:wrap;gap:10px;margin:0 0 16px}
            .card{border:1px solid #ddd;background:#fff;padding:8px 14px;min-width:110px}
            .card .v{font-size:17px;font-weight:600}
            .card .l{font-size:11px;color:#777;text-transform:uppercase;letter-spacing:.04em}
            </style></head><body>
            """);

        sb.AppendLine($"<h1>{symbol} - moving average crossover, equity of the best combination</h1>");
        sb.AppendLine($"<p class=\"meta\">A1 = {best.W1:N0} trading minutes, A2 = {best.W2:N0}, " +
                      $"StopLoss = {best.SlPips} pips, TakeProfit = {best.TpPips} pips, " +
                      $"risk per trade = {best.RiskPercent:F2}% of current equity, " +
                      $"spread charged {opt.SpreadTenths / 10.0:F1} pips per trade. " +
                      $"Start {opt.Deposit:N0}, {Fmt(times[0]):yyyy-MM-dd} .. {Fmt(times[^1]):yyyy-MM-dd}. " +
                      $"The combination is dropped if equity ever falls {opt.MaxDdPercent:F0}% below its own " +
                      "high water mark; the grey line is that moving limit.</p>");

        sb.AppendLine("<div class=\"cards\">");
        Card(sb, "Deposit", $"{opt.Deposit:N0}");
        Card(sb, "Final equity", $"{equity[^1]:N0}");
        Card(sb, "Return", $"{best.ReturnPercent:N0} %");
        Card(sb, "Max drawdown", $"{maxDd:F1} %");
        Card(sb, "Risk per trade", $"{best.RiskPercent:F2} %");
        Card(sb, "Entries", $"{best.Entries:N0}");
        Card(sb, "Total pips", $"{best.Pips:N0}");
        Card(sb, "Trade win rate", $"{best.WinRate:P1}");
        Card(sb, "Both TP / mixed / both SL", $"{best.DblWin:N0} / {best.Mixed:N0} / {best.DblLoss:N0}");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Equity</h2>");
        sb.AppendLine(FormattableString.Invariant($"<svg width=\"{Width}\" height=\"{EquityHeight}\" viewBox=\"0 0 {Width} {EquityHeight}\">"));
        AppendYearGrid(sb, tMin, tMax, X, PadTop, EquityHeight - PadBottom, true);
        long eStep = NiceStep((long)(eMax - eMin), 8);
        for (long v = (long)Math.Ceiling(eMin / eStep) * eStep; v <= eMax; v += eStep)
        {
            double y = YEquity(v);
            string stroke = v == (long)opt.Deposit ? "#bbb" : "#eee";
            sb.AppendLine(FormattableString.Invariant(
                $"<line x1=\"{PadLeft}\" y1=\"{y:F1}\" x2=\"{Width - PadRight}\" y2=\"{y:F1}\" stroke=\"{stroke}\"/>"));
            sb.AppendLine(FormattableString.Invariant(
                $"<text x=\"{PadLeft - 8}\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#888\" text-anchor=\"end\">{v:N0}</text>"));
        }

        sb.AppendLine(FormattableString.Invariant(
            $"<polyline fill=\"none\" stroke=\"#999\" stroke-width=\"1\" stroke-dasharray=\"5 3\" points=\"{BuildPoints(times, limit, X, YEquity)}\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"<polyline fill=\"none\" stroke=\"#2b6cb0\" stroke-width=\"1.4\" stroke-linejoin=\"round\" points=\"{BuildPoints(times, equity, X, YEquity)}\"/>"));
        double ddX = X(times[maxDdAt]);
        sb.AppendLine(FormattableString.Invariant(
            $"<line x1=\"{ddX:F1}\" y1=\"{PadTop}\" x2=\"{ddX:F1}\" y2=\"{EquityHeight - PadBottom}\" stroke=\"#c62828\" stroke-width=\"1\" stroke-dasharray=\"4 3\"/>"));
        sb.AppendLine("</svg>");

        sb.AppendLine($"<h2>Drawdown from peak, % (limit {opt.MaxDdPercent:F0} %)</h2>");
        sb.AppendLine(FormattableString.Invariant($"<svg width=\"{Width}\" height=\"{DrawdownHeight}\" viewBox=\"0 0 {Width} {DrawdownHeight}\">"));
        AppendYearGrid(sb, tMin, tMax, X, PadTop, DrawdownHeight - PadBottom, false);
        long dStep = NiceStep((long)opt.MaxDdPercent, 5);
        for (long v = 0; v <= opt.MaxDdPercent; v += dStep)
        {
            double y = YDrawdown(v);
            string label = v == 0 ? "0" : $"-{v}";
            sb.AppendLine(FormattableString.Invariant(
                $"<line x1=\"{PadLeft}\" y1=\"{y:F1}\" x2=\"{Width - PadRight}\" y2=\"{y:F1}\" stroke=\"#eee\"/>"));
            sb.AppendLine(FormattableString.Invariant(
                $"<text x=\"{PadLeft - 8}\" y=\"{y + 4:F1}\" font-size=\"11\" fill=\"#888\" text-anchor=\"end\">{label}</text>"));
        }

        double limitY = YDrawdown(opt.MaxDdPercent);
        sb.AppendLine(FormattableString.Invariant(
            $"<line x1=\"{PadLeft}\" y1=\"{limitY:F1}\" x2=\"{Width - PadRight}\" y2=\"{limitY:F1}\" stroke=\"#c62828\" stroke-width=\"1\" stroke-dasharray=\"5 3\"/>"));

        string ddPoints = BuildPoints(times, drawdown, X, YDrawdown);
        double ddBase = YDrawdown(0);
        sb.AppendLine(FormattableString.Invariant(
            $"<polygon fill=\"#c62828\" fill-opacity=\"0.18\" stroke=\"none\" points=\"{X(tMin):F1},{ddBase:F1} {ddPoints}{X(tMax):F1},{ddBase:F1}\"/>"));
        sb.AppendLine(FormattableString.Invariant(
            $"<polyline fill=\"none\" stroke=\"#c62828\" stroke-width=\"1\" stroke-linejoin=\"round\" points=\"{ddPoints}\"/>"));
        sb.AppendLine("</svg>");

        AppendYearTable(sb, h, opt, trades);

        sb.AppendLine("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    private static void Card(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"<div class=\"card\"><div class=\"l\">{label}</div><div class=\"v\">{value}</div></div>");

    private static DateTime Fmt(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

    private static void AppendYearGrid(StringBuilder sb, long tMin, long tMax, Func<long, double> x,
        double top, double bottom, bool labels)
    {
        int firstYear = Fmt(tMin).Year;
        int lastYear = Fmt(tMax).Year;
        for (int year = firstYear; year <= lastYear; year++)
        {
            long t = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            if (t < tMin || t > tMax) continue;
            double px = x(t);
            sb.AppendLine(FormattableString.Invariant(
                $"<line x1=\"{px:F1}\" y1=\"{top}\" x2=\"{px:F1}\" y2=\"{bottom}\" stroke=\"#eee\"/>"));
            if (labels)
                sb.AppendLine(FormattableString.Invariant(
                    $"<text x=\"{px + 3:F1}\" y=\"{bottom + 16}\" font-size=\"11\" fill=\"#999\">{year}</text>"));
        }
    }

    private static string BuildPoints(long[] times, double[] values, Func<long, double> x, Func<double, double> y)
    {
        var points = new StringBuilder();
        int lastColumn = int.MinValue;
        double colMin = 0, colMax = 0, colFirst = 0, colLast = 0;

        for (int i = 0; i < times.Length; i++)
        {
            double px = x(times[i]);
            double py = y(values[i]);
            int column = (int)px;
            if (column != lastColumn)
            {
                if (lastColumn != int.MinValue) FlushColumn(points, lastColumn, colFirst, colMin, colMax, colLast);
                lastColumn = column;
                colFirst = colMin = colMax = colLast = py;
                continue;
            }

            if (py < colMin) colMin = py;
            if (py > colMax) colMax = py;
            colLast = py;
        }

        if (lastColumn != int.MinValue) FlushColumn(points, lastColumn, colFirst, colMin, colMax, colLast);
        return points.ToString();
    }

    private static void FlushColumn(StringBuilder points, int column, double first, double min, double max, double last)
    {
        points.Append(FormattableString.Invariant($"{column},{first:F1} "));
        if (min < first || min < last) points.Append(FormattableString.Invariant($"{column},{min:F1} "));
        if (max > first || max > last) points.Append(FormattableString.Invariant($"{column},{max:F1} "));
        if (last != first) points.Append(FormattableString.Invariant($"{column},{last:F1} "));
    }

    private static void AppendYearTable(StringBuilder sb, History h, Options opt, List<Sweep.Trade> trades)
    {
        var rows = YearBreakdown.Build(h, opt.Deposit, trades);

        sb.AppendLine("<h2>By year</h2>");
        sb.AppendLine("<p class=\"meta\">The drawdown column is measured against the running high water mark, " +
                      "the same one the fail rule uses, so it does not reset in January. " +
                      "The largest value in the column is the max drawdown of the whole run.</p>");
        sb.AppendLine("<table><tr><th class=\"k\">year</th><th>deposit start</th><th>deposit end</th>" +
                      "<th>return</th><th>max drawdown</th><th>pips</th><th>entries</th>" +
                      "<th>trade win rate</th></tr>");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"<tr><td class=\"k\">{r.Label}</td><td>{r.Start:N0}</td><td>{r.End:N0}</td>" +
                $"<td class=\"{(r.ReturnPercent < 0 ? "neg" : "pos")}\">{r.ReturnPercent:N1} %</td>" +
                $"<td>{r.MaxDd:N1} %</td>" +
                $"<td class=\"{(r.Pips < 0 ? "neg" : "pos")}\">{r.Pips:N0}</td>" +
                $"<td>{r.Entries:N0}</td><td>{r.WinRate:P1}</td></tr>"));
        }

        sb.AppendLine("</table>");
    }

    private static long NiceStep(long range, int targetTicks)
    {
        if (range <= 0) return 1;
        double raw = (double)range / Math.Max(1, targetTicks);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / magnitude;
        double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return Math.Max(1, (long)(step * magnitude));
    }
}
