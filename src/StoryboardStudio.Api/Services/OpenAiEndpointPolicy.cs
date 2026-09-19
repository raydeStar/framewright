namespace StoryboardStudio.Api.Services;

internal static class OpenAiEndpointPolicy
{
    public static Uri? Resolve(
        IConfiguration configuration,
        string setting,
        string officialDefault,
        out string? error)
    {
        var value = configuration[setting] ?? officialDefault;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            error = $"{setting} is not an absolute URI.";
            return null;
        }

        var transportAllowed = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || endpoint.IsLoopback;
        if (!transportAllowed)
        {
            error = "OpenAI credentials may be sent only over HTTPS or to an explicit loopback development endpoint.";
            return null;
        }

        var official = endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
        if (!official && !configuration.GetValue("Integrations:OpenAI:AllowCustomEndpoint", false))
        {
            error = "The OpenAI endpoint is not api.openai.com. Set AllowCustomEndpoint only for a reviewed proxy contract.";
            return null;
        }

        error = null;
        return endpoint;
    }
}
