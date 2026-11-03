using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// The finished film: every shot's best picture in board order, the editorial
/// mix, fades and soft captions, rendered as a durable job and checked before
/// it enters the library.
/// </summary>
public sealed class FilmExportTests
{
    [Fact]
    public void TheEncoderConformsEveryShotAndCarriesTheMixCaptionsAndFades()
    {
        var plan = new FilmPlan(1920, 1080, 24, true,
            [new("Video", "take.mp4", 120, "SH-010"), new("Still", "frame.png", 120, "SH-020"), new("Black", null, 120, "SH-030")],
            "mix.wav", "captions.srt", Fades: true, NormalizeLoudness: true);
        var loudness = new FfmpegFilmEncoder.LoudnessMeasurement("-23.1", "-4.2", "6.0", "-33.4", "0.3");

        var args = FfmpegFilmEncoder.BuildArguments(plan, loudness, "film.mp4").ToList();
        var joined = string.Join(' ', args);
        var graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.Contains("-i take.mp4", joined);
        Assert.Contains("-loop 1 -framerate 24 -t 5 -i frame.png", joined);
        Assert.Contains("color=c=black:s=1920x1080:r=24", joined);
        Assert.Equal(3, graph.Split("scale=1920:1080:force_original_aspect_ratio=increase:flags=lanczos,crop=1920:1080").Length - 1);
        Assert.Contains("tpad=stop_mode=clone:stop_duration=5,trim=end_frame=120", graph);
        Assert.Contains("[v0][v1][v2]concat=n=3:v=1:a=0,fade=t=in:st=0:d=0.5,fade=t=out:st=14.4:d=0.6[v]", graph);
        Assert.Contains("[3:a]aresample=48000,atrim=0:15,apad=whole_dur=15,loudnorm=I=-16:TP=-1.5:LRA=11:measured_I=-23.1:measured_TP=-4.2:measured_LRA=6.0:measured_thresh=-33.4:offset=0.3:linear=true", graph);
        Assert.Contains("-map 4:s -c:s mov_text", joined);
        Assert.Contains("-frames:v 360", joined);
        Assert.Contains("-x264-params colorprim=bt709:transfer=bt709:colormatrix=bt709:range=tv", joined);
        Assert.EndsWith("film.mp4", joined);
    }

    [Fact]
    public void ASilentUncaptionedFilmGetsASilentTrackAndNoSubtitleOrFade()
    {
        var plan = new FilmPlan(1280, 720, 30, false, [new("Video", "take.mp4", 60, "SH-010")], null, null, Fades: true, NormalizeLoudness: true);

        var joined = string.Join(' ', FfmpegFilmEncoder.BuildArguments(plan, null, "film.mp4"));

        Assert.Contains("-t 2 -i anullsrc=r=48000:cl=stereo", joined);
        Assert.DoesNotContain("mov_text", joined);
        Assert.DoesNotContain("loudnorm", joined);
        // Two seconds is too short to fade in and out without eating the shot.
        Assert.DoesNotContain("fade=", joined);
        Assert.DoesNotContain("bt709", joined);
    }

    [Fact]
    public void LoudnessIsReadFromFfmpegsReportAndSilenceIsNotNormalized()
    {
        const string report = "[Parsed_loudnorm_0 @ 0x1]\n{\n \"input_i\" : \"-21.37\",\n \"input_tp\" : \"-3.10\",\n \"input_lra\" : \"4.20\",\n \"input_thresh\" : \"-31.55\",\n \"target_offset\" : \"0.12\"\n}\n";
        var measured = FfmpegFilmEncoder.ParseLoudness(report);
        Assert.Equal(new FfmpegFilmEncoder.LoudnessMeasurement("-21.37", "-3.10", "4.20", "-31.55", "0.12"), measured);

        Assert.Null(FfmpegFilmEncoder.ParseLoudness(report.Replace("-21.37", "-inf")));
        Assert.Null(FfmpegFilmEncoder.ParseLoudness("no report"));
    }

    [Fact]
    public void CaptionsBecomeNumberedSrtCuesInTimeOrder()
    {
        var srt = FilmCaptions.ToSrt([new(378, 436, "He wants us to go in."), new(18, 95, "There's no wind out here.\r\nThere never is."), new(10, 10, "empty")], 24);

        Assert.Equal("1\n00:00:00,750 --> 00:00:03,958\nThere's no wind out here.\nThere never is.\n\n2\n00:00:15,750 --> 00:00:18,167\nHe wants us to go in.\n\n", srt);
    }

