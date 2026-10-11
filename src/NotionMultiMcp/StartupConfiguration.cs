using Microsoft.Extensions.Options;
using NotionMultiMcp.Configuration;

namespace NotionMultiMcp;

/// <summary>Checks the configuration at startup and turns each mistake into one clear line.</summary>
internal static class StartupConfiguration
{
    /// <param name="services">The built application's services.</param>
    /// <param name="errors">Where configuration errors are reported (standard error in production).</param>
    /// <returns>False when anything is wrong; every problem is written, not only the first.</returns>
    public static bool TryValidate(IServiceProvider services, TextWriter errors)
    {
        // No short circuit: report the server and the Notion problems together.
        var server = IsValid<ServerOptions>(services, errors);
        var notion = IsValid<NotionOptions>(services, errors);
        return server && notion;
    }

    private static bool IsValid<TOptions>(IServiceProvider services, TextWriter errors)
        where TOptions : class
    {
        try
        {
            // Reading Value runs the validators; they throw OptionsValidationException when something is wrong.
            _ = services.GetRequiredService<IOptions<TOptions>>().Value;
            return true;
        }
        catch (OptionsValidationException e)
        {
            // Container Apps shows stderr in its log stream: say what is wrong, not a stack trace.
            foreach (var failure in e.Failures)
            {
                errors.WriteLine($"Configuration error: {failure}");
            }

            return false;
        }
        catch (InvalidOperationException e)
        {
            // The binder refuses a value that is not of the right type, e.g. Server:TokenGeneration=two.
            errors.WriteLine($"Configuration error: {e.Message}");
            return false;
        }
    }
}
