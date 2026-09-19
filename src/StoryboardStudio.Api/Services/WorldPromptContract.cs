using StoryboardStudio.Api.Persistence;
using System.Text;

namespace StoryboardStudio.Api.Services;

internal static class WorldPromptContract
{
    public static string Build(ProjectRecord project)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("PROJECT WORLD SETTINGS (UNIVERSAL — APPLY TO EVERY OUTPUT)");
        prompt.Append("VISUAL LANGUAGE: ").AppendLine(project.VisualStyle.Trim());
        prompt.Append("WORLD CANON: ").AppendLine(project.WorldCanon.Trim());
        prompt.Append("ALWAYS PRESERVE: ").AppendLine(project.PromptDirectives.Trim());
        prompt.Append("AVOID / NEVER DRIFT INTO: ").AppendLine(project.NegativeDirectives.Trim());
        prompt.AppendLine("CONSISTENCY PROTOCOL: Treat character-face and wardrobe authorities as separate compatible layers. Face authorities control facial structure, hair, skin tone, and distinctive identity marks; wardrobe authorities control clothing, materials, accessories, and their placement. Do not let one layer redesign the other.");
        prompt.AppendLine("DIRECTIONAL FEATURES: Left and right always mean the subject's anatomical left and right, never the viewer's side. Preserve asymmetric scars, eyebrow cuts, sleeves, bracers, jewelry, props, and injuries on the specified anatomical side; do not mirror or swap them.");
        prompt.AppendLine("SMALL IDENTITY MARKS: Keep approved small marks in the same location, scale, shape, and subtlety. Make them readable enough for the shot scale without enlarging them into a new scar, makeup mark, or unrelated feature. If a feature falls below draft resolution, preserve the authority rather than inventing detail.");
        prompt.Append("Shot and asset directions add specifics; they do not silently erase these project laws.");
        return prompt.ToString();
    }

    public static object Snapshot(ProjectRecord project) => new
    {
        project.VisualStyle,
        project.WorldCanon,
        project.PromptDirectives,
        project.NegativeDirectives
    };
}
