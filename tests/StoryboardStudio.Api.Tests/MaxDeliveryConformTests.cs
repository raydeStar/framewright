using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// ComfyUI's SaveVideo node converts H3 frames to YUV with BT.601 and tags
/// nothing, while an approved Rec.709 shot must carry tagged, limited-range
/// BT.709. A Max take is converted once on import so the artist can approve it,
/// with its colour unchanged and the provider's own file kept as evidence.
/// </summary>
public sealed class MaxDeliveryConformTests
{
    // A saturated green: BT.601 and BT.709 decode it far enough apart (about 23
    // levels of green) that a file merely re-tagged as BT.709 fails the check.
    private const string Green = "0x20C040";
    private static readonly byte[] GreenRgb = [0x20, 0xC0, 0x40];

    [FfmpegFact]
    public async Task AnUntaggedBt601MaxTakeIsConvertedToTaggedBt709AndCanBeApproved()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-max-conform", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = new UntaggedH3Adapter(root);
            using var factory = new StudioApiFactory(Path.Combine(root, "data"), deleteDataRoot: true, startGenerationWorker: false,
                videoMediaProbe: new FfprobeVideoMediaProbe(new ConfigurationBuilder().Build()),
                configureServices: services => services.AddSingleton<IGenerationAdapter>(video));
            using var client = factory.CreateClient();
            var project = (await (await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
                $"Conform {Guid.NewGuid():N}", "Film", "SQ-18", "The Bill Comes Due", 24, "16:9", 320, 180)))
                .Content.ReadFromJsonAsync<ProjectSummary>())!;
            (await client.PostAsync($"/api/projects/{project.Id}/activate", null)).EnsureSuccessStatusCode();
            Assert.Equal("Rec.709", project.ColorSpace);

            var image = await AssetUploads.ImageAsync(client, "conform-first.png");
            var shot = (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(
                "SH-050", "The Flash", "Orange light from frame right.", 12, "35mm · locked", "Both turn.", [], [], image.Id)))
                .Content.ReadFromJsonAsync<ShotSummary>())!;
            var low = (await (await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-manifests/prepare",
                new PrepareVideoManifestRequest(shot.Version, "Locked off; the light grows.")))
                .Content.ReadFromJsonAsync<GenerationManifestSummary>())!;
            var lowJob = (await (await client.PostAsJsonAsync($"/api/manifests/{low.Id}/dispatch", new DispatchManifestRequest(low.ManifestHash, video.Describe().Id)))
                .Content.ReadFromJsonAsync<JobSummary>())!;
            await RunAsync(factory, lowJob.Id);
            var take = await ShotAsync(client, shot.Id);

            var promote = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/video-jobs/{lowJob.Id}/promote", new PromoteVideoTakeRequest(take.Version, video.Describe().Id));
            Assert.True(promote.StatusCode == HttpStatusCode.Accepted, await promote.Content.ReadAsStringAsync());
            var maxJob = (await promote.Content.ReadFromJsonAsync<JobSummary>())!;
            await RunAsync(factory, maxJob.Id);
            var completed = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(job => job.Id == maxJob.Id);
            Assert.True(completed.State == JobState.Completed, completed.Error);

            var mastered = await ShotAsync(client, shot.Id);
            var approve = await client.PostAsJsonAsync($"/api/shots/{shot.Id}/ratify", new RatifyRequest(mastered.Version, "Max take approved for delivery."));
            Assert.True(approve.StatusCode == HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());

            var production = Path.Combine(root, "production.mp4");
            await File.WriteAllBytesAsync(production, await client.GetByteArrayAsync(mastered.ProductionVideoAssetUrl));
            Assert.Equal("tv,bt709,bt709,bt709", Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=color_range,color_space,color_transfer,color_primaries -of csv=p=0 \"{production}\"").Trim());
            Assert.Equal("12", Run("ffprobe", $"-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 \"{production}\"").Trim());
            var pixel = CentrePixel(production, root);
            for (var channel = 0; channel < 3; channel++)
                Assert.True(Math.Abs(pixel[channel] - GreenRgb[channel]) <= 6,
                    $"The approved take decodes as {string.Join(',', pixel)}; the provider drew {string.Join(',', GreenRgb)}.");

            // The provider's own file is kept, and the conversion names both.
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var conversion = await db.AuditEvents.SingleAsync(item => item.Type == "VideoConvertedForDelivery");
            using var payload = JsonDocument.Parse(conversion.PayloadJson);
            Assert.Equal(mastered.ProductionVideoAssetId, payload.RootElement.GetProperty("outputAssetId").GetGuid());
            var originalId = payload.RootElement.GetProperty("sourceAssetId").GetGuid();
            Assert.NotEqual(mastered.ProductionVideoAssetId, originalId);
            Assert.True(await db.Assets.AnyAsync(asset => asset.Id == originalId && asset.Kind == nameof(AssetKind.Video)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunAsync(StudioApiFactory factory, Guid jobId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<GenerationOrchestrator>().RunAsync(jobId, CancellationToken.None);
    }

    private static async Task<ShotSummary> ShotAsync(HttpClient client, Guid shotId)
        => (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Shots.Single(shot => shot.Id == shotId);

    /// <summary>The middle pixel as RGB, decoded the way a player reads the file's own tags.</summary>
    private static byte[] CentrePixel(string video, string root)
    {
        var raw = Path.Combine(root, $"{Guid.NewGuid():N}.rgb");
        Run("ffmpeg", $"-y -loglevel error -i \"{video}\" -frames:v 1 -vf \"crop=w=2:h=2:x=(iw/2):y=(ih/2),scale=1:1,format=rgb24\" -f rawvideo \"{raw}\"");
        return File.ReadAllBytes(raw);
    }

    private static string Run(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} failed: {error}");
        return output.Result;
    }

    /// <summary>Writes video the way ComfyUI's SaveVideo does: RGB frames
    /// converted to yuv420p with BT.601 and no colour tags.</summary>
    private sealed class UntaggedH3Adapter(string root) : IGenerationAdapter
    {
        public GenerationAdapterSummary Describe() => new(
            "untagged-h3", "Untagged H3", "Test video adapter", "Connected", "Encodes like ComfyUI's SaveVideo.", true,
            [GenerationRoute.FastDraft], [GenerationPurpose.Video]);

        public Task<GenerationAdapterOutput> ExecuteAsync(GenerationExecutionContext context, CancellationToken cancellationToken)
        {
            var (width, height) = context.VideoQuality == VideoQuality.Max
                ? (context.DeliveryWidth!.Value, context.DeliveryHeight!.Value)
                : (192, 108);
            var path = Path.Combine(root, $"{context.JobId:N}.mp4");
            Run("ffmpeg", $"-y -loglevel error -f lavfi -i \"color=c={Green}:size={width}x{height}:rate={context.VideoFramesPerSecond},format=rgb24\" " +
                $"-frames:v {context.VideoFrameCount} -pix_fmt yuv420p -c:v libx264 \"{path}\"");
            return Task.FromResult(new GenerationAdapterOutput(File.OpenRead(path), Path.GetFileName(path), "video/mp4", false, null, AssetKind.Video));
        }
    }
}
