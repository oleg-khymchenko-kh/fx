using System.Buffers.Binary;
using System.Globalization;
using System.Text;

const int HeaderSize = 64;
const int RecordSize = 16;
const uint FlagFilled = 1u << 0;
const uint FlagSpread = 1u << 2;
const int SpreadShift = 8;
const int PipPoints = 10;
const double PriceScale = 100000;
const int NotHit = int.MaxValue;

var ci = CultureInfo.InvariantCulture;
string repoRoot = @"C:\Users\Oleg\Documents\Oleg\fx";
string dataRoot = Path.Combine(repoRoot, @"FXViewer\bin\Debug\net10.0-windows\data");
string reportsDir = Path.Combine(repoRoot, "reports");
string[] symbols = ["EURUSD", "GBPUSD"];
int fromYear = 2021;
int toYear = 2026;
int summerMinute = 14 * 60;
int winterMinute = 15 * 60;
string dst = "us";
double spreadPips = 0;
bool hourScan = false;
bool reverse = false;
bool realSpread = false;
int timeScanStep = 0;
int[] tps = Grid(20, 50, 5);
int[] sls = Grid(10, 30, 5);

for (int i = 0; i < args.Length; i++)
{
    string key = args[i];
    if (key is "--hour-scan" or "--reverse" or "--real-spread")
    {
        hourScan |= key == "--hour-scan";
        reverse |= key == "--reverse";
        realSpread |= key == "--real-spread";
        continue;
    }

    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine($"Missing value for {key}");
        return 1;
    }

    string value = args[++i];
    switch (key)
    {
        case "--data": dataRoot = value; break;
        case "--out": reportsDir = value; break;
        case "--symbols":
            symbols = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant()).ToArray();
            break;
        case "--from": fromYear = int.Parse(value, ci); break;
        case "--to": toYear = int.Parse(value, ci); break;
        case "--summer": summerMinute = ClockMinute(value); break;
        case "--winter": winterMinute = ClockMinute(value); break;
        case "--dst": dst = value.ToLowerInvariant(); break;
        case "--spread": spreadPips = double.Parse(value, ci); break;
        case "--tp": tps = GridArg(value); break;
        case "--sl": sls = GridArg(value); break;
        case "--time-scan": timeScanStep = int.Parse(value, ci); break;
        default:
            Console.Error.WriteLine($"Unknown option {key}");
            Console.Error.WriteLine("Options: --symbols EURUSD,GBPUSD --from 2021 --to 2026 --summer 14:00 --winter 15:00 " +
                "--dst us|eu --spread <pips per leg> --tp 20:50:5 --sl 10:30:5 --hour-scan --time-scan <minutes> " +
                "--reverse --real-spread --data <dir> --out <dir>");
            return 1;
    }
}

if (dst is not ("us" or "eu"))
{
    Console.Error.WriteLine("--dst must be us or eu");
    return 1;
}

bool euDst = dst == "eu";
int[] distances = tps.Concat(sls).Distinct().Order().ToArray();
Combo[] combos = tps
    .SelectMany(tp => sls.Where(sl => tp > sl)
        .Select(sl => new Combo(tp, sl, Array.IndexOf(distances, tp), Array.IndexOf(distances, sl))))
    .ToArray();
int[] years = Enumerable.Range(fromYear, toYear - fromYear + 1).ToArray();
string variant = $"{Clock(summerMinute).Replace(":", "")}-{Clock(winterMinute).Replace(":", "")}-{dst}" +
    (reverse ? "-reverse" : "");
string winDays = reverse ? "both TP" : "one TP";
string lossDays = reverse ? "one TP and one SL" : "both SL";
Directory.CreateDirectory(reportsDir);
if (reverse)
    Console.WriteLine("REVERSE: trade TP = the smaller grid value, trade SL = the larger one, " +
        "so a day is either both TP or one TP and one SL; TP/SL below is the real trade TP/SL");

