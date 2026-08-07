using System.Runtime.CompilerServices;

namespace Fx.MaCross;

internal interface IScored
{
    double Score { get; }
}

internal readonly record struct Result(
    int W1, int W2, int SlPips, int TpPips, int RiskBp, double Equity, double MaxDd,
    long Tenths, int Entries, int DblWin, int Mixed, int DblLoss) : IScored
{
    public double Score => Equity;
    public double RiskPercent => RiskBp / 100.0;
    public double ReturnPercent => (Equity - 1) * 100;
    public double MaxDdPercent => MaxDd * 100;
    public double Pips => Tenths / 10.0;
    public double PipsPerEntry => Entries == 0 ? 0 : Tenths / 10.0 / Entries;
    public double WinRate => Entries == 0 ? 0 : (DblWin * 2 + Mixed) / (2.0 * Entries);
}

internal sealed class TopList<T> where T : IScored
{
    private readonly T[] _items;
    private int _count;

    public TopList(int capacity) => _items = new T[Math.Max(1, capacity)];

    public void Offer(in T r)
    {
        if (_count == _items.Length)
        {
            if (r.Score <= _items[_count - 1].Score) return;
            _count--;
        }

        int pos = _count;
        while (pos > 0 && _items[pos - 1].Score < r.Score)
        {
            _items[pos] = _items[pos - 1];
            pos--;
        }

        _items[pos] = r;
        _count++;
    }

    public IEnumerable<T> Items => _items.Take(_count);
}

internal static class Sweep
{
    public static int AllBars(int n, ref int[] buffer)
    {
        if (buffer.Length < n) buffer = new int[n];
        for (int i = 0; i < n; i++) buffer[i] = i;
        return n;
    }

    public static int FindCrossovers(long[] prefix, int n, int w1, int w2, ref int[] buffer)
    {
        int start = w2 - 1;
        if (start >= n) return 0;

        int count = 0;
        int sign = 0;
        var buf = buffer;
        for (int i = start; i < n; i++)
        {
            long end = prefix[i + 1];
            long s1 = end - prefix[i - w1 + 1];
            long s2 = end - prefix[i - w2 + 1];
            long diff = s1 * w2 - s2 * w1;
            if (diff == 0) continue;
            int s = diff > 0 ? 1 : -1;
            if (s == sign) continue;
            if (sign != 0)
            {
                if (count == buf.Length)
                {
                    Array.Resize(ref buffer, buf.Length * 2);
                    buf = buffer;
                }

                buf[count++] = i;
            }

            sign = s;
        }

        return count;
    }

