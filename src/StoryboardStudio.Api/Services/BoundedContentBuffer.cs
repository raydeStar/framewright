using System.Text;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Spools a provider response to a delete-on-close temporary file while
/// enforcing the same byte limit used by the content-addressed asset store.
/// Video responses can be hundreds of megabytes and must never be accumulated
/// in a MemoryStream before validation.
/// </summary>
internal static class BoundedContentBuffer
{
    public static async Task<string> ReadUtf8StringAsync(
        HttpContent content,
        long maxBytes,
        string overflowMessage,
        bool providerCallMade,
        string? providerRequestId,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new GenerationDispatchException(overflowMessage, providerCallMade, providerRequestId);
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new GenerationDispatchException(overflowMessage, providerCallMade, providerRequestId);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public static async Task<Stream> CopyToTemporaryFileAsync(
        HttpContent content,
        long maxBytes,
        string overflowMessage,
        string? providerRequestId,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new GenerationDispatchException(overflowMessage, true, providerRequestId);

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"storyboard-studio-provider-{Guid.NewGuid():N}.tmp");
        FileStream? output = null;
        try
        {
            output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            await using var input = await content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81_920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new GenerationDispatchException(overflowMessage, true, providerRequestId);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            output.Position = 0;
            return output;
        }
        catch
        {
            if (output is not null) await output.DisposeAsync();
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
