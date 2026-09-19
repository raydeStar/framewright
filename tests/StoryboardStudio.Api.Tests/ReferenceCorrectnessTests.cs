using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

public sealed class ReferenceCaptureGenerationAdapter : IGenerationAdapter
{
    public const string AdapterId = "reference-capture";
    public GenerationExecutionContext? LastContext { get; private set; }

    public GenerationAdapterSummary Describe() => new(
        AdapterId,
        "Reference capture",
        "Test image adapter",
        "Connected",
        "Returns the exact composition bytes and captures the frozen reference plan.",
        true,
        [GenerationRoute.FastDraft],
        [GenerationPurpose.Draft]);

    public async Task<GenerationAdapterOutput> ExecuteAsync(
        GenerationExecutionContext context,
        CancellationToken cancellationToken)
    {
        LastContext = context;
        if (context.CompositionPath is null)
            throw new GenerationDispatchException("The reference-capture adapter requires a composition.");
        var bytes = await File.ReadAllBytesAsync(context.CompositionPath, cancellationToken);
        return new(
            new MemoryStream(bytes, writable: false),
            "unchanged-reference-capture.png",
            context.Composition?.MimeType ?? "image/png",
            false);
    }
}

public sealed class ReferenceCorrectnessTests
{
    private static readonly byte[] PixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task TypedAssetReferencesReachTheAdapterAndIdenticalPixelsAreANoChange()
    {
        using var baseFactory = new StudioApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ReferenceCaptureGenerationAdapter>();
                services.AddSingleton<IGenerationAdapter>(provider =>
                    provider.GetRequiredService<ReferenceCaptureGenerationAdapter>());
            }));
        using var client = factory.CreateClient();
        var parent = await UploadAsync(client, PixelPng, "mara-source.png");
        var style = await UploadAsync(client, [.. PixelPng, 0x00], "lantern-style.png");
        var wardrobe = await UploadAsync(client, [.. PixelPng, 0x01], "court-wardrobe.png");
        var face = await UploadAsync(client, [.. PixelPng, 0x02], "mara-face.png");

        var updatedResponse = await client.PutAsJsonAsync(
            $"/api/assets/{parent.Id}",
            new UpdateAssetRequest(
                "Curated Mara source",
                null,
                ["curated", "identity"],
                "Keep this exact source metadata."));
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var curatedParent = (await updatedResponse.Content.ReadFromJsonAsync<AssetSummary>())!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/generate-image")
        {
            Content = JsonContent.Create(new GenerateAssetImageRequest(
                "Mara wardrobe refinement",
                "Keep the current person and apply only the approved wardrobe and style.",
                GenerationRoute.FastDraft,
                ReferenceCaptureGenerationAdapter.AdapterId,
                CompositionAssetId: parent.Id,
                ParentAssetId: parent.Id,
                UseCurrentFrame: true,
                ReferenceBindings:
                [
                    new(style.Id, "Style"),
                    new(wardrobe.Id, "Wardrobe"),
                    new(face.Id, "Face")
                ]))
        };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<JobSummary>())!;

        JobSummary? completed = null;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(100);
            var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            completed = snapshot!.Jobs.Single(job => job.Id == queued.Id);
            if (completed.State is JobState.Completed or JobState.Failed) break;
        }

        Assert.NotNull(completed);
        Assert.Equal(JobState.Completed, completed!.State);
        Assert.Equal(parent.Id, completed.OutputAssetId);
        Assert.StartsWith("No change", completed.Phase, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("left unchanged", completed.Error, StringComparison.OrdinalIgnoreCase);

        var context = factory.Services.GetRequiredService<ReferenceCaptureGenerationAdapter>().LastContext;
        Assert.NotNull(context);
        Assert.True(context!.IsCurrentFrameEdit);
        Assert.Collection(context.ReferenceImages,
            styleReference =>
            {
                Assert.Equal(style.Id.ToString(), styleReference.Binding.Id);
                Assert.Equal("Style", styleReference.Binding.Category);
                Assert.True(styleReference.Binding.IsPinned);
                Assert.Equal(0, styleReference.Binding.SourceOrder);
            },
            wardrobeReference =>
            {
                Assert.Equal(wardrobe.Id.ToString(), wardrobeReference.Binding.Id);
                Assert.Equal("Wardrobe", wardrobeReference.Binding.Category);
                Assert.True(wardrobeReference.Binding.IsPinned);
                Assert.Equal(1, wardrobeReference.Binding.SourceOrder);
            },
            faceReference =>
            {
                Assert.Equal(face.Id.ToString(), faceReference.Binding.Id);
                Assert.Equal("Face", faceReference.Binding.Category);
                Assert.True(faceReference.Binding.IsPinned);
                Assert.Equal(2, faceReference.Binding.SourceOrder);
            });

        var assets = (await client.GetFromJsonAsync<List<AssetSummary>>("/api/assets?includeArchived=true"))!;
        var unchangedParent = Assert.Single(assets, asset => asset.Id == parent.Id);
        Assert.Equal(curatedParent.DisplayName, unchangedParent.DisplayName);
        Assert.Equal(curatedParent.Tags, unchangedParent.Tags);
        Assert.Equal(curatedParent.Notes, unchangedParent.Notes);
        Assert.Equal(curatedParent.RevisionFamilyId, unchangedParent.RevisionFamilyId);
        Assert.Equal(curatedParent.RevisionNumber, unchangedParent.RevisionNumber);
        Assert.Equal(curatedParent.IsCurrentRevision, unchangedParent.IsCurrentRevision);
        var revisions = await client.GetFromJsonAsync<List<AssetSummary>>($"/api/assets/{parent.Id}/revisions");
        Assert.Single(revisions!);
    }

    private static async Task<AssetSummary> UploadAsync(HttpClient client, byte[] bytes, string fileName)
    {
        using var form = new MultipartFormDataContent();
        using var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/images") { Content = form };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
    }
}
