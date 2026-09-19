using Microsoft.Extensions.Configuration;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;
using StoryboardStudio.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

[CollectionDefinition("Provider adapter environment", DisableParallelization = true)]
public sealed class ProviderAdapterEnvironmentTestGroup;

[Collection("Provider adapter environment")]
public sealed class GenerationAdapterContractTests : IDisposable
{
    private static readonly byte[] PixelPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    private readonly string root = Path.Combine(Path.GetTempPath(), "storyboard-adapter-contracts", Guid.NewGuid().ToString("N"));

    public GenerationAdapterContractTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task OpenAIAdapterUsesServerBearerAuthAndTheImageEditContract()
    {
        var imagePath = Path.Combine(root, "composition.png");
        var stylePath = Path.Combine(root, "openai-style.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        await File.WriteAllBytesAsync(stylePath, PixelPng);
        var handler = new FakeHandler(async request =>
        {
            handlerRequest = new CapturedRequest(
                request.RequestUri!, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                await request.Content!.ReadAsStringAsync());
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(PixelPng)}}"}]}""", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-request-id", "req_contract_openai");
            return response;
        });
        handlerRequest = null;
        var config = Configuration(new Dictionary<string, string?>
        {
            ["Integrations:OpenAI:AllowCustomEndpoint"] = "true",
            ["Integrations:OpenAI:ImageModel"] = "gpt-image-2",
            ["Integrations:OpenAI:ImageEditEndpoint"] = "https://api.openai.test/v1/images/edits"
        });
        var previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-contract-test-only");
        try
        {
            var adapter = new OpenAiImageGenerationAdapter(new FakeFactory(handler), config, new FakeCredentials("sk-contract-test-only"));
            var descriptor = adapter.Describe();
            Assert.True(descriptor.CanDispatch);
            var styleAsset = new AssetRecord { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = "openai-style.png", MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('f', 64), StoragePath = "openai-style.png", CreatedAt = DateTimeOffset.UtcNow };
            await using var output = (await adapter.ExecuteAsync(Context(imagePath) with
            {
                ReferenceImages = [(new AuthorityBinding("lantern-trial-look", "Lantern Trial Look", "Style", 3), styleAsset, stylePath)]
            }, CancellationToken.None)).Content;
            Assert.Equal(PixelPng, await ReadAllAsync(output));
            Assert.NotNull(handlerRequest);
            Assert.Equal("https://api.openai.test/v1/images/edits", handlerRequest.Uri.ToString());
            Assert.Equal("Bearer", handlerRequest.Scheme);
            Assert.Equal("sk-contract-test-only", handlerRequest.Credential);
            Assert.Contains("gpt-image-2", handlerRequest.Body);
            Assert.Contains("Render the authority-safe frame", handlerRequest.Body);
            Assert.Contains("image[]", handlerRequest.Body);
            Assert.Contains("01-current-frame.png", handlerRequest.Body);
            Assert.Contains("02-style-lantern-trial-look-v3.png", handlerRequest.Body);
            Assert.Contains("IMAGE 2: Lantern Trial Look (Style) v3", handlerRequest.Body);
            Assert.Contains("Apply this exemplar's rendering language globally", handlerRequest.Body);
        }
        finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous); }
    }

    [Fact]
    public async Task OpenAIAdapterUsesTextGenerationWhenNoCompositionOrReferenceExists()
    {
        var handler = new FakeHandler(async request =>
        {
            handlerRequest = new CapturedRequest(request.RequestUri!, request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter, await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"data":[{"b64_json":"{{Convert.ToBase64String(PixelPng)}}"}]}""", Encoding.UTF8, "application/json")
            };
        });
        handlerRequest = null;
        var config = Configuration(new Dictionary<string, string?>
        {
            ["Integrations:OpenAI:AllowCustomEndpoint"] = "true",
            ["Integrations:OpenAI:ImageModel"] = "gpt-image-2",
            ["Integrations:OpenAI:ImageGenerationEndpoint"] = "https://api.openai.test/v1/images/generations"
        });
        var adapter = new OpenAiImageGenerationAdapter(new FakeFactory(handler), config, new FakeCredentials("sk-contract-test-only"));
        var context = Context(Path.Combine(root, "not-used.png")) with { Composition = null, CompositionPath = null };

        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.Equal("https://api.openai.test/v1/images/generations", handlerRequest!.Uri.ToString());
        Assert.Contains("\"model\":\"gpt-image-2\"", handlerRequest.Body);
        Assert.Contains("Render the authority-safe frame", handlerRequest.Body);
        Assert.DoesNotContain("image[]", handlerRequest.Body);
    }

    private CapturedRequest? handlerRequest;

    [Fact]
    public async Task CodexImageGenAdapterUsesIsolatedWorkspaceAndCurrentFrame()
    {
        var imagePath = Path.Combine(root, "composition.png");
        var stylePath = Path.Combine(root, "style.png");
        var characterPath = Path.Combine(root, "character.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        await File.WriteAllBytesAsync(stylePath, PixelPng);
        await File.WriteAllBytesAsync(characterPath, PixelPng);
        var runtime = new FakeCodexRuntime(async (arguments, workingDirectory) =>
        {
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "Framewright", "codex-imagegen"), workingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--ephemeral", arguments);
            Assert.Contains("workspace-write", arguments);
            Assert.Contains("--image", arguments);
            Assert.Contains(arguments, value => value.Contains("01-current-frame.png", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(arguments, value => value.Contains("02-style-lantern-trial-look-v3.png", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(arguments, value => value.Contains("03-character-mara-vey-v5.png", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("-", arguments[^1]);
            await File.WriteAllBytesAsync(Path.Combine(workingDirectory, "framewright-result.png"), PixelPng);
            return new(true, 0, "Image written", "");
        });
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration(new Dictionary<string, string?>
        {
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "true"
        }));

        static AssetRecord ReferenceAsset(string name) => new() { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = name, MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('e', 64), StoragePath = name, CreatedAt = DateTimeOffset.UtcNow };
        var result = await adapter.ExecuteAsync(Context(imagePath) with
        {
            Route = GenerationRoute.PrecisionDraft,
            ReferenceImages =
            [
                (new AuthorityBinding("lantern-trial-look", "Lantern Trial Look", "Style", 3), ReferenceAsset("style.png"), stylePath),
                (new AuthorityBinding("mara-vey", "Mara Vey", "Character", 5), ReferenceAsset("character.png"), characterPath)
            ]
        }, CancellationToken.None);

        await using var output = result.Content;
        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.Equal("image/png", result.MimeType);
        Assert.True(result.ProviderCallMade);
        Assert.True(adapter.Describe().CanDispatch);
        Assert.Contains("$imagegen", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("IMMUTABLE FRAMEWRIGHT PACKET", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("REQUIRED VISUAL ACCEPTANCE CHECK", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("perform one corrective ImageGen edit pass", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("IMAGE 2: Lantern Trial Look (Style) v3", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("Apply this exemplar's rendering language globally", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("IMAGE 3: Mara Vey (Character) v5", runtime.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexLastFrameRejectsAVisuallyNoncompliantResultBeforeImport()
    {
        var imagePath = Path.Combine(root, "last-frame-source.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var call = 0;
        var runtime = new FakeCodexRuntime(async (arguments, workingDirectory) =>
        {
            call++;
            if (call == 1)
            {
                await File.WriteAllBytesAsync(Path.Combine(workingDirectory, "framewright-result.png"), PixelPng);
                return new(true, 0, "Image written", "");
            }
            Assert.Contains(arguments, value => value.Contains("framewright-result.png", StringComparison.OrdinalIgnoreCase));
            return new(true, 0, "{\"passes\":true,\"criteria\":[{\"criterion\":\"head bowed\",\"passes\":false,\"evidence\":\"Her head remains upright.\"},{\"criterion\":\"eyes down\",\"passes\":false,\"evidence\":\"Her eyes face the camera.\"},{\"criterion\":\"visibly sad\",\"passes\":false,\"evidence\":\"Her expression is neutral.\"}],\"reason\":\"The requested emotional change is absent.\"}", "");
        });
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration(new Dictionary<string, string?>
        {
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "true"
        }));
        var context = Context(imagePath) with
        {
            Route = GenerationRoute.PrecisionDraft,
            IsCurrentFrameEdit = true,
            Prompt = "CREATE THE LAST FRAME FOR THIS SHOT. END-STATE DIRECTION: Mara looks down and visibly sad."
        };

        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(context, CancellationToken.None));

        Assert.True(error.ProviderCallMade);
        Assert.Contains("eyes face the camera", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not selected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, runtime.Calls);
        Assert.Contains("adversarial acceptance check", runtime.StandardInput, StringComparison.Ordinal);
        Assert.Contains("Mara looks down and visibly sad", runtime.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexLastFrameReturnsOnlyAfterVisualAcceptancePasses()
    {
        var imagePath = Path.Combine(root, "accepted-last-frame-source.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var call = 0;
        var runtime = new FakeCodexRuntime(async (_, workingDirectory) =>
        {
            call++;
            if (call == 1)
            {
                await File.WriteAllBytesAsync(Path.Combine(workingDirectory, "framewright-result.png"), PixelPng);
                return new(true, 0, "Image written", "");
            }
            if (call == 2)
                return new(true, 0, "{\"passes\":true,\"criteria\":[{\"criterion\":\"head bowed\",\"passes\":true,\"evidence\":\"Her chin is visibly lowered.\"},{\"criterion\":\"eyes down\",\"passes\":true,\"evidence\":\"Both pupils point toward the floor.\"},{\"criterion\":\"visibly sad\",\"passes\":true,\"evidence\":\"Her brows and mouth form a plainly sorrowful expression.\"}],\"reason\":\"Every requested change is visible.\"}", "");
            return new(true, 0, "{\"passes\":true,\"evidence\":\"The single requested change is unmistakably visible.\"}", "");
        });
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration(new Dictionary<string, string?>
        {
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "true"
        }));

        await using var output = (await adapter.ExecuteAsync(Context(imagePath) with
        {
            Route = GenerationRoute.PrecisionDraft,
            IsCurrentFrameEdit = true,
            Prompt = "CREATE THE LAST FRAME FOR THIS SHOT. END-STATE DIRECTION: Mara looks down and visibly sad."
        }, CancellationToken.None)).Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.Equal(5, runtime.Calls);
        Assert.Contains("LAST FRAME END-STATE PRIORITY", runtime.Inputs[0], StringComparison.Ordinal);
        Assert.Contains("Mara looks down and visibly sad", runtime.Inputs[0], StringComparison.Ordinal);
        Assert.Contains("exactly ONE visible requirement", runtime.Inputs[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexLastFrameRejectsAnOptimisticAggregateWhenIndependentEvidenceFails()
    {
        var imagePath = Path.Combine(root, "independent-check-last-frame.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var call = 0;
        var runtime = new FakeCodexRuntime(async (_, workingDirectory) =>
        {
            call++;
            if (call == 1)
            {
                await File.WriteAllBytesAsync(Path.Combine(workingDirectory, "framewright-result.png"), PixelPng);
                return new(true, 0, "Image written", "");
            }
            if (call == 2)
                return new(true, 0, "{\"passes\":true,\"criteria\":[{\"criterion\":\"eyes look down\",\"passes\":true,\"evidence\":\"The face appears downcast.\"}],\"reason\":\"The requested change appears present.\"}", "");
            return new(true, 0, "{\"passes\":false,\"evidence\":\"Both pupils visibly face the camera.\"}", "");
        });
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration(new Dictionary<string, string?>
        {
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "true"
        }));

        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(Context(imagePath) with
        {
            Route = GenerationRoute.PrecisionDraft,
            IsCurrentFrameEdit = true,
            Prompt = "CREATE THE LAST FRAME FOR THIS SHOT. END-STATE DIRECTION: Mara looks down."
        }, CancellationToken.None));

        Assert.Equal(3, runtime.Calls);
        Assert.Contains("pupils visibly face the camera", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not selected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentFrameDeltaKeepsPositiveProjectAndStyleAuthority()
    {
        var binding = new AuthorityBinding(
            "mara-vey",
            "Mara Vey",
            "Character",
            5,
            true,
            [new(.24, .55, "Keep Mara on the left.", "- SUBJECT IDENTITY ANCHOR: center Mara Vey v5 at 24% across / 55% down.")],
            0);
        var prompt = GenerationOrchestrator.BuildCurrentFrameDeltaPrompt("""
            PROJECT WORLD SETTINGS (UNIVERSAL — APPLY TO EVERY OUTPUT)
            VISUAL LANGUAGE: Painted cinematic animation with clean cel-shaded planes and controlled linework.

            SHOT OR ASSET BRIEF:
            Two people speak beside a lantern.

            REQUESTED CHANGE: Restyle the complete frame to match the approved look.

            CURRENT FRAME EDIT: preserve the composition.

            APPROVED AUTHORITY SPECS:
            - Lantern Trial Look (Style) v3: Painterly cinematic animation with graphic cel-shaded forms. Locked: No photoreal live-action skin.

            REFERENCE COMPOSITION CONTRACT:
            - Produce one continuous frame.
            """, [binding]);

        Assert.Contains("Painted cinematic animation with clean cel-shaded planes", prompt, StringComparison.Ordinal);
        Assert.Contains("Lantern Trial Look (Style) v3", prompt, StringComparison.Ordinal);
        Assert.Contains("graphic cel-shaded forms", prompt, StringComparison.Ordinal);
        Assert.Contains("Restyle the complete frame", prompt, StringComparison.Ordinal);
        Assert.Contains("center Mara Vey v5 at 24% across / 55% down", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Two people speak beside a lantern", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferenceDispatchIsStyleFirstThenPinnedAndNeverReparsesPromptProse()
    {
        var plan = GenerationReferenceDispatchPlanner.Plan(
        [
            (new AuthorityBinding("location", "Lantern court", "Location", 2, SourceOrder: 0), true),
            (new AuthorityBinding("wardrobe", "Court uniform", "Wardrobe", 4, IsPinned: true, SourceOrder: 1), true),
            (new AuthorityBinding("style", "Lantern Trial Look", "Style", 3, SourceOrder: 2), true),
            (new AuthorityBinding("mara", "Mara Vey", "Character", 5, IsPinned: true, SourceOrder: 3), true)
        ], 3, currentFrameEdit: true);

        Assert.Collection(plan,
            style =>
            {
                Assert.Equal("style", style.Binding.Id);
                Assert.True(style.WillBindVisually);
                Assert.Equal(1, style.VisualSlot);
            },
            character =>
            {
                Assert.Equal("mara", character.Binding.Id);
                Assert.True(character.WillBindVisually);
                Assert.Equal(2, character.VisualSlot);
            },
            wardrobe =>
            {
                Assert.Equal("wardrobe", wardrobe.Binding.Id);
                Assert.True(wardrobe.WillBindVisually);
                Assert.Equal(3, wardrobe.VisualSlot);
            },
            location =>
            {
                Assert.Equal("location", location.Binding.Id);
                Assert.False(location.WillBindVisually);
                Assert.Contains("no portrait pixels are reintroduced", location.Detail, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task CodexImageGenAdapterAllowsContainerToSelectOuterSandboxBoundary()
    {
        var runtime = new FakeCodexRuntime(async (arguments, workingDirectory) =>
        {
            Assert.Contains("danger-full-access", arguments);
            Assert.DoesNotContain("workspace-write", arguments);
            await File.WriteAllBytesAsync(Path.Combine(workingDirectory, "framewright-result.png"), PixelPng);
            return new(true, 0, "Image written", "");
        });
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration(new Dictionary<string, string?>
        {
            ["Integrations:Codex:NonInteractiveImageEnabled"] = "true",
            ["Integrations:Codex:Sandbox"] = "danger-full-access"
        }));

        await using var output = (await adapter.ExecuteAsync(Context(Path.Combine(root, "unused.png")) with
        {
            Composition = null,
            CompositionPath = null
        }, CancellationToken.None)).Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
    }

    [Fact]
    public async Task CodexImageGenAdapterRequiresChatGPTLoginBeforeDispatch()
    {
        var runtime = new FakeCodexRuntime((_, _) => throw new InvalidOperationException(), authenticated: false);
        var adapter = new CodexImageGenerationAdapter(runtime, Configuration([]));

        Assert.False(adapter.Describe().CanDispatch);
        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(Context("unused.png"), CancellationToken.None));
        Assert.Contains("sign-in", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task OpenAIAdapterRefusesUnreviewedCustomEndpointBeforeSendingACredential()
    {
        var imagePath = Path.Combine(root, "composition.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var calls = 0;
        var handler = new FakeHandler(request => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var adapter = new OpenAiImageGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:OpenAI:ImageEditEndpoint"] = "https://unreviewed.example/v1/images/edits"
        }), new FakeCredentials("sk-contract-test-only"));

        Assert.False(adapter.Describe().CanDispatch);
        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(Context(imagePath), CancellationToken.None));
        Assert.Contains("AllowCustomEndpoint", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ComfyUIAdapterRefusesQueueInterferenceAndBindsExternalWorkflowPlaceholders()
    {
        var imagePath = Path.Combine(root, "composition.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"1":{"class_type":"LoadImage","inputs":{"image":"{{INPUT_IMAGE}}"}},"2":{"class_type":"CLIPTextEncode","inputs":{"text":"{{PROMPT}}"}},"3":{"class_type":"KSampler","inputs":{"seed":"{{SEED}}"}}}""");
        string? submittedWorkflow = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json("{\"name\":\"studio-composition.png\",\"subfolder\":\"\",\"type\":\"input\"}");
            if (path == "/prompt") { submittedWorkflow = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"prompt-contract-1\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"prompt-contract-1\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"candidate.png\",\"subfolder\":\"studio\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalReferenceWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        Assert.True(adapter.Describe().CanDispatch);
        var result = await adapter.ExecuteAsync(Context(imagePath), CancellationToken.None);
        await using var output = result.Content;
        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.True(result.ProviderCallMade);
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("studio-composition.png", submittedWorkflow);
        Assert.Contains("Render the authority-safe frame", submittedWorkflow);
        Assert.DoesNotContain("{{PROMPT}}", submittedWorkflow);
        Assert.DoesNotContain("{{INPUT_IMAGE}}", submittedWorkflow);
    }

    [Fact]
    public async Task ComfyUIAdapterSendsPinnedAuthorityImagesToDirectWorkflowSlotsInsteadOfTextOnlyApproximations()
    {
        var compositionPath = Path.Combine(root, "composition.png");
        var magistratePath = Path.Combine(root, "magistrate.png");
        var ennixPath = Path.Combine(root, "ennix.png");
        var stylePath = Path.Combine(root, "style.png");
        var wardrobePath = Path.Combine(root, "wardrobe.png");
        foreach (var path in new[] { compositionPath, magistratePath, ennixPath, stylePath, wardrobePath }) await File.WriteAllBytesAsync(path, PixelPng);
        var workflowPath = Path.Combine(root, "authority-workflow.json");
        await File.WriteAllTextAsync(workflowPath, """
            {
              "1":{"class_type":"LoadImage","inputs":{"image":"{{INPUT_IMAGE}}"}},
              "2":{"class_type":"LoadImage","inputs":{"image":"{{REFERENCE_IMAGE_1}}"}},
              "3":{"class_type":"LoadImage","inputs":{"image":"{{REFERENCE_IMAGE_2}}"}},
              "4":{"class_type":"TextEncodeQwenImageEditPlus","inputs":{"prompt":"{{PROMPT}}","image1":["1",0],"image2":["2",0],"image3":["3",0]}},
              "5":{"class_type":"KSampler","inputs":{"seed":"{{SEED}}"}}
            }
            """);
        var uploadCount = 0;
        string? submittedWorkflow = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json($$"""{"name":"uploaded-{{++uploadCount}}.png","subfolder":"","type":"input"}""");
            if (path == "/prompt") { submittedWorkflow = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"authority-contract\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"authority-contract\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"candidate.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        static AssetRecord Asset(string name) => new() { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = name, MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('b', 64), StoragePath = name, CreatedAt = DateTimeOffset.UtcNow };
        var references = new (AuthorityBinding, AssetRecord, string)[]
        {
            (new("style", "Skychasers style", "Style", 6, SourceOrder: 0), Asset("style.png"), stylePath),
            (new("magistrate", "Magistrate", "Character", 6, true,
                [new(.47, .39, "Use the approved Magistrate here.", "- SUBJECT IDENTITY ANCHOR: Magistrate v6 at 47% across / 39% down.")], 1), Asset("magistrate.png"), magistratePath),
            (new("wardrobe", "Magistrate formal", "Wardrobe", 2, SourceOrder: 2), Asset("wardrobe.png"), wardrobePath),
            (new("ennix", "Ennix", "Character", 6, SourceOrder: 3), Asset("ennix.png"), ennixPath)
        };
        var context = Context(compositionPath) with
        {
            Prompt = "Apply the frozen pinned feedback without reparsing its prose.",
            ReferenceImages = references,
            IsCurrentFrameEdit = true
        };

        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        Assert.Equal(3, uploadCount); // composition plus the two direct authority-image sockets
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("uploaded-2.png", submittedWorkflow);
        Assert.Contains("uploaded-3.png", submittedWorkflow);
        Assert.Contains("IMAGE 2: Skychasers style (Style) v6", submittedWorkflow);
        Assert.Contains("IMAGE 3: Magistrate (Character) v6", submittedWorkflow);
        Assert.Contains("MULTI-REFERENCE SYNTHESIS CONTRACT", submittedWorkflow);
        Assert.Contains("Never make a collage", submittedWorkflow);
        Assert.Contains("Style authorities apply globally", submittedWorkflow);
        Assert.DoesNotContain("IMAGE 3: Magistrate formal", submittedWorkflow);
        Assert.DoesNotContain("IMAGE 3: Ennix", submittedWorkflow);
        Assert.DoesNotContain("{{REFERENCE_IMAGE_", submittedWorkflow);
    }

    [Fact]
    public async Task ComfyUICurrentFrameRedraftDoesNotReintroduceUnpinnedPortraitsAsExtraSubjects()
    {
        var compositionPath = Path.Combine(root, "current-frame.png");
        var remoraPath = Path.Combine(root, "remora-portrait.png");
        var magistratePath = Path.Combine(root, "magistrate-portrait.png");
        var stylePath = Path.Combine(root, "approved-style.png");
        foreach (var path in new[] { compositionPath, remoraPath, magistratePath, stylePath }) await File.WriteAllBytesAsync(path, PixelPng);
        var workflowPath = Path.Combine(root, "current-frame-workflow.json");
        await File.WriteAllTextAsync(workflowPath, """
            {
              "1":{"class_type":"LoadImage","inputs":{"image":"{{INPUT_IMAGE}}"}},
              "2":{"class_type":"LoadImage","inputs":{"image":"{{REFERENCE_IMAGE_1}}"}},
              "3":{"class_type":"LoadImage","inputs":{"image":"{{REFERENCE_IMAGE_2}}"}},
              "4":{"class_type":"TextEncodeQwenImageEditPlus","inputs":{"prompt":"{{PROMPT}}","image1":["1",0],"image2":["2",0],"image3":["3",0]}},
              "5":{"class_type":"KSampler","inputs":{"seed":"{{SEED}}"}}
            }
            """);
        var currentFrameWorkflowPath = Path.Combine(root, "pixel-preserving-current-frame-workflow.json");
        var currentFrameWorkflow = (await File.ReadAllTextAsync(workflowPath)).Replace(
            "\"class_type\":\"KSampler\"",
            "\"class_type\":\"KSampler\",\"_meta\":{\"title\":\"PIXEL PRESERVING CURRENT FRAME EDIT\"}",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(currentFrameWorkflowPath, currentFrameWorkflow);
        var uploadCount = 0;
        string? submittedWorkflow = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json($$"""{"name":"uploaded-{{++uploadCount}}.png","subfolder":"","type":"input"}""");
            if (path == "/prompt") { submittedWorkflow = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"current-frame-contract\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"current-frame-contract\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"candidate.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalCurrentFrameWorkflowPath"] = currentFrameWorkflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        static AssetRecord Asset(string name) => new() { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = name, MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('d', 64), StoragePath = name, CreatedAt = DateTimeOffset.UtcNow };
        var context = Context(compositionPath) with
        {
            Prompt = "Widen the camera slightly.\n\nTHE COMPOSITION IMAGE IS THE CURRENT GENERATED FRAME, NOT THE ORIGINAL SKETCH.",
            ReferenceImages = new[]
            {
                (new AuthorityBinding("remora", "Remora", "Character", 6), Asset("remora.png"), remoraPath),
                (new AuthorityBinding("magistrate", "Magistrate", "Character", 8), Asset("magistrate.png"), magistratePath),
                (new AuthorityBinding("style", "Skychasers style", "Style", 6), Asset("style.png"), stylePath)
            },
            IsCurrentFrameEdit = true
        };

        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        Assert.Equal(2, uploadCount); // Current frame plus global style; unpinned portraits remain textual canon.
        Assert.NotNull(submittedWorkflow);
        Assert.Contains("PIXEL PRESERVING CURRENT FRAME EDIT", submittedWorkflow);
        Assert.Contains("IMAGE 2: Skychasers style (Style) v6", submittedWorkflow);
        Assert.DoesNotContain("remora-portrait", submittedWorkflow);
        Assert.DoesNotContain("magistrate-portrait", submittedWorkflow);
        Assert.DoesNotContain("{{REFERENCE_IMAGE_", submittedWorkflow);
    }

    [Fact]
    public async Task ComfyUIAdapterUsesReferenceComposeWorkflowWhenAuthoritiesExistWithoutAComposition()
    {
        var referencePath = Path.Combine(root, "guard.png");
        await File.WriteAllBytesAsync(referencePath, PixelPng);
        var sketchWorkflow = Path.Combine(root, "sketch.json");
        var textWorkflow = Path.Combine(root, "text.json");
        var referenceWorkflow = Path.Combine(root, "reference.json");
        await File.WriteAllTextAsync(sketchWorkflow, """{"route":"sketch","text":"{{PROMPT}}","image":"{{INPUT_IMAGE}}","seed":"{{SEED}}"}""");
        await File.WriteAllTextAsync(textWorkflow, """{"route":"text","text":"{{PROMPT}}","seed":"{{SEED}}"}""");
        await File.WriteAllTextAsync(referenceWorkflow, """{"route":"reference","1":{"class_type":"LoadImage","inputs":{"image":"{{REFERENCE_IMAGE_1}}"}},"2":{"class_type":"EmptySD3LatentImage","inputs":{"width":1344,"height":768,"batch_size":1}},"text":"{{PROMPT}}","negative":"{{NEGATIVE_PROMPT}}","seed":"{{SEED}}"}""");
        string? submitted = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json("{\"name\":\"guard-authority.png\",\"subfolder\":\"\",\"type\":\"input\"}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"reference-route\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"reference-route\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"reference.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = sketchWorkflow,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = textWorkflow,
            ["Integrations:ComfyUi:ExternalReferenceWorkflowPath"] = referenceWorkflow,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        var asset = new AssetRecord { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = "guard.png", MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('c', 64), StoragePath = "guard.png", CreatedAt = DateTimeOffset.UtcNow };
        var context = Context(string.Empty) with
        {
            Composition = null,
            CompositionPath = null,
            ReferenceImages = new[] { (new AuthorityBinding("guards", "Court guards", "Role", 3), asset, referencePath) },
            DeliveryWidth = 3840,
            DeliveryHeight = 1608
        };

        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        Assert.NotNull(submitted);
        Assert.Contains("\"route\":\"reference\"", submitted);
        Assert.Contains("guard-authority.png", submitted);
        Assert.DoesNotContain("\"route\":\"text\"", submitted);
        Assert.DoesNotContain("duplicate person", submitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("never add a second instance", submitted, StringComparison.OrdinalIgnoreCase);
        using var submittedGraph = JsonDocument.Parse(submitted);
        Assert.Equal(1536, submittedGraph.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(640, submittedGraph.RootElement.GetProperty("prompt").GetProperty("2").GetProperty("inputs").GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task ComfyUIAdapterUsesTextWorkflowWithoutUploadWhenShotHasNoComposition()
    {
        var sketchWorkflowPath = Path.Combine(root, "sketch-workflow.json");
        var textWorkflowPath = Path.Combine(root, "text-workflow.json");
        await File.WriteAllTextAsync(sketchWorkflowPath, """{"route":"sketch","image":"{{INPUT_IMAGE}}","text":"{{PROMPT}}","seed":"{{SEED}}"}""");
        await File.WriteAllTextAsync(textWorkflowPath, """{"route":"text","text":"{{PROMPT}}","seed":"{{SEED}}"}""");
        var calls = new List<string>();
        string? submitted = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"text-only\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"text-only\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"text.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = sketchWorkflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = textWorkflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        var context = Context(string.Empty) with { Composition = null, CompositionPath = null };
        await using var output = (await adapter.ExecuteAsync(context, CancellationToken.None)).Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.DoesNotContain("/upload/image", calls);
        Assert.Contains("\"route\":\"text\"", submitted);
        Assert.DoesNotContain("{{PROMPT}}", submitted);
    }

    // The adapter shares one workstation ComfyUI with live production work. It
    // joins that queue and waits its turn; it must never clear, interrupt, or
    // harvest a prompt it did not submit.

    [Fact]
    public async Task ComfyUIAdapterJoinsABusyQueueAndWaitsItsTurnWithoutTouchingOtherWork()
    {
        var imagePath = Path.Combine(root, "composition.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"image":"{{INPUT_IMAGE}}","text":"{{PROMPT}}"}""");
        var calls = new List<string>();
        var phases = new List<string>();
        var historyReads = 0;
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            // Someone else is rendering, and a second job of theirs is queued
            // ahead of ours. Ours sits third.
            if (path == "/queue") return Task.FromResult(Json(
                "{\"queue_running\":[[0,\"someone-else-running\"]],\"queue_pending\":[[1,\"someone-else-pending\"],[2,\"prompt-ours\"]]}"));
            if (path == "/upload/image") return Task.FromResult(Json("{\"name\":\"studio-composition.png\"}"));
            if (path == "/prompt") return Task.FromResult(Json("{\"prompt_id\":\"prompt-ours\"}"));
            if (path.StartsWith("/history/", StringComparison.Ordinal))
                return Task.FromResult(Json(++historyReads < 3
                    ? "{}"
                    : "{\"prompt-ours\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"ours.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"));
            if (path == "/view") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        var result = await adapter.ExecuteAsync(Context(imagePath, (_, phase, _, _) => { phases.Add(phase); return Task.CompletedTask; }), CancellationToken.None);
        await using var output = result.Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.Contains("/prompt", calls);                       // it submitted despite a busy queue
        Assert.DoesNotContain("/queue/clear", calls);
        Assert.DoesNotContain("/interrupt", calls);
        Assert.Contains(phases, x => x.Contains("2 jobs ahead"));  // running + queued work ahead
    }

    [Fact]
    public async Task ComfyUIAdapterResumesItsPersistedPromptWithoutSubmittingADuplicate()
    {
        var workflowPath = Path.Combine(root, "resume-workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"text":"{{PROMPT}}","seed":"{{SEED}}"}""");
        var promptId = Guid.NewGuid().ToString();
        var calls = new List<string>();
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            if (path == "/queue") return Task.FromResult(Json(JsonSerializer.Serialize(new { queue_running = new object[] { new object[] { 0, promptId } }, queue_pending = Array.Empty<object>() })));
            if (path == $"/history/{promptId}") return Task.FromResult(Json("""{"PROMPT":{"outputs":{"9":{"images":[{"filename":"resumed.png","subfolder":"","type":"output"}]}}}}""".Replace("PROMPT", promptId, StringComparison.Ordinal)));
            if (path == "/view") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) { Headers = { ContentType = new("image/png") } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        var result = await adapter.ExecuteAsync(Context(string.Empty) with
        {
            Composition = null,
            CompositionPath = null,
            ExistingProviderRequestId = promptId
        }, CancellationToken.None);
        await using var output = result.Content;

        Assert.Equal(PixelPng, await ReadAllAsync(output));
        Assert.Equal(promptId, result.ProviderRequestId);
        Assert.DoesNotContain("/prompt", calls);
        Assert.DoesNotContain("/upload/image", calls);
        Assert.Contains($"/history/{promptId}", calls);
    }

    [Fact]
    public async Task ComfyUIAdapterNeverHarvestsAPromptItDidNotSubmit()
    {
        var imagePath = Path.Combine(root, "composition.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"image":"{{INPUT_IMAGE}}","text":"{{PROMPT}}"}""");
        var viewed = new List<string>();
        var historyReads = 0;
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Task.FromResult(Json("{\"queue_running\":[],\"queue_pending\":[]}"));
            if (path == "/upload/image") return Task.FromResult(Json("{\"name\":\"studio-composition.png\"}"));
            if (path == "/prompt") return Task.FromResult(Json("{\"prompt_id\":\"prompt-ours\"}"));
            // History carries somebody else's finished render. Ours never appears.
            if (path.StartsWith("/history/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref historyReads);
                return Task.FromResult(Json(
                    "{\"someone-elses-prompt\":{\"status\":{\"completed\":true},\"outputs\":{\"9\":{\"images\":[{\"filename\":\"skychasers-secret.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"));
            }
            if (path == "/view") { viewed.Add(request.RequestUri.Query); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PixelPng) }); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        // Let it poll a while, then stop. The point is what it never does.
        // The poll interval floors at 250ms, so allow room for several rounds.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1_100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteAsync(Context(imagePath), cancellation.Token));

        Assert.True(historyReads > 1, "the adapter should have kept polling for its own prompt");
        Assert.Empty(viewed); // never downloaded the other prompt's output
    }

    [Fact]
    public async Task ComfyUIAdapterRejectsOversizedProviderOutputBeforeBuffering()
    {
        var imagePath = Path.Combine(root, "composition.png");
        await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"image":"{{INPUT_IMAGE}}","text":"{{PROMPT}}"}""");
        var handler = new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Task.FromResult(Json("{\"queue_running\":[],\"queue_pending\":[]}"));
            if (path == "/upload/image") return Task.FromResult(Json("{\"name\":\"studio-composition.png\"}"));
            if (path == "/prompt") return Task.FromResult(Json("{\"prompt_id\":\"oversized-output\"}"));
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Task.FromResult(Json("{\"oversized-output\":{\"outputs\":{\"9\":{\"images\":[{\"filename\":\"candidate.png\",\"subfolder\":\"\",\"type\":\"output\"}]}}}}"));
            if (path == "/view")
            {
                var content = new ByteArrayContent(PixelPng);
                content.Headers.ContentLength = AssetStore.MaxImageBytes + 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var adapter = new ComfyUiGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:SubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:ExternalTextWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() => adapter.ExecuteAsync(Context(imagePath), CancellationToken.None));

        Assert.Contains("25 MB safety limit", error.Message);
        Assert.True(error.ProviderCallMade);
    }

    [Fact]
    public async Task ProviderOutputStreamIsBoundedEvenWithoutADeclaredLength()
    {
        using var content = new StreamContent(new MemoryStream(new byte[17]));
        content.Headers.ContentLength = null;

        var error = await Assert.ThrowsAsync<GenerationDispatchException>(() =>
            BoundedContentBuffer.CopyToTemporaryFileAsync(
                content, 16, "Provider stream exceeded the contract.", "req-stream-limit", CancellationToken.None));

        Assert.Contains("exceeded the contract", error.Message);
        Assert.True(error.ProviderCallMade);
        Assert.Equal("req-stream-limit", error.ProviderRequestId);
    }

    [Fact]
    public async Task ComfyUIH3VideoAdapterBindsRatifiedFramesAndReturnsVideoWithoutQueueMutation()
    {
        var imagePath = Path.Combine(root, "video-first.png"); await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "video-workflow.json"); await File.WriteAllTextAsync(workflowPath, """{"1":{"inputs":{"image":"{{INPUT_IMAGE}}"}},"2":{"inputs":{"text":"{{PROMPT}}","seed":"{{SEED}}","width":{{VIDEO_WIDTH}},"height":{{VIDEO_HEIGHT}},"steps":{{VIDEO_STEPS}},"length":{{VIDEO_LENGTH}}}},"3":{"inputs":{"image":["2",0],"width":{{VIDEO_OUTPUT_WIDTH}},"height":{{VIDEO_OUTPUT_HEIGHT}}}},"4":{"inputs":{"filename_prefix":"{{VIDEO_FILENAME_PREFIX}}","video":["3",0],"fps":{{VIDEO_FPS}}}}}""");
        var mp4 = new byte[24]; "ftyp"u8.CopyTo(mp4.AsSpan(4));
        var calls = new List<string>(); string? submitted = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath; calls.Add(path);
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json("{\"name\":\"ratified-final.png\"}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"video-contract-1\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"video-contract-1\":{\"outputs\":{\"12\":{\"gifs\":[{\"filename\":\"shot.mp4\",\"subfolder\":\"studio\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mp4) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiVideoGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        var result = await adapter.ExecuteAsync(Context(imagePath) with { Purpose = GenerationPurpose.Video, VideoQuality = VideoQuality.High, VideoSeed = 4242, VideoWidth = 1344, VideoHeight = 560, DeliveryWidth = 2304, DeliveryHeight = 960, VideoFramesPerSecond = 25, VideoFrameCount = 137 }, CancellationToken.None);
        await using var output = result.Content;
        Assert.Equal(AssetKind.Video, result.Kind); Assert.Equal(mp4, await ReadAllAsync(output)); Assert.Contains("ratified-final.png", submitted); Assert.Contains("VISUAL", submitted, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("/queue/clear", calls); Assert.DoesNotContain("/interrupt", calls);
        using var envelope = JsonDocument.Parse(submitted!);
        var graph = envelope.RootElement.GetProperty("prompt");
        Assert.Equal(4242, graph.GetProperty("2").GetProperty("inputs").GetProperty("seed").GetInt64());
        Assert.Equal(1344, graph.GetProperty("2").GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(560, graph.GetProperty("2").GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal(24, graph.GetProperty("2").GetProperty("inputs").GetProperty("steps").GetInt32());
        Assert.Equal(137, graph.GetProperty("2").GetProperty("inputs").GetProperty("length").GetInt32());
        Assert.Equal(2304, graph.GetProperty("3").GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(960, graph.GetProperty("3").GetProperty("inputs").GetProperty("height").GetInt32());
        Assert.Equal("storyboard-studio/h3-high", graph.GetProperty("4").GetProperty("inputs").GetProperty("filename_prefix").GetString());
        Assert.Equal(25, graph.GetProperty("4").GetProperty("inputs").GetProperty("fps").GetInt32());
    }

    [Fact]
    public async Task ComfyUIH3VideoAdapterPrunesTheOptionalLastFrameWhenItIsNotSelected()
    {
        var imagePath = Path.Combine(root, "video-start-only.png"); await File.WriteAllBytesAsync(imagePath, PixelPng);
        var workflowPath = Path.Combine(root, "video-start-only-workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"1":{"inputs":{"image":"{{INPUT_IMAGE}}"}},"2":{"_meta":{"optionalPlaceholder":"{{LAST_FRAME_IMAGE}}"},"inputs":{"image":"{{LAST_FRAME_IMAGE}}"}},"3":{"inputs":{"prompt":"{{PROMPT}}","seed":"{{SEED}}","length":{{VIDEO_LENGTH}},"first_frame":["1",0],"last_frame":["2",0]}},"4":{"inputs":{"video":["3",0],"fps":{{VIDEO_FPS}}}}}""");
        var mp4 = new byte[24]; "ftyp"u8.CopyTo(mp4.AsSpan(4));
        string? submitted = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json("{\"name\":\"start-only.png\"}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"video-start-only\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"video-start-only\":{\"outputs\":{\"4\":{\"videos\":[{\"filename\":\"start-only.mp4\",\"subfolder\":\"studio\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mp4) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiVideoGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));

        await using var output = (await adapter.ExecuteAsync(Context(imagePath) with { Purpose = GenerationPurpose.Video, VideoFramesPerSecond = 30, VideoFrameCount = 151 }, CancellationToken.None)).Content;
        using var envelope = JsonDocument.Parse(submitted!);
        var graph = envelope.RootElement.GetProperty("prompt");
        Assert.False(graph.TryGetProperty("2", out _));
        Assert.False(graph.GetProperty("3").GetProperty("inputs").TryGetProperty("last_frame", out _));
        Assert.Equal("start-only.png", graph.GetProperty("1").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal(151, graph.GetProperty("3").GetProperty("inputs").GetProperty("length").GetInt32());
        Assert.Equal(30, graph.GetProperty("4").GetProperty("inputs").GetProperty("fps").GetInt32());
    }

    [Fact]
    public async Task ComfyUIH3VideoAdapterUploadsAndBindsBothSelectedEndpoints()
    {
        var firstPath = Path.Combine(root, "video-first-anchor.png");
        var lastPath = Path.Combine(root, "video-last-anchor.png");
        await File.WriteAllBytesAsync(firstPath, PixelPng);
        await File.WriteAllBytesAsync(lastPath, PixelPng);
        var workflowPath = Path.Combine(root, "video-two-anchor-workflow.json");
        await File.WriteAllTextAsync(workflowPath, """{"1":{"inputs":{"image":"{{INPUT_IMAGE}}"}},"2":{"_meta":{"optionalPlaceholder":"{{LAST_FRAME_IMAGE}}"},"inputs":{"image":"{{LAST_FRAME_IMAGE}}"}},"3":{"inputs":{"prompt":"{{PROMPT}}","seed":"{{SEED}}","width":{{VIDEO_WIDTH}},"height":{{VIDEO_HEIGHT}},"steps":{{VIDEO_STEPS}},"length":{{VIDEO_LENGTH}},"first_frame":["1",0],"last_frame":["2",0]}},"4":{"inputs":{"video":["3",0],"width":{{VIDEO_OUTPUT_WIDTH}},"height":{{VIDEO_OUTPUT_HEIGHT}},"fps":{{VIDEO_FPS}},"filename_prefix":"{{VIDEO_FILENAME_PREFIX}}"}}}""");
        var mp4 = new byte[24];
        "ftyp"u8.CopyTo(mp4.AsSpan(4));
        var uploadCount = 0;
        string? submitted = null;
        var handler = new FakeHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/queue") return Json("{\"queue_running\":[],\"queue_pending\":[]}");
            if (path == "/upload/image") return Json($"{{\"name\":\"{(++uploadCount == 1 ? "start-anchor.png" : "last-anchor.png")}\"}}");
            if (path == "/prompt") { submitted = await request.Content!.ReadAsStringAsync(); return Json("{\"prompt_id\":\"video-two-anchor\"}"); }
            if (path.StartsWith("/history/", StringComparison.Ordinal)) return Json("{\"video-two-anchor\":{\"outputs\":{\"4\":{\"videos\":[{\"filename\":\"two-anchor.mp4\",\"subfolder\":\"studio\",\"type\":\"output\"}]}}}}");
            if (path == "/view") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mp4) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var adapter = new ComfyUiVideoGenerationAdapter(new FakeFactory(handler), Configuration(new Dictionary<string, string?>
        {
            ["Integrations:ComfyUi:VideoSubmissionEnabled"] = "true",
            ["Integrations:ComfyUi:Endpoint"] = "http://127.0.0.1:8188",
            ["Integrations:ComfyUi:ExternalVideoWorkflowPath"] = workflowPath,
            ["Integrations:ComfyUi:PollIntervalMilliseconds"] = "1"
        }));
        var lastAsset = new AssetRecord { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = "last.png", MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('b', 64), StoragePath = "last.png", CreatedAt = DateTimeOffset.UtcNow };

        await using var output = (await adapter.ExecuteAsync(Context(firstPath) with
        {
            Purpose = GenerationPurpose.Video,
            LastFrame = lastAsset,
            LastFramePath = lastPath,
            VideoFramesPerSecond = 24,
            VideoFrameCount = 72
        }, CancellationToken.None)).Content;

        Assert.Equal(2, uploadCount);
        using var envelope = JsonDocument.Parse(submitted!);
        var graph = envelope.RootElement.GetProperty("prompt");
        Assert.Equal("start-anchor.png", graph.GetProperty("1").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal("last-anchor.png", graph.GetProperty("2").GetProperty("inputs").GetProperty("image").GetString());
        Assert.Equal("2", graph.GetProperty("3").GetProperty("inputs").GetProperty("last_frame")[0].GetString());
    }

    [Theory]
    [InlineData(25, 137)]
    [InlineData(30, 151)]
    public void ProductionVideoProbeAcceptsExactNonDefaultProjectTiming(int fps, int frames)
    {
        var metadata = new VideoMediaMetadata(2304, 960, fps, frames, frames / (double)fps);

        var validation = FfprobeVideoMediaProbe.Validate(
            metadata,
            new VideoMediaExpectation(2304, 960, fps, frames));

        Assert.True(validation.IsValid, validation.Detail);
    }

    [Fact]
    public void ProductionVideoProbeRejectsTheOldHardCoded24Fps124FrameStream()
    {
        var oldWorkflowOutput = new VideoMediaMetadata(2304, 960, 24, 124, 124 / 24d);

        var validation = FfprobeVideoMediaProbe.Validate(
            oldWorkflowOutput,
            new VideoMediaExpectation(2304, 960, 30, 151));

        Assert.False(validation.IsValid);
        Assert.Contains("24 fps", validation.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionVideoProbeParsesCountedFramesAndRationalRate()
    {
        var metadata = FfprobeVideoMediaProbe.Parse("""
            {
              "streams": [{
                "width": 1920,
                "height": 1080,
                "avg_frame_rate": "25/1",
                "r_frame_rate": "25/1",
                "nb_frames": "136",
                "nb_read_frames": "137",
                "duration": "5.480000"
              }],
              "format": { "duration": "5.480000" }
            }
            """);

        Assert.NotNull(metadata);
        Assert.Equal(1920, metadata.Width);
        Assert.Equal(1080, metadata.Height);
        Assert.Equal(25, metadata.FramesPerSecond);
        Assert.Equal(137, metadata.FrameCount);
        Assert.Equal(5.48, metadata.DurationSeconds, 3);
    }

    private static GenerationExecutionContext Context(string imagePath, GenerationProgressReporter? report = null)
    {
        var asset = new AssetRecord { Id = Guid.NewGuid(), ProjectId = StudioDefaults.ProjectId, Kind = "Image", OriginalFileName = "composition.png", MimeType = "image/png", Bytes = PixelPng.Length, Width = 1, Height = 1, ContentHash = new string('a', 64), StoragePath = "composition.png", CreatedAt = DateTimeOffset.UtcNow };
        return new(Guid.NewGuid(), Guid.NewGuid(), "SH-TEST", GenerationRoute.FastDraft, GenerationPurpose.Draft,
            "Render the authority-safe frame. VISUAL MOTION ONLY.", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", asset, imagePath, [], null, null, report);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static async Task<byte[]> ReadAllAsync(Stream stream) { using var memory = new MemoryStream(); await stream.CopyToAsync(memory); return memory.ToArray(); }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

    private sealed record CapturedRequest(Uri Uri, string? Scheme, string? Credential, string Body);
    private sealed class FakeCredentials(string key) : IProviderCredentialStore
    {
        public string? GetOpenAiApiKey() => key;
        public CredentialStatus GetOpenAiStatus() => new(true, "Contract test", false, "Configured for a fake HTTP contract.");
        public void SaveOpenAiApiKey(string apiKey) => throw new NotSupportedException();
        public void DeleteOpenAiApiKey() => throw new NotSupportedException();
    }
    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class FakeCodexRuntime(Func<IReadOnlyList<string>, string, Task<CodexProcessResult>> run, bool authenticated = true) : ICodexRuntime
    {
        public int Calls { get; private set; }
        public string? StandardInput { get; private set; }
        public List<string> Inputs { get; } = [];
        public CodexRuntimeStatus Inspect() => new(true, authenticated, authenticated ? "Logged in using ChatGPT" : "Codex is installed, but ChatGPT sign-in is required.");
        public async Task<CodexProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
        {
            Calls++;
            StandardInput = standardInput;
            Inputs.Add(standardInput ?? string.Empty);
            return await run(arguments, workingDirectory);
        }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
