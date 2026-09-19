using System.Security.Cryptography;
using System.Text;

namespace StoryboardStudio.Api.Services;

public sealed record CredentialStatus(bool IsConfigured, string Source, bool CanManageHere, string Detail);
public sealed record SaveCredentialRequest(string ApiKey);

public interface IProviderCredentialStore
{
    string? GetOpenAiApiKey();
    CredentialStatus GetOpenAiStatus();
    void SaveOpenAiApiKey(string apiKey);
    void DeleteOpenAiApiKey();
}

/// <summary>
/// Keeps the provider secret in a Windows CurrentUser DPAPI envelope. The file
/// is intentionally outside production exports and is useless to another OS
/// user. OPENAI_API_KEY remains a read-only fallback for service deployments.
/// </summary>
public sealed class ProviderCredentialStore : IProviderCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("StoryboardStudio/OpenAI/v1");
    private readonly string credentialPath;
    private readonly object gate = new();

    public ProviderCredentialStore(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var dataRoot = StudioPaths.ResolveDataRoot(configuration, environment);
        credentialPath = Path.GetFullPath(Path.Combine(dataRoot, "credentials", "openai.dpapi"));
    }

    public string? GetOpenAiApiKey()
    {
        lock (gate)
        {
            if (OperatingSystem.IsWindows() && File.Exists(credentialPath))
            {
                try
                {
                    var protectedBytes = File.ReadAllBytes(credentialPath);
                    var clear = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(clear);
                }
                catch (CryptographicException)
                {
                    // A moved/corrupt credential must never fall through as a
                    // secret value. The environment fallback may still work.
                }
            }
        }
        return Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim() is { Length: > 0 } value ? value : null;
    }

    public CredentialStatus GetOpenAiStatus()
    {
        var stored = OperatingSystem.IsWindows() && File.Exists(credentialPath);
        var environment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var configured = !string.IsNullOrWhiteSpace(GetOpenAiApiKey());
        var source = stored ? "Windows user credential" : environment ? "Service environment" : "Not configured";
        return new(configured, source, OperatingSystem.IsWindows(), configured
            ? $"OpenAI credential available from {source.ToLowerInvariant()}; its value is never returned to the browser."
            : OperatingSystem.IsWindows()
                ? "Save a project API key into the Windows user-bound encrypted store."
                : "Set OPENAI_API_KEY in the service environment.");
    }

    public void SaveOpenAiApiKey(string apiKey)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Interactive credential storage is available only on Windows.");
        var value = apiKey?.Trim() ?? "";
        if (value.Length is < 20 or > 500 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("The API key must contain 20 to 500 non-whitespace characters.", nameof(apiKey));
        lock (gate)
        {
            var directory = Path.GetDirectoryName(credentialPath)!;
            Directory.CreateDirectory(directory);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            var temporary = credentialPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, protectedBytes);
                File.Move(temporary, credentialPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public void DeleteOpenAiApiKey()
    {
        lock (gate) { if (File.Exists(credentialPath)) File.Delete(credentialPath); }
    }
}
