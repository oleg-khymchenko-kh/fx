using System.IO;
using System.Text.Json;
using FXViewer.Calendar;
using FXViewer.Chart;

namespace FXViewer;

public sealed class AppConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public bool IsLive { get; set; }
    public ChartViewState? ChartState { get; set; }
    public List<ChartTab> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<IndicatorSymbol> Indicators { get; set; } = new();
    public bool IndicatorsInitialized { get; set; }
    public int EditHitRadiusPx { get; set; } = 3;
    public CalendarSettings Calendar { get; set; } = new();
    public Dictionary<string, long>? MirrorBases { get; set; }

    public static string Dir => AppContext.BaseDirectory;

    private static string FilePath => Path.Combine(Dir, "config.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FXViewer", "config.json");

    public static AppConfig Load()
    {
        string? path =
            File.Exists(FilePath) ? FilePath :
            File.Exists(LegacyFilePath) ? LegacyFilePath :
            null;
        if (path == null) return NewConfig();
        try
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new AppConfig();
            cfg.EnsureTabs();
            if (path != FilePath) cfg.Save();
            return cfg;
        }
        catch
        {
            return NewConfig();
        }
    }

    private static AppConfig NewConfig()
    {
        var cfg = new AppConfig();
        cfg.EnsureTabs();
        return cfg;
    }

    private void EnsureTabs()
    {
        if (Tabs.Count == 0) Tabs.Add(new ChartTab { Name = DefaultTabName, State = ChartState });
        ChartState = null;
        foreach (var tab in Tabs)
        {
            if (tab.Name.Trim().Length == 0) tab.Name = DefaultTabName;
            tab.State?.EnsureTiltedGrids();
        }
        ActiveTab = Math.Clamp(ActiveTab, 0, Tabs.Count - 1);
    }

    public const string DefaultTabName = "Chart";

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
