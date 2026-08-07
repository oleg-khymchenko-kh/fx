using System.Globalization;

namespace Fx.MaCross;

internal sealed class MirrorRow
{
    public string Label = "";
    public double MainMoney;
    public int MainWins;
    public int MainLosses;
    public double MainEnd;
    public long MainTenths;
    public double MirrorMoney;
    public int MirrorWins;
    public int MirrorLosses;
    public double MirrorEnd;
    public long MirrorTenths;
}

internal static class MirrorAccount
{
    public static List<MirrorRow> Build(History h, BlockIndex blocks, int n, Options opt, BandResult top,
        List<Sweep.Trade> trades, out int unclosed, out int maxConcurrent, out int longestMinutes)
    {
        int tpPoints = top.TpPips * opt.PipPoints;
        int slPoints = top.SlPips * opt.PipPoints;
        int tpTenths = top.TpPips * 10 - opt.SpreadTenths;
        int slTenths = top.SlPips * 10 + opt.SpreadTenths;
        double perTenth = top.RiskBp / 10000.0 / (top.SlPips * 10);

        var rows = new List<MirrorRow>();
        MirrorRow? current = null;
        double before = 1;
        double mirror = opt.Deposit;
        var openExits = new List<int>();
        unclosed = 0;
        maxConcurrent = 0;
        longestMinutes = 0;

        foreach (var t in trades)
        {
            string label = $"{DateTimeOffset.FromUnixTimeSeconds(h.Ts[t.EntryIndex]).UtcDateTime:yyyy-MM}";
            if (current is null || current.Label != label)
            {
                current = new MirrorRow { Label = label };
                rows.Add(current);
            }

            current.MainMoney += opt.Deposit * (t.Equity - before);
            current.MainTenths += t.Tenths;
            if (t.Tenths > 0) current.MainWins++;
            else current.MainLosses++;

            openExits.RemoveAll(x => x <= t.EntryIndex);
            long level = t.EntryPrice;
            int tpHit = t.CrossUp
                ? BandSweep.FirstLow(h.Lo, blocks, n, t.EntryIndex + 1, level - tpPoints)
                : BandSweep.FirstHigh(h.Hi, blocks, n, t.EntryIndex + 1, level + tpPoints);
            int slHit = t.CrossUp
                ? BandSweep.FirstHigh(h.Hi, blocks, n, t.EntryIndex + 1, level + slPoints)
                : BandSweep.FirstLow(h.Lo, blocks, n, t.EntryIndex + 1, level - slPoints);

            bool win = tpHit < slHit;
            int exit = win ? tpHit : slHit;
            if (exit >= n)
            {
                unclosed++;
            }
            else
            {
                int tenths = win ? tpTenths : -slTenths;
                mirror += opt.Deposit * before * perTenth * tenths;
                current.MirrorMoney += opt.Deposit * before * perTenth * tenths;
                current.MirrorTenths += tenths;
                if (win) current.MirrorWins++;
                else current.MirrorLosses++;
                openExits.Add(exit);
                if (openExits.Count > maxConcurrent) maxConcurrent = openExits.Count;
                if (exit - t.EntryIndex > longestMinutes) longestMinutes = exit - t.EntryIndex;
            }

            before = t.Equity;
            current.MainEnd = opt.Deposit * before;
            current.MirrorEnd = mirror;
        }

        return rows;
    }

    public static void Print(Options opt, BandResult top, List<MirrorRow> rows, int unclosed,
        int maxConcurrent, int longestMinutes)
    {
        Console.WriteLine();
        Console.WriteLine($"============ MIRROR ACCOUNT: every entry of variant {top.Variant} taken in the " +
                          $"opposite direction, same lot, SL={top.SlPips} TP={top.TpPips}, no one-at-a-time limit ============");
        Console.WriteLine("month      main money   main pips   main balance   mirror money  mirror pips  mirror balance   direct / mirror / both");

        double mainTotal = 0;
        double mirrorTotal = 0;
        long mainPips = 0, mirrorPips = 0;
        int mw = 0, ml = 0, rw = 0, rl = 0;
        foreach (var r in rows)
        {
            mainTotal += r.MainMoney;
            mirrorTotal += r.MirrorMoney;
            mainPips += r.MainTenths;
            mirrorPips += r.MirrorTenths;
            mw += r.MainWins;
            ml += r.MainLosses;
            rw += r.MirrorWins;
            rl += r.MirrorLosses;
            int entries = r.MainWins + r.MainLosses;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Label,-7}  {r.MainMoney,11:N0}  {r.MainTenths / 10.0,9:N0}  " +
                $"{r.MainEnd,11:N0}  {r.MirrorMoney,13:N0}  {r.MirrorTenths / 10.0,11:N0}  " +
                $"{r.MirrorEnd,13:N0}  {r.MainWins,8} / {r.MirrorWins} / {entries - r.MainWins - r.MirrorWins}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"total    {mainTotal,11:N0}  {mainPips / 10.0,9:N0}  {opt.Deposit + mainTotal,11:N0}  " +
            $"{mirrorTotal,13:N0}  {mirrorPips / 10.0,11:N0}  {opt.Deposit + mirrorTotal,13:N0}  " +
            $"{mw,8} / {rw} / {mw + ml - mw - rw}"));

        Console.WriteLine();
        Console.WriteLine($"Mirror trades open at the same time: at most {maxConcurrent}. " +
                          $"Longest mirror trade: {longestMinutes:N0} trading minutes.");
        if (unclosed > 0)
            Console.WriteLine($"{unclosed} mirror trades were still open when the data ended and are not counted.");
    }
}
