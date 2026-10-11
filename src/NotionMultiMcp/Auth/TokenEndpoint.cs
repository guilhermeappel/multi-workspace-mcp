namespace NotionMultiMcp.Auth;

/// <summary>The token endpoint: turns an authorization code or a refresh token into an access token.</summary>
internal sealed class TokenEndpoint(ServerSettings settings, TokenService tokens, AuthorizationCodeLedger ledger)
{
    public void Map(IEndpointRouteBuilder app) => app.MapPost(OAuthPaths.Token, (Delegate)HandleAsync);

    private async Task<IResult> HandleAsync(HttpContext ctx)
    {
        OAuthResults.NoStore(ctx);
        if (!OAuthResults.AcceptsBodySize(ctx))
        {
            return OAuthResults.BodyTooLarge();
        }

        if (!ctx.Request.HasFormContentType)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidRequest, "Use application/x-www-form-urlencoded.");
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var grantType = form["grant_type"].ToString();
        var clientId = form["client_id"].ToString();

        if (grantType.Length == 0)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidRequest, "Missing grant_type.");
        }

        if (form["resource"].ToString() is { Length: > 0 } resource && !settings.IsOwnResource(resource))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidTarget, "Unknown resource.");
        }

        if (string.Equals(grantType, GrantType.AuthorizationCode, StringComparison.Ordinal))
        {
            return ExchangeCode(form, clientId);
        }

        if (string.Equals(grantType, GrantType.RefreshToken, StringComparison.Ordinal))
        {
            return Refresh(form, clientId);
        }

        return OAuthResults.Error(OAuthErrorCode.UnsupportedGrantType, "Supported: authorization_code, refresh_token.");
    }

    private IResult ExchangeCode(IFormCollection form, string clientId)
    {
        if (!tokens.TryUnprotect<AuthorizationCode>(TokenPurpose.Code, form["code"].ToString(), out var code)
            || code.ExpiresAt < tokens.Now)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "Invalid or expired authorization code.");
        }

        if (!ledger.TryRedeem(code.Nonce, code.ExpiresAt))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "Authorization code already used.");
        }

        if (clientId.Length > 0 && !string.Equals(TokenService.Hash(clientId), code.ClientHash, StringComparison.Ordinal))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "client_id does not match the authorization code.");
        }

        if (!string.Equals(form["redirect_uri"].ToString(), code.RedirectUri, StringComparison.Ordinal))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "redirect_uri does not match the authorization request.");
        }

        if (!PkceVerifier.Matches(form["code_verifier"].ToString(), code.CodeChallenge))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "PKCE verification failed.");
        }

        return IssueTokens(code.ClientHash, sessionStartedAt: tokens.Now);
    }

    private IResult Refresh(IFormCollection form, string clientId)
    {
        if (!tokens.TryUnprotect<RefreshToken>(TokenPurpose.Refresh, form["refresh_token"].ToString(), out var refresh)
            || refresh.ExpiresAt < tokens.Now
            || refresh.Generation != settings.TokenGeneration)
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "Invalid or expired refresh token.");
        }

        if (clientId.Length > 0 && !string.Equals(TokenService.Hash(clientId), refresh.ClientHash, StringComparison.Ordinal))
        {
            return OAuthResults.Error(OAuthErrorCode.InvalidGrant, "client_id does not match the refresh token.");
        }

        return IssueTokens(refresh.ClientHash, refresh.SessionStartedAt);
    }

    /// <summary>A refresh token slides forward on every use, but never past the end of the session it belongs to.</summary>
    private IResult IssueTokens(string clientHash, long sessionStartedAt)
    {
        var now = tokens.Now;
        var accessSeconds = (long)settings.AccessTokenLifetime.TotalSeconds;
        var sessionEnd = sessionStartedAt + (long)settings.MaxSessionLifetime.TotalSeconds;
        var access = tokens.Protect(TokenPurpose.Access, new AccessToken(now + accessSeconds, settings.TokenGeneration));
        var refresh = tokens.Protect(TokenPurpose.Refresh, new RefreshToken(
            clientHash,
            Math.Min(now + (long)settings.RefreshTokenLifetime.TotalSeconds, sessionEnd),
            settings.TokenGeneration,
            TokenService.NewNonce(),
            sessionStartedAt));

        return TypedResults.Json(new
        {
            access_token = access,
            token_type = OAuthProtocol.BearerScheme,
            expires_in = accessSeconds,
            refresh_token = refresh,
        });
    }
}
