using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace FXViewer;

public static class OAuthService
{
    public const string RedirectUri = "http://localhost:53589/callback/";
    private const string AuthBase = "https://id.ctrader.com/my/settings/openapi/grantingaccess/";
    private const string TokenUrl = "https://openapi.ctrader.com/apps/token";

    public static string BuildAuthUrl(string clientId) =>
        AuthBase
        + "?client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
        + "&scope=accounts";

    public static async Task<string> WaitForCodeAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(RedirectUri);
        listener.Start();
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        var code = context.Request.QueryString["code"];
        var body = Encoding.UTF8.GetBytes(
            "<html><body><h3>FXViewer: authorization received.</h3>You can close this tab.</body></html>");
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
        listener.Stop();
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("Callback did not contain an authorization code");
        return code;
    }

    public static async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        string clientId, string clientSecret, string code)
    {
        using var http = new HttpClient();
        var url = TokenUrl
            + "?grant_type=authorization_code"
            + "&code=" + Uri.EscapeDataString(code)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&client_id=" + Uri.EscapeDataString(clientId)
            + "&client_secret=" + Uri.EscapeDataString(clientSecret);
        var json = await http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = GetString(root, "accessToken") ?? GetString(root, "access_token");
        var refresh = GetString(root, "refreshToken") ?? GetString(root, "refresh_token") ?? "";
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Token endpoint returned no access token: " + json);
        return (access, refresh);
    }

    public static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
