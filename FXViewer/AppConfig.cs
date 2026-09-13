using System.IO;
using System.Text.Json;
using FXViewer.Calendar;
using FXViewer.Chart;
using FXViewer.Compute;

namespace FXViewer;

public sealed class AppConfig
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long TokenExpiresUnix { get; set; }
    public bool IsLive { get; set; }
    public ChartViewState? ChartState { get; set; }
    public List<ChartTab> Tabs { get; set; } = new();
    public int ActiveTab { get; set; }
    public List<IndicatorSymbol> Indicators { get; set; } = new();
    public List<string> SeriesOrder { get; set; } = new();
    public List<ZoomLevel> ZoomLevels { get; set; } = new();
    public bool IndicatorsInitialized { get; set; }
    public int EditHitRadiusPx { get; set; } = 3;
    public CalendarSettings Calendar { get; set; } = new();
    public Dictionary<string, int> PairColors { get; set; } = new();
    public Dictionary<string, long>? MirrorBases { get; set; }
    public bool HideWideSpread { get; set; }
    public bool ShowAsk { get; set; }
    public int CommentSpotDiameterPx { get; set; } = CommentStyle.DefaultDiameterPx;
    public int CommentSpotColorArgb { get; set; } = CommentStyle.DefaultColorArgb;
    public int CommentSpotOpacityPercent { get; set; } = CommentStyle.DefaultOpacityPercent;
    public double? GamePanelLeft { get; set; }
    public double? GamePanelTop { get; set; }
    public string SierraDataFolder { get; set; } = "";

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
            bool merged = cfg.MergeVolumeProfiles();
            if (cfg.MigrateBandModes()) merged = true;
            if (cfg.RebaseShiftAnchors()) merged = true;
            if (cfg.EnsureZoomLevels()) merged = true;
            BackupDaily();
            if (path != FilePath || merged) cfg.Save();
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
        cfg.EnsureZoomLevels();
        return cfg;
    }

    private bool EnsureZoomLevels()
    {
        int removed = ZoomLevels.RemoveAll(x => x is not { IsValid: true });
        if (ZoomLevels.Count > 0) return removed > 0;
        ZoomLevels = ZoomLevel.Defaults();
        return true;
    }

    private bool MergeVolumeProfiles()
    {
        bool changed = false;
        for (int i = Indicators.Count - 1; i >= 0; i--)
        {
            var profile = Indicators[i];
            if (!IndicatorTypes.IsLegacyVolumeProfile(profile.Type)) continue;
            var host = Indicators.FirstOrDefault(x => IndicatorTypes.IsVolume(x.Type)
                && IndicatorSymbol.NameKey(x.Source) == IndicatorSymbol.NameKey(profile.Source));
            if (host == null)
            {
                profile.Type = IndicatorTypes.Volume;
            }
            else
            {
                if (host.DensityPeriods.Count == 0)
                    host.DensityPeriods = new List<int>(profile.DensityPeriods);
                if (host.DensityUnits.Count == 0)
                    host.DensityUnits = new List<string>(profile.DensityUnits);
                host.DensitySelected = profile.DensitySelected;
                Indicators.RemoveAt(i);
            }
            changed = true;
        }
        return changed;
    }

    private bool MigrateBandModes()
    {
        bool changed = false;
        foreach (var ind in Indicators)
        {
            if (!IndicatorTypes.IsAverageBand(ind.Type) || !ind.BandMin) continue;
            ind.BandMode = BandModes.Min;
            ind.BandMin = false;
            changed = true;
        }
        return changed;
    }

    private bool RebaseShiftAnchors()
    {
        long anchor = ShiftedSymbol.DefaultAnchorUnix();
        bool changed = false;
        foreach (var ind in Indicators)
            if (ind.RebaseShiftAnchors(anchor)) changed = true;
        foreach (var tab in Tabs)
            foreach (var placement in tab.Shifts)
                if (placement.Rebase(anchor)) changed = true;
        return changed;
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

    public CommentStyle CommentSpotStyle() => CommentStyle.Of(
        CommentSpotDiameterPx, CommentSpotColorArgb, CommentSpotOpacityPercent);

    public static string BackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FXViewer", "config-backups");

    private const int BackupsToKeep = 60;

    public static string BackupDaily()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length == 0) return "";
            Directory.CreateDirectory(BackupDir);
            var target = Path.Combine(BackupDir, "config-" + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
            if (File.Exists(target)) return "";
            File.Copy(FilePath, target);
            Prune();
            return target;
        }
        catch
        {
            return "";
        }
    }

    private static void Prune()
    {
        var files = Directory.GetFiles(BackupDir, "config-*.json");
        if (files.Length <= BackupsToKeep) return;
        Array.Sort(files, StringComparer.Ordinal);
        for (int i = 0; i < files.Length - BackupsToKeep; i++)
        {
            try { File.Delete(files[i]); }
            catch { }
        }
    }

    public void Save()
    {
        using var _ = Perf.Step("config.save");
        Directory.CreateDirectory(Dir);
        BackupDaily();
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, true);
    }
}
