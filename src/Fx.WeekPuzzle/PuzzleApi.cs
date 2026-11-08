using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fx.WeekPuzzle;

internal sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal sealed record ApiReply(int Status, string ContentType, byte[] Body);

internal sealed class PuzzleApi : IDisposable
{
    private const double PriceScale = WeekCards.PipPoints * 10000.0;
    private const double DefaultImageScale = 0.25;
    private const int DefaultImageColumns = 6;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private const string Help = """
        Day cards API. Card id = trading day yyyy-MM-dd (day = previous 21:00 .. 21:00 UTC, 22:00 in US winter).
        Bodies are JSON. A POST needs a body, send {} when there is nothing to say.
        Every change switches the window to the Days tab and is saved to day-puzzle.json.
        Non-ASCII names: send the body from a UTF-8 file (curl --data-binary @file.json) and percent-encode them in URLs.

        GET  /api/state                     tab, groups with card ids, loose card ids, mirrored card ids
        GET  /api/cards                     card list: id, day, group, mirrored, open/high/low/close, rangePips, bodyPips, highTime, lowTime,
                                            x, y, width, height (prices are always the real ones, also on a mirrored card)
             ?detail=1                      + line (drawing points [minute from card start, price]) and hours ([hh:mm, o, h, l, c])
             ?group=NAME | ?loose=1 | ?ids=a,b   only these cards
        GET  /api/cards/{id}                one card with detail
        GET  /api/groups                    groups: name, color, frame [x, y, width, height], sized, cards
        Cards lie where they are put. A group frame is drawn around the cards of the group. A sized frame (resized by hand
        or by /api/move with width/height) keeps its size and only grows when cards stick out of it.
        PUT  /api/groups                    {"groups":[{"name":"A","cards":["2026-01-05",...]},...]} replace all groups, lay out the whole table, fit view
        POST /api/group                     {"name":"A","cards":[...],"replace":false} create group or add cards (cards leave their old group);
                                            changed groups are packed at their place, a new group goes below everything
        POST /api/ungroup                   {"cards":[...]}  cards go below everything, their groups are packed
        POST /api/rename                    {"name":"A","to":"B"}
        POST /api/delete                    {"name":"A"}  cards become loose and stay where they are
        POST /api/clear                     {}  remove all groups, cards stay
        POST /api/order                     {"groups":["B","A"]}  block order for arrange
        POST /api/arrange                   {} lay out the whole table (groups as blocks, loose cards below, frame sizes reset),
                                            or {"group":"A"} pack one group (a sized frame: rows as wide as the frame)
        POST /api/move                      {"cards":[{"id":"2026-01-05","x":0,"y":0}]} or {"group":"A","x":0,"y":0} (frame top left,
                                            cards move with it); add "width"/"height" to size the frame, "fit":true to drop the size
        POST /api/mirror                    {"cards":[...]} or {"group":"A"}, "mirrored":true (default) or false  flip cards upside down
        POST /api/highlight                 {"cards":[...],"fit":true}  blink cards (and zoom to them)
        POST /api/fit                       {} all, {"group":"A"} or {"cards":[...]}
        POST /api/reload                    reload cards from the data (new days)
        POST /api/tab                       {"tab":"days"} or {"tab":"weeks"}
        POST /api/weekfill                  {"show":true,"group":"A"} (both optional)  pale green background and a red dot on the week
                                            cards over the days of one day group (default: the first group); shows the Weeks tab
        GET  /api/weeks.png                 PNG of the Weeks tab as it is on screen
        GET  /api/image?group=A|ids=a,b|loose=1[&cols=6][&scale=0.25]   PNG of cards (scale 0.25 = 240 px per card)
        GET  /api/view.png                  PNG of the Days tab as it is on screen
        """;

    private readonly HttpListener _listener = new();
    private readonly MainWindow _window;

    public PuzzleApi(MainWindow window, int port)
    {
        _window = window;
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
    }