foreach (string symbol in symbols)
{
    var bars = LoadBars(Path.Combine(dataRoot, symbol), timeScanStep > 0 ? 0 : fromYear);
    if (bars is null)
    {
        Console.Error.WriteLine($"No {symbol} data under {dataRoot}");
        continue;
    }

    if (timeScanStep > 0)
    {
        TimeScan(bars, symbol);
        continue;
    }

    var missing = new List<DateOnly>();
    var entries = FindEntries(bars, fromYear, toYear, summerMinute, winterMinute, euDst, 0, missing);
    var results = Simulate(bars, entries, distances, combos, reverse);
    int mismatches = VerifyNaive(bars, entries, combos, results, reverse);

    Console.WriteLine();
    Console.WriteLine($"==================== {symbol} ====================");
    Console.WriteLine($"entry {Clock(summerMinute)} UTC in {dst.ToUpperInvariant()} summer time, {Clock(winterMinute)} UTC in winter, " +
        "price = avg of that M1 candle, BUY and SELL at once");
    Console.WriteLine("stops checked from the next minute, a minute touching TP and SL of one leg counts as SL");
    Console.WriteLine($"data {Utc(bars.Ts[0]):yyyy-MM-dd} .. {Utc(bars.Ts[bars.Count - 1]):yyyy-MM-dd HH:mm} UTC, " +
        $"spread cost {spreadPips.ToString("0.0#", ci)} pips per leg");
    Console.WriteLine($"self-check vs naive bar-by-bar scan: {entries.Count * combos.Length * 2:N0} legs, {mismatches} mismatches");
    foreach (int y in years)
    {
        var miss = missing.Where(d => d.Year == y).ToList();
        string list = miss.Count == 0 ? "" : " (" + string.Join(", ", miss.Select(d => d.ToString("MM-dd", ci))) + ")";
        Console.WriteLine($"  {y}: {entries.Count(e => e.Day.Year == y),3} days, no candle at the entry minute: {miss.Count}{list}");
    }

    var total = new Stats[combos.Length];
    var byYear = new Stats[combos.Length, years.Length];
    for (int c = 0; c < combos.Length; c++)
    {
        total[c] = Collect(bars, entries, results, c, 0, spreadPips);
        for (int yi = 0; yi < years.Length; yi++)
            byYear[c, yi] = Collect(bars, entries, results, c, years[yi], spreadPips);
    }

    Console.WriteLine();
    Console.WriteLine(spreadPips > 0 ? "net pips after spread, by entry year" : "pips by entry year (no spread)");
    Console.WriteLine("TP/SL " + string.Concat(years.Select(y => $"{y,7}")) + "   total   win%  need%      z  maxDD  ambig");
    for (int c = 0; c < combos.Length; c++)
    {
        var sb = new StringBuilder($"{Pair(combos[c]),-5} ");
        for (int yi = 0; yi < years.Length; yi++) sb.Append($"{byYear[c, yi].Net,7:F0}");
        var t = total[c];
        double need = NeedPct(combos[c], spreadPips, reverse);
        sb.Append($"  {t.Net,6:F0}  {t.WinPct,5:F1}  {need,5:F1}  {Z(t, need),5:F1}  {t.MaxDd,5:F0}  {t.Ambiguous,5}");
        Console.WriteLine(sb.ToString());
    }

    Console.WriteLine();
    Console.WriteLine("entries by entry year, every entry is 2 trades: " +
        string.Join(", ", years.Select(y => $"{y} {entries.Count(e => e.Day.Year == y)}")) + $", all {entries.Count}");
    Console.WriteLine($"days with {winDays} / days with {lossDays}, by entry year");
    Console.WriteLine("TP/SL " + string.Concat(years.Select(y => $"{y,10}")) + "      total  open");
    for (int c = 0; c < combos.Length; c++)
    {
        var sb = new StringBuilder($"{Pair(combos[c]),-5} ");
        for (int yi = 0; yi < years.Length; yi++)
            sb.Append($"{byYear[c, yi].Wins + "/" + byYear[c, yi].Losses,10}");
        sb.Append($"{total[c].Wins + "/" + total[c].Losses,11}  {total[c].Open,4}");
        Console.WriteLine(sb.ToString());
    }

    int best = Enumerable.Range(0, combos.Length).MaxBy(c => total[c].Net);
    Console.WriteLine();
    Console.WriteLine("best per year");
    Console.WriteLine("year   TP/SL   pips   days  wins  win%  need%  maxDD");
    for (int yi = 0; yi < years.Length; yi++)
    {
        int c = Enumerable.Range(0, combos.Length).MaxBy(k => byYear[k, yi].Net);
        var s = byYear[c, yi];
        Console.WriteLine($"{years[yi]}   {Pair(combos[c]),-5}  {s.Net,5:F0}  {s.Days,5}  {s.Wins,4}  {s.WinPct,4:F1}  " +
            $"{NeedPct(combos[c], spreadPips, reverse),5:F1}  {s.MaxDd,5:F0}");
    }

    Console.WriteLine();
    Console.WriteLine($"best over all years: TP {TradeTp(combos[best])} / SL {TradeSl(combos[best])}");
    Console.WriteLine("year    pips   days  wins  losses  open  win%  need%  maxDD  close p50/p90/max min");
    for (int yi = 0; yi <= years.Length; yi++)
    {
        var s = yi < years.Length ? byYear[best, yi] : total[best];
        string label = yi < years.Length ? years[yi].ToString(ci) : "all ";
        Console.WriteLine($"{label}  {s.Net,6:F0}  {s.Days,5}  {s.Wins,4}  {s.Losses,6}  {s.Open,4}  {s.WinPct,4:F1}  " +
            $"{NeedPct(combos[best], spreadPips, reverse),5:F1}  {s.MaxDd,5:F0}  {s.ClosePct(0.5)}/{s.ClosePct(0.9)}/{s.ClosePct(1)}");
    }

    double[] spreads = entries.Where(e => bars.Spread[e.Bar] >= 0).Select(e => bars.Spread[e.Bar] / 10.0).Order().ToArray();
    Console.WriteLine();
    Console.WriteLine(spreads.Length == 0
        ? "no stored spread at the entry minutes"
        : $"stored max spread inside the entry minute, {spreads.Length} days: median {Pct(spreads, 0.5):F1}, " +
          $"p75 {Pct(spreads, 0.75):F1}, p90 {Pct(spreads, 0.9):F1}, max {spreads[^1]:F1} pips");

    string baseName = Path.Combine(reportsDir, $"{symbol.ToLowerInvariant()}-straddle-{variant}");
    using (var w = new StreamWriter(baseName + ".csv"))
    {
        w.WriteLine("symbol;year;tp;sl;days;wins;losses;open;winPct;needPct;grossPips;spreadPerLeg;netPips;maxDdPips;ambiguousLegs;closeP50Min;closeP90Min;closeMaxMin");
        for (int c = 0; c < combos.Length; c++)
            for (int yi = 0; yi <= years.Length; yi++)
            {
                var s = yi < years.Length ? byYear[c, yi] : total[c];
                string year = yi < years.Length ? years[yi].ToString(ci) : "all";
                w.WriteLine(string.Join(';', symbol, year, TradeTp(combos[c]), TradeSl(combos[c]), s.Days, s.Wins, s.Losses, s.Open,
                    s.WinPct.ToString("F2", ci), NeedPct(combos[c], spreadPips, reverse).ToString("F2", ci), s.Gross,
                    spreadPips.ToString("0.0#", ci), s.Net.ToString("F1", ci), s.MaxDd.ToString("F1", ci), s.Ambiguous,
                    s.ClosePct(0.5), s.ClosePct(0.9), s.ClosePct(1)));
            }
    }

    string daysPath = $"{baseName}-days-tp{TradeTp(combos[best])}-sl{TradeSl(combos[best])}.csv";
    using (var w = new StreamWriter(daysPath))
    {
        w.WriteLine("date;weekday;entryUtc;entryPrice;buyPips;buyCloseUtc;sellPips;sellCloseUtc;dayPips;ambiguousLegs;spreadAtEntryPips");
        for (int e = 0; e < entries.Count; e++)
        {
            var r = results[e, best];
            int bar = entries[e].Bar;
            w.WriteLine(string.Join(';', entries[e].Day.ToString("yyyy-MM-dd", ci), entries[e].Day.DayOfWeek.ToString()[..3],
                Utc(bars.Ts[bar]).ToString("HH:mm", ci), (bars.Avg[bar] / PriceScale).ToString("F5", ci),
                r.Buy.Pips, CloseTime(bars, r.Buy), r.Sell.Pips, CloseTime(bars, r.Sell),
                r.Buy.CloseBar < 0 || r.Sell.CloseBar < 0 ? "" : (r.Buy.Pips + r.Sell.Pips).ToString(ci),
                (r.Buy.Ambiguous ? 1 : 0) + (r.Sell.Ambiguous ? 1 : 0),
                bars.Spread[bar] < 0 ? "" : (bars.Spread[bar] / 10.0).ToString("F1", ci)));
        }
    }

    Console.WriteLine($"CSV: {baseName}.csv");
    Console.WriteLine($"CSV: {daysPath}");

    if (realSpread) RealSpreadCheck(bars, entries, results);

    if (!hourScan) continue;

    Console.WriteLine();
    Console.WriteLine($"control: same rules at every hour of the day, {years[0]}..{years[^1]} together");
    Console.WriteLine($"entry UTC summer/winter   days  combos>0  avg pips/combo  best combo   pips   TP{TradeTp(combos[best])}/SL{TradeSl(combos[best])} pips  win%-need%");
    using var hw = new StreamWriter(baseName + "-hours.csv");
    hw.WriteLine("summerUtc;winterUtc;tp;sl;days;wins;losses;winPct;needPct;netPips;maxDdPips");
    int firstShift = -(summerMinute / 60);
    for (int k = firstShift; k < firstShift + 24; k++)
    {
        var hourEntries = FindEntries(bars, fromYear, toYear, summerMinute, winterMinute, euDst, k * 60, null);
        var hourResults = Simulate(bars, hourEntries, distances, combos, reverse);
        var hourStats = Enumerable.Range(0, combos.Length)
            .Select(c => Collect(bars, hourEntries, hourResults, c, 0, spreadPips)).ToArray();
        for (int c = 0; c < combos.Length; c++)
            hw.WriteLine(string.Join(';', Clock(summerMinute + k * 60), Clock(winterMinute + k * 60), TradeTp(combos[c]),
                TradeSl(combos[c]), hourStats[c].Days, hourStats[c].Wins, hourStats[c].Losses,
                hourStats[c].WinPct.ToString("F2", ci), NeedPct(combos[c], spreadPips, reverse).ToString("F2", ci),
                hourStats[c].Net.ToString("F1", ci), hourStats[c].MaxDd.ToString("F1", ci)));

        int hourBest = Enumerable.Range(0, combos.Length).MaxBy(c => hourStats[c].Net);
        var main = hourStats[best];
        string mark = k == 0 ? " <<" : "";
        Console.WriteLine($"{Clock(summerMinute + k * 60)} / {Clock(winterMinute + k * 60)}        {hourEntries.Count,5}  " +
            $"{hourStats.Count(s => s.Net > 0),5}/{combos.Length}  {hourStats.Average(s => s.Net),14:F0}  " +
            $"{Pair(combos[hourBest]),7}  {hourStats[hourBest].Net,6:F0}  {main.Net,12:F0}  " +
            $"{main.WinPct - NeedPct(combos[best], spreadPips, reverse),9:F1}{mark}");
    }

    Console.WriteLine($"CSV: {baseName}-hours.csv");
}

return 0;

void TimeScan(Bars bars, string symbol)
{
    int firstYear = Utc(bars.Ts[0]).Year;
    int yearCount = toYear - firstYear + 1;
    int searchFrom = fromYear - firstYear;
    int searchYears = yearCount - searchFrom;
    var schedules = new List<Schedule>();
    for (int m = 0; m < 1440; m += timeScanStep)
    {
        schedules.Add(new Schedule(dst, m, m + 60));
        schedules.Add(new Schedule("fixed", m, m));
    }

    var scans = new ScanResult[schedules.Count];
    Parallel.For(0, schedules.Count, s =>
    {
        var sc = schedules[s];
        var entries = FindEntries(bars, firstYear, toYear, sc.Summer, sc.Winter, euDst, 0, null);
        var results = Simulate(bars, entries, distances, combos, reverse);
        var r = new ScanResult(sc, combos.Length, yearCount);
        for (int e = 0; e < entries.Count; e++)
        {
            int yi = entries[e].Day.Year - firstYear;
            for (int c = 0; c < combos.Length; c++)
            {
                var d = results[e, c];
                if (d.Buy.CloseBar < 0 || d.Sell.CloseBar < 0)
                {
                    r.Open[c, yi]++;
                    continue;
                }

                int pips = d.Buy.Pips + d.Sell.Pips;
                r.Pips[c, yi] += pips;
                if (pips > 0) r.Wins[c, yi]++;
                else r.Losses[c, yi]++;
            }
        }

        double[] spreads = entries.Where(e => bars.Spread[e.Bar] >= 0)
            .Select(e => bars.Spread[e.Bar] / 10.0).Order().ToArray();
        r.Spread = spreads.Length == 0 ? 0 : Pct(spreads, 0.5);
        scans[s] = r;
    });

    var configs = new List<ScanConfig>();
    foreach (var r in scans)
        for (int c = 0; c < combos.Length; c++)
        {
            long pips = 0, checkPips = 0;
            int wins = 0, losses = 0, positive = 0, netPositive = 0, checkPositive = 0, checkYears = 0;
            double net = 0, checkNet = 0;
            for (int yi = 0; yi < yearCount; yi++)
            {
                int days = r.Wins[c, yi] + r.Losses[c, yi];
                if (yi >= searchFrom)
                {
                    double yearNet = r.Pips[c, yi] - 2 * r.Spread * days;
                    pips += r.Pips[c, yi];
                    wins += r.Wins[c, yi];
                    losses += r.Losses[c, yi];
                    net += yearNet;
                    if (r.Pips[c, yi] > 0) positive++;
                    if (yearNet > 0) netPositive++;
                }
                else if (days > 0)
                {
                    checkPips += r.Pips[c, yi];
                    checkNet += r.Pips[c, yi] - 2 * r.Spread * days;
                    checkYears++;
                    if (r.Pips[c, yi] > 0) checkPositive++;
                }
            }

            configs.Add(new ScanConfig(r, c, pips, wins, losses, positive, net, netPositive, checkPips, checkNet,
                checkPositive, checkYears));
        }

    var gross = configs.Where(x => x.PositiveYears == searchYears).ToList();
    var afterSpread = configs.Where(x => x.NetPositiveYears == searchYears).ToList();

    Console.WriteLine();
    Console.WriteLine($"==================== {symbol} time scan ====================");
    Console.WriteLine($"entry every {timeScanStep} min, two clocks: {dst} = T in {dst.ToUpperInvariant()} summer time and T+1h in winter, " +
        "fixed = T all year");
    Console.WriteLine($"search years {fromYear}..{toYear}, check years {firstYear}..{fromYear - 1}, {combos.Length} TP/SL combos, " +
        $"{configs.Count:N0} configs");
    Console.WriteLine("spread = median stored max spread in the entry minute of that clock, paid on both trades of a day");
    Console.WriteLine("share of configs positive per search year: " + string.Join(", ", Enumerable.Range(searchFrom, searchYears)
        .Select(yi => $"{firstYear + yi} {100.0 * configs.Count(x => x.Scan.Pips[x.Combo, yi] > 0) / configs.Count:F0}%")));

    PrintFound("positive in every search year, no spread, pips by year without spread", gross, x => x.Pips, false);
    PrintFound("positive in every search year after spread, pips by year after spread", afterSpread, x => x.Net, true);

    Console.WriteLine();
    Console.WriteLine($"check years {firstYear}..{fromYear - 1} total > 0: all configs {CheckShare(configs):F1}%, " +
        $"found without spread {CheckShare(gross):F1}%, found after spread {CheckShare(afterSpread):F1}%");

    string path = Path.Combine(reportsDir,
        $"{symbol.ToLowerInvariant()}-straddle-time-scan-{fromYear}{(reverse ? "-reverse" : "")}.csv");
    string winColumn = reverse ? "bothWin" : "oneWin";
    string lossColumn = reverse ? "oneLose" : "bothLose";
    using (var w = new StreamWriter(path))
    {
        var head = new List<string> { "symbol", "clock", "summerUtc", "winterUtc", "tp", "sl", "spreadPips" };
        for (int yi = 0; yi < yearCount; yi++) head.Add($"pips{firstYear + yi}");
        for (int yi = searchFrom; yi < yearCount; yi++)
        {
            head.Add($"{winColumn}{firstYear + yi}");
            head.Add($"{lossColumn}{firstYear + yi}");
        }

        head.AddRange(["pips", winColumn, lossColumn, "positiveYears", "netPips", "netPositiveYears",
            "checkPips", "checkNetPips", "checkPositiveYears", "checkYears"]);
        w.WriteLine(string.Join(';', head));
        foreach (var x in configs)
        {
            var row = new List<string>
            {
                symbol, x.Scan.Schedule.Variant, Clock(x.Scan.Schedule.Summer), Clock(x.Scan.Schedule.Winter),
                TradeTp(combos[x.Combo]).ToString(ci), TradeSl(combos[x.Combo]).ToString(ci), x.Scan.Spread.ToString("F1", ci),
            };
            for (int yi = 0; yi < yearCount; yi++) row.Add(x.Scan.Pips[x.Combo, yi].ToString(ci));
            for (int yi = searchFrom; yi < yearCount; yi++)
            {
                row.Add(x.Scan.Wins[x.Combo, yi].ToString(ci));
                row.Add(x.Scan.Losses[x.Combo, yi].ToString(ci));
            }

            row.AddRange([x.Pips.ToString(ci), x.Wins.ToString(ci), x.Losses.ToString(ci), x.PositiveYears.ToString(ci),
                x.Net.ToString("F1", ci), x.NetPositiveYears.ToString(ci), x.CheckPips.ToString(ci),
                x.CheckNet.ToString("F1", ci), x.CheckPositiveYears.ToString(ci), x.CheckYears.ToString(ci)]);
            w.WriteLine(string.Join(';', row));
        }
    }

    Console.WriteLine($"CSV: {path}");

    void PrintFound(string title, List<ScanConfig> found, Func<ScanConfig, double> key, bool netYears)
    {
        Console.WriteLine();
        Console.WriteLine($"{title}: {found.Count} configs at {found.Select(x => x.Scan).Distinct().Count()} clocks");
        if (found.Count == 0) return;
        string yearHead = string.Concat(Enumerable.Range(fromYear, searchYears).Select(y => $"{y,6}"));
        Console.WriteLine($"clock              combos  TP/SL{yearHead}   gross  win/loss  spread     net | " +
            $"{firstYear}-{fromYear - 1} gross   net  yrs>0");
        var clocks = found.GroupBy(x => x.Scan)
            .Select(g => (Count: g.Count(), Best: g.MaxBy(key)))
            .OrderByDescending(g => key(g.Best))
            .Take(40);
        foreach (var (count, best) in clocks)
        {
            var sc = best.Scan;
            var cb = combos[best.Combo];
            var sb = new StringBuilder($"{sc.Schedule.Variant,-5} {Label(sc.Schedule),-12} {count,3}/{combos.Length}  {Pair(cb),-5}");
            for (int yi = searchFrom; yi < yearCount; yi++)
            {
                int days = sc.Wins[best.Combo, yi] + sc.Losses[best.Combo, yi];
                double value = netYears ? sc.Pips[best.Combo, yi] - 2 * sc.Spread * days : sc.Pips[best.Combo, yi];
                sb.Append($"{value,6:F0}");
            }

            sb.Append($"  {best.Pips,6}  {best.Wins,4}/{best.Losses,-4}  {sc.Spread,5:F1}  {best.Net,6:F0} | " +
                $"{best.CheckPips,11}  {best.CheckNet,5:F0}  {best.CheckPositiveYears,2}/{best.CheckYears}");
            Console.WriteLine(sb.ToString());
        }
    }

    static double CheckShare(List<ScanConfig> list) =>
        list.Count == 0 ? 0 : 100.0 * list.Count(x => x.CheckPips > 0) / list.Count;
}

void RealSpreadCheck(Bars bars, List<Entry> entries, DayResult[,] results)
{
    var known = Enumerable.Range(0, entries.Count).Where(e => bars.Spread[entries[e].Bar] >= 0).ToList();
    Console.WriteLine();
    if (known.Count == 0)
    {
        Console.WriteLine("real spread check: no stored spread at the entry minutes");
        return;
    }

    Console.WriteLine($"real spread check: {known.Count} days with a stored spread at the entry minute, " +
        $"{entries[known[0]].Day:yyyy-MM-dd} .. {entries[known[^1]].Day:yyyy-MM-dd}");
    Console.WriteLine("  bid only    = the model above, no spread");
    Console.WriteLine("  flat spread = bid only minus 2 x the entry minute spread of that day");
    Console.WriteLine("  real spread = buy opens at ask = avg + spread and closes on bid, sell opens at bid and closes on " +
        "ask = bid + the stored max spread of every minute (last known one if a minute has none)");
    Console.WriteLine("TP/SL   days   bid only  flat spread  real spread  real/day  real win/loss days  exits in 20:55-22:15 UTC");
    for (int c = 0; c < combos.Length; c++)
    {
        var cb = combos[c];
        int days = 0, realWins = 0, realLosses = 0, rolloverExits = 0;
        long bidOnly = 0, real = 0;
        double flat = 0;
        foreach (int e in known)
        {
            int bar = entries[e].Bar;
            var r = results[e, c];
            var buy = NaiveLegAsk(bars, bar, 1, TradeTp(cb), TradeSl(cb));
            var sell = NaiveLegAsk(bars, bar, -1, TradeTp(cb), TradeSl(cb));
            if (r.Buy.CloseBar < 0 || r.Sell.CloseBar < 0 || buy.CloseBar < 0 || sell.CloseBar < 0) continue;
            days++;
            int pips = r.Buy.Pips + r.Sell.Pips;
            bidOnly += pips;
            flat += pips - 2 * bars.Spread[bar] / 10.0;
            int realPips = buy.Pips + sell.Pips;
            real += realPips;
            if (realPips > 0) realWins++;
            else realLosses++;
            foreach (int close in new[] { buy.CloseBar, sell.CloseBar })
            {
                int minuteOfDay = (int)(bars.Ts[close] % 86400 / 60);
                if (minuteOfDay >= 20 * 60 + 55 && minuteOfDay <= 22 * 60 + 15) rolloverExits++;
            }
        }

        Console.WriteLine($"{Pair(cb),-5}  {days,5}  {bidOnly,9}  {flat,11:F0}  {real,11}  {(days == 0 ? 0 : (double)real / days),8:F2}  " +
            $"{realWins,8}/{realLosses,-9}  {rolloverExits,8} of {days * 2}");
    }
}

string Pair(Combo c) => $"{TradeTp(c)}/{TradeSl(c)}";

int TradeTp(Combo c) => reverse ? c.Sl : c.Tp;

int TradeSl(Combo c) => reverse ? c.Tp : c.Sl;

static string Label(Schedule s) => s.Summer == s.Winter ? Clock(s.Summer) : $"{Clock(s.Summer)}/{Clock(s.Winter)}";

static Bars? LoadBars(string dir, int fromYear)
{
    if (!Directory.Exists(dir)) return null;
    var files = Directory.GetFiles(dir, "*.m1")
        .Select(p => (Path: p, Year: int.TryParse(Path.GetFileNameWithoutExtension(p), out int y) ? y : 0))
        .Where(f => f.Year >= fromYear)
        .OrderBy(f => f.Year)
        .ToList();
    if (files.Count == 0) return null;

    long capacity = files.Sum(f => (new FileInfo(f.Path).Length - HeaderSize) / RecordSize);
    var b = new Bars(capacity);
    foreach (var (path, year) in files)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        long yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        int records = (bytes.Length - HeaderSize) / RecordSize;
        for (int i = 0; i < records && b.Count < capacity; i++)
        {
            int off = HeaderSize + i * RecordSize;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 12));
            if ((flags & FlagFilled) == 0) continue;
            int n = b.Count++;
            b.Lo[n] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off));
            b.Hi[n] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 4));
            b.Avg[n] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(off + 8));
            b.Ts[n] = yearStart + i * 60L;
            int code = (int)((flags >> SpreadShift) & 0xFF);
            b.Spread[n] = (flags & FlagSpread) == 0 ? (short)-1 : (short)(code <= 127 ? code : (code - 115) * 10);
        }
    }

    return b.Count == 0 ? null : b;
}

