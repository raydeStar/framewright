using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class AnimationExportApiTests
{
    private sealed class RecordingCompiler : ICompilerGateway
    {
        public List<string[]> Selections { get; } = [];

        public Task<CompilerCapabilities> DescribeAsync(CancellationToken token) =>
            Task.FromResult(CompilerCapabilities.Absent("Not needed for this test."));

        public Task<CompilerStageRun> RunStageAsync(string stage, string sourcePath,
            string outputPath, string reportPath, CancellationToken token,
            IEnumerable<KeyValuePair<string, string>>? options = null) =>
            throw new NotSupportedException();

        public Task<CompilerAnimationExport> ExportAnimationsAsync(string sourcePath,
            IReadOnlyList<string> clips, CancellationToken token)
        {
            Assert.True(File.Exists(sourcePath));
            Selections.Add([.. clips]);
            return Task.FromResult(new CompilerAnimationExport(
                Encoding.UTF8.GetBytes(string.Join(",", clips)), null));
        }
    }

    [Fact]
    public async Task ExportPassesOnlyValidatedClipNamesToCompilerWithoutChangingTheLibraryAsset()
    {
        var compiler = new RecordingCompiler();
        using var factory = new StudioApiFactory(services => services.AddSingleton<ICompilerGateway>(compiler));
        using var client = factory.CreateClient();
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(ModelFixtures.ClipArmRaise());
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        form.Add(file, "file", "two-clips.glb");
        using var import = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = form };
        import.Headers.Add("X-Storyboard-Studio", "1");
        var imported = await client.SendAsync(import);
        imported.EnsureSuccessStatusCode();
        using var record = await imported.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();
        var id = record.RootElement.GetProperty("id").GetGuid();
        var original = await client.GetByteArrayAsync($"/api/assets/{id}/content");

        var all = await client.GetAsync($"/api/assets/{id}/animation-export?clip=Arm%20raise&clip=Step%20forward");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Equal("Arm raise,Step forward", await all.Content.ReadAsStringAsync());
        var none = await client.GetAsync($"/api/assets/{id}/animation-export");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal("", await none.Content.ReadAsStringAsync());
        var one = await client.GetAsync($"/api/assets/{id}/animation-export?clip=Step%20forward");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal("Step forward", await one.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/assets/{id}/animation-export?clip=Ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/assets/{id}/animation-export?clip=Arm%20raise&clip=Arm%20raise")).StatusCode);
        Assert.Equal(3, compiler.Selections.Count);
        Assert.Equal(original, await client.GetByteArrayAsync($"/api/assets/{id}/content"));
    }
}
