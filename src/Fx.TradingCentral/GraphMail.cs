using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fx.TradingCentral;

public sealed record MailMessage(string Id, string Subject, long ReceivedUnix, string Body);

public sealed record DeviceLogin(
    string DeviceCode, string UserCode, string VerificationUri, int ExpiresInSeconds,
    int IntervalSeconds);

public sealed class LoginRequiredException : Exception
{
    public LoginRequiredException(string message) : base(message)
    {
    }
}

public sealed class GraphMail : IDisposable
{
    public const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const string Scope = "https://graph.microsoft.com/Mail.Read offline_access";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private const int PageSize = 50;
    private const int MinPollSeconds = 5;
    private const int SlowDownSeconds = 5;
    private const int ExpiryMarginSeconds = 120;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly string _clientId;

    public GraphMail(string clientId)
    {
        _clientId = clientId;
    }

    public void Dispose() => _http.Dispose();

    public async Task<DeviceLogin> StartLoginAsync(CancellationToken ct)
    {
        using var doc = await PostFormAsync($"{Authority}/devicecode", new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["scope"] = Scope,
        }, ct);
        var root = doc.RootElement;
        string device = Text(root, "device_code");
        if (device.Length == 0)
            throw new InvalidOperationException("Device code request failed: " + ErrorText(root));
        return new DeviceLogin(device, Text(root, "user_code"), Text(root, "verification_uri"),
            (int)Number(root, "expires_in"), (int)Number(root, "interval"));
    }

    public async Task<TokenState> WaitLoginAsync(DeviceLogin login, CancellationToken ct)
    {
        int interval = Math.Max(MinPollSeconds, login.IntervalSeconds);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(login.ExpiresInSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            using var doc = await PostFormAsync($"{Authority}/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrant,
                ["client_id"] = _clientId,
                ["device_code"] = login.DeviceCode,
            }, ct);
            var root = doc.RootElement;
            if (Text(root, "access_token").Length > 0) return StateOf(root, "");
            string error = Text(root, "error");
            if (error == "authorization_pending") continue;
            if (error == "slow_down")
            {
                interval += SlowDownSeconds;
                continue;
            }
            throw new InvalidOperationException("Sign-in failed: " + ErrorText(root));
        }
        throw new InvalidOperationException("Sign-in timed out, the code has expired");
    }

    public async Task<TokenState> EnsureAccessAsync(
        TokenState state, bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && state.AccessToken.Length > 0
            && state.AccessExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return state;
        if (state.RefreshToken.Length == 0)
            throw new LoginRequiredException("No refresh token is stored");
        using var doc = await PostFormAsync($"{Authority}/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId,
            ["refresh_token"] = state.RefreshToken,
            ["scope"] = Scope,
        }, ct);
        var root = doc.RootElement;
        if (Text(root, "access_token").Length == 0)
            throw new LoginRequiredException("Token refresh failed: " + ErrorText(root));
        return StateOf(root, state.RefreshToken);
    }

    public async Task<List<MailMessage>> ListAsync(
        TokenState state, string sender, DateTime sinceUtc, CancellationToken ct)
    {
        string since = sinceUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string filter = $"receivedDateTime ge {since} and from/emailAddress/address eq '{sender}'";
        string? url = $"{GraphBase}/me/messages?$filter={Uri.EscapeDataString(filter)}"
            + "&$orderby=receivedDateTime%20desc"
            + $"&$top={PageSize}&$select=id,subject,receivedDateTime,body";
        var messages = new List<MailMessage>();
        while (url != null)
        {
            string json = await GetJsonAsync(state, url, true, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    string receivedText = Text(item, "receivedDateTime");
                    if (!DateTimeOffset.TryParse(receivedText, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var received))
                        continue;
                    string body = item.TryGetProperty("body", out var b) ? Text(b, "content") : "";
                    messages.Add(new MailMessage(
                        Text(item, "id"), Text(item, "subject"), received.ToUnixTimeSeconds(), body));
                }
            string next = Text(root, "@odata.nextLink");
            url = next.Length > 0 ? next : null;
        }
        messages.Sort((a, b) => a.ReceivedUnix.CompareTo(b.ReceivedUnix));
        return messages;
    }

    public async Task<string?> GetHtmlAsync(TokenState state, string messageId, CancellationToken ct)
    {
        string url = $"{GraphBase}/me/messages/{Uri.EscapeDataString(messageId)}?$select=body";
        using var doc = JsonDocument.Parse(await GetJsonAsync(state, url, false, ct));
        if (!doc.RootElement.TryGetProperty("body", out var body)) return null;
        return string.Equals(Text(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase)
            ? Text(body, "content")
            : null;
    }

    private async Task<string> GetJsonAsync(TokenState state, string url, bool textBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.AccessToken);
        if (textBody) request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
        using var response = await _http.SendAsync(request, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LoginRequiredException("The mailbox rejected the token: " + Short(json));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Mailbox request failed ({(int)response.StatusCode}): {Short(json)}");
        return json;
    }

    private TokenState StateOf(JsonElement root, string previousRefresh)
    {
        string refresh = Text(root, "refresh_token");
        return new TokenState
        {
            ClientId = _clientId,
            RefreshToken = refresh.Length > 0 ? refresh : previousRefresh,
            AccessToken = Text(root, "access_token"),
            AccessExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                + Math.Max(0, Number(root, "expires_in") - ExpiryMarginSeconds),
        };
    }

    private async Task<JsonDocument> PostFormAsync(
        string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(url, content, ct);
        string json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Unexpected answer ({(int)response.StatusCode}) from {url}: {Short(json)}");
        }
    }

    private static string ErrorText(JsonElement root)
    {
        string error = Text(root, "error");
        string description = Text(root, "error_description");
        int cut = description.IndexOf('\n');
        if (cut > 0) description = description[..cut].Trim();
        return error.Length > 0 ? $"{error}: {description}" : "no error details";
    }

    private static string Short(string text) =>
        text.Length <= 300 ? text : text[..300] + "...";

    private static string Text(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? ""
            : "";

    private static long Number(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var el)
        && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long value)
            ? value
            : 0;
}