static List<Entry> FindEntries(Bars b, int fromYear, int toYear, int summerMinute, int winterMinute, bool euDst,
    int shiftMinutes, List<DateOnly>? missing)
{
    var list = new List<Entry>();
    for (var day = new DateOnly(fromYear, 1, 1); day.Year <= toYear; day = day.AddDays(1))
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
        bool summer = euDst ? EuSummer(day) : UsSummer(day);
        long unix = UnixOf(day) + ((summer ? summerMinute : winterMinute) + shiftMinutes) * 60L;
        if (unix > b.Ts[b.Count - 1]) break;
        int bar = Array.BinarySearch(b.Ts, 0, b.Count, unix);
        if (bar >= 0) list.Add(new Entry(day, bar));
        else missing?.Add(day);
    }

    return list;
}

static DayResult[,] Simulate(Bars b, List<Entry> entries, int[] distances, Combo[] combos, bool reverse)
{
    var results = new DayResult[entries.Count, combos.Length];
    var up = new int[distances.Length];
    var down = new int[distances.Length];
    for (int e = 0; e < entries.Count; e++)
    {
        FirstHits(b, entries[e].Bar, distances, combos, up, down);
        for (int c = 0; c < combos.Length; c++)
        {
            var cb = combos[c];
            results[e, c] = reverse
                ? new DayResult(
                    Resolve(up[cb.SlIdx], down[cb.TpIdx], cb.Sl, cb.Tp),
                    Resolve(down[cb.SlIdx], up[cb.TpIdx], cb.Sl, cb.Tp))
                : new DayResult(
                    Resolve(up[cb.TpIdx], down[cb.SlIdx], cb.Tp, cb.Sl),
                    Resolve(down[cb.TpIdx], up[cb.SlIdx], cb.Tp, cb.Sl));
        }
    }

    return results;
}

