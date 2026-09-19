using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace StoryboardStudio.Api.Tests;

public sealed class VoiceSynthesisContractTests : IDisposable
{
    private readonly SpeechFactory factory = new();

    [Fact]
    public async Task SpeechRouteUsesServerAuthAndAttachesWAVToExactSavedClip()
    {
        var client = factory.CreateClient();
        var sample = new byte[44]; "RIFF"u8.CopyTo(sample); "WAVE"u8.CopyTo(sample.AsSpan(8));
        using var form = new MultipartFormDataContent();
        var sampleContent = new ByteArrayContent(sample); sampleContent.Headers.ContentType = new("audio/wav");
        form.Add(sampleContent, "file", "ennix-approved.wav");
        using var sampleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/assets/media?kind=Audio") { Content = form };
        sampleRequest.Headers.Add("X-Storyboard-Studio", "1");
        var sampleResponse = await client.SendAsync(sampleRequest);
        var sampleAsset = await sampleResponse.Content.ReadFromJsonAsync<AssetSummary>();
        Assert.NotNull(sampleAsset);
        var profileResponse = await client.PostAsJsonAsync("/api/voice-profiles", new CreateVoiceProfileRequest(
            "Ennix OpenAI preset", VoiceProfileKind.ProviderPreset, "OpenAI", "voice-contract", "Ennix", false, "", "ennix", sampleAsset!.Id));
        var profile = await profileResponse.Content.ReadFromJsonAsync<VoiceProfileSummary>();
        Assert.NotNull(profile);
        var clipResponse = await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(
            TimelineTrackKind.Voice, "Ennix line", 12, 72, 0, .9, null, profile.Id, "The bridge remembers.", null));
        var clip = await clipResponse.Content.ReadFromJsonAsync<TimelineClipSummary>();
        Assert.NotNull(clip);

        var response = await client.PostAsJsonAsync($"/api/timeline/clips/{clip.Id}/synthesize", new SynthesizeVoiceRequest(clip.UpdatedAt));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = await response.Content.ReadFromJsonAsync<JobSummary>();
        Assert.NotNull(queued);
        Assert.Equal("Voice", queued.WorkType);

        JobSummary? completed = queued;
        for (var attempt = 0; attempt < 100 && completed.State is JobState.Queued or JobState.Running; attempt++)
        {
            await Task.Delay(50);
            var snapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
            completed = snapshot!.Jobs.Single(job => job.Id == queued.Id);
        }

        Assert.Equal(JobState.Completed, completed!.State);
        Assert.NotNull(completed.OutputAssetId);
        Assert.Equal("req_speech_contract", completed.ProviderRequestId);
        var clips = await client.GetFromJsonAsync<List<TimelineClipSummary>>("/api/timeline/clips");
        var synthesized = Assert.Single(clips!, item => item.Id == clip.Id);
        Assert.Equal(completed.OutputAssetId, synthesized.AssetId);
        Assert.Equal("Bearer", factory.Handler.Scheme);
        Assert.Equal("sk-speech-contract-test-only", factory.Handler.Credential);
        Assert.Contains("tts-1-hd", factory.Handler.Body);
        Assert.Contains("voice-contract", factory.Handler.Body);
        Assert.Contains("The bridge remembers.", factory.Handler.Body);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(synthesized.AssetUrl)).StatusCode);
    }

    public void Dispose() => factory.Dispose();

    private sealed class SpeechFactory : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "storyboard-speech-contract", Guid.NewGuid().ToString("N"));
        public CaptureHandler Handler { get; } = new();
        public SpeechFactory() => Directory.CreateDirectory(root);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Studio:DataRoot"] = root,
                ["Studio:AssetRoot"] = Path.Combine(root, "assets"),
                ["Integrations:OpenAI:SpeechSubmissionEnabled"] = "true",
                ["Integrations:OpenAI:AllowCustomEndpoint"] = "true",
                ["Integrations:OpenAI:SpeechModel"] = "tts-1-hd",
                ["Integrations:OpenAI:SpeechEndpoint"] = "https://api.openai.test/v1/audio/speech",
                ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:1"
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<StudioDbContext>(); services.RemoveAll<DbContextOptions<StudioDbContext>>(); services.RemoveAll<IProviderCredentialStore>();
                services.AddDbContext<StudioDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False"));
                services.AddSingleton<IProviderCredentialStore>(new FakeCredentials());
                services.AddHttpClient("openai-generation").ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Scheme { get; private set; }
        public string? Credential { get; private set; }
        public string Body { get; private set; } = "";
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Scheme = request.Headers.Authorization?.Scheme; Credential = request.Headers.Authorization?.Parameter; Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var wav = new byte[44]; Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(wav) };
            response.Content.Headers.ContentType = new("audio/wav"); response.Headers.Add("x-request-id", "req_speech_contract"); return response;
        }
    }

    private sealed class FakeCredentials : IProviderCredentialStore
    {
        public string? GetOpenAiApiKey() => "sk-speech-contract-test-only";
        public CredentialStatus GetOpenAiStatus() => new(true, "Contract test", false, "Configured for fake HTTP only.");
        public void SaveOpenAiApiKey(string apiKey) => throw new NotSupportedException();
        public void DeleteOpenAiApiKey() => throw new NotSupportedException();
    }
}
