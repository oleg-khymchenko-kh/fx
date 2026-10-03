using System.Globalization;
using System.Text;
using System.Text.Json;
using Fx.TradingCentral;
using FXViewer.Storage;

const string DefaultDataRoot = @"C:\Users\Oleg\Documents\Oleg\fx\FXViewer\bin\Debug\net10.0-windows\data";
const string DefaultSender = "no-reply@fxpro.com";
const string MailFolder = "mail";
const int DefaultDays = 7;
const int ExitOk = 0;
const int ExitError = 1;
const int ExitLoginRequired = 2;
const int ExitWarnings = 3;
const string Saved = "saved";
const string Updated = "updated";
const string Unchanged = "unchanged";
const string KeptNewer = "kept the newer set";
const string KeptManual = "kept the manual set";
const int NewYorkCloseHour = 17;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return ExitError;
}

var options = ReadOptions(args.Skip(1).ToArray());
string dataRoot = options.GetValueOrDefault("data", DefaultDataRoot);
string levelsFolder = Path.Combine(dataRoot, TradingCentralStore.DefaultFolder);
string tokenPath = options.GetValueOrDefault("token", TokenStore.DefaultPath);
string clientId = options.GetValueOrDefault("client", GraphMail.DefaultClientId);

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "login":
            return await LoginAsync();
        case "fetch":
            return await FetchAsync();
        case "parse":
            return await ParseFileAsync();
        case "signal":
            return await SignalAsync();
        default:
            PrintUsage();
            return ExitError;
    }
}
catch (LoginRequiredException ex)
{
    Console.Error.WriteLine("LOGIN REQUIRED: " + ex.Message);
    Console.Error.WriteLine("Run: Fx.TradingCentral login");
    return ExitLoginRequired;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return ExitError;
}

async Task<int> LoginAsync()
{
    using var mail = new GraphMail(clientId);
    var login = await mail.StartLoginAsync(CancellationToken.None);
    Console.WriteLine("Open: " + login.VerificationUri);
    Console.WriteLine("Code: " + login.UserCode);
    Console.WriteLine($"Valid: {login.ExpiresInSeconds / 60} min");
    Console.WriteLine("Waiting for the sign-in...");
    var state = await mail.WaitLoginAsync(login, CancellationToken.None);
    TokenStore.Save(tokenPath, state);
    Console.WriteLine("Signed in. Token saved to " + tokenPath);
    return ExitOk;
}

async Task<int> FetchAsync()
{
    if (!Directory.Exists(dataRoot))
    {
        Console.Error.WriteLine("ERROR: data folder not found: " + dataRoot);
        return ExitError;
    }
    int days = options.TryGetValue("days", out var daysText)
        && int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDays)
        && parsedDays > 0
            ? parsedDays
            : DefaultDays;
    string sender = options.GetValueOrDefault("sender", DefaultSender);
    var stored = TokenStore.Load(tokenPath)
        ?? throw new LoginRequiredException("No token file at " + tokenPath);
    using var mail = new GraphMail(stored.ClientId.Length > 0 ? stored.ClientId : clientId);
    bool forceRefresh = options.ContainsKey("refresh");
    var state = await mail.EnsureAccessAsync(stored, forceRefresh, CancellationToken.None);
    if (!ReferenceEquals(state, stored))
    {
        TokenStore.Save(tokenPath, state);
        Console.WriteLine("Token refreshed");
    }
    var since = DateTime.UtcNow.Date.AddDays(-days);
    var messages = await mail.ListAsync(state, sender, since, CancellationToken.None);
    Console.WriteLine($"Mailbox: {messages.Count} message(s) from {sender} since {since:yyyy-MM-dd}");
    using var images = new ImageDownloader();
    int warnings = 0;
    int changed = 0;
    foreach (var message in messages)
    {
        string id = message.Id;
        var outcome = await StoreAsync(message.Subject, message.ReceivedUnix, id, message.Body, images,
            () => mail.GetHtmlAsync(state, id, CancellationToken.None));
        warnings += outcome.Warnings;
        if (outcome.Changed) changed++;
    }
    Console.WriteLine($"Done: {changed} set(s) written, {warnings} warning(s)");
    return warnings > 0 ? ExitWarnings : ExitOk;
}