static void FirstHits(Bars b, int entry, int[] distances, Combo[] combos, int[] up, int[] down)
{
    Array.Fill(up, NotHit);
    Array.Fill(down, NotHit);
    int price = b.Avg[entry];
    int k = distances.Length;
    int ku = 0, kd = 0;
    for (int j = entry + 1; j < b.Count; j++)
    {
        bool moved = false;
        while (ku < k && b.Hi[j] >= price + distances[ku] * PipPoints)
        {
            up[ku++] = j;
            moved = true;
        }

        while (kd < k && b.Lo[j] <= price - distances[kd] * PipPoints)
        {
            down[kd++] = j;
            moved = true;
        }

        if (moved && AllDecided(combos, ku, kd)) return;
    }
}

static bool AllDecided(Combo[] combos, int ku, int kd)
{
    foreach (var c in combos)
        if (!((ku > c.TpIdx || kd > c.SlIdx) && (kd > c.TpIdx || ku > c.SlIdx)))
            return false;
    return true;
}

static Leg Resolve(int win, int loss, int tp, int sl)
{
    if (win == NotHit && loss == NotHit) return new Leg(0, -1, false);
    if (win < loss) return new Leg(tp, win, false);
    return new Leg(-sl, loss, win == loss);
}

static int VerifyNaive(Bars b, List<Entry> entries, Combo[] combos, DayResult[,] results, bool reverse)
{
    int bad = 0;
    for (int e = 0; e < entries.Count; e++)
        for (int c = 0; c < combos.Length; c++)
        {
            int tp = reverse ? combos[c].Sl : combos[c].Tp;
            int sl = reverse ? combos[c].Tp : combos[c].Sl;
            if (NaiveLeg(b, entries[e].Bar, 1, tp, sl) != results[e, c].Buy) bad++;
            if (NaiveLeg(b, entries[e].Bar, -1, tp, sl) != results[e, c].Sell) bad++;
        }

    return bad;
}

