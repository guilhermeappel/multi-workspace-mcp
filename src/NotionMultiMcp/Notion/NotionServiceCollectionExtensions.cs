namespace NotionMultiMcp.Notion;

internal static class NotionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="NotionClient"/> on a typed <see cref="HttpClient"/>. The client is captured by
    /// singletons, so the handler never rotates; connection recycling is handled by
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> instead (keeps DNS fresh).
    /// </summary>
    public static IServiceCollection AddNotionClient(this IServiceCollection services)
    {
        services
            .AddHttpClient<NotionClient>(http =>
            {
                http.BaseAddress = NotionClient.BaseAddress;
                http.Timeout = NotionClient.RequestTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        return services;
    }
}