    public readonly record struct Sequence(int Entries, long Tenths, int DblWin, int Mixed, int DblLoss);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static Sequence BuildSequence(
        int[] cross, int crossCount, int n, int delay,
        int[] upTp, int[] dnSl, int[] dnTp, int[] upSl,
        int tpTenths, int slTenths, int spreadTenths,
        ref short[] firstLeg, ref short[] netLeg)
    {
        var first = firstLeg;
        var net = netLeg;
        long tenths = 0;
        int entries = 0;
        int dblWin = 0;
        int mixed = 0;
        int dblLoss = 0;
        int nextAllowed = 0;

        for (int k = 0; k < crossCount; k++)
        {
            int i = cross[k] + delay;
            if (i >= n) break;
            if (i < nextAllowed) continue;

            int a = upTp[i];
            int b = dnSl[i];
            int c = dnTp[i];
            int e = upSl[i];

            bool buyWin = a < b;
            bool sellWin = c < e;
            int buyExit = buyWin ? a : b;
            int sellExit = sellWin ? c : e;
            int exit = buyExit > sellExit ? buyExit : sellExit;
            if (exit >= n) break;

            int buyPnl = (buyWin ? tpTenths : -slTenths) - spreadTenths;
            int sellPnl = (sellWin ? tpTenths : -slTenths) - spreadTenths;
            int netPnl = buyPnl + sellPnl;
            int firstPnl = buyExit == sellExit ? netPnl : buyExit < sellExit ? buyPnl : sellPnl;

            if (entries == first.Length)
            {
                Array.Resize(ref firstLeg, first.Length * 2);
                Array.Resize(ref netLeg, net.Length * 2);
                first = firstLeg;
                net = netLeg;
            }

            first[entries] = (short)firstPnl;
            net[entries] = (short)netPnl;
            tenths += netPnl;
            entries++;

            if (buyWin && sellWin) dblWin++;
            else if (buyWin || sellWin) mixed++;
            else dblLoss++;

            nextAllowed = exit + 1;
        }

        return new Sequence(entries, tenths, dblWin, mixed, dblLoss);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool RunEquity(short[] firstLeg, short[] netLeg, int entries, double perTenth, double keep,
        out double finalEquity, out double maxDrawdown)
    {
        double equity = 1;
        double peak = 1;
        double worst = 0;

        for (int k = 0; k < entries; k++)
        {
            double mid = equity * (1 + perTenth * firstLeg[k]);
            if (mid > peak) peak = mid;
            else
            {
                double dd = 1 - mid / peak;
                if (dd > worst) worst = dd;
                if (mid < peak * keep)
                {
                    finalEquity = mid;
                    maxDrawdown = dd;
                    return false;
                }
            }

            equity *= 1 + perTenth * netLeg[k];
            if (equity > peak) peak = equity;
            else
            {
                double dd = 1 - equity / peak;
                if (dd > worst) worst = dd;
                if (equity < peak * keep)
                {
                    finalEquity = equity;
                    maxDrawdown = dd;
                    return false;
                }
            }
        }

        finalEquity = equity;
        maxDrawdown = worst;
        return true;
    }

    public readonly record struct Trade(
        int EntryIndex, int FirstExitIndex, int ExitIndex, int EntryPrice, bool CrossUp, bool BuyWin, bool SellWin,
        int FirstTenths, int Tenths, long CumulativeTenths, double FirstEquity, double Equity);

    public static bool FastAboveSlow(long[] prefix, int i, int w1, int w2)
    {
        long end = prefix[i + 1];
        return (end - prefix[i - w1 + 1]) * w2 - (end - prefix[i - w2 + 1]) * w1 > 0;
    }

    public static List<Trade> SimulateDetailed(
        int[] cross, int crossCount, int n, int delay, int[] avg,
        int[] upTp, int[] dnSl, int[] dnTp, int[] upSl,
        int tpTenths, int slTenths, int spreadTenths, double perTenth, long[] prefix, int w1, int w2)
    {
        var trades = new List<Trade>();
        long cumulative = 0;
        double equity = 1;
        int nextAllowed = 0;

        for (int k = 0; k < crossCount; k++)
        {
            int i = cross[k] + delay;
            if (i >= n) break;
            if (i < nextAllowed) continue;

            int a = upTp[i];
            int b = dnSl[i];
            int c = dnTp[i];
            int e = upSl[i];

            bool buyWin = a < b;
            bool sellWin = c < e;
            int buyExit = buyWin ? a : b;
            int sellExit = sellWin ? c : e;
            int exit = buyExit > sellExit ? buyExit : sellExit;
            if (exit >= n) break;

            int buyPnl = (buyWin ? tpTenths : -slTenths) - spreadTenths;
            int sellPnl = (sellWin ? tpTenths : -slTenths) - spreadTenths;
            int netPnl = buyPnl + sellPnl;
            int firstPnl = buyExit == sellExit ? netPnl : buyExit < sellExit ? buyPnl : sellPnl;

            cumulative += netPnl;
            double firstEquity = equity * (1 + perTenth * firstPnl);
            equity *= 1 + perTenth * netPnl;
            trades.Add(new Trade(i, buyExit < sellExit ? buyExit : sellExit, exit, avg[i],
                FastAboveSlow(prefix, i, w1, w2), buyWin, sellWin,
                firstPnl, netPnl, cumulative, firstEquity, equity));
            nextAllowed = exit + 1;
        }

        return trades;
    }
}
