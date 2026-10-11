namespace NotionMultiMcp.Auth;

internal static class AuthServiceCollectionExtensions
{
    /// <summary>Registers the token service, the login protections and the OAuth endpoints.</summary>
    public static IServiceCollection AddOAuthServer(this IServiceCollection services)
    {
        services.AddSingleton<TokenService>();
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<AuthorizationCodeLedger>();
        services.AddSingleton<RedirectPolicy>();
        services.AddSingleton<DiscoveryEndpoints>();
        services.AddSingleton<RegistrationEndpoint>();
        services.AddSingleton<AuthorizeEndpoints>();
        services.AddSingleton<TokenEndpoint>();
        return services;
    }
}
