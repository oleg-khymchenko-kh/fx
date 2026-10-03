using FXViewer.Chart;

namespace FXViewer;

public sealed class Note
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long StartUnix { get; set; }
    public long EndUnix { get; set; }
    public ChartViewState? State { get; set; }
    public List<ShiftPlacement> Shifts { get; set; } = new();

    public static string NewId() => Guid.NewGuid().ToString("N");
}
