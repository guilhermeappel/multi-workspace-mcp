using System.Text.Json.Nodes;

namespace NotionMultiMcp.Auth;

/// <summary>Dynamic client registration (RFC 7591). The client id is the signed registration itself.</summary>
internal sealed class RegistrationEndpoint(TokenService tokens, RedirectPolicy redirects)
{
    private const int MaxRedirectUris = 10;
    private const int MaxClientNameLength = 200;

    public void Map(IEndpointRouteBuilder app) => app.MapPost(OAuthPaths.Register, (Delegate)HandleAsync);

    private async Task<IResult> HandleAsync(HttpContext ctx)
    {
        if (!OAuthResults.AcceptsBodySize(ctx))
        {
            return OAuthResults.BodyTooLarge();
        }

        var parsed = await J.TryParseAsync(ctx.Request.Body, ctx.RequestAborted);
        if (!parsed.IsValid)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidClientMetadata, "Body must be JSON.");
        }

        var body = parsed.Node;
        var redirectUris = J.Arr(body?["redirect_uris"]).Select(n => J.Str(n) ?? "").ToArray();
        var name = J.Str(body?["client_name"]);
        if (redirectUris.Length > MaxRedirectUris || name?.Length > MaxClientNameLength)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidClientMetadata, "client_name or redirect_uris is too long.");
        }

        if (redirectUris.Length == 0 || redirectUris.Any(u => !redirects.IsAllowed(u)))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidRedirectUri, "redirect_uris not allowed by this server.");
        }

        var registration = new ClientRegistration(redirectUris, name, tokens.Now);
        var clientId = tokens.Protect(TokenPurpose.Client, registration);

        var registered = new JsonObject
        {
            ["client_id"] = clientId,
            ["client_id_issued_at"] = registration.IssuedAt,
            ["redirect_uris"] = new JsonArray([.. redirectUris.Select(u => (JsonNode)u)]),
            ["token_endpoint_auth_method"] = OAuthProtocol.AuthMethodNone,
            ["grant_types"] = new JsonArray(GrantType.AuthorizationCode, GrantType.RefreshToken),
            ["response_types"] = new JsonArray(OAuthProtocol.ResponseTypeCode),
        };
        if (name is not null)
        {
            registered["client_name"] = name;
        }

        OAuthResults.NoStore(ctx);
        return TypedResults.Json(registered, statusCode: StatusCodes.Status201Created);
    }
}
