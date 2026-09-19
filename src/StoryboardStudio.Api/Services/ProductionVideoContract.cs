using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Validates the frozen delivery contract carried by a rendered video manifest.
/// A Max badge alone is not production evidence: the take must also match the
/// project's current frame rate, canvas, color space, and audio sample rate.
/// </summary>
internal static class ProductionVideoContract
{
    public static bool IsMaxForProject(GenerationManifestRecord manifest, ProjectRecord project, ShotRecord shot)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest.ManifestJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("videoQuality", out var quality) ||
                !string.Equals(quality.GetString(), nameof(VideoQuality.Max), StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("projectFormat", out var format) ||
                format.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return StringEquals(format, "aspectRatio", project.AspectRatio) &&
                IntEquals(format, "framesPerSecond", project.FramesPerSecond) &&
                IntEquals(format, "deliveryWidth", project.DeliveryWidth) &&
                IntEquals(format, "deliveryHeight", project.DeliveryHeight) &&
                StringEquals(format, "colorSpace", project.ColorSpace) &&
                IntEquals(format, "audioSampleRate", project.AudioSampleRate) &&
                root.TryGetProperty("videoCanvas", out var videoCanvas) &&
                CanvasEquals(videoCanvas, VideoCanvas(project.DeliveryWidth, project.DeliveryHeight, VideoQuality.Max)) &&
                root.TryGetProperty("shot", out var frozenShot) &&
                frozenShot.ValueKind == JsonValueKind.Object &&
                GuidEquals(frozenShot, "id", shot.Id) &&
                StringEquals(frozenShot, "code", shot.Code) &&
                IntEquals(frozenShot, "version", manifest.ShotVersion) &&
                StringEquals(frozenShot, "description", shot.Description) &&
                IntEquals(frozenShot, "durationFrames", shot.DurationFrames) &&
                StringEquals(frozenShot, "camera", shot.Camera) &&
                StringEquals(frozenShot, "action", shot.Action);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool StringEquals(JsonElement parent, string propertyName, string expected)
        => parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString()?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IntEquals(JsonElement parent, string propertyName, int expected)
        => parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var actual) &&
            actual == expected;

    private static bool GuidEquals(JsonElement parent, string propertyName, Guid expected)
        => parent.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetGuid(out var actual) &&
            actual == expected;

    private static bool CanvasEquals(JsonElement canvas, (int Width, int Height) expected)
        => canvas.ValueKind == JsonValueKind.Object &&
            IntEquals(canvas, "width", expected.Width) &&
            IntEquals(canvas, "height", expected.Height);

    public static (int Width, int Height) VideoCanvas(int deliveryWidth, int deliveryHeight, VideoQuality quality)
    {
        var targetPixels = quality switch
        {
            VideoQuality.Medium => 560_000d,
            VideoQuality.High or VideoQuality.Max => 750_000d,
            _ => 400_000d
        };
        var aspect = (double)deliveryWidth / deliveryHeight;
        // H3 patchifies the latent in 32-pixel image-space blocks. Keeping this
        // calculation beside validation prevents a "Max" label from surviving
        // on a low-resolution proxy canvas.
        var height = Math.Max(32, (int)Math.Round(Math.Sqrt(targetPixels / aspect) / 32d) * 32);
        var width = Math.Max(32, (int)Math.Round(height * aspect / 32d) * 32);
        return (width, height);
    }
}
