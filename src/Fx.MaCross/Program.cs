using System.Diagnostics;
using System.Globalization;

namespace Fx.MaCross;

internal static class Program
{
    private static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt is null) return 1;
        opt.ApplyPin();

        if (opt.MultiWindow && !opt.Bands && !(opt.Reversal && opt.WalkForwardKey > 0) &&
            !(opt.Comeback && (opt.PerYear || opt.PerMonth)) &&
            !(opt.Robot2 && opt.WalkForwardKey > 0))
        {
            Console.Error.WriteLine("--per-year and --per-month work with --bands and --comeback, " +
                                    "--rolling-start only with --bands; --reversal supports --walk-forward.");
            return 1;
        }

        var total = Stopwatch.StartNew();

        Console.WriteLine($"Loading {opt.Symbol} from {opt.DataRoot}");
        var sw = Stopwatch.StartNew();
        var history = History.Load(opt.DataRoot, opt.Symbol, opt.FromYear, opt.ToYear);
        if (history is null) return 1;

        int n = history.Count;
        Array.Resize(ref history.Lo, n);
        Array.Resize(ref history.Hi, n);
        Array.Resize(ref history.Avg, n);
        Array.Resize(ref history.Ts, n);
        GC.Collect();

        Console.WriteLine($"Total trading minutes: {n:N0} " +
                          $"({history.TimeUtc(0):yyyy-MM-dd} .. {history.TimeUtc(n - 1):yyyy-MM-dd}), " +
                          $"loaded in {sw.Elapsed.TotalSeconds:F1}s");

        var periods = opt.BuildPeriodGrid();
        var riskGrid = opt.BuildRiskGrid();
        var slGrid = opt.BuildStopGrid(opt.SlMin, opt.SlMax, opt.SlStep);
        var tpGrid = opt.BuildStopGrid(opt.TpMin, opt.TpMax, opt.TpStep);
        var distPips = slGrid.Concat(tpGrid).Distinct().Order().ToArray();
        var distPoints = distPips.Select(p => p * opt.PipPoints).ToArray();
        var slDist = slGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();
        var tpDist = tpGrid.Select(p => Array.IndexOf(distPips, p)).ToArray();

        if (opt.Comeback)
        {
            Console.WriteLine();
            var cbWindows = opt.MultiWindow ? BuildWindows(opt, history, n) : new[] { ("all", 0, n) };
            if (cbWindows.Length == 0)
            {
                Console.Error.WriteLine("No window falls inside the loaded history.");
                return 1;
            }

            int comebackCode = ComebackSweep.Run(opt, history, slGrid, tpGrid, riskGrid, cbWindows);
            Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
            return comebackCode;
        }

        if (opt.Swing)
        {
            Console.WriteLine();
            int swingCode = SwingSweep.Run(opt, history, slGrid, tpGrid, riskGrid);
            Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
            return swingCode;
        }

        if (opt.Robot2)
        {
            int robotCode = opt.WalkForwardKey > 0
                ? Robot2Sweep.WalkForward(opt, history, BuildWindows(opt, history, n),
                    BuildTestWindows(opt, history, n))
                : Robot2Sweep.Run(opt, history);
            Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
            return robotCode;
        }

        var pairs = new List<(int W1, int W2)>();
        if (opt.Bands)
        {
            pairs.Add((1, 2));
            periods = new[] { 1, 2 };
        }
        else if (opt.AlwaysIn && opt.Pin is null)
        {
            pairs.Add((1, 2));
            periods = new[] { 1, 2 };
        }
        else if (opt.Pin is { } pin)
        {
            if (pin.A1 >= pin.A2)
            {
                Console.Error.WriteLine("--pin needs a1 < a2");
                return 1;
            }

            pairs.Add((pin.A1, pin.A2));
            periods = new[] { pin.A1, pin.A2 };
        }
        else
        {
            if (periods.Length < 2)
            {
                Console.Error.WriteLine("Period grid needs at least two values.");
                return 1;
            }

            for (int i = 0; i < periods.Length; i++)
            for (int j = i + 1; j < periods.Length; j++)
                pairs.Add((periods[i], periods[j]));

            if (opt.WithMaMax > 0)
                pairs = pairs.Where(p =>
                    (p.W1 >= opt.WithMaMin && p.W1 <= opt.WithMaMax) ||
                    (p.W2 >= opt.WithMaMin && p.W2 <= opt.WithMaMax)).ToList();
            if (opt.MinPeriod2 > 0)
                pairs = pairs.Where(p => p.W2 >= opt.MinPeriod2).ToList();
        }

        if (periods[^1] >= n)
        {
            Console.Error.WriteLine($"Longest period {periods[^1]:N0} does not fit into {n:N0} minutes of history.");
            return 1;
        }

        long sequences = (long)pairs.Count * slGrid.Length * tpGrid.Length;
        long runs = sequences * riskGrid.Length;
        double tableGb = (double)distPoints.Length * 2 * n * 4 / (1024 * 1024 * 1024);

        Console.WriteLine();
        Console.WriteLine($"MA periods:   {periods.Length} values {periods[0]}..{periods[^1]} trading minutes");
        Console.WriteLine($"              {Preview(periods)}");
        Console.WriteLine($"MA pairs:     {pairs.Count:N0}" +
                          (opt.WithMaMax > 0 ? $" (at least one MA inside {opt.WithMaMin}..{opt.WithMaMax} minutes)" : ""));
        Console.WriteLine($"StopLoss:     {slGrid.Length} values {opt.SlMin}..{opt.SlMax} step {opt.SlStep} pips");
        Console.WriteLine($"TakeProfit:   {tpGrid.Length} values {opt.TpMin}..{opt.TpMax} step {opt.TpStep} pips");
        Console.WriteLine($"Risk/trade:   {riskGrid.Length} values {riskGrid[0] / 100.0:F2}%..{riskGrid[^1] / 100.0:F2}% of equity");
        Console.WriteLine($"              {Preview(riskGrid.Select(b => b / 100.0).ToArray())}");
        Console.WriteLine($"Combinations: {runs:N0} ({sequences:N0} trade sequences x {riskGrid.Length} sizes)");
        Console.WriteLine($"Deposit:      {opt.Deposit:N0}, dropped when equity falls " +
                          $"{opt.MaxDdPercent:F0}% below its own peak");
        Console.WriteLine($"Filters:      at least {opt.MinCrossovers:N0} crossovers per MA pair, " +
                          $"at least {opt.MinEntries:N0} entries per result");
        Console.WriteLine($"Spread cost:  {opt.SpreadTenths / 10.0:F1} pips per trade " +
                          $"({opt.SpreadTenths / 5.0:F1} pips per entry, both trades)");
        Console.WriteLine($"Threads:      {opt.Threads}");
        Console.WriteLine();

        var prefix = new long[n + 1];
        {
            var avg = history.Avg;
            long acc = 0;
            for (int i = 0; i < n; i++)
            {
                acc += avg[i];
                prefix[i + 1] = acc;
            }
        }

        if (opt.Reversal)
        {
            var fitWindows = opt.WalkForwardKey > 0 ? BuildWindows(opt, history, n) : new[] { ("all", 0, n) };
            var revTests = opt.WalkForwardKey > 0 ? BuildTestWindows(opt, history, n) : [];
            var revLastFit = opt.WalkForwardKey > 0 ? BuildLastFitMonthStarts(opt, history, n) : [];
            int code = ReversalSweep.Run(opt, history, prefix, pairs, slGrid, tpGrid, riskGrid,
                fitWindows, revTests, revLastFit);
            Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
            return code;
        }

        Console.WriteLine($"Building barrier tables ({distPoints.Length} distances x 2 directions, ~{tableGb:F2} GB)...");
        sw.Restart();
        BarrierTable table;
        try
        {
            table = BarrierTable.Build(history, distPoints, opt.Threads, Console.WriteLine);
        }
        catch (OutOfMemoryException)
        {
            Console.Error.WriteLine(
                $"Out of memory: the barrier table needs ~{tableGb:F2} GB. " +
                "Narrow the stop grid (--sl-step / --tp-step / --sl-max) or the history (--from / --to).");
            return 1;
        }

        Console.WriteLine($"Barrier tables ready in {sw.Elapsed.TotalSeconds:F1}s");

        if (opt.VerifySamples > 0)
        {
            Console.WriteLine();
            sw.Restart();
            bool ok = Verify.Run(history, table, prefix, distPoints, distPips, periods, riskGrid,
                opt.SpreadTenths, opt.EntryDelay, 1 - opt.MaxDdPercent / 100, opt.VerifySamples, 12345);
            Console.WriteLine($"Verification took {sw.Elapsed.TotalSeconds:F1}s");
            if (!ok) return 1;
            if (opt.VerifyOnly) return 0;
        }

        if (opt.Chain)
            return ChainSweep.Run(opt, history, table, prefix, distPips, pairs);

        if (opt.Replay is { } replayPath)
            return BandReplay.Run(opt, history, prefix, replayPath);

        if (opt.Bands)
            return RunBands(opt, history, table, prefix, distPips, slGrid, tpGrid, riskGrid, slDist, tpDist,
                total);

        Console.WriteLine();
        Console.WriteLine("Counting crossovers per MA pair...");
        sw.Restart();
        var crossCounts = CountCrossovers(prefix, n, pairs, opt.Threads);
        var sorted = crossCounts.Order().ToArray();
        Console.WriteLine($"  crossovers per pair: min {sorted[0]:N0}, " +
                          $"p10 {Percentile(sorted, 0.10):N0}, p25 {Percentile(sorted, 0.25):N0}, " +
                          $"median {Percentile(sorted, 0.50):N0}, p75 {Percentile(sorted, 0.75):N0}, " +
                          $"max {sorted[^1]:N0}  ({sw.Elapsed.TotalSeconds:F1}s)");

        if (opt.MinCrossovers > 0)
        {
            var kept = new List<(int W1, int W2)>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
                if (crossCounts[i] >= opt.MinCrossovers)
                    kept.Add(pairs[i]);

            Console.WriteLine($"  keeping {kept.Count:N0} of {pairs.Count:N0} pairs with at least " +
                              $"{opt.MinCrossovers:N0} crossovers ({pairs.Count - kept.Count:N0} dropped)");
            pairs = kept;
            if (pairs.Count == 0)
            {
                Console.Error.WriteLine("No MA pair passed --min-crossovers.");
                return 1;
            }

            sequences = (long)pairs.Count * slGrid.Length * tpGrid.Length;
            runs = sequences * riskGrid.Length;
            Console.WriteLine($"  combinations now {runs:N0}");
        }

        Console.WriteLine();
        Console.WriteLine("Running sweep...");
        sw.Restart();

        double keep = 1 - opt.MaxDdPercent / 100;
        int done = 0;
        long entriesTotal = 0;
        long survivors = 0;
        long blownUp = 0;
        int nextPair = -1;
        var tops = new TopList<Result>[opt.Threads];
        var workers = new Task[opt.Threads];

        for (int t = 0; t < opt.Threads; t++)
        {
            int slot = t;
            workers[slot] = Task.Run(() =>
            {
                var top = new TopList<Result>(opt.TopKeep);
                tops[slot] = top;
                var cross = new int[1 << 16];
                var firstLeg = new short[1 << 16];
                var netLeg = new short[1 << 16];
                long localEntries = 0;
                long localSurvivors = 0;
                long localBlownUp = 0;

                while (true)
                {
                    int p = Interlocked.Increment(ref nextPair);
                    if (p >= pairs.Count) break;
                    var (w1, w2) = pairs[p];
                    int c = opt.AlwaysIn
                        ? Sweep.AllBars(n, ref cross)
                        : Sweep.FindCrossovers(prefix, n, w1, w2, ref cross);

                    for (int ti = 0; ti < tpGrid.Length; ti++)
                    {
                        var upTp = table.Up[tpDist[ti]];
                        var dnTp = table.Down[tpDist[ti]];
                        int tpPips = tpGrid[ti];
                        int tpTenths = tpPips * 10;

                        for (int si = 0; si < slGrid.Length; si++)
                        {
                            var dnSl = table.Down[slDist[si]];
                            var upSl = table.Up[slDist[si]];
                            int slPips = slGrid[si];

                            var seq = Sweep.BuildSequence(cross, c, n, opt.EntryDelay, upTp, dnSl, dnTp, upSl,
                                tpTenths, slPips * 10, opt.SpreadTenths, ref firstLeg, ref netLeg);
                            localEntries += seq.Entries;
                            if (seq.Entries < opt.MinEntries) continue;

                            var bestOfSizes = default(Result);
                            bool any = false;
                            foreach (int riskBp in riskGrid)
                            {
                                double perTenth = riskBp / 10000.0 / (slPips * 10);
                                if (!Sweep.RunEquity(firstLeg, netLeg, seq.Entries, perTenth, keep,
                                        out double eq, out double dd))
                                {
                                    localBlownUp++;
                                    continue;
                                }

                                localSurvivors++;
                                if (any && eq <= bestOfSizes.Equity) continue;
                                any = true;
                                bestOfSizes = new Result(w1, w2, slPips, tpPips, riskBp, eq, dd,
                                    seq.Tenths, seq.Entries, seq.DblWin, seq.Mixed, seq.DblLoss);
                            }

                            if (any) top.Offer(bestOfSizes);
                        }
                    }

                    Interlocked.Increment(ref done);
                }

                Interlocked.Add(ref entriesTotal, localEntries);
                Interlocked.Add(ref survivors, localSurvivors);
                Interlocked.Add(ref blownUp, localBlownUp);
            });
        }

        var all = Task.WhenAll(workers);
        while (!all.Wait(2000))
        {
            int d = Volatile.Read(ref done);
            double share = (double)d / pairs.Count;
            double eta = share > 0.001 ? sw.Elapsed.TotalSeconds * (1 - share) / share : 0;
            Console.WriteLine($"  {d:N0}/{pairs.Count:N0} pairs ({share:P1})  " +
                              $"elapsed {sw.Elapsed.TotalSeconds:F0}s  eta {eta:F0}s");
        }

        if (all.IsFaulted)
        {
            Console.Error.WriteLine(all.Exception?.GetBaseException().ToString());
            return 1;
        }

        sw.Stop();
        Console.WriteLine($"Sweep done in {sw.Elapsed.TotalSeconds:F1}s " +
                          $"({runs / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1000:N0}k combinations/s, " +
                          $"{entriesTotal:N0} simulated entries)");
        Console.WriteLine($"Survived the {opt.MaxDdPercent:F0}% drawdown limit: {survivors:N0} of {runs:N0} " +
                          $"({(double)survivors / Math.Max(1, runs):P1}), {blownUp:N0} dropped");

        var best = tops.Where(x => x is not null).SelectMany(x => x.Items)
            .OrderByDescending(r => r.Equity)
            .Take(opt.TopKeep)
            .ToList();

        if (best.Count == 0)
        {
            Console.Error.WriteLine("No combination survived the drawdown limit.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"==================== TOP {Math.Min(opt.TopPrint, best.Count)} ====================");
        Console.WriteLine("  #      A1      A2    SL    TP   risk      deposit        final       return   maxDD  entries        pips   win%");
        for (int i = 0; i < Math.Min(opt.TopPrint, best.Count); i++)
        {
            var r = best[i];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{i + 1,3}  {r.W1,6}  {r.W2,6}  {r.SlPips,4}  {r.TpPips,4}  {r.RiskPercent,4:F2}%  " +
                $"{opt.Deposit,11:N0}  {opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,10:N0}%  " +
                $"{r.MaxDdPercent,5:F1}%  {r.Entries,7:N0}  {r.Pips,10:N0}  {r.WinRate,5:P1}"));
        }

        string outDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(outDir);
        string prefixName = $"{opt.Symbol.ToLowerInvariant()}-ma-cross";

        string csvPath = Path.Combine(outDir, prefixName + "-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,a1Minutes,a2Minutes,stopLossPips,takeProfitPips,riskPercent,deposit,finalEquity," +
                        "returnPercent,maxDrawdownPercent,entries,pips,pipsPerEntry,winRate," +
                        "doubleWin,mixed,doubleLoss");
            for (int i = 0; i < best.Count; i++)
            {
                var r = best[i];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.W1},{r.W2},{r.SlPips},{r.TpPips},{r.RiskPercent:F2},{opt.Deposit:F0}," +
                    $"{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2},{r.Entries}," +
                    $"{r.Pips:F1},{r.PipsPerEntry:F3},{r.WinRate:F4},{r.DblWin},{r.Mixed},{r.DblLoss}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({best.Count} rows)");

        var trades = WriteBestTrades(opt, history, table, prefix, distPips, best[0],
            Path.Combine(outDir, prefixName + "-best-trades.csv"));

        if (opt.Bench > 0) Benchmark(opt, history, table, prefix, distPips, best[0], opt.Bench);

        PrintYearBreakdown(opt, history, best[0], trades);
        if (!opt.AlwaysIn)
        {
            LegChoice.Analyze(history, prefix, opt, best[0], trades, opt.SplitYear);
            LegChoice.ThirdMa(history, prefix, opt, best[0], trades, opt.SplitYear,
                opt.BuildMa3Grid().Where(p => p < n).ToArray(),
                Path.Combine(outDir, prefixName + "-third-ma.csv"));
            EntryFilter.Analyze(history, prefix, opt, best[0], trades, opt.Ma3Filter,
                Path.Combine(outDir, prefixName + "-entry-filter.csv"));
        }

        string chartPath = Path.Combine(outDir, prefixName + "-equity.html");
        EquityChart.Write(chartPath, opt.Symbol, opt, history, best[0], trades);
        Console.WriteLine($"Equity:     {chartPath}");

        Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static List<Sweep.Trade> WriteBestTrades(Options opt, History history, BarrierTable table, long[] prefix,
        int[] distPips, Result best, string path)
    {
        int n = history.Count;
        var cross = new int[1 << 16];
        int c = opt.AlwaysIn
            ? Sweep.AllBars(n, ref cross)
            : Sweep.FindCrossovers(prefix, n, best.W1, best.W2, ref cross);
        int ti = Array.IndexOf(distPips, best.TpPips);
        int si = Array.IndexOf(distPips, best.SlPips);
        double perTenth = best.RiskBp / 10000.0 / (best.SlPips * 10);
        var trades = Sweep.SimulateDetailed(cross, c, n, opt.EntryDelay, history.Avg,
            table.Up[ti], table.Down[si], table.Down[ti], table.Up[si],
            best.TpPips * 10, best.SlPips * 10, opt.SpreadTenths, perTenth, prefix, best.W1, best.W2);

        using var w = new StreamWriter(path);
        w.WriteLine("entryUtc,exitUtc,heldMinutes,entryPrice,cross,buy,sell,winner," +
                    "pips,cumulativePips,equityAfterFirstLeg,equity");
        foreach (var t in trades)
        {
            string winner = t.BuyWin == t.SellWin ? "none" : t.BuyWin ? "buy" : "sell";
            w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{history.TimeUtc(t.EntryIndex):yyyy-MM-dd HH:mm},{history.TimeUtc(t.ExitIndex):yyyy-MM-dd HH:mm}," +
                $"{t.ExitIndex - t.EntryIndex},{t.EntryPrice / 100000.0:F5},{(t.CrossUp ? "up" : "down")}," +
                $"{(t.BuyWin ? "tp" : "sl")},{(t.SellWin ? "tp" : "sl")},{winner}," +
                $"{t.Tenths / 10.0:F1},{t.CumulativeTenths / 10.0:F1}," +
                $"{opt.Deposit * t.FirstEquity:F2},{opt.Deposit * t.Equity:F2}"));
        }

        Console.WriteLine($"Best trades: {path} ({trades.Count} entries, A1={best.W1} A2={best.W2} " +
                          $"SL={best.SlPips} TP={best.TpPips} risk={best.RiskPercent:F2}%)");
        return trades;
    }

    private static int RunBands(Options opt, History history, BarrierTable table, long[] prefix,
        int[] distPips, int[] slGrid, int[] tpGrid, int[] riskGrid, int[] slDist, int[] tpDist,
        Stopwatch total)
    {
        int n = history.Count;
        var maGrid = opt.BuildBandMaGrid().Where(p => p < n).ToArray();
        var bandGrid = opt.BuildBandGrid();
        var distGrid = opt.BuildDistanceGrid();
        var labelled = BuildWindows(opt, history, n);
        var testWindows = opt.WalkForwardKey > 0 ? BuildTestWindows(opt, history, n) : [];
        var lastFitMonths = opt.WalkForwardKey > 0 ? BuildLastFitMonthStarts(opt, history, n) : [];
        if (labelled.Length == 0)
        {
            Console.Error.WriteLine("No window falls inside the loaded history.");
            return 1;
        }

        var windows = labelled.Select(y => (y.Start, y.End)).ToArray();
        double averageBars = windows.Average(w => (double)(w.End - w.Start));
        var windowMinEntries = windows
            .Select(w => Math.Max(1, (int)Math.Round(opt.MinEntries * (w.End - w.Start) / averageBars)))
            .ToArray();
        long runs = (long)maGrid.Length * bandGrid.Length * distGrid.Length * opt.Variants.Length *
                    slGrid.Length * tpGrid.Length * riskGrid.Length * windows.Length;

        Console.WriteLine();
        Console.WriteLine($"MA periods:   {maGrid.Length} values {BandSweep.Label(maGrid[0])}..{BandSweep.Label(maGrid[^1])}");
        Console.WriteLine($"Band width:   {bandGrid.Length} values {opt.BandMin}..{opt.BandMax} step {opt.BandStep} pips");
        Console.WriteLine($"Distance:     {distGrid.Length} values {opt.DistMin}..{opt.DistMax} step {opt.DistStep} pips");
        Console.WriteLine($"Variants:     {opt.Variants}");
        Console.WriteLine(BandSweep.VariantHelp);
        Console.WriteLine("Entry is a pending order sitting at the band level, filled at that exact price when the bar reaches it.");
        if (opt.MultiWindow)
        {
            Console.WriteLine($"Windows:      {labelled.Length} ({labelled[0].Label}..{labelled[^1].Label}), " +
                              "each picked on its own, each starting from a fresh deposit");
            Console.WriteLine($"Min entries:  {opt.MinEntries:N0} per full year, scaled by window length " +
                              $"({windowMinEntries.Min()}..{windowMinEntries.Max()})");
        }
        Console.WriteLine($"Combinations: {runs:N0}");

        Console.WriteLine();
        var sw = Stopwatch.StartNew();
        var blocks = BlockIndex.Build(history, n);
        Console.WriteLine($"Block index ready in {sw.Elapsed.TotalSeconds:F1}s ({blocks.Blocks:N0} blocks)");
        if (opt.VerifySamples > 0 && !BandVerify.Run(history, blocks, prefix, opt, distPips, opt.VerifySamples))
            return 1;

        Console.WriteLine();
        Console.WriteLine("Running band sweep...");
        sw.Restart();
        var perWindow = BandSweep.Run(history, blocks, prefix, opt, maGrid, bandGrid, distGrid,
            slGrid, tpGrid, riskGrid, distPips, windows, windowMinEntries,
            out long entriesTotal, out long survived, out long blown);
        sw.Stop();

        Console.WriteLine($"Sweep done in {sw.Elapsed.TotalSeconds:F1}s, {entriesTotal:N0} simulated entries");
        Console.WriteLine($"Survived the {opt.MaxDdPercent:F0}% drawdown limit: {survived:N0} of {runs:N0} " +
                          $"({(double)survived / Math.Max(1, runs):P1}), {blown:N0} dropped");

        string reportDir = opt.OutDir ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reports"));
        Directory.CreateDirectory(reportDir);

        if (opt.WalkForwardKey > 0)
        {
            int code = WalkForward.Run(opt, history, blocks, n, distPips, labelled, testWindows,
                lastFitMonths, perWindow, reportDir);
            Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
            return code;
        }

        if (opt.MultiWindow)
            return WriteWindows(opt, history, labelled, perWindow, reportDir, total);

        var best = perWindow[0];
        if (best.Count == 0)
        {
            Console.Error.WriteLine("No combination survived the drawdown limit.");
            return 1;
        }

        BandSweep.PrintTop(opt, best, opt.TopPrint, $"TOP {Math.Min(opt.TopPrint, best.Count)}");
        foreach (char v in "ABCD")
        {
            var ofVariant = best.Where(r => r.Variant == v).ToList();
            if (ofVariant.Count > 0) BandSweep.PrintTop(opt, ofVariant, 3, $"BEST OF VARIANT {v}");
            else Console.WriteLine($"\nVariant {v}: nothing in the kept top {best.Count}.");
        }

        string outDir = reportDir;
        string prefixName = $"{opt.Symbol.ToLowerInvariant()}-bands";

        string csvPath = Path.Combine(outDir, prefixName + "-top.csv");
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("rank,variant,maMinutes,bandPips,distancePips,stopLossPips,takeProfitPips,riskPercent," +
                        "deposit,finalEquity,returnPercent,maxDrawdownPercent,entries,pips,winRate," +
                        "wins,mirrorWins,bothLose");
            for (int i = 0; i < best.Count; i++)
            {
                var r = best[i];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{i + 1},{r.Variant},{r.Period},{r.BandPips},{r.DistancePips},{r.SlPips},{r.TpPips}," +
                    $"{r.RiskPercent:F3},{opt.Deposit:F0},{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1}," +
                    $"{r.MaxDdPercent:F2},{r.Entries},{r.Pips:F1},{r.WinRate:F4},{r.Wins},{r.MirrorWins}," +
                    $"{r.Entries - r.Wins - r.MirrorWins}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Top CSV:    {csvPath} ({best.Count} rows)");

        var top = best[0];
        var trades = BandSweep.Detailed(prefix, history, blocks, n, top, opt.PipPoints, distPips,
            opt.SpreadTenths, top.RiskBp / 10000.0 / (top.SlPips * 10));

        string period = opt.BreakdownByMonth ? "MONTH" : "YEAR";
        Console.WriteLine();
        Console.WriteLine($"============ BY {period}: variant {top.Variant}, MA {BandSweep.Label(top.Period)}, " +
                          $"band {top.BandPips}, distance {top.DistancePips}, SL={top.SlPips} TP={top.TpPips} " +
                          $"risk={top.RiskPercent:F2}% ============");
        Console.WriteLine($"{period.ToLowerInvariant(),-7}       start          end     return   maxDD     pips  entries   win%");
        foreach (var r in YearBreakdown.Build(history, opt.Deposit, trades, opt.BreakdownByMonth))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Label,-7}  {r.Start,11:N0}  {r.End,11:N0}  {r.ReturnPercent,8:N1}%  {r.MaxDd,5:F1}%  " +
                $"{r.Pips,7:N0}  {r.Entries,7:N0}  {r.WinRate,5:P1}"));

        if (opt.DealsFile is { } dealsValue)
        {
            string dealsPath = DealsExport.Resolve(opt, dealsValue);
            int written = DealsExport.Write(dealsPath, opt, history, top, trades);
            Console.WriteLine($"Deals file: {dealsPath} ({written:N0} trades, symbol {opt.Symbol})");
        }

        if (opt.Mirror)
        {
            var mirrorRows = MirrorAccount.Build(history, blocks, n, opt, top, trades,
                out int unclosed, out int maxConcurrent, out int longest);
            MirrorAccount.Print(opt, top, mirrorRows, unclosed, maxConcurrent, longest);
        }

        Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static List<(int Key, int Index, DateTime Time)> MonthStarts(History history, int n)
    {
        var starts = new List<(int Key, int Index, DateTime Time)>();
        int previous = -1;
        for (int i = 0; i < n; i++)
        {
            var t = history.TimeUtc(i);
            int key = t.Year * 12 + t.Month;
            if (key == previous) continue;
            previous = key;
            starts.Add((key, i, t));
        }

        return starts;
    }

    private static (int Start, int End)[] BuildTestWindows(Options opt, History history, int n)
    {
        var starts = MonthStarts(history, n);
        var tests = new List<(int, int)>();
        for (int i = opt.WalkForwardMonths; i < starts.Count; i++)
        {
            if (starts[i].Key < opt.WalkForwardKey) continue;
            tests.Add((starts[i].Index, i + 1 < starts.Count ? starts[i + 1].Index : n));
        }

        return tests.ToArray();
    }

    private static int[] BuildLastFitMonthStarts(Options opt, History history, int n)
    {
        var starts = MonthStarts(history, n);
        var list = new List<int>();
        for (int i = opt.WalkForwardMonths; i < starts.Count; i++)
        {
            if (starts[i].Key < opt.WalkForwardKey) continue;
            list.Add(starts[i - 1].Index);
        }

        return list.ToArray();
    }

    private static (string Label, int Start, int End)[] BuildWindows(Options opt, History history, int n)
    {
        if (!opt.MultiWindow) return new[] { ("all", 0, n) };

        var starts = MonthStarts(history, n);

        if (opt.WalkForwardKey > 0)
        {
            var fits = new List<(string, int, int)>();
            for (int i = opt.WalkForwardMonths; i < starts.Count; i++)
            {
                if (starts[i].Key < opt.WalkForwardKey) continue;
                fits.Add(($"{starts[i].Time:yyyy-MM}", starts[i - opt.WalkForwardMonths].Index, starts[i].Index));
            }

            return fits.ToArray();
        }

        if (opt.RollingStartKey > 0)
            return starts.Where(s => s.Key >= opt.RollingStartKey)
                .Select(s => ($"{s.Time:yyyy-MM}", s.Index, n))
                .ToArray();

        if (opt.PerMonth)
            return starts
                .Select((s, i) => ($"{s.Time:yyyy-MM}", s.Index, i + 1 < starts.Count ? starts[i + 1].Index : n))
                .ToArray();

        var years = starts.Where(s => s.Index == 0 || s.Time.Month == 1).ToList();
        return years
            .Select((s, i) => ($"{s.Time.Year}", s.Index, i + 1 < years.Count ? years[i + 1].Index : n))
            .ToArray();
    }

    private static int WriteWindows(Options opt, History history, (string Label, int Start, int End)[] windows,
        List<BandResult>[] perWindow, string outDir, Stopwatch total)
    {
        Console.WriteLine();
        Console.WriteLine("==================== BEST COMBINATION OF EVERY WINDOW ====================");
        Console.WriteLine("window     from          to         var   MA period  band  dist    SL    TP   risk        start          end     return   maxDD  entries   win%    b/e%");

        double chained = 1;
        int found = 0;
        double sumReturn = 0;
        for (int w = 0; w < windows.Length; w++)
        {
            var (label, start, end) = windows[w];
            string range = $"{history.TimeUtc(start):yyyy-MM-dd}  {history.TimeUtc(end - 1):yyyy-MM-dd}";
            var rows = perWindow[w];
            if (rows.Count == 0)
            {
                Console.WriteLine($"{label,-7}  {range}  nothing passed the filters");
                continue;
            }

            var r = rows[0];
            double breakEven = 100.0 * (r.SlPips * 10 + opt.SpreadTenths) /
                               (r.SlPips * 10 + opt.SpreadTenths + r.TpPips * 10 - opt.SpreadTenths);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{label,-7}  {range}    {r.Variant}  {BandSweep.Label(r.Period),10}  {r.BandPips,4}  " +
                $"{r.DistancePips,4}  {r.SlPips,4}  {r.TpPips,4}  {r.RiskPercent,4:F2}%  {opt.Deposit,11:N0}  " +
                $"{opt.Deposit * r.Equity,11:N0}  {r.ReturnPercent,9:N1}%  {r.MaxDdPercent,5:F1}%  " +
                $"{r.Entries,7:N0}  {r.WinRate,5:P1}  {breakEven,5:F1}%"));
            chained *= r.Equity;
            sumReturn += r.ReturnPercent;
            found++;
        }

        Console.WriteLine();
        Console.WriteLine($"Every window starts again from {opt.Deposit:N0} and is optimised on its own data only.");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Average window: {sumReturn / Math.Max(1, found):N1}%."));
        if (opt.RollingStartKey <= 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Chaining all {found} winners: {opt.Deposit * chained:N0}. That number is not reachable, it needs the next winner known in advance."));

        string prefixName = $"{opt.Symbol.ToLowerInvariant()}-bands";
        string suffix = opt.RollingStartKey > 0 ? "-rolling-start.csv" : opt.PerMonth ? "-per-month.csv" : "-per-year.csv";
        string csvPath = Path.Combine(outDir, prefixName + suffix);
        int keptRows = opt.TopKeep;
        using (var w = new StreamWriter(csvPath))
        {
            w.WriteLine("window,fromUtc,toUtc,rank,variant,maMinutes,bandPips,distancePips,stopLossPips," +
                        "takeProfitPips,riskPercent,deposit,finalEquity,returnPercent,maxDrawdownPercent," +
                        "entries,pips,winRate");
            for (int y = 0; y < windows.Length; y++)
            {
                var rows = perWindow[y];
                string from = $"{history.TimeUtc(windows[y].Start):yyyy-MM-dd}";
                string to = $"{history.TimeUtc(windows[y].End - 1):yyyy-MM-dd}";
                for (int i = 0; i < Math.Min(keptRows, rows.Count); i++)
                {
                    var r = rows[i];
                    w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{windows[y].Label},{from},{to},{i + 1},{r.Variant},{r.Period},{r.BandPips}," +
                        $"{r.DistancePips},{r.SlPips},{r.TpPips},{r.RiskPercent:F2},{opt.Deposit:F0}," +
                        $"{opt.Deposit * r.Equity:F0},{r.ReturnPercent:F1},{r.MaxDdPercent:F2},{r.Entries}," +
                        $"{r.Pips:F1},{r.WinRate:F4}"));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Window CSV: {csvPath} (top {keptRows} of every window)");
        Console.WriteLine($"Total time: {total.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static void Benchmark(Options opt, History history, BarrierTable table, long[] prefix,
        int[] distPips, Result best, int repeats)
    {
        int n = history.Count;
        var cross = new int[1 << 16];
        var firstLeg = new short[1 << 16];
        var netLeg = new short[1 << 16];
        int ti = Array.IndexOf(distPips, best.TpPips);
        int si = Array.IndexOf(distPips, best.SlPips);
        var upTp = table.Up[ti];
        var dnTp = table.Down[ti];
        var dnSl = table.Down[si];
        var upSl = table.Up[si];
        int tpTenths = best.TpPips * 10;
        int slTenths = best.SlPips * 10;
        double perTenth = best.RiskBp / 10000.0 / slTenths;
        double keep = 1 - opt.MaxDdPercent / 100;

        Console.WriteLine();
        Console.WriteLine($"===== BENCHMARK: one combination, single thread, {repeats} repeats =====");

        var sw = Stopwatch.StartNew();
        int c = 0;
        for (int r = 0; r < repeats; r++)
            c = Sweep.FindCrossovers(prefix, n, best.W1, best.W2, ref cross);
        double crossMs = sw.Elapsed.TotalMilliseconds / repeats;

        sw.Restart();
        var seq = default(Sweep.Sequence);
        for (int r = 0; r < repeats; r++)
            seq = Sweep.BuildSequence(cross, c, n, opt.EntryDelay, upTp, dnSl, dnTp, upSl,
                tpTenths, slTenths, opt.SpreadTenths, ref firstLeg, ref netLeg);
        double seqMs = sw.Elapsed.TotalMilliseconds / repeats;

        sw.Restart();
        for (int r = 0; r < repeats; r++)
            Sweep.RunEquity(firstLeg, netLeg, seq.Entries, perTenth, keep, out _, out _);
        double moneyMs = sw.Elapsed.TotalMilliseconds / repeats;

        sw.Restart();
        for (int r = 0; r < repeats; r++)
            Verify.SimulateNaive(history, cross, c, opt.EntryDelay, best.SlPips * opt.PipPoints, best.TpPips * opt.PipPoints,
                best.SlPips, best.TpPips, opt.SpreadTenths, perTenth, keep);
        double naiveMs = sw.Elapsed.TotalMilliseconds / repeats;

        Console.WriteLine($"A1={best.W1} A2={best.W2} SL={best.SlPips} TP={best.TpPips}: " +
                          $"{c:N0} crossovers, {seq.Entries:N0} entries over {n:N0} bars");
        Console.WriteLine();
        Console.WriteLine("stage                                        per run   per entry");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"find crossovers (prefix sums, whole history)  {crossMs,8:F2} ms          -"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"build trade sequence (barrier tables)         {seqMs,8:F2} ms  {seqMs * 1e6 / seq.Entries,8:F0} ns"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  same, by forward scanning (naive)           {naiveMs,8:F2} ms  {naiveMs * 1e6 / seq.Entries,8:F0} ns"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"money walk, one risk value                    {moneyMs,8:F2} ms  {moneyMs * 1e6 / seq.Entries,8:F0} ns"));
        Console.WriteLine();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Barrier tables make the sequence {naiveMs / seqMs:F1}x cheaper, and the money walk is {seqMs / moneyMs:F0}x cheaper than the sequence."));
    }

    private static void PrintYearBreakdown(Options opt, History history, Result best, List<Sweep.Trade> trades)
    {
        var rows = YearBreakdown.Build(history, opt.Deposit, trades);
        if (rows.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"============ BY YEAR: A1={best.W1} A2={best.W2} SL={best.SlPips} TP={best.TpPips} " +
                          $"risk={best.RiskPercent:F2}% ============");
        Console.WriteLine("year        start          end     return   maxDD     pips  entries   win%");
        foreach (var r in rows)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Label,-7}  {r.Start,11:N0}  {r.End,11:N0}  {r.ReturnPercent,8:N1}%  {r.MaxDd,5:F1}%  " +
                $"{r.Pips,7:N0}  {r.Entries,7:N0}  {r.WinRate,5:P1}"));
        }

        Console.WriteLine("maxDD is measured against the running high water mark, it does not reset in January");
    }

    private static int[] CountCrossovers(long[] prefix, int n, List<(int W1, int W2)> pairs, int threads)
    {
        var counts = new int[pairs.Count];
        int next = -1;
        var workers = new Task[Math.Min(threads, pairs.Count)];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = Task.Run(() =>
            {
                var buffer = new int[1 << 16];
                while (true)
                {
                    int p = Interlocked.Increment(ref next);
                    if (p >= pairs.Count) break;
                    counts[p] = Sweep.FindCrossovers(prefix, n, pairs[p].W1, pairs[p].W2, ref buffer);
                }
            });
        }

        Task.WaitAll(workers);
        return counts;
    }

    private static int Percentile(int[] sorted, double share) =>
        sorted[Math.Clamp((int)(share * (sorted.Length - 1)), 0, sorted.Length - 1)];

    private static string Preview<T>(T[] values)
    {
        if (values.Length <= 12) return string.Join(", ", values);
        var head = string.Join(", ", values.Take(8));
        var tail = string.Join(", ", values.TakeLast(3));
        return $"{head}, ..., {tail}";
    }
}


