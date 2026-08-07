using System.Globalization;
using System.IO;

namespace FXViewer.Storage;

public readonly record struct Candle(long MinuteUnixSeconds, int Min, int Max, int Avg, bool AvgApproximated)
{
    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeSeconds(MinuteUnixSeconds).UtcDateTime;
}

public sealed class CandleDatabase : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<(string Symbol, int Year), CandleYearFile> _files = new();
    private readonly object _lock = new();
    private bool _disposed;

    public string Root => _root;

    public CandleDatabase(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    private static string Sanitize(string symbol) =>
        new string(symbol.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public string SymbolDirectory(string symbol) => Path.Combine(_root, Sanitize(symbol));

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static long YearStartUnix(int year) =>
        new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private string PathFor(string symbol, int year) =>
        Path.Combine(_root, Sanitize(symbol), year.ToString(CultureInfo.InvariantCulture) + ".m1");

    private CandleYearFile GetFile(string symbol, int year)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (Sanitize(symbol), year);
            if (!_files.TryGetValue(key, out var f))
            {
                f = new CandleYearFile(PathFor(symbol, year), Sanitize(symbol), year);
                _files[key] = f;
            }
            return f;
        }
    }

    private bool FileExists(string symbol, int year)
    {
        lock (_lock)
        {
            if (_files.ContainsKey((Sanitize(symbol), year))) return true;
        }
        return File.Exists(PathFor(symbol, year));
    }

    public void WriteMinute(string symbol, DateTime minuteUtc, int min, int max, int avg, bool avgApprox,
        bool provisional = false)
    {
        var utc = AsUtc(minuteUtc);
        long unix = ((DateTimeOffset)utc).ToUnixTimeSeconds();
        int year = utc.Year;
        int minuteOfYear = (int)((unix - YearStartUnix(year)) / 60);
        GetFile(symbol, year).Write(minuteOfYear, min, max, avg, avgApprox, provisional);
    }

    public DateTime? FirstProvisionalMinuteUtc(string symbol, DateTime fromUtc, DateTime toUtc) =>
        ScanProvisional(symbol, fromUtc, toUtc, forward: true);

    public DateTime? LastProvisionalMinuteUtc(string symbol, DateTime fromUtc, DateTime toUtc) =>
        ScanProvisional(symbol, fromUtc, toUtc, forward: false);

    private DateTime? ScanProvisional(string symbol, DateTime fromUtc, DateTime toUtc, bool forward)
    {
        var from = AsUtc(fromUtc);
        var to = AsUtc(toUtc);
        if (to < from) return null;
        long fromUnix = ((DateTimeOffset)from).ToUnixTimeSeconds();
        long toUnix = ((DateTimeOffset)to).ToUnixTimeSeconds();
        for (int i = 0; i <= to.Year - from.Year; i++)
        {
            int year = forward ? from.Year + i : to.Year - i;
            if (!FileExists(symbol, year)) continue;
            var file = GetFile(symbol, year);
            long yearStart = YearStartUnix(year);
            int fromMoy = (int)Math.Max(0, (fromUnix - yearStart) / 60);
            int toMoy = (int)Math.Min(file.MinutesInYear - 1L, (toUnix - yearStart) / 60);
            int moy = forward
                ? file.FirstProvisionalMinute(fromMoy, toMoy)
                : file.LastProvisionalMinute(fromMoy, toMoy);
            if (moy >= 0)
                return DateTimeOffset.FromUnixTimeSeconds(yearStart + (long)moy * 60).UtcDateTime;
        }
        return null;
    }

    public List<Candle> ReadRange(string symbol, DateTime fromUtc, DateTime toUtc)
    {
        var res = new List<Candle>();
        var from = AsUtc(fromUtc);
        var to = AsUtc(toUtc);
        long fromUnix = ((DateTimeOffset)from).ToUnixTimeSeconds();
        long toUnix = ((DateTimeOffset)to).ToUnixTimeSeconds();
        for (int year = from.Year; year <= to.Year; year++)
        {
            if (!FileExists(symbol, year)) continue;
            var file = GetFile(symbol, year);
            long yearStart = YearStartUnix(year);
            int fromMoy = (int)Math.Max(0, (fromUnix - yearStart) / 60);
            int toMoy = (int)Math.Min(file.MinutesInYear - 1L, (toUnix - yearStart) / 60);
            foreach (var sc in file.ReadRange(fromMoy, toMoy))
            {
                long minuteUnix = yearStart + (long)sc.MinuteOfYear * 60;
                res.Add(new Candle(minuteUnix, sc.Min, sc.Max, sc.Avg, sc.AvgApproximated));
            }
        }
        return res;
    }

    public DateTime? LastFilledMinuteUtc(string symbol)
    {
        var years = ExistingYears(symbol);
        for (int i = years.Count - 1; i >= 0; i--)
        {
            int moy = GetFile(symbol, years[i]).LastFilledMinute();
            if (moy >= 0)
                return DateTimeOffset.FromUnixTimeSeconds(YearStartUnix(years[i]) + (long)moy * 60).UtcDateTime;
        }
        return null;
    }

    public (DateTime FirstUtc, DateTime LastUtc)? FilledRangeUtc(string symbol, int year)
    {
        if (!FileExists(symbol, year)) return null;
        var file = GetFile(symbol, year);
        int first = file.FirstFilledMinute();
        if (first < 0) return null;
        int last = file.LastFilledMinute();
        long yearStart = YearStartUnix(year);
        return (
            DateTimeOffset.FromUnixTimeSeconds(yearStart + (long)first * 60).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(yearStart + (long)last * 60).UtcDateTime);
    }

    public int CountFilled(string symbol, int year) =>
        FileExists(symbol, year) ? GetFile(symbol, year).CountFilled() : 0;

    public IReadOnlyList<int> ExistingYears(string symbol)
    {
        var dir = Path.Combine(_root, Sanitize(symbol));
        if (!Directory.Exists(dir)) return Array.Empty<int>();
        var years = new List<int>();
        foreach (var path in Directory.GetFiles(dir, "*.m1"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var y))
                years.Add(y);
        }
        years.Sort();
        return years;
    }

    public void DeleteSymbol(string symbol)
    {
        var key = Sanitize(symbol);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stale = _files.Keys.Where(k => k.Symbol == key).ToList();
            foreach (var k in stale)
            {
                _files[k].Dispose();
                _files.Remove(k);
            }
        }
        var dir = Path.Combine(_root, key);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public void RenameSymbol(string oldName, string newName)
    {
        var oldKey = Sanitize(oldName);
        var newKey = Sanitize(newName);
        if (oldKey == newKey) return;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stale = _files.Keys.Where(k => k.Symbol == oldKey || k.Symbol == newKey).ToList();
            foreach (var k in stale)
            {
                _files[k].Dispose();
                _files.Remove(k);
            }
            var oldDir = Path.Combine(_root, oldKey);
            if (!Directory.Exists(oldDir)) return;
            var newDir = Path.Combine(_root, newKey);
            if (Directory.Exists(newDir)) Directory.Delete(newDir, true);
            Directory.Move(oldDir, newDir);
        }
    }

    public void FlushAll()
    {
        lock (_lock)
            foreach (var f in _files.Values) f.Flush();
    }

    public void FlushSymbol(string symbol)
    {
        var key = Sanitize(symbol);
        lock (_lock)
            foreach (var (k, f) in _files)
                if (k.Symbol == key) f.Flush();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var f in _files.Values) f.Dispose();
            _files.Clear();
        }
    }
}
