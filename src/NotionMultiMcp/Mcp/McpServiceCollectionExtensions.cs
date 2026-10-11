using NotionMultiMcp.Mcp.Tools;

namespace NotionMultiMcp.Mcp;

internal static class McpServiceCollectionExtensions
{
    /// <summary>Registers the MCP endpoint, its protocol handler and the tools.</summary>
    public static IServiceCollection AddMcpServer(this IServiceCollection services)
    {
        services.AddSingleton<OriginPolicy>();
        services.AddSingleton<ToolContext>();
        services.AddSingleton<DataSourceResolver>();
        services.AddSingleton<ReadTools>();
        services.AddSingleton<WriteTools>();
        services.AddSingleton<ToolCatalog>();
        services.AddSingleton<ToolDispatcher>();
        services.AddSingleton<McpHandler>();
        services.AddSingleton<McpEndpoint>();
        return services;
    }
}
