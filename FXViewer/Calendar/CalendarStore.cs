using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FXViewer.Calendar;

public sealed class CalendarStore
{
    private const int Magic = 0x43414C34;
    private const int MagicV3 = 0x43414C33;
    private const int MagicV2 = 0x43414C32;
    private const int MagicV1 = 0x43414C31;
    private const string LegacyIndexName = "index.bin";
    private const string LegacyDetailsName = "details.jsonl";

    private readonly string _dir;

    public CalendarStore(string dir)
    {
        _dir = dir;
    }

    private string IndexPath(int year) =>
        Path.Combine(_dir, year.ToString(CultureInfo.InvariantCulture) + ".idx");

    private string DetailsPath(int year) =>
        Path.Combine(_dir, year.ToString(CultureInfo.InvariantCulture) + ".jsonl");

    private static int YearOf(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.Year;

    private sealed record Stored(long T, string C, string I, string E, string A, string F, string P, string D);

    private static Stored ToStored(CalendarDetail d) =>
        new(d.UnixSeconds, d.Currency, d.Impact, d.Event, d.Actual, d.Forecast, d.Previous, d.Detail);

    private static CalendarDetail FromStored(Stored s) =>
        new(s.T, s.C ?? "", s.I ?? "", s.E ?? "", s.A ?? "", s.F ?? "", s.P ?? "", s.D ?? "");

    public IReadOnlyList<int> ExistingYears()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<int>();
        var years = new List<int>();
        foreach (var path in Directory.GetFiles(_dir, "*.idx"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var y))
                years.Add(y);
        years.Sort();
        return years;
    }

    public CalendarEntry[] Load()
    {
        var all = new List<CalendarEntry>();
        foreach (var year in ExistingYears()) all.AddRange(LoadYear(year));
        return all.ToArray();
    }

    public int UpgradeOutdatedIndexes(Action<string>? log = null)
    {
        int rebuilt = 0;
        foreach (var year in ExistingYears())
        {
            if (IndexVersion(year) == Magic) continue;
            if (ReindexYear(year)) rebuilt++;
        }
        if (rebuilt > 0) log?.Invoke($"Calendar: reindexed {rebuilt} year files");
        return rebuilt;
    }

