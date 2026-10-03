using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FXViewer.Storage;

namespace Fx.TradingCentral;

public sealed class SignalInput
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Image { get; set; } = "";
    public string ReadAt { get; set; } = "";
}

public sealed record ParsedSignal(
    string Day, TradingCentralSet Set, TradingCentralLevels? Levels, string ImageUrl,
    IReadOnlyList<string> Warnings);

public static class SignalParser
{
    public const string Source = "FxPro Direct, Trading signals";
    public const string IdPrefix = "fxpro-signal:";

    private const string PivotLabel = "pivot";
    private const string PreferenceLabel = "our preference";
    private const string AlternativeLabel = "alternative scenario";
    private const string SupportsLabel = "supports and resistances";
    private const double SamePriceTolerance = 1e-9;

    private static readonly string[] Labels =
        { PivotLabel, PreferenceLabel, AlternativeLabel, "comment", SupportsLabel };

    private static readonly string[] DroppedLines = { "Trade on FxPro Web Terminal" };

    private static readonly string[] DayFormatsWithYear =
        { "MMM d, yyyy", "d MMM yyyy", "MMMM d, yyyy", "d MMMM yyyy" };

    private static readonly string[] DayFormatsWithoutYear = { "MMM d", "d MMM", "MMMM d", "d MMMM" };

    private static readonly char[] Spaces = { ' ', ' ', ' ', ' ' };

    private static readonly Regex Header = new(
        @"^[^:]{1,60}?\s+intraday\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TimeLine = new(
        @"^(?<day>.+?),\s*(?<time>\d{1,2}:\d{2})$",
        RegexOptions.CultureInvariant);

    private static readonly Regex EnglishNumber = new(
        @"(?<![\w.,])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?",
        RegexOptions.CultureInvariant);

