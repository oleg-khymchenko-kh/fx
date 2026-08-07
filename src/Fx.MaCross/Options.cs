using System.Globalization;

namespace Fx.MaCross;

internal sealed class Options
{
    public string DataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
    public string Symbol = "EURUSD";
    public int PipPoints;
    public int FromYear = int.MinValue;
    public int ToYear = int.MaxValue;

    public int MinPeriod = 30;
    public int MinPeriod2;
    public int MaxPeriod = 14400;
    public int WithMaMin;
    public int WithMaMax;

    public int SlMin = 20;
    public int SlMax = 100;
    public int SlStep = 5;
    public int TpMin = 20;
    public int TpMax = 100;
    public int TpStep = 5;

    public double Deposit = 100000;
    public double MaxDdPercent = 25;
    public int RiskMinBp = 5;
    public int RiskMaxBp = 50;

    public int SpreadTenths;
    public int MinCrossovers = 2000;
    public int MinEntries = 1000;
    public bool PerYear;
    public bool PerMonth;
    public int RollingStartKey = -1;
    public int WalkForwardKey = -1;
    public int WalkForwardMonths = 12;
    public bool BreakdownByMonth;
    public bool Mirror;
    public double NormDdPercent;
    public bool PrevMonthPositive;
    public string? DealsFile;
    public string Variants = "ABCD";
    public int SplitYear = 2020;
    public (int A1, int A2, int Sl, int Tp, int RiskBp)? Pin;
    public int Ma3Max = 43200;
    public int Ma3Filter = 24480;
    public bool AlwaysIn;
    public int Bench;

    public int EntryDelay = 1;
    public bool Chain;
    public bool ChainStrict;
    public bool ChainOneTrade;
    public bool Reversal;
    public bool ReversalBoth;
    public bool Comeback;
    public bool Swing;
    public bool Robot2;
    public int ProfFilter = 3000;
    public int LossFilter = -500;
    public int XMin = 10;
    public int XMax = 40;
    public int XStep = 5;
    public int YMin = 15;
    public int YMax = 40;
    public int YStep = 5;
    public int XyGap;
    public double RrMax;
    public List<(int From, int To)> SkipMonths = new();
    public bool Bands;
    public int BandMin;
    public int BandMax = 50;
    public int BandStep = 5;
    public int BandMaMin = 60;
    public int BandMaMax = 20160;
    public string? Replay;
    public int DistMin;
    public int DistMax = 20;
    public int DistStep = 1;
    public int TopPrint = 10;
    public int TopKeep = 500;
    public int Threads = Environment.ProcessorCount;
    public string? OutDir;
    public int VerifySamples;
    public bool VerifyOnly;

    public static Options? Parse(string[] args)
    {
        var o = new Options();
        bool minEntriesSet = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "-h" or "--help" or "/?")
            {
                PrintHelp();
                return null;
            }

            if (a == "--bands")
            {
                o.Bands = true;
                o.MinCrossovers = 0;
                continue;
            }

            if (a == "--always")
            {
                o.AlwaysIn = true;
                continue;
            }

            if (a == "--chain")
            {
                o.Chain = true;
                continue;
            }

            if (a == "--strict")
            {
                o.ChainStrict = true;
                continue;
            }

            if (a == "--one-trade")
            {
                o.ChainOneTrade = true;
                continue;
            }

            if (a == "--reversal")
            {
                o.Reversal = true;
                continue;
            }

            if (a == "--both")
            {
                o.ReversalBoth = true;
                continue;
            }

            if (a == "--comeback")
            {
                o.Comeback = true;
                continue;
            }

            if (a == "--swing")
            {
                o.Swing = true;
                continue;
            }

            if (a == "--robot2")
            {
                o.Robot2 = true;
                continue;
            }

            if (a == "--per-year")
            {
                o.PerYear = true;
                continue;
            }

            if (a == "--per-month")
            {
                o.PerMonth = true;
                continue;
            }

            if (a == "--mirror")
            {
                o.Mirror = true;
                continue;
            }

            if (a == "--prev-positive")
            {
                o.PrevMonthPositive = true;
                continue;
            }