static Leg NaiveLegAsk(Bars b, int entry, int dir, int tp, int sl)
{
    int spread = b.Spread[entry] * PipPoints / 10;
    int open = dir > 0 ? b.Avg[entry] + spread : b.Avg[entry];
    int target = open + dir * tp * PipPoints;
    int stop = open - dir * sl * PipPoints;
    for (int j = entry + 1; j < b.Count; j++)
    {
        if (b.Spread[j] >= 0) spread = b.Spread[j] * PipPoints / 10;
        int lo = dir > 0 ? b.Lo[j] : b.Lo[j] + spread;
        int hi = dir > 0 ? b.Hi[j] : b.Hi[j] + spread;
        bool hitTp = dir > 0 ? hi >= target : lo <= target;
        bool hitSl = dir > 0 ? lo <= stop : hi >= stop;
        if (hitSl) return new Leg(-sl, j, hitTp);
        if (hitTp) return new Leg(tp, j, false);
    }

    return new Leg(0, -1, false);
}

static Leg NaiveLeg(Bars b, int entry, int dir, int tp, int sl)
{
    int price = b.Avg[entry];
    int target = price + dir * tp * PipPoints;
    int stop = price - dir * sl * PipPoints;
    for (int j = entry + 1; j < b.Count; j++)
    {
        bool hitTp = dir > 0 ? b.Hi[j] >= target : b.Lo[j] <= target;
        bool hitSl = dir > 0 ? b.Lo[j] <= stop : b.Hi[j] >= stop;
        if (hitSl) return new Leg(-sl, j, hitTp);
        if (hitTp) return new Leg(tp, j, false);
    }

    return new Leg(0, -1, false);
}

