using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The studio's Content Security Policy is deliberately tight, and one of its
/// directives is load-bearing for 3D in a way nothing else tests: glTF carries
/// its textures inside the file, and the browser's loader hands those bytes to
/// itself as blob URLs and fetches them. A policy that forbids connecting to a
/// blob does not fail loudly — every model simply renders untextured, which
/// reads as a broken asset rather than a blocked request.
/// </summary>
public sealed class SecurityHeaderTests
{
    [Fact]
    public async Task ThePolicyLetsTheBrowserReadTexturesOutOfAModelItAlreadyHas()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var policy = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));

        // A glTF's embedded textures are fetched from blob URLs the page made
        // itself. Without this the model loads and renders white.
        Assert.Contains("connect-src 'self' blob:", policy, StringComparison.Ordinal);
        // The same bytes are also used directly as images and media.
        Assert.Contains("img-src 'self' blob: data:", policy, StringComparison.Ordinal);
        Assert.Contains("media-src 'self' blob:", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePolicyStillRefusesToReachAnywhereElse()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        var policy = string.Join(" ", response.Headers.GetValues("Content-Security-Policy"));

        // Allowing a blob is not allowing the network: scripts, styles and
        // everything else stay on this origin.
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("connect-src 'self' blob: http", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoredModelIsServedAsSomethingTheBrowserWillLoad()
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(ModelFixtures.RiggedFigure());
        file.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(file, "file", "textured-check.glb");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/assets/models") { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        var imported = await client.SendAsync(request);
        imported.EnsureSuccessStatusCode();
        using var asset = await imported.Content.ReadFromJsonAsync<JsonDocument>() ?? throw new InvalidOperationException();

        var served = await client.GetAsync($"/api/assets/{asset.RootElement.GetProperty("id").GetGuid()}/content");

        served.EnsureSuccessStatusCode();
        Assert.Equal("model/gltf-binary", served.Content.Headers.ContentType?.MediaType);
        Assert.True(served.Content.Headers.ContentLength > 0);
    }
}
