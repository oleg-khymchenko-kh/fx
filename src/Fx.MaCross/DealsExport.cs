using System.Text.Json;

namespace Fx.MaCross;

internal sealed class DealRecord
{
    public long PositionId { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public double Lots { get; set; }
    public long OpenTimeUnix { get; set; }
    public double OpenPrice { get; set; }
    public long CloseTimeUnix { get; set; }
    public double ClosePrice { get; set; }
    public double Profit { get; set; }
    public double Pips { get; set; }
}

internal sealed class DealsFileModel
{
    public long Account { get; set; }
    public long ExportedAtUnix { get; set; }
    public List<DealRecord> Deals { get; set; } = new();
}

internal static class DealsExport
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Resolve(Options opt, string value)
    {
        string path = value;
        if (path.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0)
        {
            string root = Path.GetDirectoryName(opt.DataRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? ".";
            path = Path.Combine(root, "deals", path);
        }

        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json";
    }

    public static int Write(string path, Options opt, History h, BandResult top, List<Sweep.Trade> trades)
    {
        int tpPoints = top.TpPips * opt.PipPoints;
        int slPoints = top.SlPips * opt.PipPoints;
        double riskFraction = top.RiskBp / 10000.0;
        double perTenth = riskFraction / (top.SlPips * 10);

        var model = new DealsFileModel
        {
            Account = 0,
            ExportedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        double before = 1;
        long id = 0;
        foreach (var t in trades)
        {
            bool buy = t.CrossUp;
            bool win = t.Tenths > 0;
            int exitPoints = buy
                ? win ? t.EntryPrice + tpPoints : t.EntryPrice - slPoints
                : win ? t.EntryPrice - tpPoints : t.EntryPrice + slPoints;
            double equity = opt.Deposit * before;

            model.Deals.Add(new DealRecord
            {
                PositionId = ++id,
                Symbol = opt.Symbol,
                Side = buy ? "Buy" : "Sell",
                Lots = Math.Round(equity * riskFraction / (top.SlPips * 10), 4),
                OpenTimeUnix = h.Ts[t.EntryIndex],
                OpenPrice = t.EntryPrice / 100000.0,
                CloseTimeUnix = h.Ts[t.ExitIndex],
                ClosePrice = exitPoints / 100000.0,
                Profit = Math.Round(equity * perTenth * t.Tenths, 2),
                Pips = t.Tenths / 10.0,
            });

            before = t.Equity;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, Options));
        File.Move(tmp, path, true);
        return model.Deals.Count;
    }
}
