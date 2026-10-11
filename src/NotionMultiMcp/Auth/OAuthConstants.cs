namespace NotionMultiMcp.Auth;

internal static class GrantType
{
    public const string AuthorizationCode = "authorization_code";
    public const string RefreshToken = "refresh_token";
}

internal static class OAuthErrorCode
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidGrant = "invalid_grant";
    public const string InvalidToken = "invalid_token";
    public const string Unauthorized = "unauthorized";
    public const string ServerError = "server_error";
    public const string InvalidTarget = "invalid_target";
    public const string InvalidRedirectUri = "invalid_redirect_uri";
    public const string InvalidClientMetadata = "invalid_client_metadata";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string UnsupportedResponseType = "unsupported_response_type";
}

internal static class OAuthProtocol
{
    public const string ResponseTypeCode = "code";
    public const string CodeChallengeMethodS256 = "S256";
    public const string BearerScheme = "Bearer";
    public const string BearerMethodHeader = "header";
    public const string AuthMethodNone = "none";
}
