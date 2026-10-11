using System.Net;
using System.Text;

namespace NotionMultiMcp.Auth;

/// <summary>The password and error pages: their markup and the security headers they are served with.</summary>
internal static class HtmlPage
{
    private const string HstsValue = "max-age=31536000";

    public static IResult Result(HttpContext ctx, string body, int status, bool useHsts)
    {
        var headers = ctx.Response.Headers;
        headers["X-Frame-Options"] = "DENY";
        headers["X-Content-Type-Options"] = "nosniff";

        // No form-action: Chrome would apply it to the redirect after the login form and block the callback.
        headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        if (useHsts)
        {
            headers["Strict-Transport-Security"] = HstsValue;
        }

        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Notion MCP</title>
              <style>
                :root { color-scheme: light dark; --bg:#f6f5f2; --card:#fff; --fg:#1f1f1f; --muted:#666; --accent:#2f6fed; --err:#c0392b; }
                @media (prefers-color-scheme: dark) { :root { --bg:#161616; --card:#222; --fg:#eee; --muted:#aaa; --accent:#6b9bff; --err:#ff7b6b; } }
                body { margin:0; min-height:100vh; display:grid; place-items:center; background:var(--bg); color:var(--fg);
                       font:16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; padding:16px; box-sizing:border-box; }
                main { background:var(--card); max-width:420px; width:100%; padding:28px; border-radius:12px; box-shadow:0 2px 16px rgba(0,0,0,.08); }
                h1 { font-size:1.3rem; margin:0 0 12px; }
                p { color:var(--muted); margin:0 0 16px; }
                .error { color:var(--err); font-weight:600; }
                .warning { font-size:.9rem; border-left:3px solid var(--err); padding-left:10px; }
                label { display:block; font-weight:600; margin-bottom:6px; }
                input[type=password] { width:100%; box-sizing:border-box; padding:10px 12px; font-size:1rem; border-radius:8px; border:1px solid #8886; background:transparent; color:inherit; }
                button { margin-top:14px; width:100%; padding:10px; font-size:1rem; font-weight:600; border:0; border-radius:8px; background:var(--accent); color:#fff; cursor:pointer; }
                code { font-size:.9em; }
              </style>
            </head>
            <body><main>{{body}}</main></body>
            </html>
            """;
        return TypedResults.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }

    /// <summary>The password form shown to the person who is connecting a client.</summary>
    public static string LoginBody(AuthorizeRequest request, string? error)
    {
        var target = Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var parsed) ? parsed.Host : request.RedirectUri;
        var hidden = new StringBuilder();
        foreach (var (name, value) in request.HiddenFields())
        {
            if (value is not null)
            {
                hidden.Append($"<input type=\"hidden\" name=\"{name}\" value=\"{WebUtility.HtmlEncode(value)}\">");
            }
        }

        var errorHtml = error is null ? "" : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>";
        return $"""
            <h1>Connect to Notion</h1>
            <p>A client (<strong>{WebUtility.HtmlEncode(target)}</strong>) is asking for access to this MCP server.
            Enter the password set in <code>Server:AuthPassword</code> to authorize.</p>
            <p class="warning">Only continue if you just started connecting this server from your own Claude account.
            If someone sent you this page, close it.</p>
            {errorHtml}
            <form method="post" action="{OAuthPaths.Authorize}">
              {hidden}
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" autofocus required>
              <button type="submit">Authorize</button>
            </form>
            """;
    }

    public static string ErrorBody(string message) =>
        $"<h1>Could not authorize</h1><p class=\"error\">{WebUtility.HtmlEncode(message)}</p>";
}
