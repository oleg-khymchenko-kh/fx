using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FXViewer.Storage;

namespace Fx.TradingCentral;

public sealed record ParsedMail(
    string Day, string Session, TradingCentralSet Set, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, int> Charts, int ChartCount);

public static class MailParser
{
    public const int MiddayFromHourUtc = 9;

    private const string PreferenceLabel = "our preference";
    private const string AlternativeLabel = "alternative scenario";
    private const string CommentLabel = "comment";
    private const string PivotLabel = "pivot";
    private const double SamePriceTolerance = 1e-9;

    private static readonly Regex Header = new(
        @"^(?<name>[^:]{1,60}?)\s+intraday\s*:\s*(?<title>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Parenthesized = new(@"\s*\([^)]*\)", RegexOptions.CultureInvariant);

    private static readonly Regex PivotSentence = new(
        @"pivot(?:\s+point)?\s+is\s+at\s+(?<price>[\d.,]*\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Number = new(
        @"(?<![\w.,])(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?",
        RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static ParsedMail Parse(string subject, long receivedUnix, string messageId, string body)
    {
        var received = DateTimeOffset.FromUnixTimeSeconds(receivedUnix).UtcDateTime;
        var warnings = new List<string>();
        var set = new TradingCentralSet
        {
            MadeAtUnix = receivedUnix,
            Subject = Squeeze(subject),
            MessageId = messageId,
        };
        var lines = Clean(body).Split('\n');
        var starts = new List<int>();
        var charts = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (Header.IsMatch(lines[i].Trim())) starts.Add(i);
            if (IsChartPlaceholder(lines[i])) charts.Add(i);
        }
        var chartOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int b = 0; b < starts.Count; b++)
        {
            int from = starts[b];
            int to = b + 1 < starts.Count ? starts[b + 1] : lines.Length;
            var levels = ParseBlock(lines, from, to, warnings);
            if (levels == null) continue;
            if (set.Pairs.Any(p => p.Pair == levels.Pair))
            {
                warnings.Add($"{levels.Name}: second block for the same instrument skipped");
                continue;
            }
            set.Pairs.Add(levels);
            int chart = charts.FindIndex(line => line > from && line < to);
            if (chart >= 0) chartOf[levels.Pair] = chart;
        }
        if (starts.Count == 0) warnings.Add("no instrument blocks found");
        return new ParsedMail(
            received.ToString(TradingCentralStore.DayFormat, CultureInfo.InvariantCulture),
            SessionOf(subject, received), set, warnings, chartOf, charts.Count);
    }

    public static string SessionOf(string subject, DateTime receivedUtc)
    {
        if (subject.Contains("US Open", StringComparison.OrdinalIgnoreCase))
            return TradingCentralSessions.Midday;
        if (subject.Contains("Europe", StringComparison.OrdinalIgnoreCase))
            return TradingCentralSessions.Morning;
        return receivedUtc.Hour < MiddayFromHourUtc
            ? TradingCentralSessions.Morning
            : TradingCentralSessions.Midday;
    }

    public static string PairKey(string name)
    {
        var bare = Parenthesized.Replace(name, "");
        return new string(bare.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private static TradingCentralLevels? ParseBlock(
        string[] lines, int from, int to, List<string> warnings)
    {
        var head = Header.Match(lines[from].Trim());
        string name = Squeeze(Parenthesized.Replace(head.Groups["name"].Value, ""));
        string pair = PairKey(name);
        if (pair.Length == 0)
        {
            warnings.Add($"line {from + 1}: block without an instrument name");
            return null;
        }
        string preference = Section(lines, from + 1, to, PreferenceLabel);
        string alternative = Section(lines, from + 1, to, AlternativeLabel);
        double pivot = PivotOf(lines, from + 1, to);
        if (pivot <= 0)
        {
            warnings.Add($"{name}: no pivot found");
            return null;
        }
        var targets = PricesOf(preference, pivot);
        var alternatives = PricesOf(alternative, pivot);
        if (targets.Count == 0) warnings.Add($"{name}: no targets found");
        if (alternatives.Count == 0) warnings.Add($"{name}: no alternative targets found");
        if (targets.Count > 0 && alternatives.Count > 0
            && (targets[0] > pivot) == (alternatives[0] > pivot))
            warnings.Add($"{name}: targets and alternative targets are on the same side of the pivot");
        return new TradingCentralLevels
        {
            Pair = pair,
            Name = name,
            Title = Squeeze(head.Groups["title"].Value),
            Pivot = pivot,
            Targets = targets,
            Alternatives = alternatives,
            Comment = Section(lines, from + 1, to, CommentLabel),
            Text = BlockText(lines, from, SectionsEnd(lines, from + 1, to)),
        };
    }

    private static int SectionsEnd(string[] lines, int from, int to)
    {
        int end = from;
        for (int i = from; i < to; i++)
        {
            if (!IsAnyLabel(lines[i])) continue;
            int j = i + 1;
            while (j < to && lines[j].Trim().Length == 0) j++;
            while (j < to && lines[j].Trim().Length > 0 && !IsAnyLabel(lines[j])) j++;
            end = j;
            i = j - 1;
        }
        return end;
    }

    private static string BlockText(string[] lines, int from, int to)
    {
        var text = new StringBuilder();
        bool gap = false;
        for (int i = from; i < to; i++)
        {
            string line = Squeeze(lines[i]);
            if (line.Length == 0 || IsChartPlaceholder(line))
            {
                gap = text.Length > 0;
                continue;
            }
            if (text.Length > 0) text.Append(gap ? "\n\n" : "\n");
            text.Append(line);
            gap = false;
        }
        return text.ToString();
    }

    private static bool IsChartPlaceholder(string line)
    {
        string t = line.Trim();
        return t.Length > 2 && t[0] == '[' && t[^1] == ']'
            && string.Equals(t[1..^1].Trim(), ChartImages.ChartAlt, StringComparison.OrdinalIgnoreCase);
    }

    private static double PivotOf(string[] lines, int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            var sentence = PivotSentence.Match(lines[i]);
            if (sentence.Success && TryPrice(sentence.Groups["price"].Value, out double price))
                return price;
        }
        var labelled = PricesOf(Section(lines, from, to, PivotLabel), 0);
        return labelled.Count > 0 ? labelled[0] : 0;
    }

    private static string Section(string[] lines, int from, int to, string label)
    {
        for (int i = from; i < to; i++)
        {
            if (!IsLabel(lines[i], label)) continue;
            var text = new StringBuilder();
            for (int j = i + 1; j < to; j++)
            {
                string line = lines[j].Trim();
                if (line.Length == 0)
                {
                    if (text.Length > 0) break;
                    continue;
                }
                if (IsAnyLabel(line)) break;
                if (text.Length > 0) text.Append(' ');
                text.Append(line);
            }
            return Squeeze(text.ToString());
        }
        return "";
    }

    private static bool IsAnyLabel(string line) =>
        IsLabel(line, PreferenceLabel) || IsLabel(line, AlternativeLabel)
        || IsLabel(line, CommentLabel) || IsLabel(line, PivotLabel);

    private static bool IsLabel(string line, string label) =>
        string.Equals(line.Trim().TrimEnd(':').TrimEnd(), label, StringComparison.OrdinalIgnoreCase);

    private static List<double> PricesOf(string text, double pivot)
    {
        var prices = new List<double>();
        foreach (Match m in Number.Matches(text))
        {
            if (!TryPrice(m.Value, out double price) || price <= 0) continue;
            if (pivot > 0 && Math.Abs(price - pivot) < SamePriceTolerance) continue;
            if (prices.Any(p => Math.Abs(p - price) < SamePriceTolerance)) continue;
            prices.Add(price);
        }
        return prices;
    }

    private static bool TryPrice(string text, out double price) =>
        double.TryParse(text.Replace(",", "").TrimEnd('.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out price);

    private static string Clean(string body)
    {
        var text = new StringBuilder(body.Length);
        foreach (char c in body)
        {
            if (c == '\r') continue;
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Format) continue;
            text.Append(c == ' ' ? ' ' : c);
        }
        return text.ToString();
    }

    private static string Squeeze(string text) => Spaces.Replace(text, " ").Trim();
}
