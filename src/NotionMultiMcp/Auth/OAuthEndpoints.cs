namespace NotionMultiMcp.Auth;

/// <summary>
/// Minimal single-user OAuth 2.1 server: Claude registers a client (DCR), opens /authorize in your
/// browser, you type Server:AuthPassword and it receives the tokens.
/// </summary>
internal static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        app.Services.GetRequiredService<DiscoveryEndpoints>().Map(app);
        app.Services.GetRequiredService<RegistrationEndpoint>().Map(app);
        app.Services.GetRequiredService<AuthorizeEndpoints>().Map(app);
        app.Services.GetRequiredService<TokenEndpoint>().Map(app);
    }
}
