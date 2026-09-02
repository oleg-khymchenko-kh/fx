using FXViewer.Chart;
using FXViewer.Compute;
using FXViewer.Storage;

namespace FXViewer;

public sealed class Note
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long StartUnix { get; set; }
    public long EndUnix { get; set; }
    public ChartViewState? State { get; set; }
    public List<ShiftPlacement> Shifts { get; set; } = new();
    public Dictionary<string, double[][][]> Drawings { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public PivotPoint[][]? LinesOf(string symbol)
    {
        var key = IndicatorSymbol.NameKey(symbol);
        foreach (var (name, raw) in Drawings)
            if (IndicatorSymbol.NameKey(name) == key) return DrawingStore.FromRaw(raw);
        return null;
    }

    public void SetLines(string symbol, PivotPoint[][] lines)
    {
        var key = IndicatorSymbol.NameKey(symbol);
        foreach (var name in Drawings.Keys)
            if (IndicatorSymbol.NameKey(name) == key)
            {
                Drawings[name] = DrawingStore.ToRaw(lines);
                return;
            }
        Drawings[symbol] = DrawingStore.ToRaw(lines);
    }

    public void RenameSymbol(string oldName, string newName)
    {
        var key = IndicatorSymbol.NameKey(oldName);
        foreach (var name in Drawings.Keys.ToList())
            if (IndicatorSymbol.NameKey(name) == key)
            {
                var raw = Drawings[name];
                Drawings.Remove(name);
                Drawings[newName] = raw;
                return;
            }
    }

    public void RemoveSymbol(string symbol)
    {
        var key = IndicatorSymbol.NameKey(symbol);
        foreach (var name in Drawings.Keys.ToList())
            if (IndicatorSymbol.NameKey(name) == key) Drawings.Remove(name);
    }
}
