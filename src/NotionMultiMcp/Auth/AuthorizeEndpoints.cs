using System.Globalization;

namespace NotionMultiMcp.Auth;

/// <summary>The password page: shows it for a valid authorization request and turns a correct password into a code.</summary>
internal sealed partial class AuthorizeEndpoints(
    ServerSettings settings,
    TokenService tokens,
    LoginThrottle throttle,
    RedirectPolicy redirects,
    ILogger<AuthorizeEndpoints> logger)
{

    private bool UseHsts => settings.PublicBaseUrl.StartsWith(Uri.UriSchemeHttps + "://", StringComparison.Ordinal);

    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(OAuthPaths.Authorize, ShowLoginPage);
        app.MapPost(OAuthPaths.Authorize, (Delegate)SubmitLoginAsync);
    }

    private IResult ShowLoginPage(HttpContext ctx)
    {
        var request = AuthorizeRequest.From(key => ctx.Request.Query[key]);
        return ValidateClient(ctx, request) ?? ValidateParameters(request) ?? LoginPage(ctx, request, null);
    }

    private async Task<IResult> SubmitLoginAsync(HttpContext ctx)
    {
        if (!ctx.Request.HasFormContentType)
        {
            return TypedResults.BadRequest();
        }

        if (!OAuthResults.AcceptsBodySize(ctx))
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var request = AuthorizeRequest.From(key => form[key]);

        var invalid = ValidateClient(ctx, request) ?? ValidateParameters(request);
        if (invalid is not null)
        {
            return invalid;
        }

        // The attempt is counted before the password is checked, so concurrent guesses cannot slip through.
        var decision = throttle.TryBegin(LoginThrottle.ClientKeyFor(ctx.Connection.RemoteIpAddress));
        if (!decision.IsAllowed)
        {
            LogLoginLocked();
            var minutes = (int)Math.Ceiling(decision.RetryAfter.TotalMinutes);
            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return LoginPage(ctx, request, $"Too many attempts. Try again in {Math.Max(minutes, 1)} minute(s).", StatusCodes.Status429TooManyRequests);
        }

        if (!tokens.PasswordMatches(form["password"].ToString()))
        {
            LogIncorrectPassword();
            return LoginPage(ctx, request, "Incorrect password.", StatusCodes.Status403Forbidden);
        }

        throttle.Refund(decision.Attempt!.Value);

        var code = tokens.Protect(TokenPurpose.Code, new AuthorizationCode(
            TokenService.Hash(request.ClientId),
            request.RedirectUri,
            request.CodeChallenge,
            tokens.Now + (long)settings.AuthorizationCodeLifetime.TotalSeconds,
            TokenService.NewNonce()));

        return OAuthResults.SeeOther(OAuthResults.WithQuery(request.RedirectUri, ("code", code), ("state", request.State)));
    }

    /// <summary>The client must be one we registered, and the redirect must be one it registered.</summary>
    private IResult? ValidateClient(HttpContext ctx, AuthorizeRequest request)
    {
        if (!tokens.TryUnprotect<ClientRegistration>(TokenPurpose.Client, request.ClientId, out var registration))
        {
            return ErrorPage(ctx, "Invalid client_id, or it was issued with a different Server:SigningKey. Remove the connector in Claude and add it again.");
        }

        if (string.IsNullOrEmpty(request.RedirectUri)
            || !redirects.IsAllowed(request.RedirectUri)
            || !registration.RedirectUris.Any(registered => RedirectPolicy.Matches(registered, request.RedirectUri)))
        {
            return ErrorPage(ctx, "redirect_uri is not registered for this client.");
        }

        return null;
    }

    /// <summary>Problems with the request itself are reported to the (already validated) redirect URI.</summary>
    private IResult? ValidateParameters(AuthorizeRequest request)
    {
        if (string.IsNullOrEmpty(request.ResponseType))
        {
            return RedirectError(request, OAuthErrorCode.InvalidRequest, "Missing response_type.");
        }

        if (!string.Equals(request.ResponseType, OAuthProtocol.ResponseTypeCode, StringComparison.Ordinal))
        {
            return RedirectError(request, OAuthErrorCode.UnsupportedResponseType, "Only response_type=code is supported.");
        }

        if (string.IsNullOrEmpty(request.CodeChallenge) || !string.Equals(request.CodeChallengeMethod, OAuthProtocol.CodeChallengeMethodS256, StringComparison.Ordinal))
        {
            return RedirectError(request, OAuthErrorCode.InvalidRequest, "PKCE with S256 is required.");
        }

        if (!PkceVerifier.IsValidChallenge(request.CodeChallenge))
        {
            return RedirectError(request, OAuthErrorCode.InvalidRequest, "code_challenge must be a base64url-encoded SHA-256 hash.");
        }

        if (request.Resource is { Length: > 0 } resource && !settings.IsOwnResource(resource))
        {
            return RedirectError(request, OAuthErrorCode.InvalidTarget, "Unknown resource.");
        }

        return null;
    }

    private static IResult RedirectError(AuthorizeRequest request, string error, string description) =>
        OAuthResults.SeeOther(OAuthResults.WithQuery(
            request.RedirectUri,
            ("error", error),
            ("error_description", description),
            ("state", request.State)));

    private IResult LoginPage(HttpContext ctx, AuthorizeRequest request, string? error, int status = StatusCodes.Status200OK) =>
        HtmlPage.Result(ctx, HtmlPage.LoginBody(request, error), status, UseHsts);

    private IResult ErrorPage(HttpContext ctx, string message) =>
        HtmlPage.Result(ctx, HtmlPage.ErrorBody(message), StatusCodes.Status400BadRequest, UseHsts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization rejected: the login is locked after too many incorrect passwords.")]
    private partial void LogLoginLocked();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization rejected: incorrect password.")]
    private partial void LogIncorrectPassword();
}
