using System.IO;
using System.Text.Json;

namespace FXViewer.Storage;

public sealed class DealRecord
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

    public bool IsClosed => CloseTimeUnix > 0;
    public bool IsBuy => Side.Equals("Buy", StringComparison.OrdinalIgnoreCase);
}

public sealed class DealsFileModel
{
    public long Account { get; set; }
    public long ExportedAtUnix { get; set; }
    public List<DealRecord> Deals { get; set; } = new();
}

public static class DealsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<DealRecord> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new List<DealRecord>();
        try
        {
            var model = JsonSerializer.Deserialize<DealsFileModel>(File.ReadAllText(path));
            return model?.Deals ?? new List<DealRecord>();
        }
        catch
        {
            return new List<DealRecord>();
        }
    }

    public static void Save(string path, DealsFileModel model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, Options));
        File.Move(tmp, path, true);
    }
}