    [Fact]
    public async Task EachShotUsesItsBestPictureAndTheStatusSaysWhich()
    {
        using var factory = Factory(new FakeFilmEncoder());
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);
        var (production, take, still, missing) = await FourShotsAsync(factory, client, project);

        var status = (await client.GetFromJsonAsync<FilmExportStatus>("/api/export/film/status"))!;

        Assert.True(status.CanRender);
        Assert.Equal(480, status.TotalFrames);
        Assert.Equal(["Production", "Take", "Still", "Missing"], status.Shots.Select(shot => shot.Source).ToArray());
        Assert.Equal(production, status.Shots[0].AssetId);
        Assert.Equal("Approved Max take", status.Shots[0].Detail);
        Assert.Equal(take, status.Shots[1].AssetId);
        Assert.Equal("Latest Low video take", status.Shots[1].Detail);
        Assert.Equal(still, status.Shots[2].AssetId);
        Assert.Contains("held for 5 s", status.Shots[2].Detail);
        Assert.Null(status.Shots[3].AssetId);
        Assert.Equal(1, status.ApprovedShots);
        Assert.Contains("2 of 4 shots will hold a still or black", status.Detail);
        _ = missing;
    }

    [Fact]
    public async Task ATakeOfAnEarlierVersionIsNotCutIn()
    {
        using var factory = Factory(new FakeFilmEncoder());
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);
        var still = await AssetUploads.ImageAsync(client, "restyled-still.png");
        var shot = await ShotAsync(client, "SH-030", 120, still.Id);
        await AddTakeAsync(factory, project, shot.Id, (await AssetUploads.VideoAsync(client, "old-style-take.mp4")).Id, "Low", current: false);

        var source = Assert.Single((await client.GetFromJsonAsync<FilmExportStatus>("/api/export/film/status"))!.Shots);

        Assert.Equal("Still", source.Source);
        Assert.Equal(still.Id, source.AssetId);
        Assert.Contains("from an earlier version of the shot", source.Detail);
    }

    [Fact]
    public async Task RenderingFreezesTheCutAndStoresACheckedFilmInTheLibrary()
    {
        var encoder = new FakeFilmEncoder();
        using var factory = Factory(encoder);
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);
        await FourShotsAsync(factory, client, project);
        (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Dialogue, "Remora", 378, 58, 0, 1, null, null, "He wants us to go in."))).EnsureSuccessStatusCode();

        var queued = await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest(Fades: true, Captions: true, NormalizeLoudness: true));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var job = await WaitAsync(client, (await queued.Content.ReadFromJsonAsync<JobSummary>())!.Id);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("Film", job.WorkType);
        var plan = Assert.Single(encoder.Plans);
        Assert.Equal(["Video", "Video", "Still", "Black"], plan.Segments.Select(segment => segment.Kind).ToArray());
        Assert.All(plan.Segments.Where(segment => segment.Kind != "Black"), segment => Assert.Contains(segment.Path!, encoder.ExistedAtEncode));
        Assert.Equal(480, plan.TotalFrames);
        Assert.True(plan.Fades);
        Assert.Null(plan.AudioMixPath);
        Assert.Contains("00:00:15,750 --> 00:00:18,167\nHe wants us to go in.", encoder.Captions);

        var film = (await client.GetFromJsonAsync<AssetSummary[]>("/api/assets"))!.Single(asset => asset.Id == job.OutputAssetId);
        Assert.Equal(AssetKind.Video, film.Kind);
        Assert.Contains("The Impossible Wind film 1 ", film.DisplayName);
        // Nothing about the cut itself changed.
        var shots = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Shots;
        Assert.All(shots, shot => Assert.NotEqual(job.OutputAssetId, shot.CurrentAssetId));

        // Rendering again, even within the same minute, gives a name of its own.
        var again = await WaitAsync(client, (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest())).Content.ReadFromJsonAsync<JobSummary>())!.Id);
        var second = (await client.GetFromJsonAsync<AssetSummary[]>("/api/assets"))!.Single(asset => asset.Id == again.OutputAssetId);
        Assert.Contains("The Impossible Wind film 2 ", second.DisplayName);
    }

    [Fact]
    public async Task AFailedRenderRetriesFromTheSameFrozenPacket()
    {
        var encoder = new FakeFilmEncoder { FailFirst = true };
        using var factory = Factory(encoder);
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);
        await FourShotsAsync(factory, client, project);

        var first = await WaitAsync(client, (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest())).Content.ReadFromJsonAsync<JobSummary>())!.Id);
        Assert.Equal(JobState.Failed, first.State);
        Assert.Contains("Deliberate encoder failure", first.Error);

        var retry = await client.PostAsync($"/api/jobs/{first.Id}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var second = await WaitAsync(client, (await retry.Content.ReadFromJsonAsync<JobSummary>())!.Id);
        Assert.Equal(JobState.Completed, second.State);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(first.Id, second.RetryOfJobId);
        Assert.Equal(encoder.Plans[0].Segments.Select(segment => segment.Kind), encoder.Plans[1].Segments.Select(segment => segment.Kind));
    }

    [Fact]
    public async Task ARenderInterruptedByARestartResumesInAFreshFolderAndClearsTheOldOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-film-restart", Guid.NewGuid().ToString("N"));
        try
        {
            Guid jobId;
            string stray;
            var first = new FakeFilmEncoder();
            using (var factory = new StudioApiFactory(root, deleteDataRoot: false, configureServices: services => { services.RemoveAll<IFilmEncoder>(); services.AddSingleton<IFilmEncoder>(first); }))
            {
                using var client = factory.CreateClient();
                var project = await NewProjectAsync(client);
                await FourShotsAsync(factory, client, project);
                jobId = (await WaitAsync(client, (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest())).Content.ReadFromJsonAsync<JobSummary>())!.Id)).Id;

                // As if the app died mid-encode: the job is still running under
                // an unexpired lease held by a process that has gone, and the
                // attempt's folder, possibly still being written, remains.
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
                var job = await db.Jobs.IgnoreQueryFilters().SingleAsync(item => item.Id == jobId);
                job.State = JobState.Running.ToString(); job.Progress = 40; job.CompletedAt = null; job.OutputAssetId = null;
                job.LeaseOwner = $"{Environment.MachineName}:{await ExitedProcessIdAsync()}:crashed-worker";
                job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4);
                await db.SaveChangesAsync();
                stray = Path.Combine(factory.DataRoot, "exports", $"film-{jobId:N}-interrupted");
                Directory.CreateDirectory(stray);
                await File.WriteAllTextAsync(Path.Combine(stray, "film.mp4"), "half a film");
            }

            var second = new FakeFilmEncoder();
            using (var restarted = new StudioApiFactory(root, deleteDataRoot: false, configureServices: services => { services.RemoveAll<IFilmEncoder>(); services.AddSingleton<IFilmEncoder>(second); }))
            {
                using var client = restarted.CreateClient();
                var resumed = await WaitAsync(client, jobId);

                Assert.Equal(JobState.Completed, resumed.State);
                Assert.NotNull(resumed.OutputAssetId);
                var output = Assert.Single(second.OutputPaths);
                Assert.NotEqual(stray, Path.GetDirectoryName(output));
                Assert.False(Directory.Exists(stray));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ARenderIsRefusedWithoutShotsOrFfmpegAndNeverQueuedTwice()
    {
        var encoder = new FakeFilmEncoder { Ready = false };
        using var factory = new StudioApiFactory(Path.Combine(Path.GetTempPath(), "framewright-film-refusals", Guid.NewGuid().ToString("N")), deleteDataRoot: true,
            startGenerationWorker: false, configureServices: services => { services.RemoveAll<IFilmEncoder>(); services.AddSingleton<IFilmEncoder>(encoder); });
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);

        var empty = await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("Add at least one shot", await empty.Content.ReadAsStringAsync());

        await FourShotsAsync(factory, client, project);
        var noFfmpeg = await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noFfmpeg.StatusCode);
        Assert.Contains("FFmpeg was not found", await noFfmpeg.Content.ReadAsStringAsync());

        encoder.Ready = true;
        var once = (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest())).Content.ReadFromJsonAsync<JobSummary>())!;
        var twice = (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest())).Content.ReadFromJsonAsync<JobSummary>())!;
        Assert.Equal(once.Id, twice.Id);
        Assert.Single((await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs, item => item.WorkType == "Film");
    }

    [FfmpegFact]
    public async Task FfmpegRendersAFilmAtTheExactDeliveryFormatWithSoundAndCaptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-film-real", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new StudioApiFactory(Path.Combine(root, "data"), deleteDataRoot: true,
                videoMediaProbe: new FfprobeVideoMediaProbe(new ConfigurationBuilder().Build()));
            using var client = factory.CreateClient();
            var project = await NewProjectAsync(client, 640, 360);
            // A real 2-second 4:3 take that is shorter than its 3-second shot,
            // a real still, and a real tone placed on the voice lane.
            var takePath = Path.Combine(root, "take.mp4");
            Ffmpeg($"-f lavfi -i testsrc2=size=800x600:rate=30 -t 2 -pix_fmt yuv420p \"{takePath}\"");
            var tonePath = Path.Combine(root, "line.wav");
            Ffmpeg($"-f lavfi -i sine=frequency=440:sample_rate=48000 -t 1.5 \"{tonePath}\"");
            var take = await UploadAsync(client, "/api/assets/media?kind=Video", takePath, "video/mp4");
            var tone = await UploadAsync(client, "/api/assets/media?kind=Audio", tonePath, "audio/wav");
            var still = await PictureAsync(client, "still.png", 640, 360);
            var first = await ShotAsync(client, "SH-A", 72, null);
            await ShotAsync(client, "SH-B", 48, still.Id);
            await AddTakeAsync(factory, project, first.Id, take.Id, "Low");
            (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Dialogue, "Line", 24, 36, 0, 1, tone.Id, null, "A real line."))).EnsureSuccessStatusCode();

            var queued = await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest());
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var job = await WaitAsync(client, (await queued.Content.ReadFromJsonAsync<JobSummary>())!.Id, TimeSpan.FromMinutes(2));
            Assert.True(job.State == JobState.Completed, job.Error);

            var film = Path.Combine(root, "film.mp4");
            await File.WriteAllBytesAsync(film, await client.GetByteArrayAsync(job.OutputAssetUrl));
            using var probe = JsonDocument.Parse(Run("ffprobe", $"-v error -count_frames -show_streams -of json \"{film}\""));
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
            Assert.Equal(640, video.GetProperty("width").GetInt32());
            Assert.Equal(360, video.GetProperty("height").GetInt32());
            Assert.Equal("24/1", video.GetProperty("r_frame_rate").GetString());
            Assert.Equal("120", video.GetProperty("nb_read_frames").GetString());
            Assert.Equal("bt709", video.GetProperty("color_primaries").GetString());
            Assert.Contains(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
            Assert.Contains(streams, stream => stream.GetProperty("codec_type").GetString() == "subtitle" && stream.GetProperty("codec_name").GetString() == "mov_text");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnEffectsCueIsItsOwnLaneStaysOutOfCaptionsAndNeedsSoundForProduction()
    {
        var encoder = new FakeFilmEncoder();
        using var factory = Factory(encoder);
        using var client = factory.CreateClient();
        var project = await NewProjectAsync(client);
        await FourShotsAsync(factory, client, project);
        (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Dialogue, "Remora", 378, 58, 0, 1, null, null, "He wants us to go in."))).EnsureSuccessStatusCode();
        var placed = await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Effects, "Hauler fire", 120, 48, 0, 1, null, null, "Whoomph, then crackling flames."));
        Assert.True(placed.IsSuccessStatusCode, await placed.Content.ReadAsStringAsync());

        var effect = Assert.Single((await client.GetFromJsonAsync<TimelineClipSummary[]>("/api/timeline/clips"))!, clip => clip.Label == "Hauler fire");
        Assert.Equal(TimelineTrackKind.Effects, effect.Track);
        // A sound-design cue is not dialogue: it never becomes a subtitle.
        var job = await WaitAsync(client, (await (await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest(Captions: true))).Content.ReadFromJsonAsync<JobSummary>())!.Id);
        Assert.Equal(JobState.Completed, job.State);
        Assert.Contains("He wants us to go in.", encoder.Captions);
        Assert.DoesNotContain("Whoomph", encoder.Captions);
        // Like a music cue, a placed effect with no sound is unfinished delivery.
        var readiness = (await client.GetFromJsonAsync<ProductionExportReadiness>("/api/export/production/status"))!;
        Assert.Contains(readiness.Blockers, blocker => blocker.Contains("Hauler fire", StringComparison.Ordinal));
    }

    [FfmpegFact]
    public async Task FfmpegMixesAnEffectIntoTheFilmAtItsFrame()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-film-effect", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new StudioApiFactory(Path.Combine(root, "data"), deleteDataRoot: true,
                videoMediaProbe: new FfprobeVideoMediaProbe(new ConfigurationBuilder().Build()));
            using var client = factory.CreateClient();
            await NewProjectAsync(client, 320, 180);
            await ShotAsync(client, "SH-A", 48, (await PictureAsync(client, "still.png", 320, 180)).Id);
            var tonePath = Path.Combine(root, "whoomph.wav");
            Ffmpeg($"-f lavfi -i sine=frequency=1000:sample_rate=48000 -t 0.5 \"{tonePath}\"");
            var tone = await UploadAsync(client, "/api/assets/media?kind=Audio", tonePath, "audio/wav");
            (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Effects, "Whoomph", 24, 12, 0, 1, tone.Id, null, "The hauler goes up."))).EnsureSuccessStatusCode();

            var queued = await client.PostAsJsonAsync("/api/export/film", new FilmExportRequest(Fades: false, Captions: true, NormalizeLoudness: false));
            var job = await WaitAsync(client, (await queued.Content.ReadFromJsonAsync<JobSummary>())!.Id, TimeSpan.FromMinutes(2));
            Assert.True(job.State == JobState.Completed, job.Error);

            var film = Path.Combine(root, "film.mp4");
            await File.WriteAllBytesAsync(film, await client.GetByteArrayAsync(job.OutputAssetUrl));
            // Frame 24 at 24 fps: silence for the first second, the effect for the next half.
            Assert.True(Level(film, 0.1, 0.8) < -60, "The film should be silent before the effect's frame.");
            Assert.True(Level(film, 1.05, 1.45) > -30, "The effect should play from its frame.");
            using var probe = JsonDocument.Parse(Run("ffprobe", $"-v error -show_streams -of json \"{film}\""));
            Assert.DoesNotContain(probe.RootElement.GetProperty("streams").EnumerateArray(), stream => stream.GetProperty("codec_type").GetString() == "subtitle");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task FfmpegMixStaysStereoWhenAMonoLineStartsFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "framewright-mix-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new StudioApiFactory(Path.Combine(root, "data"), deleteDataRoot: true);
            using var client = factory.CreateClient();
            await NewProjectAsync(client, 320, 180);
            await ShotAsync(client, "SH-A", 48, (await PictureAsync(client, "still.png", 320, 180)).Id);
            // A mono line at frame 0, then a stereo gust heard only on the left from frame 24.
            var linePath = Path.Combine(root, "line.wav");
            Ffmpeg($"-f lavfi -i sine=frequency=440:sample_rate=48000 -t 0.5 -ac 1 \"{linePath}\"");
            var gustPath = Path.Combine(root, "gust.wav");
            Ffmpeg($"-f lavfi -i sine=frequency=1000:sample_rate=48000 -t 0.5 -af \"pan=stereo|c0=c0|c1=0*c0\" \"{gustPath}\"");
            var line = await UploadAsync(client, "/api/assets/media?kind=Audio", linePath, "audio/wav");
            var gust = await UploadAsync(client, "/api/assets/media?kind=Audio", gustPath, "audio/wav");
            (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Dialogue, "Mostly", 0, 12, 0, 1, line.Id, null, "Mostly."))).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/timeline/clips", new SaveTimelineClipRequest(TimelineTrackKind.Effects, "Gust", 24, 12, 0, 1, gust.Id, null, "Wind from the left."))).EnsureSuccessStatusCode();

            var mix = Path.Combine(root, "mix.wav");
            await File.WriteAllBytesAsync(mix, await client.GetByteArrayAsync("/api/export/audio-mix"));
            var source = ChannelLevels(linePath, 0.1, 0.4).Single();
            var lineLevels = ChannelLevels(mix, 0.1, 0.4);
            var gustLevels = ChannelLevels(mix, 1.1, 1.4);
            // Starting first does not fold the mix to mono: the line plays centred, 3 dB down in each channel,
            Assert.Equal(2, lineLevels.Length);
            Assert.All(lineLevels, level => Assert.InRange(level, source - 3.5, source - 2.5));
            // and the stereo gust keeps its side.
            Assert.True(gustLevels[0] > -30, $"The gust should play on the left; it measured {gustLevels[0]:0.0} dB.");
            Assert.True(gustLevels[1] < -60, $"The gust should stay off the right; it measured {gustLevels[1]:0.0} dB.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>RMS level in dBFS of a film's audio between two times; -inf (silence) reads as -200.</summary>
    private static double Level(string film, double from, double to)
        => ParseDb(Astats(film, from, to).Last(text => text.Contains("RMS level dB:", StringComparison.Ordinal)));

    /// <summary>RMS level in dBFS of each channel between two times.</summary>
    private static double[] ChannelLevels(string media, double from, double to)
    {
        var levels = new List<double>();
        var inChannel = false;
        foreach (var text in Astats(media, from, to))
        {
            if (text.Contains("] Channel: ", StringComparison.Ordinal)) inChannel = true;
            else if (text.Contains("] Overall", StringComparison.Ordinal)) inChannel = false;
            else if (inChannel && text.Contains("RMS level dB:", StringComparison.Ordinal)) levels.Add(ParseDb(text));
        }
        return [.. levels];
    }

    private static string[] Astats(string media, double from, double to)
    {
        using var process = Process.Start(new ProcessStartInfo("ffmpeg", $"-v info -i \"{media}\" -map 0:a -af atrim={from.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{to.ToString(System.Globalization.CultureInfo.InvariantCulture)},astats=metadata=0 -f null -")
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
        var report = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return report.Split('\n');
    }

    private static double ParseDb(string line)
    {
        var value = line[(line.IndexOf("RMS level dB:", StringComparison.Ordinal) + 13)..].Trim();
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var level) ? level : -200;
    }

    /// <summary>The id of a process that has already exited, as a crashed app's would be.</summary>
    private static async Task<int> ExitedProcessIdAsync()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.Id;
    }

    private static StudioApiFactory Factory(FakeFilmEncoder encoder)
        => new(services => { services.RemoveAll<IFilmEncoder>(); services.AddSingleton<IFilmEncoder>(encoder); });

    private static async Task<ProjectSummary> NewProjectAsync(HttpClient client, int width = 1920, int height = 1080)
    {
        var created = (await (await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Film {Guid.NewGuid():N}", production = "Film", sequenceCode = "SQ-09", sequenceName = "The Impossible Wind",
            framesPerSecond = 24, aspectRatio = "16:9", deliveryWidth = width, deliveryHeight = height,
        })).Content.ReadFromJsonAsync<ProjectSummary>())!;
        (await client.PostAsync($"/api/projects/{created.Id}/activate", null)).EnsureSuccessStatusCode();
        return created;
    }

    /// <summary>Four 5-second shots: an approved Max take, a Low review take, a
    /// still only, and nothing at all.</summary>
    private static async Task<(Guid Production, Guid Take, Guid Still, Guid Missing)> FourShotsAsync(StudioApiFactory factory, HttpClient client, ProjectSummary project)
    {
        var productionVideo = await AssetUploads.VideoAsync(client, "max-take.mp4");
        var takeVideo = await AssetUploads.VideoAsync(client, "low-take.mp4");
        var still = await AssetUploads.ImageAsync(client, "held-still.png");
        var a = await ShotAsync(client, "SH-010", 120, (await AssetUploads.ImageAsync(client, "a-still.png")).Id);
        var b = await ShotAsync(client, "SH-020", 120, (await AssetUploads.ImageAsync(client, "b-still.png")).Id);
        await ShotAsync(client, "SH-030", 120, still.Id);
        var d = await ShotAsync(client, "SH-040", 120, null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(project.Id);
            var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
            var shot = await db.Shots.SingleAsync(row => row.Id == a.Id);
            shot.ProductionVideoAssetId = productionVideo.Id;
            shot.Approval = nameof(ApprovalState.Ratified);
            await db.SaveChangesAsync();
        }
        await AddTakeAsync(factory, project, b.Id, takeVideo.Id, "Low");
        return (productionVideo.Id, takeVideo.Id, still.Id, d.Id);
    }

    /// <summary>A completed take; when current, it becomes the shot's live head as a real completion does.</summary>
    private static async Task AddTakeAsync(StudioApiFactory factory, ProjectSummary project, Guid shotId, Guid videoId, string quality, bool current = true)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProjectScope>().Bind(project.Id);
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var shot = await db.Shots.SingleAsync(row => row.Id == shotId);
        var manifestId = Guid.NewGuid();
        db.GenerationManifests.Add(new GenerationManifestRecord
        {
            Id = manifestId, ShotId = shotId, ShotCode = shot.Code, ShotVersion = shot.Version, SketchId = Guid.Empty, SketchRevision = 0,
            Route = nameof(GenerationRoute.FastDraft), Purpose = nameof(GenerationPurpose.Video), State = nameof(ManifestState.Completed),
            CreativeBrief = "A take.", AuthoritiesJson = "[]", ConstraintsJson = "[]", ManifestJson = $"{{\"videoQuality\":\"{quality}\"}}",
            ManifestHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow
        });
        db.Jobs.Add(new JobRecord
        {
            Id = Guid.NewGuid(), ShotId = shotId, ShotCode = shot.Code, Kind = "Video", WorkType = "Shot", State = nameof(JobState.Completed),
            Progress = 100, Phase = "Candidate ready for review", Backend = "Test", CreatedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            ManifestId = manifestId, AdapterId = "test", OutputAssetId = videoId
        });
        if (current) (await db.CandidateVersions.SingleAsync(candidate => candidate.ShotId == shotId && candidate.IsCurrent)).SourceManifestId = manifestId;
        await db.SaveChangesAsync();
    }

    private static async Task<ShotSummary> ShotAsync(HttpClient client, string code, int frames, Guid? imageId)
        => (await (await client.PostAsJsonAsync("/api/shots", new CreateShotRequest(code, code, "A shot.", frames, "35mm", "Hold.", [], [], imageId)))
            .Content.ReadFromJsonAsync<ShotSummary>())!;

    private static async Task<JobSummary> WaitAsync(HttpClient client, Guid jobId, TimeSpan? limit = null)
    {
        var deadline = DateTimeOffset.UtcNow + (limit ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = (await client.GetFromJsonAsync<StudioSnapshot>("/api/studio"))!.Jobs.Single(item => item.Id == jobId);
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled || DateTimeOffset.UtcNow > deadline) return job;
            await Task.Delay(100);
        }
    }

    private static async Task<AssetSummary> UploadAsync(HttpClient client, string url, string path, string mimeType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(file, "file", Path.GetFileName(path));
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-Storyboard-Studio", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AssetSummary>())!;
    }

    private static async Task<AssetSummary> PictureAsync(HttpClient client, string name, int width, int height)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        await File.WriteAllBytesAsync(path, ProofPngRenderer.Render(new string('a', 64), width, height));
        try { return await UploadAsync(client, "/api/assets/images", path, "image/png"); }
        finally { File.Delete(path); }
    }

    private static void Ffmpeg(string arguments) => Run("ffmpeg", "-y -loglevel error " + arguments);

    private static string Run(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} failed: {error}");
        return output.Result;
    }

    internal sealed class FakeFilmEncoder : IFilmEncoder
    {
        public bool Ready { get; set; } = true;
        public bool FailFirst { get; set; }
        public List<FilmPlan> Plans { get; } = [];
        public HashSet<string> ExistedAtEncode { get; } = [];
        public List<string> OutputPaths { get; } = [];
        public string Captions { get; private set; } = "";

        public FilmEncoderReadiness Inspect() => Ready ? new(true, "Test encoder ready.") : new(false, "FFmpeg was not found. Install it to render a film.");

        public async Task<FilmEncodeResult> EncodeAsync(FilmPlan plan, string outputPath, Func<double, Task> progress, CancellationToken cancellationToken)
        {
            lock (Plans) { Plans.Add(plan); OutputPaths.Add(outputPath); }
            foreach (var segment in plan.Segments.Where(segment => segment.Path is not null && File.Exists(segment.Path))) ExistedAtEncode.Add(segment.Path!);
            if (plan.CaptionsPath is not null) Captions = await File.ReadAllTextAsync(plan.CaptionsPath, cancellationToken);
            await progress(.5);
            if (FailFirst && Plans.Count == 1) return new(false, "Deliberate encoder failure.");
            var mp4 = new byte[64];
            "ftyp"u8.CopyTo(mp4.AsSpan(4));
            Guid.NewGuid().ToByteArray().CopyTo(mp4.AsSpan(32));
            await File.WriteAllBytesAsync(outputPath, mp4, cancellationToken);
            return new(true, "Test film written.");
        }
    }
}

/// <summary>Runs only where FFmpeg and FFprobe are installed; elsewhere it is
/// reported as skipped, never as passed.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string tool) => path.Any(directory => File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? $"{tool}.exe" : tool)));
        if (!Has("ffmpeg") || !Has("ffprobe")) Skip = "FFmpeg and FFprobe are not on PATH.";
    }
}