    private static readonly Regex MarkedLevel = new(
        @"^(?<number>\d[\d\s   .,]*?)\s+(?<mark>Pivot|Last)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ParsedSignal Parse(SignalInput input, TimeZoneInfo zone)
    {
        var warnings = new List<string>();
        var readAt = ReadAtOf(input.ReadAt, warnings);
        long readUnix = new DateTimeOffset(readAt, TimeSpan.Zero).ToUnixTimeSeconds();
        var lines = input.Text.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !DroppedLines.Any(d => d.Equals(l, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        int header = lines.FindIndex(l => Header.IsMatch(l));
        if (header < 0)
        {
            warnings.Add("no \"<instrument> intraday : <title>\" line found");
            return new ParsedSignal("", new TradingCentralSet(), null, "", warnings);
        }
        var block = lines.Skip(header).ToList();
        NormalizeAppNumbers(block, warnings);
        string image = input.Image.Trim();
        long published = ChartImages.PublishedOf(image, readUnix);
        if (published == 0) published = TimeOf(lines.Take(header), readAt, zone) ?? 0;
        if (published == 0) warnings.Add("no publication time found, the read time is used");
        string id = IdPrefix + (input.Id.Trim().Length > 0
            ? input.Id.Trim()
            : (published > 0 ? published : readUnix).ToString(CultureInfo.InvariantCulture));
        var mail = MailParser.Parse(Source, readUnix, id, MailText(block));
        warnings.AddRange(mail.Warnings);
        if (mail.Set.Pairs.Count != 1)
            warnings.Add($"{mail.Set.Pairs.Count} instrument(s) parsed, 1 expected");
        var levels = mail.Set.Pairs.FirstOrDefault();
        if (levels != null)
        {
            mail.Set.Pairs.RemoveAll(p => !ReferenceEquals(p, levels));
            levels.Text = ReadableText(block);
            levels.PublishedAtUnix = published;
            string view = ChartImages.ViewOf(image);
            levels.View = view.Length > 0 || published == 0
                ? view
                : $"{levels.Pair}@{published.ToString(CultureInfo.InvariantCulture)}";
        }
        return new ParsedSignal(mail.Day, mail.Set, levels, image, warnings);
    }

    private static void NormalizeAppNumbers(List<string> block, List<string> warnings)
    {
        int label = block.FindIndex(l => IsLabel(l, PivotLabel));
        if (label < 0 || label + 1 >= block.Count) return;
        string raw = block[label + 1];
        var english = SentenceNumbers(block);
        var style = StyleOf(raw, english);
        if (style == null)
        {
            warnings.Add($"cannot read the pivot \"{raw}\"");
            return;
        }
        block[label + 1] = Invariant(raw, style.Value)!;
        for (int i = 0; i < block.Count; i++)
        {
            var match = MarkedLevel.Match(block[i]);
            if (!match.Success) continue;
            string? number = Invariant(match.Groups["number"].Value, style.Value);
            if (number != null) block[i] = number + " " + match.Groups["mark"].Value;
        }
    }

    private static (char Decimal, char Group)? StyleOf(string raw, List<double> english)
    {
        var styles = new List<(char Decimal, char Group)> { ('.', ','), (',', '.'), (',', ' '), ('.', ' ') };
        var readable = styles
            .Select(s => (Style: s, Value: Parse(raw, s)))
            .Where(x => x.Value != null)
            .ToList();
        foreach (var (style, value) in readable)
            if (english.Any(e => Math.Abs(e - value!.Value) < SamePriceTolerance))
                return style;
        var fallback = readable.FirstOrDefault(x => x.Style.Decimal == DecimalOf(raw));
        return fallback.Value != null ? fallback.Style : null;
    }

    private static char DecimalOf(string raw)
    {
        int dot = raw.LastIndexOf('.');
        int comma = raw.LastIndexOf(',');
        if (dot < 0 && comma < 0) return '.';
        int last = Math.Max(dot, comma);
        int digitsAfter = raw.Length - last - 1;
        return digitsAfter == 3 && (dot < 0 || comma < 0) ? (raw[last] == '.' ? ',' : '.') : raw[last];
    }

    private static double? Parse(string raw, (char Decimal, char Group) style)
    {
        string text = raw.Trim();
        if (text.Length == 0) return null;
        var clean = new StringBuilder();
        bool seenDecimal = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                clean.Append(c);
            }
            else if (c == style.Decimal && !seenDecimal)
            {
                clean.Append('.');
                seenDecimal = true;
            }
            else if (c == style.Group || (style.Group == ' ' && Spaces.Contains(c)))
            {
                if (seenDecimal) return null;
            }
            else
            {
                return null;
            }
        }
        return double.TryParse(clean.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private static string? Invariant(string raw, (char Decimal, char Group) style)
    {
        var value = Parse(raw, style);
        if (value == null) return null;
        int decimals = 0;
        int point = raw.LastIndexOf(style.Decimal);
        if (point >= 0) decimals = raw.Length - point - 1;
        return value.Value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    private static List<double> SentenceNumbers(List<string> block)
    {
        var numbers = new List<double>();
        foreach (var label in new[] { PreferenceLabel, AlternativeLabel })
        {
            int at = block.FindIndex(l => IsLabel(l, label));
            if (at < 0 || at + 1 >= block.Count) continue;
            foreach (Match m in EnglishNumber.Matches(block[at + 1]))
                if (double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value))
                    numbers.Add(value);
        }
        if (block.Count > 1)
            foreach (Match m in EnglishNumber.Matches(block[1]))
                if (double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double value))
                    numbers.Add(value);
        return numbers;
    }

    private static DateTime ReadAtOf(string text, List<string> warnings)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var readAt))
            return readAt.UtcDateTime;
        if (text.Trim().Length > 0) warnings.Add($"bad readAt \"{text}\", the current time is used");
        return DateTime.UtcNow;
    }

    private static long? TimeOf(IEnumerable<string> lines, DateTime readAt, TimeZoneInfo zone)
    {
        var readLocal = TimeZoneInfo.ConvertTimeFromUtc(readAt, zone);
        foreach (var line in lines)
        {
            var match = TimeLine.Match(line);
            if (!match.Success) continue;
            if (!TimeSpan.TryParseExact(match.Groups["time"].Value, @"h\:mm", CultureInfo.InvariantCulture, out var time))
                continue;
            var date = DateOf(match.Groups["day"].Value.Trim(), readLocal.Date);
            if (date == null) continue;
            var local = DateTime.SpecifyKind(date.Value + time, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (utc > readAt.AddMinutes(10)) continue;
            return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
        }
        return null;
    }

    private static DateTime? DateOf(string day, DateTime today)
    {
        if (day.Equals("today", StringComparison.OrdinalIgnoreCase)) return today;
        if (day.Equals("yesterday", StringComparison.OrdinalIgnoreCase)) return today.AddDays(-1);
        if (DateTime.TryParseExact(day, DayFormatsWithYear, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var full))
            return full.Date;
        if (!DateTime.TryParseExact(day, DayFormatsWithoutYear, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            return null;
        var guess = new DateTime(today.Year, date.Month, date.Day);
        return guess > today ? guess.AddYears(-1) : guess;
    }

    private static string MailText(List<string> block)
    {
        var text = new StringBuilder();
        foreach (var line in block)
        {
            if (IsLabel(line, SupportsLabel)) text.Append('\n');
            text.Append(line).Append('\n');
        }
        return text.ToString();
    }

    private static string ReadableText(List<string> block)
    {
        var text = new StringBuilder();
        for (int i = 0; i < block.Count; i++)
        {
            string line = block[i];
            bool label = Labels.Any(l => IsLabel(line, l));
            if (i > 0) text.Append(label || i == 1 ? "\n\n" : "\n");
            text.Append(label ? line.TrimEnd(':').TrimEnd() + ":" : line);
        }
        return text.ToString();
    }

    private static bool IsLabel(string line, string label) =>
        string.Equals(line.Trim().TrimEnd(':').TrimEnd(), label, StringComparison.OrdinalIgnoreCase);
}