async Task<int> ParseFileAsync()
{
    if (!options.TryGetValue("file", out var file) || !File.Exists(file))
    {
        Console.Error.WriteLine("ERROR: --file <path> is required and must exist");
        return ExitError;
    }
    if (!options.TryGetValue("received", out var receivedText)
        || !DateTimeOffset.TryParse(receivedText, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var received))
    {
        Console.Error.WriteLine("ERROR: --received <UTC time, 2026-09-28T10:02:09Z> is required");
        return ExitError;
    }
    string? htmlFile = options.GetValueOrDefault("html");
    if (htmlFile != null && !File.Exists(htmlFile))
    {
        Console.Error.WriteLine("ERROR: --html file not found: " + htmlFile);
        return ExitError;
    }
    string subject = options.GetValueOrDefault("subject", "");
    string id = options.GetValueOrDefault("id", "file:" + Path.GetFileName(file));
    using var images = new ImageDownloader();
    var outcome = await StoreAsync(subject, received.ToUnixTimeSeconds(), id, File.ReadAllText(file), images,
        () => Task.FromResult(htmlFile == null ? null : File.ReadAllText(htmlFile)));
    return outcome.Warnings > 0 ? ExitWarnings : ExitOk;
}

async Task<int> SignalAsync()
{
    if (!options.TryGetValue("file", out var file) || !File.Exists(file))
    {
        Console.Error.WriteLine("ERROR: --file <signal json> is required and must exist");
        return ExitError;
    }
    var inputs = ReadSignals(File.ReadAllText(file));
    if (inputs.Count == 0)
    {
        Console.Error.WriteLine("ERROR: the signal file has no signal with text");
        return ExitError;
    }
    int warnings = 0;
    foreach (var input in inputs) warnings += await StoreSignalAsync(input);
    return warnings > 0 ? ExitWarnings : ExitOk;
}

async Task<int> StoreSignalAsync(SignalInput input)
{
    var parsed = SignalParser.Parse(input, TimeZoneInfo.Local);
    var warnings = new List<string>(parsed.Warnings);
    var levels = parsed.Levels;
    if (levels == null)
    {
        foreach (var warning in warnings) Console.WriteLine("WARN signal: " + warning);
        Console.WriteLine("No levels stored");
        return Math.Max(1, warnings.Count);
    }
    var published = DateTimeOffset.FromUnixTimeSeconds(
        levels.PublishedAtUnix > 0 ? levels.PublishedAtUnix : parsed.Set.MadeAtUnix).UtcDateTime;
    string label = $"{parsed.Day} signal {levels.Pair} {published:HH:mm} UTC";
    var days = LoadAround(parsed.Day, warnings);
    var owner = FindView(days.Values.Select(d => d.Model), levels.View);
    string result;
    bool stored = false;
    if (owner is { Session: var session } && !TradingCentralSessions.IsSignal(session))
    {
        result = $"skipped, already in the {owner.Value.Day} {session.ToLowerInvariant()} mail";
    }
    else if (owner != null)
    {
        result = $"{Unchanged}, already read on {owner.Value.Day}";
    }
    else if (FindSignal(days, parsed.Set.MessageId).Home == null
             && SameLevelsInEffect(days.Values.Select(d => d.Model), levels) is { } same)
    {
        result = $"skipped, same levels as the {same.Day} {SourceName(same.Session)}";
    }
    else
    {
        var (home, index) = FindSignal(days, parsed.Set.MessageId);
        var existing = home != null ? home.Model.Signals![index] : null;
        string baseName = $"{parsed.Day}-signal-{levels.Pair}-{published:HHmm}";
        await AttachSignalImageAsync(parsed, levels, existing, baseName, warnings);
        home ??= days[parsed.Day];
        var signals = home.Model.Signals ??= new List<TradingCentralSet>();
        if (existing != null) signals[index] = parsed.Set;
        else signals.Add(parsed.Set);
        signals.Sort((a, b) => a.MadeAtUnix.CompareTo(b.MadeAtUnix));
        bool changed = SaveChanged(days.Values);
        result = existing == null ? Saved : changed ? Updated : Unchanged;
        stored = true;
        WriteText(Path.Combine(levelsFolder, MailFolder, baseName + ".txt"), input.Text);
    }
    foreach (var warning in warnings) Console.WriteLine($"WARN {label}: {warning}");
    Console.WriteLine($"{label}  \"{levels.Title}\"  pivot {Prices(new[] { levels.Pivot })}"
        + $"  targets {Prices(levels.Targets)}  alternatives {Prices(levels.Alternatives)}"
        + (stored ? $"  {levels.Images.Count} chart(s)" : "") + $"  -> {result}");
    return warnings.Count;
}

