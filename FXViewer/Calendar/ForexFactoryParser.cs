using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace FXViewer.Calendar;

public static class ForexFactoryParser
{
    public static IEnumerable<CalendarDetail> ReadCsv(TextReader reader)
    {
        bool first = true;
        foreach (var row in ReadCsvRows(reader))
        {
            if (first)
            {
                first = false;
                if (row.Length > 0 && string.Equals(row[0], "DateTime", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (row.Length < 4) continue;
            if (!TryParseUnix(row[0], out long unix)) continue;
            yield return new CalendarDetail(
                unix,
                row[1].Trim(),
                row[2].Trim(),
                row[3],
                Field(row, 4),
                Field(row, 5),
                Field(row, 6),
                Field(row, 7));
        }
    }

    public static IReadOnlyList<CalendarDetail> ReadWeeklyJson(string json)
    {
        var result = new List<CalendarDetail>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string date = Str(e, "date");
            if (!TryParseUnix(date, out long unix)) continue;
            result.Add(new CalendarDetail(
                unix,
                Str(e, "country").Trim(),
                Str(e, "impact").Trim(),
                Str(e, "title"),
                Str(e, "actual"),
                Str(e, "forecast"),
                Str(e, "previous"),
                ""));
        }
        return result;
    }

    public static IReadOnlyList<CalendarDetail> ReadWeekPage(string html)
    {
        var result = new List<CalendarDetail>();
        var days = ExtractDaysArray(html);
        if (days == null) return result;
        using var doc = JsonDocument.Parse(days);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var day in doc.RootElement.EnumerateArray())
        {
            if (!day.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in events.EnumerateArray())
            {
                if (!e.TryGetProperty("dateline", out var dateline)
                    || !dateline.TryGetInt64(out long unix)) continue;
                result.Add(new CalendarDetail(
                    unix,
                    Str(e, "currency").Trim(),
                    Str(e, "impactName").Trim(),
                    Str(e, "name"),
                    Str(e, "actual"),
                    Str(e, "forecast"),
                    Str(e, "previous"),
                    ""));
            }
        }
        return result;
    }

    public static bool LooksLikeWeekPage(string html) =>
        html.Contains("calendarComponentStates[", StringComparison.Ordinal);

    private static string? ExtractDaysArray(string html)
    {
        int anchor = html.IndexOf("calendarComponentStates[", StringComparison.Ordinal);
        if (anchor < 0) return null;
        int start = html.IndexOf("days:", anchor, StringComparison.Ordinal);
        if (start < 0) return null;
        start = html.IndexOf('[', start);
        if (start < 0) return null;
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return html.Substring(start, i - start + 1);
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string Field(string[] row, int i) => i < row.Length ? row[i] : "";

    private static bool TryParseUnix(string value, out long unix)
    {
        unix = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto))
            return false;
        unix = dto.ToUnixTimeSeconds();
        return true;
    }

    private static IEnumerable<string[]> ReadCsvRows(TextReader reader)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        bool inQuotes = false;
        bool started = false;
        int ci;
        while ((ci = reader.Read()) >= 0)
        {
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    started = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    started = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    started = false;
                    break;
                default:
                    field.Append(c);
                    started = true;
                    break;
            }
        }
        if (started || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
