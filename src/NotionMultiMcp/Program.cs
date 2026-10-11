using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotionMultiMcp;
using NotionMultiMcp.Auth;
using NotionMultiMcp.Configuration;
using NotionMultiMcp.Mcp;
using NotionMultiMcp.Notion;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = McpEndpoint.MaxBodyBytes);

builder.Services.AddServerConfiguration();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddNotionClient();
builder.Services.AddOAuthServer();
builder.Services.AddMcpServer();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// UseExceptionHandler needs this service registered even though GlobalExceptionHandler answers everything itself.
builder.Services.AddProblemDetails();

// The app is only reachable through the Container Apps ingress, which puts the real client address in
// X-Forwarded-For. The login throttle needs that address, so trust the header (see ADR 0003).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (!StartupConfiguration.TryValidate(app.Services, Console.Error))
{
    return 1;
}

StartupLog.Describe(app.Logger, app.Services.GetRequiredService<ServerSettings>(), app.Services.GetRequiredService<WorkspaceRegistry>());

app.UseForwardedHeaders();
app.UseExceptionHandler();

app.MapHealthEndpoints();
app.MapOAuthEndpoints();
app.MapMcpEndpoint();

app.Run();

return 0;
