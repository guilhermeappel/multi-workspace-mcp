namespace NotionMultiMcp.Auth;

/// <summary>The discovery documents Claude reads after the 401: protected resource (RFC 9728) and server (RFC 8414).</summary>
internal sealed class DiscoveryEndpoints(ServerSettings settings)
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(OAuthPaths.ProtectedResourceMetadata, ProtectedResource);
        app.MapGet(OAuthPaths.ProtectedResourceMetadata + "/mcp", ProtectedResource);
        app.MapGet(OAuthPaths.AuthorizationServerMetadata, AuthorizationServer);
    }

    private IResult ProtectedResource() => TypedResults.Json(new
    {
        resource = settings.McpUrl,
        authorization_servers = new[] { settings.PublicBaseUrl },
        bearer_methods_supported = new[] { OAuthProtocol.BearerMethodHeader },
        resource_name = "Notion multi-workspace",
    });

    private IResult AuthorizationServer() => TypedResults.Json(new
    {
        issuer = settings.PublicBaseUrl,
        authorization_endpoint = settings.PublicBaseUrl + OAuthPaths.Authorize,
        token_endpoint = settings.PublicBaseUrl + OAuthPaths.Token,
        registration_endpoint = settings.PublicBaseUrl + OAuthPaths.Register,
        response_types_supported = new[] { OAuthProtocol.ResponseTypeCode },
        grant_types_supported = new[] { GrantType.AuthorizationCode, GrantType.RefreshToken },
        code_challenge_methods_supported = new[] { OAuthProtocol.CodeChallengeMethodS256 },
        token_endpoint_auth_methods_supported = new[] { OAuthProtocol.AuthMethodNone },
    });
}
