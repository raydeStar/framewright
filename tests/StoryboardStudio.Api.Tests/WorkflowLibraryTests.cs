using StoryboardStudio.Api.Services;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

/// <summary>
/// Guards validation for deployment-owned workflow libraries. Workflow graphs
/// intentionally stay outside this repository, so every fixture here is
/// isolated and disposable.
/// </summary>
public sealed class WorkflowLibraryTests
{
    [Fact]
    public void EveryShippedWorkflowSatisfiesItsDeclaredContract()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "workflows"));
        var results = new WorkflowLibrary(root).ValidateAll();

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.True(result.Valid, $"{result.Id}: {string.Join("; ", result.Problems)}"));
        Assert.Contains(results, result => result.Id == "text-draft" && !result.Placeholders.Contains("{{INPUT_IMAGE}}"));
        Assert.Contains(results, result => result.Id == "fast-draft" && result.Placeholders.Contains("{{INPUT_IMAGE}}"));
        Assert.Contains(results, result => result.Id == "current-frame-edit" && result.Capabilities!.Contains("image-edit") && result.MaxReferenceImages == 2);
        Assert.Contains(results, result => result.Id == "h3-video-i2v" && result.Capabilities!.Contains("last-frame-optional"));
    }

    [Theory]
    [InlineData("h3-video-i2v.json", "19", "16")]
    [InlineData("h3-video-t2v.json", "17", "16")]
    public void H3WorkflowsTrimModelPaddingToTheFrozenFrameCount(string fileName, string trimNodeId, string scaleNodeId)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "workflows"));
        var template = File.ReadAllText(Path.Combine(root, fileName)).Replace("{{VIDEO_LENGTH}}", "120");
        using var document = JsonDocument.Parse(template
            .Replace("{{VIDEO_WIDTH}}", "864")
            .Replace("{{VIDEO_HEIGHT}}", "480")
            .Replace("{{VIDEO_OUTPUT_WIDTH}}", "1920")
            .Replace("{{VIDEO_OUTPUT_HEIGHT}}", "1080")
            .Replace("{{VIDEO_STEPS}}", "8")
            .Replace("{{VIDEO_FPS}}", "24")
            .Replace("{{SEED}}", "1")
            .Replace("{{PROMPT}}", "motion")
            .Replace("{{INPUT_IMAGE}}", "first.png")
            .Replace("{{LAST_FRAME_IMAGE}}", "last.png")
            .Replace("{{VIDEO_FILENAME_PREFIX}}", "qa/h3"));

        var graph = document.RootElement;
        var trim = graph.GetProperty(trimNodeId);
        Assert.Equal("ImageFromBatch", trim.GetProperty("class_type").GetString());
        Assert.Equal(120, trim.GetProperty("inputs").GetProperty("length").GetInt32());
        Assert.Equal("13", trim.GetProperty("inputs").GetProperty("image")[0].GetString());
        Assert.Equal(trimNodeId, graph.GetProperty(scaleNodeId).GetProperty("inputs").GetProperty("image")[0].GetString());
    }

    [Fact]
    public void CapabilityClaimsAreRejectedWhenTheGraphCannotHonorThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "overclaim.json"),
                """{"1":{"class_type":"KSampler","inputs":{"text":"{{PROMPT}}"}}}""");
            var entry = new WorkflowEntry("overclaim", "Overclaim", "overclaim.json", "Image",
                "Integrations:ComfyUi:ExternalWorkflowPath", "", ["{{PROMPT}}"], [], ["KSampler"], [], "image",
                ["image-edit", "multi-reference"], 3);

            var result = new WorkflowLibrary(root).Validate(entry);

            Assert.False(result.Valid);
            Assert.Contains(result.Problems, problem => problem.Contains("requires {{INPUT_IMAGE}}"));
            Assert.Contains(result.Problems, problem => problem.Contains("only has 0 reference placeholders"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingExternalLibraryIsAnEmptyOptionalConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-" + Guid.NewGuid().ToString("N"));
        var results = new WorkflowLibrary(root).ValidateAll();
        Assert.Empty(results);
    }

    [Fact]
    public void AWorkflowWithADanglingNodeLinkIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "broken.json"),
                """{"1":{"class_type":"KSampler","inputs":{"model":["99",0],"text":"{{PROMPT}}"}}}""");
            var entry = new WorkflowEntry("broken", "Broken", "broken.json", "Image",
                "Integrations:ComfyUi:ExternalWorkflowPath", "", ["{{PROMPT}}"], [], ["KSampler"], [], "image");

            var result = new WorkflowLibrary(root).Validate(entry);

            Assert.False(result.Valid);
            Assert.Contains(result.Problems, x => x.Contains("links to node '99'"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AnUnbindablePlaceholderIsRejectedBeforeItCanReachComfyUI()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "typo.json"),
                """{"1":{"class_type":"KSampler","inputs":{"text":"{{PROMTP}}"}}}""");
            var entry = new WorkflowEntry("typo", "Typo", "typo.json", "Image",
                "Integrations:ComfyUi:ExternalWorkflowPath", "", [], [], ["KSampler"], [], "image");

            var result = new WorkflowLibrary(root).Validate(entry);

            Assert.False(result.Valid);
            Assert.Contains(result.Problems, x => x.Contains("{{PROMTP}}"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AModelThatDisagreesWithTheDeclaredRequirementIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "drift.json"),
                """{"1":{"class_type":"UNETLoader","inputs":{"unet_name":"something-else.safetensors"}},"2":{"class_type":"KSampler","inputs":{"model":["1",0],"text":"{{PROMPT}}"}}}""");
            var entry = new WorkflowEntry("drift", "Drift", "drift.json", "Image",
                "Integrations:ComfyUi:ExternalWorkflowPath", "", ["{{PROMPT}}"], [], ["UNETLoader", "KSampler"],
                new() { ["UNETLoader.unet_name"] = "expected.safetensors" }, "image");

            var result = new WorkflowLibrary(root).Validate(entry);

            Assert.False(result.Valid);
            Assert.Contains(result.Problems, x => x.Contains("something-else.safetensors"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
