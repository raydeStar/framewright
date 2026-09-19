namespace StoryboardStudio.Api.Services;

internal sealed record RuntimePathCheck(bool Writable, string Detail);

/// <summary>
/// Performs a bounded, non-destructive permission probe without changing any
/// Framewright records or external provider state. The temporary sentinel is
/// opened with DeleteOnClose and is removed again in the fallback cleanup path.
/// </summary>
internal static class RuntimePathPreflight
{
    public static RuntimePathCheck InspectWritableDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
            return new(false, $"A file occupies the required directory path: {fullPath}");

        var probeDirectory = Directory.Exists(fullPath) ? fullPath : FindNearestExistingDirectory(fullPath);
        if (probeDirectory is null)
            return new(false, $"No existing parent directory can create: {fullPath}");

        var sentinel = Path.Combine(probeDirectory, $".framewright-readiness-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                sentinel,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose | FileOptions.WriteThrough);
            stream.WriteByte(0);
            stream.Flush(flushToDisk: true);
            return new(true, Directory.Exists(fullPath)
                ? $"Writable: {fullPath}"
                : $"Writable parent can create: {fullPath}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(false, $"Not writable: {fullPath}. {error.Message}");
        }
        finally
        {
            try { if (File.Exists(sentinel)) File.Delete(sentinel); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? FindNearestExistingDirectory(string path)
    {
        var current = Directory.GetParent(path);
        while (current is not null)
        {
            if (current.Exists) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