static Stats Collect(Bars b, List<Entry> entries, DayResult[,] results, int combo, int year, double spread)
{
    var s = new Stats();
    for (int e = 0; e < entries.Count; e++)
    {
        if (year != 0 && entries[e].Day.Year != year) continue;
        var r = results[e, combo];
        if (r.Buy.CloseBar < 0 || r.Sell.CloseBar < 0)
        {
            s.Open++;
            continue;
        }

        int pips = r.Buy.Pips + r.Sell.Pips;
        s.Days++;
        if (pips > 0) s.Wins++;
        else s.Losses++;
        s.Ambiguous += (r.Buy.Ambiguous ? 1 : 0) + (r.Sell.Ambiguous ? 1 : 0);
        s.Gross += pips;
        s.Net += pips - 2 * spread;
        if (s.Net > s.Peak) s.Peak = s.Net;
        if (s.Peak - s.Net > s.MaxDd) s.MaxDd = s.Peak - s.Net;
        int close = Math.Max(r.Buy.CloseBar, r.Sell.CloseBar);
        s.CloseMinutes.Add((int)((b.Ts[close] - b.Ts[entries[e].Bar]) / 60));
    }

    s.CloseMinutes.Sort();
    return s;
}

static double NeedPct(Combo c, double spread, bool reverse) =>
    100.0 * ((reverse ? c.Tp - c.Sl : 2 * c.Sl) + 2 * spread) / (c.Tp + c.Sl);