static List<SignalInput> ReadSignals(string json)
{
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    var items = root.ValueKind == JsonValueKind.Array
        ? root.EnumerateArray().ToList()
        : new List<JsonElement> { root };
    return items
        .Where(item => item.ValueKind == JsonValueKind.Object)
        .Select(item => item.Deserialize<SignalInput>(jsonOptions))
        .Where(item => item != null && item.Text.Trim().Length > 0)
        .Select(item => item!)
        .ToList();
}

Dictionary<string, DayFile> LoadAround(string day, List<string> warnings)
{
    var date = DateTime.ParseExact(day, TradingCentralStore.DayFormat, CultureInfo.InvariantCulture);
    var days = new Dictionary<string, DayFile>(StringComparer.Ordinal);
    for (int offset = -1; offset <= 1; offset++)
    {
        string name = date.AddDays(offset).ToString(TradingCentralStore.DayFormat, CultureInfo.InvariantCulture);
        string path = TradingCentralStore.PathOf(levelsFolder, name);
        var model = TradingCentralStore.LoadFile(path);
        if (model == null && File.Exists(path))
        {
            if (offset != 0) continue;
            File.Copy(path, path + ".bad", true);
            warnings.Add($"unreadable day file kept as {Path.GetFileName(path)}.bad");
        }
        if (model == null && offset != 0) continue;
        model ??= new TradingCentralDay();
        model.Day = name;
        days[name] = new DayFile(path, model, File.Exists(path) ? TradingCentralStore.Serialize(model) : "");
    }
    return days;
}

static bool SaveChanged(IEnumerable<DayFile> days)
{
    bool changed = false;
    foreach (var day in days)
    {
        if (day.Before == TradingCentralStore.Serialize(day.Model)) continue;
        TradingCentralStore.Save(day.Path, day.Model);
        changed = true;
    }
    return changed;
}

static (string Day, string Session)? FindView(IEnumerable<TradingCentralDay> days, string view)
{
    if (view.Length == 0) return null;
    foreach (var day in days)
        foreach (var (session, set) in day.Sets())
            if (set.Pairs.Any(p => p.View == view)) return (day.Day, session);
    return null;
}

static (string Day, string Session)? SameLevelsInEffect(IEnumerable<TradingCentralDay> days, TradingCentralLevels levels)
{
    long start = levels.PublishedAtUnix;
    if (start <= 0) return null;
    (string Day, string Session, long Start, long End, TradingCentralLevels Levels)? current = null;
    foreach (var day in days)
        foreach (var (session, set) in day.Sets())
            foreach (var other in set.Pairs.Where(p => p.Pair == levels.Pair))
            {
                long otherStart = TradingCentralTimes.StartOf(set, other);
                if (otherStart > start || (current != null && otherStart <= current.Value.Start)) continue;
                long end = NewYorkCloseAfter(Math.Max(otherStart, set.MadeAtUnix));
                current = (day.Day, session, otherStart, end, other);
            }
    if (current == null || start >= current.Value.End) return null;
    var old = current.Value.Levels;
    bool same = SamePrice(old.Pivot, levels.Pivot)
        && old.Targets.Count == levels.Targets.Count && old.Targets.Zip(levels.Targets).All(p => SamePrice(p.First, p.Second))
        && old.Alternatives.Count == levels.Alternatives.Count
        && old.Alternatives.Zip(levels.Alternatives).All(p => SamePrice(p.First, p.Second));
    return same ? (current.Value.Day, current.Value.Session) : null;
}

static bool SamePrice(double a, double b) => Math.Abs(a - b) < 1e-9;

static long NewYorkCloseAfter(long unix)
{
    var zone = TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var iana) ? iana
        : TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, zone);
    var close = local.Date.AddHours(NewYorkCloseHour);
    if (close <= local) close = close.AddDays(1);
    return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(close, zone)).ToUnixTimeSeconds();
}

static string SourceName(string session) =>
    TradingCentralSessions.IsSignal(session) ? "page read" : session.ToLowerInvariant() + " mail";

static (DayFile? Home, int Index) FindSignal(Dictionary<string, DayFile> days, string messageId)
{
    foreach (var day in days.Values)
    {
        int index = day.Model.Signals?.FindIndex(s => s.MessageId == messageId) ?? -1;
        if (index >= 0) return (day, index);
    }
    return (null, -1);
}

Dictionary<string, string> SignalViewsAround(string day, TradingCentralDay current)
{
    var views = new Dictionary<string, string>(StringComparer.Ordinal);
    var around = LoadAround(day, new List<string>());
    around[day] = new DayFile("", current, "");
    foreach (var model in around.Values.Select(d => d.Model))
        foreach (var signal in model.Signals ?? new List<TradingCentralSet>())
            foreach (var levels in signal.Pairs.Where(p => p.View.Length > 0))
                views[levels.View] =
                    $"{DateTimeOffset.FromUnixTimeSeconds(signal.MadeAtUnix).UtcDateTime:yyyy-MM-dd HH:mm} UTC";
    return views;
}