    private bool ReindexYear(int year)
    {
        var details = DetailsPath(year);
        if (!File.Exists(details)) return false;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(details); }
        catch { return false; }
        var index = new List<CalendarEntry>();
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            int length = i - start;
            if (length > 0 && ParseSummary(bytes.AsSpan(start, length)) is { } summary)
            {
                byte currency = Currencies.IdOf(summary.Currency);
                if (currency != 0)
                    index.Add(new CalendarEntry(
                        summary.UnixSeconds, (byte)summary.Impact, currency, start, length,
                        Currencies.IsRateDecision(summary.Currency, summary.Event)));
            }
            start = i + 1;
        }
        WriteIndex(year, index);
        return true;
    }

    private static bool IsKnownMagic(int magic) =>
        magic == Magic || magic == MagicV3 || magic == MagicV2 || magic == MagicV1;

    private int IndexVersion(int year)
    {
        try
        {
            using var stream = new FileStream(IndexPath(year), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            return reader.ReadInt32();
        }
        catch
        {
            return 0;
        }
    }

    public int TotalTracked()
    {
        int total = 0;
        foreach (var year in ExistingYears()) total += ReadCount(year);
        return total;
    }

    private int ReadCount(int year)
    {
        try
        {
            using var stream = new FileStream(IndexPath(year), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (!IsKnownMagic(reader.ReadInt32())) return 0;
            int count = reader.ReadInt32();
            return count < 0 ? 0 : count;
        }
        catch
        {
            return 0;
        }
    }

    private List<CalendarEntry> LoadYear(int year)
    {
        var entries = new List<CalendarEntry>();
        try
        {
            using var stream = new FileStream(IndexPath(year), FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            int magic = reader.ReadInt32();
            if (!IsKnownMagic(magic)) return entries;
            bool hasRateFlag = magic == Magic;
            int count = reader.ReadInt32();
            if (count < 0) return entries;
            entries.Capacity = count;
            for (int i = 0; i < count; i++)
                entries.Add(new CalendarEntry(
                    reader.ReadInt64(), reader.ReadByte(), reader.ReadByte(),
                    reader.ReadInt64(), reader.ReadInt32(),
                    hasRateFlag && reader.ReadBoolean()));
        }
        catch
        {
            entries.Clear();
        }
        return entries;
    }

    public CalendarDetail? ReadDetail(CalendarEntry entry)
    {
        if (entry.DetailLength <= 0) return null;
        var path = DetailsPath(YearOf(entry.UnixSeconds));
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(entry.DetailOffset, SeekOrigin.Begin);
            var buffer = new byte[entry.DetailLength];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) break;
                read += n;
            }
            var stored = JsonSerializer.Deserialize<Stored>(buffer.AsSpan(0, read));
            return stored == null ? null : FromStored(stored);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<CalendarDetail> ReadJsonl(string path)
    {
        if (!File.Exists(path)) yield break;
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            Stored? stored;
            try { stored = JsonSerializer.Deserialize<Stored>(line); }
            catch { continue; }
            if (stored != null) yield return FromStored(stored);
        }
    }

    public IEnumerable<CalendarDetail> LoadYearDetails(int year) => ReadJsonl(DetailsPath(year));

    public List<CalendarSummary> LoadSummaries()
    {
        var all = new List<CalendarSummary>();
        foreach (var year in ExistingYears()) ReadSummaries(DetailsPath(year), all);
        all.Sort((a, b) => a.UnixSeconds.CompareTo(b.UnixSeconds));
        return all;
    }

    private static void ReadSummaries(string path, List<CalendarSummary> into)
    {
        if (!File.Exists(path)) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch { return; }
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            int length = i - start;
            if (length > 0 && ParseSummary(bytes.AsSpan(start, length)) is { } summary)
                into.Add(summary);
            start = i + 1;
        }
    }

    private static CalendarSummary? ParseSummary(ReadOnlySpan<byte> line)
    {
        long unix = 0;
        string currency = "";
        string impact = "";
        string name = "";
        string actual = "";
        string forecast = "";
        string previous = "";
        try
        {
            var reader = new Utf8JsonReader(line);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                if (reader.ValueTextEquals("T"))
                {
                    reader.Read();
                    unix = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : 0;
                    continue;
                }
                bool isC = reader.ValueTextEquals("C");
                bool isI = !isC && reader.ValueTextEquals("I");
                bool isE = !isC && !isI && reader.ValueTextEquals("E");
                bool isA = !isC && !isI && !isE && reader.ValueTextEquals("A");
                bool isF = !isC && !isI && !isE && !isA && reader.ValueTextEquals("F");
                bool isP = !isC && !isI && !isE && !isA && !isF && reader.ValueTextEquals("P");
                if (!isC && !isI && !isE && !isA && !isF && !isP)
                {
                    reader.Read();
                    reader.Skip();
                    continue;
                }
                reader.Read();
                string value = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                if (isC) currency = value;
                else if (isI) impact = value;
                else if (isE) name = value;
                else if (isA) actual = value;
                else if (isF) forecast = value;
                else previous = value;
            }
        }
        catch
        {
            return null;
        }
        if (unix == 0) return null;
        return new CalendarSummary(
            unix, currency, Currencies.ParseImpact(impact, currency, name),
            name, actual, forecast, previous);
    }

    public int Merge(IReadOnlyList<CalendarDetail> fresh, Action<string>? log = null)
    {
        Directory.CreateDirectory(_dir);
        var byYear = new Dictionary<int, List<CalendarDetail>>();
        foreach (var e in fresh)
        {
            int year = YearOf(e.UnixSeconds);
            if (!byYear.TryGetValue(year, out var list))
            {
                list = new List<CalendarDetail>();
                byYear[year] = list;
            }
            list.Add(e);
        }
        foreach (var year in byYear.Keys.OrderBy(y => y))
        {
            var combined = new List<CalendarDetail>(LoadYearDetails(year));
            combined.AddRange(byYear[year]);
            var (t, s) = WriteYear(year, combined);
            log?.Invoke($"Calendar {year}: {s} events on disk, {t} tracked");
        }
        return TotalTracked();
    }

    private void WriteIndex(int year, List<CalendarEntry> index)
    {
        var indexTmp = IndexPath(year) + ".tmp";
        using (var stream = new FileStream(indexTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(index.Count);
            foreach (var entry in index)
            {
                writer.Write(entry.UnixSeconds);
                writer.Write(entry.Impact);
                writer.Write(entry.Currency);
                writer.Write(entry.DetailOffset);
                writer.Write(entry.DetailLength);
                writer.Write(entry.RateDecision);
            }
        }
        File.Move(indexTmp, IndexPath(year), true);
    }

    private static CalendarDetail FillGaps(CalendarDetail main, CalendarDetail spare) => main with
    {
        Impact = string.IsNullOrEmpty(main.Impact) ? spare.Impact : main.Impact,
        Actual = string.IsNullOrEmpty(main.Actual) ? spare.Actual : main.Actual,
        Forecast = string.IsNullOrEmpty(main.Forecast) ? spare.Forecast : main.Forecast,
        Previous = string.IsNullOrEmpty(main.Previous) ? spare.Previous : main.Previous,
        Detail = string.IsNullOrEmpty(main.Detail) ? spare.Detail : main.Detail,
    };

    private (int Tracked, int Stored) WriteYear(int year, List<CalendarDetail> events)
    {
        var deduped = new Dictionary<(long, string, string), CalendarDetail>();
        foreach (var e in events)
        {
            var key = (e.UnixSeconds, e.Currency, e.Event);
            if (!deduped.TryGetValue(key, out var existing))
            {
                deduped[key] = e;
                continue;
            }
            bool keepExisting = !string.IsNullOrEmpty(existing.Actual) && string.IsNullOrEmpty(e.Actual);
            deduped[key] = keepExisting ? FillGaps(existing, e) : FillGaps(e, existing);
        }
        var ordered = deduped.Values
            .OrderBy(e => e.UnixSeconds)
            .ThenBy(e => e.Currency, StringComparer.Ordinal)
            .ThenBy(e => e.Event, StringComparer.Ordinal)
            .ToList();

        var detailsTmp = DetailsPath(year) + ".tmp";
        var index = new List<CalendarEntry>(ordered.Count);

        using (var details = new FileStream(detailsTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            long offset = 0;
            var newline = new[] { (byte)'\n' };
            foreach (var e in ordered)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(ToStored(e));
                details.Write(bytes, 0, bytes.Length);
                details.Write(newline, 0, 1);
                byte currency = Currencies.IdOf(e.Currency);
                if (currency != 0)
                    index.Add(new CalendarEntry(
                        e.UnixSeconds, (byte)e.ImpactLevel, currency, offset, bytes.Length,
                        Currencies.IsRateDecision(e.Currency, e.Event)));
                offset += bytes.Length + 1;
            }
        }

        File.Move(detailsTmp, DetailsPath(year), true);
        WriteIndex(year, index);
        return (index.Count, ordered.Count);
    }

    public bool MigrateLegacy(Action<string>? log = null)
    {
        var legacyDetails = Path.Combine(_dir, LegacyDetailsName);
        if (!File.Exists(legacyDetails)) return false;
        log?.Invoke("Calendar: migrating store to per-year files...");
        Merge(ReadJsonl(legacyDetails).ToList(), log);
        TryDelete(legacyDetails);
        TryDelete(Path.Combine(_dir, LegacyIndexName));
        return true;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