static double Z(Stats s, double needPct)
{
    if (s.Days == 0) return 0;
    double p = needPct / 100;
    return (s.WinPct / 100 - p) / Math.Sqrt(p * (1 - p) / s.Days);
}

static string CloseTime(Bars b, Leg leg) =>
    leg.CloseBar < 0 ? "open" : Utc(b.Ts[leg.CloseBar]).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

static double Pct(double[] sorted, double q)
{
    double pos = q * (sorted.Length - 1);
    int i = (int)pos;
    return i + 1 < sorted.Length ? sorted[i] + (pos - i) * (sorted[i + 1] - sorted[i]) : sorted[i];
}

static int[] Grid(int from, int to, int step) =>
    Enumerable.Range(0, (to - from) / step + 1).Select(i => from + i * step).ToArray();

static int[] GridArg(string value)
{
    int[] p = value.Split(':').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    return p.Length switch
    {
        1 => p,
        3 => Grid(p[0], p[1], p[2]),
        _ => throw new FormatException("A grid needs from:to:step or one value"),
    };
}

static int ClockMinute(string value)
{
    var t = TimeOnly.ParseExact(value, "HH:mm", CultureInfo.InvariantCulture);
    return t.Hour * 60 + t.Minute;
}

static string Clock(int minute)
{
    int m = (minute % 1440 + 1440) % 1440;
    return $"{m / 60:00}:{m % 60:00}";
}