    public string Url { get; }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public void Dispose() => _listener.Close();

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => Serve(context));
        }
    }

    private async Task Serve(HttpListenerContext context)
    {
        var request = context.Request;
        ApiReply reply;
        try
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            string path = request.Url!.AbsolutePath.TrimEnd('/');
            var query = request.QueryString;
            reply = await _window.Dispatcher.InvokeAsync(() => Handle(request.HttpMethod, path, query, body));
        }
        catch (Exception ex)
        {
            reply = Error(500, ex.Message);
        }
        try
        {
            var response = context.Response;
            response.StatusCode = reply.Status;
            response.ContentType = reply.ContentType;
            response.ContentLength64 = reply.Body.Length;
            await response.OutputStream.WriteAsync(reply.Body);
            response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
        }
    }

    private ApiReply Handle(string method, string path, NameValueCollection query, string body)
    {
        try
        {
            return Route(method, path, query, body.Trim().Length > 0 ? JsonNode.Parse(body) : null);
        }
        catch (ApiException ex)
        {
            return Error(ex.Status, ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return Error(400, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(500, ex.Message);
        }
    }

    private DayBoard Board
    {
        get
        {
            _window.Days.EnsureLoaded();
            return _window.Days.Board;
        }
    }

    private ApiReply Route(string method, string path, NameValueCollection query, JsonNode? body)
    {
        var days = _window.Days;
        switch (method, path)
        {
            case ("GET", ""):
            case ("GET", "/api"):
                return new ApiReply(200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Help));
            case ("GET", "/api/state"):
                return Ok(State());
            case ("GET", "/api/cards"):
                return Ok(CardsOf(query).Select(c => Card(c, Flag(query["detail"]))).ToList());
            case ("GET", "/api/groups"):
                return Ok(Groups());
            case ("PUT", "/api/groups"):
                return Change(board => board.ReplaceAll(GroupList(board, body)), Moves.All);
            case ("POST", "/api/group"):
                return Change(board => board.Group(Text(body, "name"), CardList(board, body), Flag(body?["replace"])), Moves.Tidy);
            case ("POST", "/api/ungroup"):
                return Change(board => board.Ungroup(CardList(board, body)), Moves.Tidy);
            case ("POST", "/api/rename"):
                return Change(board => board.Rename(GroupOf(board, Text(body, "name")), Text(body, "to")), Moves.None);
            case ("POST", "/api/delete"):
                return Change(board => board.Delete(GroupOf(board, Text(body, "name"))), Moves.None);
            case ("POST", "/api/clear"):
                return Change(board => board.Clear(), Moves.None);
            case ("POST", "/api/order"):
                return Change(board => board.Order(Texts(body, "groups")), Moves.None);
            case ("POST", "/api/arrange"):
                if (body?["group"] is null) return Change(_ => { }, Moves.All);
                return Change(board =>
                {
                    var group = GroupOf(board, Text(body, "group"));
                    var frame = board.FrameOf(group) ?? throw new ApiException(400, $"group {group.Name} has no cards here");
                    board.ArrangeGroup(group, frame.X, frame.Y);
                }, Moves.None);
            case ("POST", "/api/move"):
                return Change(board => MoveCards(board, body), Moves.None);
            case ("POST", "/api/mirror"):
                return Change(board => board.Mirror(
                    body?["group"] is not null ? GroupOf(board, Text(body, "group")).Cards.ToList() : CardList(board, body),
                    body?["mirrored"] is null || Flag(body["mirrored"])), Moves.None);
            case ("POST", "/api/reload"):
                _window.ShowTab(true);
                days.Reload();
                return Ok(State());
            case ("POST", "/api/fit"):
                return Fit(body);
            case ("POST", "/api/highlight"):
            {
                var cards = CardList(Board, body);
                _window.ShowTab(true);
                if (Flag(body?["fit"])) days.View.FitTo(DayView.RectOf(cards));
                days.View.Highlight(cards);
                return Ok(new { highlighted = cards.Count });
            }
            case ("POST", "/api/tab"):
            {
                string tab = Text(body, "tab");
                if (tab != DayStore.DaysTab && tab != DayStore.WeeksTab) throw new ApiException(400, "tab must be days or weeks");
                _window.ShowTab(tab == DayStore.DaysTab);
                return Ok(new { tab });
            }
            case ("GET", "/api/image"):
            {
                var cards = CardsOf(query);
                int columns = query["cols"] is { } cols ? int.Parse(cols, Inv) : Math.Min(cards.Count, DefaultImageColumns);
                double scale = query["scale"] is { } s ? double.Parse(s, Inv) : DefaultImageScale;
                if (scale <= 0 || scale > 4) throw new ApiException(400, "scale must be in (0, 4]");
                return new ApiReply(200, "image/png", days.View.RenderCardsPng(cards, columns, scale));
            }
            case ("GET", "/api/view.png"):
                days.EnsureLoaded();
                _window.ShowTab(true);
                return new ApiReply(200, "image/png", days.View.RenderViewPng());
            case ("POST", "/api/weekfill"):
            {
                string? group = body?["group"] is null ? null : GroupOf(Board, Text(body, "group")).Name;
                _window.ShowTab(false);
                _window.Weeks.SetDayFill(body?["show"] is null ? null : Flag(body["show"]), group);
                return Ok(_window.Weeks.DayFillInfo());
            }
            case ("GET", "/api/weeks.png"):
                _window.ShowTab(false);
                return new ApiReply(200, "image/png", _window.Weeks.View.RenderViewPng());
        }
        const string cardPrefix = "/api/cards/";
        if (method == "GET" && path.StartsWith(cardPrefix, StringComparison.Ordinal))
            return Ok(Card(CardOf(Board, Uri.UnescapeDataString(path[cardPrefix.Length..])), true));
        throw new ApiException(404, $"no route {method} {path}, GET /api lists the routes");
    }

    private enum Moves { None, Tidy, All }

    private ApiReply Change(Action<DayBoard> action, Moves moves)
    {
        var board = Board;
        var before = board.Snapshot();
        action(board);
        if (moves == Moves.Tidy) board.Tidy(before);
        if (moves == Moves.All) board.Arrange();
        _window.ShowTab(true);
        _window.Days.View.Refresh();
        if (moves == Moves.All) _window.Days.View.Fit();
        return Ok(State());
    }

    private static void MoveCards(DayBoard board, JsonNode? body)
    {
        if (body?["group"] is not null)
        {
            var group = GroupOf(board, Text(body, "group"));
            var frame = board.FrameOf(group) ?? throw new ApiException(400, $"group {group.Name} has no cards here");
            double x = Optional(body, "x") ?? frame.X;
            double y = Optional(body, "y") ?? frame.Y;
            board.MoveGroup(group, x - frame.X, y - frame.Y);
            if (Flag(body["fit"])) board.FitFrame(group);
            double? width = Optional(body, "width"), height = Optional(body, "height");
            if (width is not null || height is not null)
                board.SetFrame(group, new WorldRect(x, y, width ?? frame.Width, height ?? frame.Height));
            return;
        }
        var items = body?["cards"] as JsonArray ?? throw new ApiException(400, "body must be {\"cards\":[{\"id\":...,\"x\":...,\"y\":...}]} or {\"group\":...,\"x\":...,\"y\":...}");
        var moves = items.Select(n => (Card: CardOf(board, Text(n, "id")), X: Number(n, "x"), Y: Number(n, "y"))).ToList();
        foreach (var (card, x, y) in moves)
        {
            card.X = x;
            card.Y = y;
        }
    }

    private static double Number(JsonNode? body, string key)
    {
        double value = body?[key]?.GetValue<double>() ?? throw new ApiException(400, $"missing \"{key}\"");
        if (!double.IsFinite(value)) throw new ApiException(400, $"\"{key}\" is not a number");
        return value;
    }

    private static double? Optional(JsonNode? body, string key) => body?[key] is null ? null : Number(body, key);

    private ApiReply Fit(JsonNode? body)
    {
        var board = Board;
        var view = _window.Days.View;
        _window.ShowTab(true);
        if (body?["group"] is not null)
        {
            var group = GroupOf(board, Text(body, "group"));
            view.FitTo(board.FrameOf(group) ?? throw new ApiException(400, $"group {group.Name} has no cards here"));
        }
        else if (body?["cards"] is not null)
        {
            view.FitTo(DayView.RectOf(CardList(board, body)));
        }
        else
        {
            view.Fit();
        }
        return Ok(new { zoom = view.Zoom });
    }

    private object State()
    {
        var board = Board;
        return new
        {
            tab = _window.DaysShown ? DayStore.DaysTab : DayStore.WeeksTab,
            cards = board.Cards.Count,
            first = board.Cards.FirstOrDefault()?.Id,
            last = board.Cards.LastOrDefault()?.Id,
            groups = Groups(),
            loose = board.Loose.Select(c => c.Id).ToList(),
            mirrored = board.Cards.Where(c => c.Mirrored).Select(c => c.Id).ToList(),
            weekFill = _window.Weeks.DayFillInfo(),
        };
    }

    private object Groups()
    {
        var board = Board;
        return board.Groups.Select(g => new
        {
            name = g.Name,
            color = g.Color,
            count = g.Cards.Count,
            frame = board.FrameOf(g) is { } f ? new[] { Round(f.X), Round(f.Y), Round(f.Width), Round(f.Height) } : null,
            sized = g.Frame is not null,
            cards = g.Cards.Select(c => c.Id).ToList(),
            absent = g.Absent,
        }).ToList();
    }

    private static double Round(double value) => Math.Round(value, 1);

    private List<DayCard> CardsOf(NameValueCollection query)
    {
        var board = Board;
        if (query["group"] is { } name) return GroupOf(board, name).Cards.ToList();
        if (Flag(query["loose"])) return board.Loose.ToList();
        if (query["ids"] is { } ids)
            return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => CardOf(board, id)).ToList();
        return board.Cards.ToList();
    }

    private static Dictionary<string, object?> Card(DayCard card, bool detail)
    {
        var s = card.Shape;
        var result = new Dictionary<string, object?>
        {
            ["id"] = s.Id,
            ["day"] = s.Date.ToString("ddd", Inv),
            ["group"] = card.Group?.Name,
            ["mirrored"] = card.Mirrored,
            ["start"] = Iso(s.Start),
            ["end"] = Iso(s.End),
            ["open"] = Price(s.Candle.Open),
            ["high"] = Price(s.Candle.High),
            ["low"] = Price(s.Candle.Low),
            ["close"] = Price(s.Candle.Close),
            ["rangePips"] = Pips(s.Candle.Range),
            ["bodyPips"] = Pips(s.Candle.Body),
            ["highTime"] = Clock(s.HighUnix),
            ["lowTime"] = Clock(s.LowUnix),
            ["x"] = Round(card.X),
            ["y"] = Round(card.Y),
            ["width"] = Round(s.Width),
            ["height"] = Round(s.Height),
        };
        if (detail)
        {
            result["line"] = s.RawLines
                .Select(line => line.Select(p => new[] { Math.Round((p.Unix - s.Start) / 60.0, 1), Price(p.Value) }).ToList())
                .ToList();
            result["hours"] = s.Hours
                .Select(h => new object[] { Clock(h.Unix), Price(h.Open), Price(h.High), Price(h.Low), Price(h.Close) })
                .ToList();
        }
        return result;
    }

    private static List<(string Name, IReadOnlyList<DayCard> Cards)> GroupList(DayBoard board, JsonNode? body)
    {
        var array = body as JsonArray ?? body?["groups"] as JsonArray
            ?? throw new ApiException(400, "body must be {\"groups\":[{\"name\":...,\"cards\":[...]}]}");
        var result = new List<(string, IReadOnlyList<DayCard>)>();
        var seen = new Dictionary<DayCard, string>();
        foreach (var node in array)
        {
            string name = Text(node, "name");
            if (result.Any(r => string.Equals(r.Item1, name, StringComparison.OrdinalIgnoreCase)))
                throw new ApiException(400, $"group {name} is listed twice");
            var cards = CardList(board, node);
            foreach (var card in cards)
                if (!seen.TryAdd(card, name)) throw new ApiException(400, $"card {card.Id} is in {seen[card]} and in {name}");
            result.Add((name, cards));
        }
        return result;
    }

    private static List<DayCard> CardList(DayBoard board, JsonNode? body)
    {
        var ids = Texts(body, "cards");
        var unknown = ids.Where(id => board.Find(id) is null).ToList();
        if (unknown.Count > 0) throw new ApiException(400, $"unknown cards: {string.Join(", ", unknown)}");
        return ids.Select(id => board.Find(id)!).Distinct().ToList();
    }

    private static DayCard CardOf(DayBoard board, string id) =>
        board.Find(id) ?? throw new ApiException(404, $"no card {id}");

    private static DayGroup GroupOf(DayBoard board, string name) =>
        board.FindGroup(name) ?? throw new ApiException(404, $"no group {name}");

    private static string Text(JsonNode? body, string key) =>
        body?[key]?.GetValue<string>() ?? throw new ApiException(400, $"missing \"{key}\"");

    private static List<string> Texts(JsonNode? body, string key) =>
        (body?[key] as JsonArray ?? throw new ApiException(400, $"missing \"{key}\" array"))
        .Select(n => n?.GetValue<string>() ?? throw new ApiException(400, $"null in \"{key}\""))
        .ToList();

    private static bool Flag(JsonNode? node) => node is not null && node.GetValue<bool>();

    private static bool Flag(string? value) => value is "1" or "true" or "yes";

    private static double Price(double points) => Math.Round(points / PriceScale, 5);

    private static double Pips(double points) => Math.Round(points / WeekCards.PipPoints, 1);

    private static string Clock(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("HH:mm", Inv);

    private static string Iso(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", Inv);

    private static ApiReply Ok(object value) =>
        new(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, Json));

    private static ApiReply Error(int status, string message) =>
        new(status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { error = message }, Json));
}