async Task AttachSignalImageAsync(ParsedSignal parsed, TradingCentralLevels levels, TradingCentralSet? existing,
    string baseName, List<string> warnings)
{
    var old = existing?.Pairs.FirstOrDefault(p => p.Pair == levels.Pair);
    if (existing?.MessageId == parsed.Set.MessageId && old != null && old.Images.Count > 0
        && old.Images.All(ImageExists))
    {
        levels.Images = new List<string>(old.Images);
        return;
    }
    if (parsed.ImageUrl.Length == 0) return;
    var uri = ChartImages.AllowedUri(parsed.ImageUrl);
    if (uri == null)
    {
        warnings.Add($"{levels.Name}: chart image from an unexpected address skipped: {parsed.ImageUrl}");
        return;
    }
    using var images = new ImageDownloader();
    var (file, error) = await images.DownloadAsync(uri, Path.Combine(levelsFolder, ChartImages.Folder),
        baseName, CancellationToken.None);
    if (file == null)
    {
        warnings.Add($"{levels.Name}: chart image not saved: {error}");
        return;
    }
    levels.Images = new List<string> { ChartImages.Folder + "/" + file };
}

static string Prices(IEnumerable<double> prices) =>
    string.Join(" ", prices.Select(p => p.ToString(CultureInfo.InvariantCulture)));

async Task<(bool Changed, int Warnings)> StoreAsync(string subject, long receivedUnix, string id,
    string body, ImageDownloader images, Func<Task<string?>> html)
{
    var parsed = MailParser.Parse(subject, receivedUnix, id, body);
    var received = DateTimeOffset.FromUnixTimeSeconds(receivedUnix).UtcDateTime;
    string label = $"{parsed.Day} {parsed.Session} {received:HH:mm} UTC";
    var warnings = new List<string>(parsed.Warnings);
    if (parsed.Set.Pairs.Count == 0)
    {
        warnings.Add($"no levels parsed from \"{parsed.Set.Subject}\"");
        foreach (var warning in warnings) Console.WriteLine($"WARN {label}: {warning}");
        return (false, warnings.Count);
    }
    string path = TradingCentralStore.PathOf(levelsFolder, parsed.Day);
    var model = TradingCentralStore.LoadFile(path);
    if (model == null && File.Exists(path))
    {
        File.Copy(path, path + ".bad", true);
        warnings.Add($"unreadable day file kept as {Path.GetFileName(path)}.bad");
    }
    model ??= new TradingCentralDay();
    model.Day = parsed.Day;
    var existing = model.SetOf(parsed.Session);
    string result;
    if (existing is { Manual: true })
    {
        result = KeptManual;
    }
    else if (existing != null && existing.MessageId != parsed.Set.MessageId
             && existing.MadeAtUnix > parsed.Set.MadeAtUnix)
    {
        result = KeptNewer;
    }
    else
    {
        var signalViews = SignalViewsAround(parsed.Day, model);
        await AttachImagesAsync(parsed, existing, images, html, warnings, signalViews.ContainsKey);
        foreach (var levels in parsed.Set.Pairs.Where(p => p.View.Length > 0 && signalViews.ContainsKey(p.View)).ToList())
        {
            parsed.Set.Pairs.Remove(levels);
            Console.WriteLine($"INFO {label}: {levels.Pair} skipped, the same view was already read from "
                + $"FxPro Direct at {signalViews[levels.View]}");
        }
        string before = File.Exists(path) ? TradingCentralStore.Serialize(model) : "";
        model.Put(parsed.Session, parsed.Set);
        if (before == TradingCentralStore.Serialize(model))
        {
            result = Unchanged;
        }
        else
        {
            TradingCentralStore.Save(path, model);
            result = existing == null ? Saved : Updated;
        }
        WriteText(MailPath(parsed, ".txt"), body);
    }
    foreach (var warning in warnings) Console.WriteLine($"WARN {label}: {warning}");
    string pairs = string.Join(" ", parsed.Set.Pairs.Select(p => p.Pair));
    int charts = parsed.Set.Pairs.Count(p => p.Images.Count > 0);
    Console.WriteLine($"{label}  \"{parsed.Set.Subject}\"  {parsed.Set.Pairs.Count} instrument(s): {pairs}"
        + $"  {charts} chart(s)  -> {result}");
    return (result == Saved || result == Updated, warnings.Count);
}

