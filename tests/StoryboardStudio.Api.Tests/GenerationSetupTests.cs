using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The generation switches in Production setup replace hand-editing JSON. They
/// must change what the engines report, survive a restart, stay off by default,
/// never override what the launcher pinned, and be changeable only from the
/// workstation itself.
/// </summary>
public sealed class GenerationSetupTests
{
    [Fact]
    public async Task SwitchesStartOffAndSavingThemChangesWhatEnginesReportAcrossARestart()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-generation-setup", Guid.NewGuid().ToString("N"));
        try
        {
            using (var factory = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = factory.CreateClient())
            {
                var initial = await client.GetFromJsonAsync<GenerationSetupSummary>("/api/setup/generation") ?? throw new InvalidOperationException();
                Assert.True(initial.CanManageHere);
                Assert.False(initial.ComfyUi.ImagesEnabled);
                Assert.False(initial.ComfyUi.VideoEnabled);
                Assert.False(initial.Codex.OneClickImages);
                Assert.Equal("Protected", await AdapterStateAsync(client, "comfyui-fast-draft"));

                using var save = Put(new GenerationSetupChange("http://127.0.0.1:8188/", true, false, false));
                var saved = await client.SendAsync(save);
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var summary = await saved.Content.ReadFromJsonAsync<GenerationSetupSummary>() ?? throw new InvalidOperationException();
                Assert.True(summary.ComfyUi.ImagesEnabled);
                Assert.Equal("http://127.0.0.1:8188", summary.ComfyUi.Endpoint);
                // The switch alone moves the engine out of "turned off"; it
                // is a permission, and nothing has been sent anywhere.
                Assert.NotEqual("Protected", await AdapterStateAsync(client, "comfyui-fast-draft"));
                Assert.Empty((await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs);
            }

            using (var restarted = new StudioApiFactory(dataRoot, deleteDataRoot: false))
            using (var client = restarted.CreateClient())
            {
                var reread = await client.GetFromJsonAsync<GenerationSetupSummary>("/api/setup/generation") ?? throw new InvalidOperationException();
                Assert.True(reread.ComfyUi.ImagesEnabled);
                Assert.Equal("http://127.0.0.1:8188", reread.ComfyUi.Endpoint);
            }
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ChangesNeedTheStudioHeaderAndALocalComfyUiAddress()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var withoutHeader = await client.PutAsJsonAsync("/api/setup/generation", new GenerationSetupChange("http://127.0.0.1:8188", true, true, true));
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);

        using var remote = Put(new GenerationSetupChange("http://192.168.1.40:8188", true, false, false));
        var refused = await client.SendAsync(remote);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("must run on this computer", await refused.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<GenerationSetupSummary>("/api/setup/generation") ?? throw new InvalidOperationException();
        Assert.False(after.ComfyUi.ImagesEnabled);
        Assert.False(after.Codex.OneClickImages);
    }

    [Fact]
    public async Task APairedTabletCanSeeButNotChangeOrProbe()
    {
        using var baseFactory = new StudioApiFactory();
        using var remoteFactory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:AllowLan"] = "true" }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddress(IPAddress.Parse("172.31.240.77"))));
        });
        using var tablet = remoteFactory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        var code = remoteFactory.Services.GetRequiredService<PairingService>().Status(loopback: true, paired: false).PairingCode;
        Assert.Equal(HttpStatusCode.OK, (await tablet.PostAsJsonAsync("/api/pairing/claim", new PairingClaimRequest(code!))).StatusCode);

        var seen = await tablet.GetFromJsonAsync<GenerationSetupSummary>("/api/setup/generation") ?? throw new InvalidOperationException();
        Assert.False(seen.CanManageHere);
        using var change = Put(new GenerationSetupChange("http://127.0.0.1:8188", true, true, true));
        Assert.Equal(HttpStatusCode.Forbidden, (await tablet.SendAsync(change)).StatusCode);
        using var probe = new HttpRequestMessage(HttpMethod.Post, "/api/setup/comfyui/test") { Content = JsonContent.Create(new ComfyUiConnectionTestRequest("http://127.0.0.1:8188")) };
        probe.Headers.Add("X-Storyboard-Studio", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await tablet.SendAsync(probe)).StatusCode);
    }

    [Fact]
    public async Task TheConnectionTestExplainsAnAddressThatDoesNotAnswer()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        using var probe = new HttpRequestMessage(HttpMethod.Post, "/api/setup/comfyui/test") { Content = JsonContent.Create(new ComfyUiConnectionTestRequest("http://127.0.0.1:1")) };
        probe.Headers.Add("X-Storyboard-Studio", "1");
        var result = await (await client.SendAsync(probe)).Content.ReadFromJsonAsync<ComfyUiConnectionTest>() ?? throw new InvalidOperationException();
        Assert.False(result.Reachable);
        Assert.Contains("Nothing answered", result.Detail);
    }

    [Fact]
    public void ALauncherPinnedValueWinsOverTheSavedSwitchAndIsReportedLocked()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-generation-lock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:DataRoot"] = dataRoot })
                .AddCommandLine(["--Integrations:Codex:NonInteractiveImageEnabled=false"])
                .Build();
            var store = new GenerationSettingsStore(configuration, new TestEnvironment(dataRoot));
            Assert.Null(store.Save(new GenerationSetupChange("http://127.0.0.1:8188", true, false, true)));

            Assert.True(store.ComfyUiImagesEnabled);
            Assert.False(store.CodexOneClickImages);
            var described = store.Describe(true);
            Assert.Contains("oneClickImages", described.Codex.Locked);
            Assert.Empty(described.ComfyUi.Locked);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void AnUnreadableSettingsFileFailsClosed()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "framewright-generation-corrupt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            File.WriteAllText(Path.Combine(dataRoot, "generation-settings.json"), "{ \"comfyUiImagesEnabled\": tr");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Studio:DataRoot"] = dataRoot })
                .Build();
            var store = new GenerationSettingsStore(configuration, new TestEnvironment(dataRoot));
            Assert.False(store.ComfyUiImagesEnabled);
            Assert.False(store.ComfyUiVideoEnabled);
            Assert.False(store.CodexOneClickImages);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    private static HttpRequestMessage Put(GenerationSetupChange change)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/setup/generation") { Content = JsonContent.Create(change) };
        request.Headers.Add("X-Storyboard-Studio", "1");
        return request;
    }

    private static async Task<string> AdapterStateAsync(HttpClient client, string adapterId)
    {
        var adapters = await client.GetFromJsonAsync<GenerationAdapterSummary[]>("/api/generation/adapters") ?? throw new InvalidOperationException();
        return adapters.Single(adapter => adapter.Id == adapterId).State;
    }

    private sealed class RemoteAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continueRequest) => { context.Connection.RemoteIpAddress = address; await continueRequest(); });
            next(application);
        };
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Framewright";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
