using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Net.Http.Json;

namespace StoryboardStudio.Api.Tests;

public sealed class AudioProjectQueueTests
{
    [Fact]
    public async Task IdenticalMusicRequestsInTwoProjectsAreDistinctDurableJobs()
    {
        using var factory = new PausedAudioQueueFactory();
        using var client = factory.CreateClient();
        var firstSnapshot = await client.GetFromJsonAsync<StudioSnapshot>("/api/studio");
        Assert.NotNull(firstSnapshot);
        var firstComposition = await CreateCompositionAsync(client, "First chamber cue");
        var firstResponse = await PostMusicAsync(client, firstComposition.CurrentRevisionId);
        var firstJob = await firstResponse.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.NotNull(firstJob);

        var create = await client.PostAsJsonAsync("/api/projects", new CreateProjectRequest(
            "Second audio project", "Queue isolation", "AUD-02", "Second sequence",
            25, "16:9", 1920, 1080));
        var secondProject = await create.Content.ReadFromJsonAsync<ProjectSummary>();
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.NotNull(secondProject);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/projects/{secondProject.Id}/activate", null)).StatusCode);

        var secondComposition = await CreateCompositionAsync(client, "Second chamber cue");
        var secondResponse = await PostMusicAsync(client, secondComposition.CurrentRevisionId);
        var secondJob = await secondResponse.Content.ReadFromJsonAsync<JobSummary>();
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.NotNull(secondJob);
        Assert.NotEqual(firstJob.Id, secondJob.Id);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var jobs = await db.Jobs.IgnoreQueryFilters()
            .Where(job => job.Id == firstJob.Id || job.Id == secondJob.Id)
            .ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, job => job.ProjectId == firstSnapshot.Project.Id &&
            job.IdempotencyKey!.StartsWith($"project:{firstSnapshot.Project.Id:N}:", StringComparison.Ordinal));
        Assert.Contains(jobs, job => job.ProjectId == secondProject.Id &&
            job.IdempotencyKey!.StartsWith($"project:{secondProject.Id:N}:", StringComparison.Ordinal));
        Assert.NotEqual(jobs[0].IdempotencyKey, jobs[1].IdempotencyKey);
    }

    private static async Task<MusicCompositionSummary> CreateCompositionAsync(HttpClient client, string title)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/music/compositions")
        {
            Content = JsonContent.Create(new CreateMusicCompositionRequest(
                title,
                "A restrained arcane chamber cue.",
                "restrained chamber strings",
                "[Instrumental]",
                90,
                "4/4",
                "D minor",
                [new MusicSection("intro", "instrumental", 8, "[Instrumental]", ["Dm", "Bb", "F", "C"], "Low rising phrase")],
                "Small strings and low percussion",
                "X:1\nT:Chamber Cue\nM:4/4\nL:1/8\nQ:1/4=90\nK:Dm\nV:Vocal\nV:Ins\n[V:Vocal] z8 | z8 |\n[V:Ins] D2 F2 A2 d2 | B2 A2 F2 E2 |\n"))
        };
        message.Headers.Add("X-Storyboard-Studio", "1");
        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MusicCompositionSummary>())!;
    }

    private static async Task<HttpResponseMessage> PostMusicAsync(HttpClient client, Guid revisionId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/music/revisions/{revisionId}/render");
        message.Headers.Add("X-Storyboard-Studio", "1");
        return await client.SendAsync(message);
    }

    private sealed class PausedAudioQueueFactory : StudioApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["YuE2:Enabled"] = "true",
                    ["YuE2:Endpoint"] = "http://127.0.0.1:5182"
                }));
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("yue2-health")
                    .ConfigurePrimaryHttpMessageHandler(() => new HealthHandler());
                // Keep the two jobs queued long enough to prove database-level
                // project isolation; no provider should run in this contract test.
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                        descriptor.ImplementationType == typeof(GenerationJobWorker))
                    .ToArray();
                foreach (var worker in workers) services.Remove(worker);
            });
        }

        private sealed class HealthHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        ready = true,
                        detail = "test worker ready",
                        model = "m-a-p/YuE2-3B",
                        device = "cuda"
                    })
                });
        }
    }
}