async Task AttachImagesAsync(ParsedMail parsed, TradingCentralSet? existing, ImageDownloader images,
    Func<Task<string?>> html, List<string> warnings, Func<string, bool> taken)
{
    var wanted = parsed.Set.Pairs.Where(p => parsed.Charts.ContainsKey(p.Pair)).ToList();
    if (wanted.Count == 0) return;
    bool sameMail = existing != null && existing.MessageId == parsed.Set.MessageId;
    if (sameMail)
        foreach (var levels in wanted)
        {
            var old = existing!.Pairs.FirstOrDefault(p => p.Pair == levels.Pair);
            if (old == null) continue;
            levels.View = old.View;
            levels.PublishedAtUnix = old.PublishedAtUnix;
            if (old.Images.Count > 0 && old.Images.All(ImageExists))
                levels.Images = new List<string>(old.Images);
        }
    var missing = wanted.Where(p => p.View.Length == 0 || (p.Images.Count == 0 && !taken(p.View))).ToList();
    if (missing.Count == 0) return;
    string? page;
    string localCopy = MailPath(parsed, ".html");
    try
    {
        page = sameMail && File.Exists(localCopy) ? File.ReadAllText(localCopy) : await html();
    }
    catch (Exception ex) when (ex is not LoginRequiredException)
    {
        warnings.Add("cannot read the HTML of the mail, chart images skipped: " + ex.Message);
        return;
    }
    if (page == null)
    {
        Console.WriteLine($"INFO {parsed.Day} {parsed.Session}: no HTML of the mail, chart images skipped");
        return;
    }
    WriteText(localCopy, page);
    var urls = ChartImages.ChartUrls(page);
    if (urls.Count != parsed.ChartCount)
    {
        warnings.Add($"{urls.Count} chart image(s) in the HTML but {parsed.ChartCount} in the text, "
            + "chart images skipped");
        return;
    }
    string folder = Path.Combine(levelsFolder, ChartImages.Folder);
    foreach (var levels in missing)
    {
        string url = urls[parsed.Charts[levels.Pair]];
        levels.View = ChartImages.ViewOf(url);
        levels.PublishedAtUnix = ChartImages.PublishedOf(url, parsed.Set.MadeAtUnix);
        if (levels.Images.Count > 0 || taken(levels.View)) continue;
        var uri = ChartImages.AllowedUri(url);
        if (uri == null)
        {
            warnings.Add($"{levels.Name}: chart image from an unexpected address skipped: {url}");
            continue;
        }
        string baseName = $"{parsed.Day}-{parsed.Session.ToLowerInvariant()}-{levels.Pair}";
        var (file, error) = await images.DownloadAsync(uri, folder, baseName, CancellationToken.None);
        if (file == null)
        {
            warnings.Add($"{levels.Name}: chart image not saved: {error}");
            continue;
        }
        levels.Images = new List<string> { ChartImages.Folder + "/" + file };
    }
}

bool ImageExists(string relative) => File.Exists(Path.Combine(levelsFolder, relative));

string MailPath(ParsedMail parsed, string extension) =>
    Path.Combine(levelsFolder, MailFolder, $"{parsed.Day}-{parsed.Session.ToLowerInvariant()}{extension}");

static void WriteText(string path, string text)
{
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    if (File.Exists(path) && File.ReadAllText(path) == text) return;
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, text);
    File.Move(tmp, path, true);
}

static Dictionary<string, string> ReadOptions(string[] rest)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < rest.Length; i++)
    {
        if (!rest[i].StartsWith("--", StringComparison.Ordinal)) continue;
        options[rest[i][2..]] = rest[i + 1];
        i++;
    }
    return options;
}

static void PrintUsage()
{
    Console.WriteLine("Fx.TradingCentral login [--client <id>] [--token <file>]");
    Console.WriteLine("Fx.TradingCentral fetch [--days <n>] [--data <folder>] [--sender <address>] "
        + "[--refresh yes]");
    Console.WriteLine("Fx.TradingCentral parse --file <mail text> --received <UTC time> "
        + "[--subject <text>] [--id <id>] [--html <mail html>] [--data <folder>]");
    Console.WriteLine("Fx.TradingCentral signal --file <json: one signal or an array, fields id, text, image, readAt> "
        + "[--data <folder>]");
    Console.WriteLine("Exit codes: 0 ok, 1 error, 2 login required, 3 parsed with warnings");
}

sealed record DayFile(string Path, TradingCentralDay Model, string Before);
