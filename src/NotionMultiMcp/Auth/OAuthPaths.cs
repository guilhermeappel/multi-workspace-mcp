namespace NotionMultiMcp.Auth;

/// <summary>The paths of the OAuth endpoints and of the discovery documents.</summary>
internal static class OAuthPaths
{
    public const string ProtectedResourceMetadata = "/.well-known/oauth-protected-resource";
    public const string AuthorizationServerMetadata = "/.well-known/oauth-authorization-server";
    public const string Register = "/register";
    public const string Authorize = "/authorize";
    public const string Token = "/token";
}
