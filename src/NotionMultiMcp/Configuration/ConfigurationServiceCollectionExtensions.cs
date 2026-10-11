using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotionMultiMcp.Notion;

namespace NotionMultiMcp.Configuration;

internal static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Binds the "Server" and "Notion" sections, validates them, and exposes the validated result as
    /// <see cref="ServerSettings"/> and <see cref="WorkspaceRegistry"/>, which is what the rest of the app uses.
    /// </summary>
    public static IServiceCollection AddServerConfiguration(this IServiceCollection services)
    {
        services.AddOptions<ServerOptions>().BindConfiguration(ServerOptions.SectionName).ValidateOnStart();
        services.AddOptions<NotionOptions>().BindConfiguration(NotionOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NotionOptions>, NotionOptionsValidator>());

        services.AddSingleton(provider => ServerSettings.From(
            provider.GetRequiredService<IOptions<ServerOptions>>().Value,
            provider.GetRequiredService<IHostEnvironment>()));
        services.AddSingleton(provider => WorkspaceRegistry.From(
            provider.GetRequiredService<IOptions<NotionOptions>>().Value));

        return services;
    }
}