static DateTime Utc(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

static long UnixOf(DateOnly d) => new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

static bool UsSummer(DateOnly d) => d >= NthSunday(d.Year, 3, 2) && d < NthSunday(d.Year, 11, 1);

static bool EuSummer(DateOnly d) => d >= LastSunday(d.Year, 3) && d < LastSunday(d.Year, 10);

static DateOnly NthSunday(int year, int month, int n)
{
    var d = new DateOnly(year, month, 1);
    while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
    return d.AddDays(7 * (n - 1));
}

static DateOnly LastSunday(int year, int month)
{
    var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
    while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
    return d;
}

sealed class Bars(long capacity)
{
    public readonly int[] Lo = new int[capacity];
    public readonly int[] Hi = new int[capacity];
    public readonly int[] Avg = new int[capacity];
    public readonly long[] Ts = new long[capacity];
    public readonly short[] Spread = new short[capacity];
    public int Count;
}

sealed class Stats
{
    public int Days;
    public int Wins;
    public int Losses;
    public int Open;
    public int Ambiguous;
    public long Gross;
    public double Net;
    public double Peak;
    public double MaxDd;
    public readonly List<int> CloseMinutes = [];

    public double WinPct => Days == 0 ? 0 : 100.0 * Wins / Days;

    public int ClosePct(double q) =>
        CloseMinutes.Count == 0 ? 0 : CloseMinutes[(int)Math.Round(q * (CloseMinutes.Count - 1))];
}

sealed class ScanResult(Schedule schedule, int combos, int years)
{
    public readonly Schedule Schedule = schedule;
    public readonly long[,] Pips = new long[combos, years];
    public readonly int[,] Wins = new int[combos, years];
    public readonly int[,] Losses = new int[combos, years];
    public readonly int[,] Open = new int[combos, years];
    public double Spread;
}

readonly record struct Schedule(string Variant, int Summer, int Winter);

readonly record struct ScanConfig(ScanResult Scan, int Combo, long Pips, int Wins, int Losses, int PositiveYears,
    double Net, int NetPositiveYears, long CheckPips, double CheckNet, int CheckPositiveYears, int CheckYears);

readonly record struct Entry(DateOnly Day, int Bar);

readonly record struct Combo(int Tp, int Sl, int TpIdx, int SlIdx);

readonly record struct Leg(int Pips, int CloseBar, bool Ambiguous);

readonly record struct DayResult(Leg Buy, Leg Sell);
