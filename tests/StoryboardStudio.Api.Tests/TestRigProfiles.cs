using StoryboardStudio.Api.Services;
using System.Text.Json;

namespace StoryboardStudio.Api.Tests;

internal static class TestRigProfiles
{
    public static readonly HumanoidRigProfile HumanoidA = new(
        "humanoid-a",
        "Humanoid A test fixture",
        [
            "Hips", "Spine", "Chest", "Neck", "Head",
            "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot",
            "RightUpperLeg", "RightLowerLeg", "RightFoot",
        ],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Spine"] = "Hips", ["Chest"] = "Spine", ["Neck"] = "Chest", ["Head"] = "Neck",
            ["LeftUpperArm"] = "Chest", ["LeftLowerArm"] = "LeftUpperArm", ["LeftHand"] = "LeftLowerArm",
            ["RightUpperArm"] = "Chest", ["RightLowerArm"] = "RightUpperArm", ["RightHand"] = "RightLowerArm",
            ["LeftUpperLeg"] = "Hips", ["LeftLowerLeg"] = "LeftUpperLeg", ["LeftFoot"] = "LeftLowerLeg",
            ["RightUpperLeg"] = "Hips", ["RightLowerLeg"] = "RightUpperLeg", ["RightFoot"] = "RightLowerLeg",
        },
        [], false, 17, "Hips", false, 4, 20_000);

    public static CompilerSkeletonProfileSet Set { get; } = new([HumanoidA], null);

    public static string WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "humanoid-a.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            profile_id = HumanoidA.Id,
            description = HumanoidA.Name,
            required_bones = HumanoidA.RequiredBones,
            expected_parents = HumanoidA.ExpectedParents,
            optional_bones = HumanoidA.OptionalBones,
            allow_unlisted_bones = HumanoidA.AllowUnlistedBones,
            exact_bone_count = HumanoidA.ExactBoneCount,
            root_bone = HumanoidA.RootBone,
            root_may_be_armature_object = HumanoidA.RootMayBeArmatureObject,
            max_influences = HumanoidA.MaxInfluences,
            tri_budget = HumanoidA.TriangleBudget,
        }));
        return directory;
    }
}