            if (a == "--verify-only")
            {
                o.VerifyOnly = true;
                if (o.VerifySamples == 0) o.VerifySamples = 300;
                continue;
            }

            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Unexpected argument: {a}");
                PrintHelp();
                return null;
            }

            string key = a[2..];
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {a}");
                return null;
            }

            string value = args[++i];
            switch (key)
            {
                case "data": o.DataRoot = value; break;
                case "symbol": o.Symbol = value.ToUpperInvariant(); break;
                case "pip-points": o.PipPoints = Int(value); break;
                case "from": o.FromYear = Int(value); break;
                case "to": o.ToYear = Int(value); break;
                case "min-period": o.MinPeriod = Int(value); break;
                case "min-period2": o.MinPeriod2 = Int(value); break;
                case "max-period": o.MaxPeriod = Int(value); break;
                case "with-ma":
                {
                    var parts = value.Split('-');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out int wmLo) ||
                        !int.TryParse(parts[1], out int wmHi) || wmLo < 1 || wmHi < wmLo)
                    {
                        Console.Error.WriteLine("--with-ma needs <from>-<to> in trading minutes, e.g. 180-240");
                        return null;
                    }

                    o.WithMaMin = wmLo;
                    o.WithMaMax = wmHi;
                    break;
                }
                case "sl-min": o.SlMin = Int(value); break;
                case "sl-max": o.SlMax = Int(value); break;
                case "sl-step": o.SlStep = Int(value); break;
                case "tp-min": o.TpMin = Int(value); break;
                case "tp-max": o.TpMax = Int(value); break;
                case "tp-step": o.TpStep = Int(value); break;
                case "deposit": o.Deposit = Dbl(value); break;
                case "max-dd": o.MaxDdPercent = Dbl(value); break;
                case "risk-min": o.RiskMinBp = (int)Math.Round(Dbl(value) * 100); break;
                case "risk-max": o.RiskMaxBp = (int)Math.Round(Dbl(value) * 100); break;
                case "spread": o.SpreadTenths = (int)Math.Round(Dbl(value) * 10); break;
                case "min-crossovers": o.MinCrossovers = Int(value); break;
                case "min-entries": o.MinEntries = Int(value); minEntriesSet = true; break;
                case "split-year": o.SplitYear = Int(value); break;
                case "breakdown": o.BreakdownByMonth = value.Equals("month", StringComparison.OrdinalIgnoreCase); break;
                case "norm-dd": o.NormDdPercent = Dbl(value); break;
                case "deals": o.DealsFile = value; break;
                case "variant":
                {
                    o.Variants = value.ToUpperInvariant();
                    if (o.Variants.Length == 0 || o.Variants.Any(c => !"ABCD".Contains(c)))
                    {
                        Console.Error.WriteLine("--variant takes letters from ABCD, e.g. B or BD");
                        return null;
                    }

                    break;
                }
                case "rolling-start":
                {
                    if (!TryMonthKey(value, out int rollingKey)) return null;
                    o.RollingStartKey = rollingKey;
                    break;
                }
                case "walk-forward":
                {
                    if (!TryMonthKey(value, out int walkKey)) return null;
                    o.WalkForwardKey = walkKey;
                    break;
                }
                case "walk-months": o.WalkForwardMonths = Int(value); break;
                case "skip":
                {
                    var parts = value.Split("..");
                    if (parts.Length != 2 || !TryMonthKey(parts[0], out int skipFrom) ||
                        !TryMonthKey(parts[1], out int skipTo) || skipTo < skipFrom)
                    {
                        Console.Error.WriteLine("--skip needs YYYY-MM..YYYY-MM");
                        return null;
                    }

                    o.SkipMonths.Add((skipFrom, skipTo));
                    break;
                }
                case "ma3-max": o.Ma3Max = Int(value); break;
                case "ma3-filter": o.Ma3Filter = Int(value); break;
                case "bench": o.Bench = Int(value); break;
                case "entry-delay": o.EntryDelay = Int(value); break;
                case "band-min": o.BandMin = Int(value); break;
                case "band-max": o.BandMax = Int(value); break;
                case "band-step": o.BandStep = Int(value); break;
                case "band-ma-min": o.BandMaMin = Int(value); break;
                case "band-ma-max": o.BandMaMax = Int(value); break;
                case "replay": o.Replay = value; o.Bands = true; o.MinCrossovers = 0; break;
                case "rr-max": o.RrMax = Dbl(value); break;
                case "x-min": o.XMin = Int(value); break;
                case "x-max": o.XMax = Int(value); break;
                case "x-step": o.XStep = Int(value); break;
                case "y-min": o.YMin = Int(value); break;
                case "y-max": o.YMax = Int(value); break;
                case "y-step": o.YStep = Int(value); break;
                case "xy-gap": o.XyGap = Int(value); break;
                case "prof-filter": o.ProfFilter = Int(value); break;
                case "loss-filter": o.LossFilter = Int(value); break;
                case "dist-min": o.DistMin = Int(value); break;
                case "dist-max": o.DistMax = Int(value); break;
                case "dist-step": o.DistStep = Int(value); break;
                case "pin":
                {
                    var parts = value.Split(',');
                    if (parts.Length != 5)
                    {
                        Console.Error.WriteLine("--pin needs a1,a2,sl,tp,riskPercent");
                        return null;
                    }

                    o.Pin = (Int(parts[0]), Int(parts[1]), Int(parts[2]), Int(parts[3]),
                        (int)Math.Round(Dbl(parts[4]) * 100));
                    break;
                }
                case "top": o.TopPrint = Int(value); break;
                case "top-keep": o.TopKeep = Int(value); break;
                case "threads": o.Threads = Math.Max(1, Int(value)); break;
                case "out": o.OutDir = value; break;
                case "verify": o.VerifySamples = Int(value); break;
                default:
                    Console.Error.WriteLine($"Unknown option: {a}");
                    PrintHelp();
                    return null;
            }
        }

        if (o.PipPoints <= 0)
            o.PipPoints = o.Symbol.Contains("JPY", StringComparison.Ordinal) ? 1000 : 10;
        if (o.Chain)
        {
            o.SlMax = o.SlMin;
            o.TpMax = o.TpMin;
            o.MinCrossovers = 0;
        }

        if ((o.Reversal || o.Comeback || o.Swing) && !minEntriesSet) o.MinEntries = 300;

        o.TopKeep = Math.Max(o.TopKeep, o.TopPrint);
        if (o.MultiWindow && !minEntriesSet && !o.Robot2) o.MinEntries = 200;
        if (o.Robot2 && !minEntriesSet) o.MinEntries = 10;

        string? error = o switch
        {
            { MinPeriod: < 1 } => "--min-period must be at least 1",
            _ when o.MaxPeriod <= o.MinPeriod => "--max-period must be greater than --min-period",
            { SlMin: < 1 } or { TpMin: < 1 } => "--sl-min and --tp-min must be at least 1",
            _ when o.SlMax < o.SlMin => "--sl-max must not be below --sl-min",
            _ when o.TpMax < o.TpMin => "--tp-max must not be below --tp-min",
            { SlStep: < 1 } or { TpStep: < 1 } => "--sl-step and --tp-step must be at least 1",
            { TopPrint: < 1 } => "--top must be at least 1",
            { RiskMinBp: < 1 } => "--risk-min must be at least 0.01",
            _ when o.RiskMaxBp < o.RiskMinBp => "--risk-max must not be below --risk-min",
            { Deposit: <= 0 } => "--deposit must be positive",
            not { MaxDdPercent: > 0 and < 100 } => "--max-dd must be between 0 and 100",
            _ => null,
        };

        if (error is null) return o;
        Console.Error.WriteLine(error);
        return null;
    }

    public bool MultiWindow => PerYear || PerMonth || RollingStartKey > 0 || WalkForwardKey > 0;

    private static bool TryMonthKey(string value, out int key)
    {
        key = -1;
        var parts = value.Split('-');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int year) ||
            !int.TryParse(parts[1], out int month) || month < 1 || month > 12)
        {
            Console.Error.WriteLine("A month option needs YYYY-MM");
            return false;
        }

        key = year * 12 + month;
        return true;
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static double Dbl(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public int[] BuildStopGrid(int min, int max, int step)
    {
        var list = new List<int>();
        for (int v = min; v <= max; v += Math.Max(1, step)) list.Add(v);
        return list.ToArray();
    }

    private static readonly (int UpTo, int Step)[] RiskSteps =
    {
        (50, 5),
        (100, 10),
        (300, 25),
        (500, 50),
        (int.MaxValue, 100),
    };

    public int[] BuildRiskGrid() => Grow(RiskMinBp, RiskMaxBp, RiskSteps);

    private static readonly (int UpTo, int Step)[] Ma3Steps =
    {
        (2880, 30),
        (14400, 240),
        (int.MaxValue, 1440),
    };

    public int[] BuildMa3Grid() => Grow(30, Ma3Max, Ma3Steps);

    public int[] BuildBandMaGrid() => Grow(BandMaMin, BandMaMax, PeriodSteps);

    public int[] BuildBandGrid() => BuildStopGrid(BandMin, BandMax, BandStep);

    public int[] BuildDistanceGrid() => BuildStopGrid(DistMin, DistMax, DistStep);

    public void ApplyPin()
    {
        if (AlwaysIn)
        {
            MinCrossovers = 0;
            if (Pin is { } p) Pin = (1, 2, p.Sl, p.Tp, p.RiskBp);
        }

        if (Pin is not { } pin) return;
        SlMin = SlMax = pin.Sl;
        TpMin = TpMax = pin.Tp;
        RiskMinBp = RiskMaxBp = pin.RiskBp;
        MinCrossovers = 0;
        MinEntries = 0;
        TopPrint = 1;
        TopKeep = 1;
    }

    private static readonly (int UpTo, int Step)[] PeriodSteps =
    {
        (10, 1),
        (30, 5),
        (2880, 30),
        (int.MaxValue, 240),
    };

    public int[] BuildPeriodGrid() => Grow(MinPeriod, MaxPeriod, PeriodSteps);

    private static int[] Grow(int min, int max, (int UpTo, int Step)[] steps)
    {
        var list = new List<int> { min };
        int v = min;
        while (v < max)
        {
            int step = steps[^1].Step;
            foreach (var (upTo, s) in steps)
            {
                if (v < upTo) { step = s; break; }
            }

            v += step;
            if (v > max) v = max;
            list.Add(v);
        }

        return list.Distinct().Order().ToArray();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Fx.MaCross - brute-force search for the best moving-average crossover parameters.

            Strategy: two SMAs (A1 and A2 trading minutes back). On every crossover open a BUY and
            a SELL at the same time, same size, same StopLoss and TakeProfit. Wait until both
            trades are closed, then wait for the next crossover. No new entry while a trade is open.

            Each trade risks a fixed share of the current equity: hitting the stop loss costs
            exactly that share, hitting the take profit gains share * TP / SL. A combination is
            dropped as soon as equity falls below (100 - maxDd)% of its own high water mark.

            Usage: Fx.MaCross [options]

              --data <dir>        data root with <SYMBOL>/<year>.m1 files
              --symbol <name>     symbol to test (default EURUSD)
              --pip-points <n>    points in one pip (default 10, or 1000 for *JPY*)
              --from <year>       first year to load
              --to <year>         last year to load
              --min-period <min>  smallest MA period in trading minutes (default 30)
              --min-period2 <min> smallest allowed period for the SLOW MA of a pair (0 = off)
              --max-period <min>  largest MA period in trading minutes (default 14400 = 2 trading weeks)
              --with-ma <a>-<b>   keep only MA pairs with at least one period inside a..b trading minutes
              --sl-min/-max/-step stop loss grid in pips (default 20 100 5)
              --tp-min/-max/-step take profit grid in pips (default 20 100 5)
              --min-crossovers <n> skip an MA pair that crosses fewer than n times (default 2000)
              --risk-min <pct>    smallest risk per trade, % of equity (default 0.05)
              --risk-max <pct>    largest risk per trade, % of equity (default 0.5)
              --deposit <amount>  starting equity, only scales the report (default 100000)
              --max-dd <pct>      drop a combination at this drawdown from its peak (default 25)
              --spread <pips>     cost charged per trade, e.g. 0.6 (default 0)
              --min-entries <n>   drop results with fewer entries than this (default 1000; in a window
                                  mode the default is 200 per full year, scaled by the window length)
              --per-year          pick the best combination for every calendar year on its own.
                                  History is still loaded whole, only the entry window is cut, so the
                                  MA is already warm on 1 January and a December trade may close in January.
              --per-month         same, one window per calendar month
              --variant <letters> restrict --bands to these variants, e.g. B or BD (default ABCD)
              --breakdown month   split the winner's final table by month instead of by year
              --norm-dd <pct>     score every combination at a fixed drawdown instead of a fixed risk.
                                  Run it at --risk-max first; if the drawdown is above <pct>, shrink the
                                  trade size by binary search until the drawdown equals <pct> and take the
                                  profit at that size. Combinations already under <pct> keep --risk-max.
              --deals <file>      write the winner's trades as a FXViewer deals file the Deals indicator
                                  can open. A bare name goes to <data root>/../deals/<name>.json,
                                  a path with separators is used as given. --bands only.
              --mirror            run a second account alongside the winner: every entry is taken in the
                                  opposite direction with the same lot and the same SL and TP. The mirror
                                  has no one-trade-at-a-time limit and its balance may go negative.
              --walk-forward <YYYY-MM>  walk-forward test. For every month from that month on, optimise
                                  on the --walk-months months before it, take the top --top rows and
                                  apply each to the month the optimiser never saw. Reports money for the
                                  fitting window, for window+month, and for the month alone.
              --walk-months <n>   length of the fitting window for --walk-forward (default 12)
              --prev-positive     with --walk-forward, drop every candidate whose last month inside the
                                  fitting window lost money, then take --top of what is left. Needs
                                  --top-keep well above --top so the pool survives the filter.
              --rolling-start <YYYY-MM>  one window per month starting at that month, every window
                                  running to the end of the data. Shows when a combination started
                                  working: pin the parameters and read the row where it turns positive.
              --chain             rank MA pairs by their longest chain of winning crossover minutes.
                                  SL and TP are fixed at --sl-min and --tp-min. A minute wins when a buy
                                  or a sell entered at its average price reaches TP before SL. A single
                                  loss stays inside the chain when it has 2 straight wins before and after.
              --strict            with --chain: every loss ends the chain, no forgiveness
              --one-trade         with --chain: one decision at a time. A crossover during an open
                                  trade is skipped; the next entry is allowed after the exit bar
              --skip <A..B>       with --chain: drop entries opened inside months A..B and break
                                  chains at the gap, e.g. --skip 2024-07..2024-11 (repeatable)
              --reversal          crossover sets an anchor at that bar's average price; pending
                                  orders at anchor +- X pips enter AGAINST the move at that exact
                                  level with SL/TP from it. Orders die at the next crossover, one
                                  trade at a time. Sweeps both MA periods, X, SL and TP.
              --x-min/-max/-step  X grid in pips for --reversal and --comeback (default 10 40 5)
              --comeback          no averages. An anchor sits at a bar's average price. The price
                                  must touch anchor+X and anchor-X (either order) while staying
                                  inside anchor+-Y (X < Y), then a later bar must come back through
                                  the anchor price. There a BUY and a SELL open at once, same SL
                                  and TP each. When both close, the exit bar becomes the next
                                  anchor. A bar beyond anchor+-Y re-anchors at that bar.
              --y-min/-max/-step  Y grid in pips for --comeback and --swing (default 15 40 5)
              --xy-gap <pips>     keep only X/Y pairs with Y - X equal to this gap (0 = off)
              --robot2            the legacy 2015 Robot2 strategy from docs/legacy-robot2-strategy.md:
                                  SMA breakout armed at avg+-diffLevel1, fired at avg+-diffLevel2,
                                  limit entry orderStartDelta pips back, cancel at the TP distance,
                                  many trades at once with a 50-pip same-direction duplicate filter.
                                  Runs the fixed legacy grid (718,740 combinations), ranks by the
                                  legacy score, uses --spread per closed trade (legacy had 5.0)
              --prof-filter <p>   --robot2: keep combos with at least this many pips after spread
                                  (default 3000, the legacy job template)
                                  --sl-min/--sl-max cut the legacy lossLimit grid in --robot2,
                                  --rr-max caps profLimit at ratio x lossLimit; --tp-* are ignored
              --loss-filter <p>   --robot2: abort a combo when its worst running loss drops below
                                  this many pips (default -500, the legacy job template)
              --swing             no averages. From an anchor at a bar's average price the price
                                  must run at least X but at most Y pips to one side (largest
                                  excursion = P), then turn and touch the mirror level anchor-P
                                  (after a move up) or anchor+P (after a move down). That touch
                                  enters ONE trade at that level in the direction of the turn.
                                  A bar beyond anchor+-Y re-anchors at that bar; the exit bar of
                                  a trade becomes the next anchor.
              --both              with --reversal: open a BUY and a SELL at the touched level at
                                  once, same SL and TP each; the next entry waits for both to close
              --rr-max <ratio>    drop combinations with TP above ratio x SL (0 = off, default)
              --split-year <year> first year of the out-of-sample half in the leg-choice table (default 2020)
              --pin a1,a2,sl,tp,risk  skip the sweep and report this exact combination
              --always            ignore the averages: re-enter on the bar after both trades close
              --bands             one MA with bands at +-N pips, one trade at a time, variants A/B/C/D
              --band-min/-max/-step  band width grid in pips (default 0 50 5)
              --band-ma-min/-max  MA period range for --bands (default 60 20160 trading minutes)
              --replay <csv>      re-run the combinations from a bands top CSV on the loaded range
                                  without re-optimising anything (out-of-sample check)
              --dist-min/-max/-step  distance the price must have travelled before the entry,
                                  in pips (default 0 20 1; 0 means no such requirement)
              --entry-delay <n>   enter n bars after the signal bar (default 1; 0 is not executable
                                  because the signal is made of the same price you would trade at)
              --ma3-max <min>     largest third-MA period in trading minutes (default 43200 = 30 trading days)
              --top <n>           rows printed to console (default 10)
              --top-keep <n>      rows written to CSV (default 500)
              --threads <n>       worker threads (default: logical cores)
              --out <dir>         report directory (default <repo>/reports)
              --verify <n>        cross-check n barrier lookups and 5 full runs against a naive scan
              --verify-only       run the self-check and exit without sweeping
            """);
    }
}


