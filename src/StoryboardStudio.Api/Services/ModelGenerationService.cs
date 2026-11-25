using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// One image revision, frozen into a request, compiled into a model candidate.
///
/// The artist picks a reference image; this freezes exactly which bytes of it
/// were chosen, queues an owned job, and later asks the Reference Asset
/// Compiler to turn it into a model. Nothing about that is instant: these
/// stages run for minutes, so the job is durable, resumable, and honest about
/// where it has got to, and the artist can close the screen.
///
/// The route is uncommissioned until the artist says otherwise: a capability
/// that exists is not permission to spend an hour of GPU on it.
/// </summary>
public sealed class ModelGenerationService(
    StudioDbContext db,
    AssetStore assets,
    ICompilerGateway compiler,
    TimeProvider timeProvider,
    IConfiguration configuration,
    IWebHostEnvironment environment)
{
    public const string ModelWorkType = "Model";
    public const string GeometryStage = "geometry";
    public const string StageMeshStage = "stage-mesh";
    public const string RemeshStage = "remesh";
    public const string UvUnwrapStage = "uv-unwrap";
    public const string TextureStage = "texture";
    public const string GlassStage = "glass";
    public const string BrowserPayloadStage = "browser-payload";
    public const string AdoptMeshStage = "adopt-mesh";
    public const string ReduceMeshStage = "reduce-mesh";
    public const string RebakeMapsStage = "rebake-maps";
    public const string ReviewViewsStage = "review-views";
    public const string SurveySurfacesStage = "survey-surfaces";
    public const string BakeDetailStage = "bake-detail";
    public const string AssignSurfacesStage = "assign-surfaces";
    public const string CompressTexturesStage = "compress-textures";
    public const string CullUnseenStage = "cull-unseen";
    public const string PaintHeadStage = "paint-head";
    public const string PaintSectionsStage = "paint-sections";
    public const string RigStage = "rig";
    public const string PixalGeometryStage = "pixal-geometry";
    public const string BakeShapeStage = "bake-shape";
    public const string ProjectReferenceStage = "project-reference";

    /// <summary>
    /// What makes the shape. Hunyuan3D is the route every model was made by
    /// until 25 November and stays the default. Pixal3D keeps rivets, straps
    /// and plank seams as real geometry where Hunyuan3D rounds them into blobs;
    /// the owner compared the two on the Frastown skiff and chose Pixal3D for
    /// props "by a landslide". It makes props only: it has no head pass.
    /// </summary>
    public const string HunyuanEngine = "hunyuan3d";
    public const string PixalEngine = "pixal3d";
    private static readonly string[] PixalUps = ["shortest", "middle", "longest"];

    /// <summary>
    /// The Pixal3D prop recipe, as the owner approved it on the skiff (24
    /// November): the game mesh from Pixal's own simplifier, an 8192 sheet for a
    /// hero, relief raised from the paint to a 6 degree steepness, the dense
    /// mesh's million triangles baked into the normal map, and 8192 JPEG at 92
    /// for the browser. Set dressing gets a lighter mesh and a 4096 sheet.
    /// </summary>
    private const string PixalHeroVertices = "100000";
    private const string PixalSetVertices = "40000";
    private const string PixalHeroAtlas = "8192";
    private const string PixalSetAtlas = "4096";
    private const string PixalBakeResolution = "2048";
    private const int PixalHeroColourSize = 8192;
    private const int PixalHeroDataSize = 8192;
    private const int PixalSetColourSize = 4096;
    private const int PixalSetDataSize = 2048;

    /// <summary>Which of the two things a frozen packet is asking for.</summary>
    public const string GenerateWork = "generate";
    public const string PrepareWork = "prepare";
    public const string SurfaceWork = "surface";
    public const string CompressWork = "compress";
    public const string CullWork = "cull";
    public const string RigWork = "rig";
    public const string SectionsWork = "sections";

    /// <summary>Every work kind that starts from a model and delivers a revision beside it.</summary>
    private static bool IsDerivativeWork(string? work) =>
        work is PrepareWork or SurfaceWork or CompressWork or CullWork or RigWork or SectionsWork;

    /// <summary>
    /// How close the camera will get. Everything that follows from it is a
    /// number the compiler is told; this is the one question the artist is
    /// asked, in the terms they would ask it.
    /// </summary>
    public const string SetDetail = "set";
    public const string HeroDetail = "hero";

    /// <summary>
    /// What a hero is made of, measured on a ninja.
    ///
    /// Triangles: at 20,000 a hand is two hundred triangles and its facets are
    /// the silhouette; 80,000 is where a character stops being a polygon
    /// count. The remesh grid goes finer with it (640 rather than 420, so
    /// fingers survive the rebuild) and smooths less (two passes rather than
    /// five, which was eroding exactly the small features the budget now
    /// affords). The generator's octree goes to 384 so there is detail for
    /// the grid to keep. The sheet doubles to 4096: twelve views at 768, each
    /// enhanced fourfold and baked at 2048, carry more than 2048 can hold. And
    /// the head is painted a second time on its own, cropped to match, because
    /// the face is what a close-up is of.
    ///
    /// Delivered as JPEG at 92 on a 4096 sheet with 2048 data maps: a lossless
    /// hero is sixty megabytes, and the whole route's masters stay on disk.
    ///
    /// The triangle count is the compiler's to decide wherever it can. A
    /// compiler whose remesh stage takes an asset name decides the budget
    /// itself, from what the thing is and its real size, before anything is
    /// painted -- which is the whole point: a budget decided after paint drags
    /// the UVs with every collapse. A hero is then told its role rather than a
    /// number. <see cref="HeroTriangleBudget"/>, <see cref="HeroTargetTriangles"/>
    /// and <see cref="RuntimeTriangleBudget"/> are only the fallback for a
    /// compiler older than that, which has nowhere else to get a number; the
    /// grid, smoothing, octree and sheet sizes stay part of the hero recipe
    /// either way.
    /// </summary>
    private const string HeroOctreeResolution = "384";
    private const string HeroTriangleBudget = "80000";
    private const string HeroTargetTriangles = "72000";
    private const string HeroVoxelResolution = "640";
    private const string HeroSmoothIterations = "2";
    private const string HeroAtlas = "4096";
    private const string SetAtlas = "2048";
    private const string HeadFrom = "0.78";
    private const string HeadFeather = "0.03";

    /// <summary>
    /// What a hero prop says where a figure says where its head is. A prop has
    /// no head, and the head pass on one paints its top again as if it were a
    /// face: a 2.2 m mast came back crisp dark iron above a hard line and flat
    /// putty below it. A prop is painted again in sections instead, which
    /// covers every face and so leaves no line.
    /// </summary>
    public const string NoHead = "none";
    private static readonly string[] HeadEnds = ["top", "left", "right", NoHead];

    /// <summary>
    /// What painting in sections is told, and what the artist is told it
    /// costs. The painter sees a whole model through twelve views of 768
    /// pixels whatever its size, so a 45 cm prop gets about 0.6 mm of surface
    /// a painted pixel and a 2.2 m skiff about 3 mm. Sections of about the size
    /// of a small prop get back the small prop's scale. A model shorter than
    /// the minimum is already painted at that scale and passes through; the
    /// section count is capped because each one is a painter run of its own.
    /// These are sent rather than left to the compiler's defaults, so the
    /// sentence the artist reads before queuing is what the stage was told.
    ///
    /// The finer cut is the one the owner judged. At 0.6 m a 2.2 m mast came
    /// out at about 1.3 mm a painted pixel in six sections; at 0.45 m, a 0.2
    /// overlap and a 0.05 margin of context a 2.2 m skiff came out at about
    /// 0.7 mm in 45 sections, and the owner called it impressive. Overlap and
    /// context are sent with the length because the compiler's defaults (0.25
    /// and 0.1) would make each finer section paint a third more than it keeps.
    /// </summary>
    public const double SectionsMinimumLength = 1.2;
    public const double SectionLength = 0.45;
    public const double SectionOverlap = 0.2;
    public const double SectionContext = 0.05;
    public const int MaximumSections = 48;

    /// <summary>
    /// What a section costs, as measured on the workstation that ran them: six
    /// sections of a 2.2 m mast in 11.6 minutes, 45 of the skiff in 109, and 32
    /// of a 98k-triangle skiff at about 3.5 each. It is said as a rough figure
    /// because another GPU and another mesh are another figure.
    /// </summary>
    private const int MinutesPerSection = 3;
    private const int HeroColourSize = 4096;
    private const int HeroDataSize = 2048;
    private const int HeroTextureQuality = 92;

    /// <summary>The stages a hero needs beyond the ordinary route.</summary>
    private static readonly string[] HeroExtraStages = [PaintHeadStage, CompressTexturesStage];

    /// <summary>
    /// What a reference image goes through to become something a browser can
    /// load. Four stages, because four different things happen and each can
    /// fail on its own terms.
    ///
    /// The generator makes a mesh from a picture, and that mesh is dense and
    /// sizeless: the first real run produced 2.4 million triangles at roughly
    /// two metres tall, whatever the subject. Staging gives it the size the
    /// artist said it is, which is what makes the reduction gate's millimetres
    /// mean anything. The remesh rebuilds it on a uniform grid and collapses
    /// that to a runtime budget, which is the difference between a clean
    /// surface and a creased one: a generator's output has no topology worth
    /// preserving, so compressing it only preserves the noise.
    ///
    /// What comes out of that is the right shape and the wrong colour: a
    /// generated mesh has none, and no UVs to put any on. Unwrapping gives it
    /// a map without moving a vertex, and the painter fills that map from the
    /// same reference the geometry came from. Only then is there a mesh worth
    /// exporting for a browser.
    /// </summary>
    public static readonly string[] ReferenceToModelRoute =
    [
        GeometryStage, StageMeshStage, RemeshStage,
        UvUnwrapStage, TextureStage, BrowserPayloadStage,
    ];

    /// <summary>
    /// The Pixal3D prop route's own stages. Pixal3D makes the shape and its
    /// game mesh in one generation, levelled at the real size by the same stage,
    /// so there is no staging or remesh: neither of the compiler's reductions
    /// keeps Pixal's hard edges. After the paint, the reference picture itself
    /// is laid onto the faces it shows (Pixal3D built the mesh in that
    /// picture's camera), then occlusion and the dense mesh's shape go into the
    /// maps, and the result is re-encoded for a browser.
    /// </summary>
    public static readonly string[] PixalRoute =
    [
        PixalGeometryStage, UvUnwrapStage, TextureStage, ProjectReferenceStage, BakeDetailStage, BakeShapeStage,
        BrowserPayloadStage, CompressTexturesStage,
    ];

    /// <summary>
    /// What an existing library model goes through to become a derivative a
    /// browser or a runtime can carry.
    ///
    /// This is the opposite handling from generation, and getting that
    /// backwards is the mistake worth naming. A generated mesh has no topology
    /// worth keeping, so it is rebuilt on a grid and painted from scratch. A
    /// model already in this library is the other way round: its UVs, its
    /// materials and its shell are what somebody looked at and accepted, and a
    /// preparation that rebuilt any of them would be throwing that review away
    /// and calling the result a derivative.
    ///
    /// So it is adopted unchanged, collapsed with its UVs and materials riding
    /// along, and exported through the same gate a generated model goes
    /// through. The fixed views are rendered twice, from the original and from
    /// the derivative, because comparing the two is the review -- a
    /// derivative's views on their own show only that something rendered.
    /// </summary>
    private static readonly RouteStepPlan[] PreparationPlan =
    [
        // Read from the original, not from the step before: this is evidence
        // of what the source looked like, and there is no step before it.
        new(ReviewViewsStage, ReadsOriginal: true),
        new(AdoptMeshStage, ReadsOriginal: true),
        new(ReduceMeshStage),
        new(BrowserPayloadStage),
        new(ReviewViewsStage),
    ];

    /// <summary>
    /// What a model goes through to be given the surfaces it is supposed to
    /// have.
    ///
    /// The bake comes first and reads the original, because occlusion is a
    /// measurement of the geometry and has nothing to do with which material a
    /// face points at. Assigning surfaces then releases the roughness map on
    /// every part it touches -- which is why the occlusion had to go into
    /// glTF's own slot, where releasing a map cannot take it with it.
    /// </summary>
    private static readonly RouteStepPlan[] SurfacingPlan =
    [
        new(ReviewViewsStage, ReadsOriginal: true),
        new(BakeDetailStage, ReadsOriginal: true),
        new(AssignSurfacesStage),
        new(BrowserPayloadStage),
        new(ReviewViewsStage),
    ];

    /// <summary>
    /// What a model goes through to carry the same pictures in fewer bytes.
    ///
    /// Three steps and no export: the re-encoder writes a model directly,
    /// because it rewrites the file around new image bytes rather than opening
    /// it in a mesh library. That is the whole reason a rigged asset survives
    /// this, and putting a round trip through Blender on the end would undo it.
    /// </summary>
    private static readonly RouteStepPlan[] CompressionPlan =
    [
        new(ReviewViewsStage, ReadsOriginal: true),
        new(CompressTexturesStage, ReadsOriginal: true),
        new(ReviewViewsStage),
    ];

    /// <summary>
    /// What a model goes through to lose the faces nothing can see.
    ///
    /// The cull writes a model directly, the same way the re-encoder does, so
    /// there is no export step on the end. That is deliberate rather than
    /// convenient: this stage only ever deletes faces, and a round trip
    /// through an exporter is exactly how a mesh acquires vertex data it did
    /// not arrive with. The fixed views are rendered from the source and from
    /// the result, because "you cannot see the difference" is the claim being
    /// made and somebody has to be able to check it.
    /// </summary>
    private static readonly RouteStepPlan[] CullPlan =
    [
        new(ReviewViewsStage, ReadsOriginal: true),
        new(CullUnseenStage, ReadsOriginal: true),
        new(ReviewViewsStage),
    ];

    /// <summary>
    /// What a humanoid goes through to be given a skeleton.
    ///
    /// The fixed views are rendered from the source and from the result in
    /// its bind pose, because a rig must not move the model it binds, and
    /// the two sets side by side are how a person sees that it did not. The
    /// judgement that matters more, whether it bends well, is the pose suite
    /// the rig stage renders itself and hands back as its own evidence.
    /// </summary>
    private static readonly RouteStepPlan[] RigPlan =
    [
        new(ReviewViewsStage, ReadsOriginal: true),
        new(RigStage, ReadsOriginal: true),
        new(ReviewViewsStage),
    ];

    /// <summary>
    /// What a large prop goes through to be painted again in sections.
    ///
    /// The section pass reads the reviewed model itself, not an adoption: it
    /// moves no vertex and no UV, only lays new paint on the same sheet. The
    /// export after it is the same gate every derivative leaves through, and
    /// the fixed views are rendered from the source and from the result,
    /// because "sharper and still the same object" is a claim somebody has to
    /// be able to check by looking.
    /// </summary>
    private static readonly RouteStepPlan[] SectionsPlan =
    [
        new(ReviewViewsStage, ReadsOriginal: true),
        new(PaintSectionsStage, ReadsOriginal: true),
        new(BrowserPayloadStage),
        new(ReviewViewsStage),
    ];

    /// <summary>Every stage painting in sections needs, for the capability check.</summary>
    public static readonly string[] SectionsRoute =
        [.. SectionsPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>Every stage rigging needs, for the capability check.</summary>
    public static readonly string[] RigRoute =
        [.. RigPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>Every stage the cull needs, for the capability check.</summary>
    public static readonly string[] CullRoute =
        [.. CullPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>Every stage re-encoding needs, for the capability check.</summary>
    public static readonly string[] CompressionRoute =
        [.. CompressionPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>Every stage surfacing needs, for the capability check.</summary>
    public static readonly string[] SurfacingRoute =
        [.. SurfacingPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>Every stage a preparation needs, for the capability check.</summary>
    public static readonly string[] PreparationRoute =
        [.. PreparationPlan.Select(step => step.Stage).Distinct(StringComparer.Ordinal)];

    /// <summary>A step of a route before its output suffix is known.</summary>
    private sealed record RouteStepPlan(string Stage, bool ReadsOriginal = false);

    /// <summary>
    /// The preparation route for one model.
    ///
    /// A painted model is re-baked after its reduction when the compiler can:
    /// the collapse moves the UVs with every vertex it keeps, the paint slides
    /// with them, and no deviation threshold sees it -- a floor brazier kept its
    /// silhouette within millimetres and lost its iron bands and rivets. The
    /// compiler bakes the paint back from the adopted original and holds the
    /// result to the original's fixed views, climbing its budget ladder until a
    /// rung looks like the original or refusing if none does. A model with no
    /// textures has nothing to slide, and an older compiler has no such stage;
    /// both keep the route they always had.
    /// </summary>
    private static RouteStepPlan[] PreparationPlanFor(bool rebake)
    {
        if (!rebake) return PreparationPlan;
        var reduction = Array.FindIndex(PreparationPlan, step => step.Stage == ReduceMeshStage);
        return [.. PreparationPlan.Take(reduction + 1), new(RebakeMapsStage),
                .. PreparationPlan.Skip(reduction + 1)];
    }

    /// <summary>
    /// The route for one request. Glazing is only in it when the artist said
    /// this thing has glass, because most props have none and a stage that ran
    /// anyway would have to guess which colour was a window -- turning an
    /// ordinary painted surface see-through.
    ///
    /// A hero is painted a second time up close, one of two ways, and never
    /// both. A figure or an animal has its head painted again on its own. A
    /// prop has no head, and the head pass on one leaves a hard line where its
    /// top was repainted as if it were a face; a large prop is painted again in
    /// sections instead, which covers every face. The artist says which it is.
    /// </summary>
    private static string[] RouteFor(
        string? glassColour, string detail = SetDetail, string headEnd = "top", bool sections = false,
        string engine = HunyuanEngine)
    {
        var hero = string.Equals(detail, HeroDetail, StringComparison.Ordinal);
        var prop = string.Equals(headEnd, NoHead, StringComparison.Ordinal);
        if (string.Equals(engine, PixalEngine, StringComparison.Ordinal))
        {
            // A prop route: no head pass. Sections go straight after the first
            // paint, on its own sheet; the bakes come after every paint, because
            // they read the colour; glass comes after the bakes, because the
            // shape bake wants the one painted material it was given.
            var pixal = new List<string> { PixalGeometryStage, UvUnwrapStage, TextureStage };
            if (hero && sections) pixal.Add(PaintSectionsStage);
            pixal.AddRange([ProjectReferenceStage, BakeDetailStage, BakeShapeStage]);
            if (!string.IsNullOrWhiteSpace(glassColour)) pixal.Add(GlassStage);
            pixal.AddRange([BrowserPayloadStage, CompressTexturesStage]);
            return [.. pixal];
        }
        var route = new List<string>
        {
            GeometryStage, StageMeshStage, RemeshStage, UvUnwrapStage, TextureStage,
        };
        // The second paint goes straight after the first, on the first's own
        // sheet, before anything else touches the file.
        if (hero && !prop) route.Add(PaintHeadStage);
        if (hero && prop && sections) route.Add(PaintSectionsStage);
        if (!string.IsNullOrWhiteSpace(glassColour)) route.Add(GlassStage);
        route.Add(BrowserPayloadStage);
        // Re-encoded last, from the exported payload, so what is compressed is
        // exactly what would otherwise have been delivered.
        if (hero) route.Add(CompressTexturesStage);
        return [.. route];
    }

    /// <summary>What each step is doing, in words an artist reading a queue would use.</summary>
    private static readonly Dictionary<string, string> StagePhase = new(StringComparer.Ordinal)
    {
        [GeometryStage] = "Making a mesh from the reference",
        [StageMeshStage] = "Setting its real size",
        [RemeshStage] = "Rebuilding it evenly at a size a browser can carry",
        [UvUnwrapStage] = "Unfolding it so it can be painted",
        [TextureStage] = "Painting it from the reference",
        [GlassStage] = "Letting the glass through",
        [BrowserPayloadStage] = "Preparing the mesh for the browser",
        [AdoptMeshStage] = "Taking the reviewed mesh in, unchanged",
        [ReduceMeshStage] = "Collapsing it to a runtime budget",
        [RebakeMapsStage] = "Baking its paint back from the original, and comparing the two",
        [ReviewViewsStage] = "Rendering the views a person judges it by",
        [BakeDetailStage] = "Baking the detail its own shape already implies",
        [AssignSurfacesStage] = "Giving each part the surface it should be",
        [CompressTexturesStage] = "Re-encoding its textures",
        [CullUnseenStage] = "Looking from every side, and dropping what nothing sees",
        [PaintHeadStage] = "Painting the head again, on its own, up close",
        [PaintSectionsStage] = "Painting it again in sections, each at a small prop's scale",
        [RigStage] = "Building a skeleton, binding it, and bending it through five poses",
        [PixalGeometryStage] = "Making the shape with Pixal3D, and standing it up at its real size",
        [BakeShapeStage] = "Baking the dense mesh's bolts, seams and edges into the normal map",
        [ProjectReferenceStage] = "Laying the reference picture onto the faces it shows",
    };

    /// <summary>
    /// What the reduction is told when the artist named no budget. The
    /// compiler decides from the mesh's real size and the asset's name, and
    /// its receipt records the decision. There used to be a fixed 10,000 here,
    /// which gave a coin and a cart the same budget; deciding what something
    /// should cost is the compiler's job, not a number this studio keeps.
    /// </summary>
    private const string AutoBudgetArgument = "auto";

    /// <summary>
    /// The generator's own default octree is a production one, and this is a
    /// browser studio: 512 cost two minutes and 2.4 million triangles, 256
    /// costs forty-seven seconds and 592,000, and both are reduced to the same
    /// runtime budget afterwards. Asking for more detail than survives the
    /// reduction is spending hardware on something nobody will ever see.
    /// </summary>
    private const string BrowserOctreeResolution = "256";

    /// <summary>
    /// What the painter is asked for, rather than what it defaults to.
    ///
    /// The painter's own defaults are six views at 512, and they are what makes
    /// a generated character fall apart the moment somebody pans in. Measured
    /// on a ninja: at six views the hands carry dark smears where the diffusion
    /// never saw between the fingers and guessed, and 512 is the resolution
    /// every one of those guesses is made at before being upscaled into a 2048
    /// atlas. Twelve views at 768 costs about a minute more on this hardware
    /// and removes most of both.
    ///
    /// These are the painter's ceiling, not a preference: it accepts 6 to 12
    /// views and either 512 or 768, and asking for more is refused.
    /// </summary>
    private const string PaintViews = "12";
    private const string PaintResolution = "768";

    /// <summary>
    /// The V1 cohort contract's ceiling, which the library also enforces. Sent
    /// only to a compiler too old to decide a remesh budget itself, where it is
    /// what "set dressing" has always meant (see <see cref="HeroOctreeResolution"/>).
    /// </summary>
    private const string RuntimeTriangleBudget = "20000";

    /// <summary>
    /// The option a remesh stage lists once it can decide its own budget. The
    /// compiler added the asset's name, role and notes to remesh together with
    /// Auto, so a stage that names one of them is a stage that takes "auto".
    /// </summary>
    private const string RemeshDecidesBudgetOption = "asset_name";

    /// <summary>The role a hero is budgeted as, in the compiler's vocabulary.</summary>
    private const string HeroRole = "hero";

    // Version 2 carries a route where version 1 carried one stage name. A
    // version 1 packet is not migrated, because the single stage it names is
    // the payload export reading an image, which could never have produced a
    // model. Failing it honestly beats rerunning work that cannot succeed.
    // Version 6 carries what kind of work the packet is asking for. A version
    // 5 packet named no work at all, and defaulting one would be guessing at
    // an answer the artist gave; there are only ever a handful in flight, and
    // asking again costs a click.
    // Version 8 carries what the cull is allowed to remove. A version 7 packet
    // could never be asking for one, so nothing is lost by refusing it -- and
    // there are only ever a handful in flight.
    // Version 9 carries how close the camera will get. A version 8 packet
    // could only have been asking for set dressing, and defaulting it would
    // be right -- but a packet that says nothing and a packet that says "set"
    // should not be the same bytes, so it is asked for again.
    // Auto budgets did not need a version 10. A version 9 preparation always
    // carried a number (the studio filled one in when the artist did not), so
    // reading one without AutoTriangleBudget as "the number it carries" is
    // exactly what it meant; only new packets can say "auto".
    private const int FrozenPacketVersion = 9;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What the request froze. Stored on the job so a restart, a retry, or an
    /// argument six weeks later all read the same answer: these bytes, that
    /// stage, this compiler.
    /// </summary>
    /// <param name="PaintReferenceAssetId">
    /// The picture a model already in the library is painted again from, when
    /// it is painted in sections: the one linked to it when the work was asked
    /// for, frozen by identity and by hash, so re-linking the model afterwards
    /// does not change what a queued job paints from. Generation needs none;
    /// its source is the picture. Old packets carry none, and could never have
    /// been asking for sections, so no new packet version is needed.
    /// </param>
    /// <param name="AutoTriangleBudget">
    /// True when the artist left the budget to the compiler. The number is
    /// then the compiler's to decide when the stage runs, and its receipt says
    /// what it chose; nothing here guesses at it in advance.
    /// </param>
    private sealed record FrozenModelRequest(
        int PacketVersion, Guid SourceAssetId, string SourceContentHash, string SourceName,
        int SourceRevisionNumber, RouteStep[] Route, string? CompilerVersion, DateTimeOffset FrozenAt,
        string Size, double SizeAdjust, string? GlassColour,
        string Work = GenerateWork, int TriangleBudget = 0,
        ModelSurfaceAssignment[]? Assignments = null, int BakeResolution = 0, double EdgeWear = 0,
        double Relief = 0,
        int ColourSize = 0, int DataSize = 0, int Quality = 0, string? TextureFormat = null,
        int Directions = 0, double Most = 0, double LargestPart = 0,
        bool IgnoreTransparency = false, string Detail = SetDetail, string HeadEnd = "top",
        bool AutoTriangleBudget = false,
        Guid? PaintReferenceAssetId = null, string? PaintReferenceContentHash = null,
        string Engine = HunyuanEngine, int Seed = 0, string? Up = null,
        string? Topology = null, double? SizeM = null, string? SizeIs = null, string? GameExport = null);

    /// <summary>
    /// What one step of the route actually did, kept so the delivery can say
    /// which steps ran, which were adopted from an interrupted attempt, and
    /// which had to be run a second time.
    /// </summary>
    /// <summary>
    /// One step of a frozen route: which stage, and what it writes. The shape
    /// is frozen with the route because a step's file has to be named before
    /// the stage runs, and a restart must name the same file it named before —
    /// otherwise a finished step looks unfinished and the expensive one is
    /// paid for twice.
    /// </summary>
    /// <param name="ReadsOriginal">
    /// True for a step that reads the frozen source rather than what the step
    /// before it wrote. A preparation needs two of them -- the source's own
    /// fixed views, and the adoption -- and without this a route can only ever
    /// be a chain, which would make the source's views a picture of the
    /// source's views.
    /// </param>
    private sealed record RouteStep(string Stage, string OutputSuffix, bool ReadsOriginal = false);

    /// <summary>
    /// A step's answer is present when its file is there -- or, for the one
    /// stage whose output is a directory of fixed views, when the directory
    /// is. A resume that could not recognise a directory would re-render
    /// evidence the compiler refuses to overwrite, and fail.
    /// </summary>
    private static bool Produced(string path) => File.Exists(path) || Directory.Exists(path);

    private sealed record StepOutcome(
        string Stage, string OutputPath, string? ReceiptJson, bool Adopted, bool RerunAfterPartial);

    /// <summary>Can this workstation do the work, and is it allowed to?</summary>
    public Task<ModelGenerationReadiness> PreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(ReferenceToModelRoute, "Model generation", cancellationToken, offerDetails: true);

    /// <summary>
    /// The same question for the preparation route, which shares this
    /// compiler and this commissioning but needs different stages. Asking
    /// about the route that will actually run is the difference between a
    /// refusal and a job that dies partway.
    /// </summary>
    public Task<ModelGenerationReadiness> PreparationPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(PreparationRoute, "Preparing a derivative", cancellationToken);

    /// <summary>The same question again, for the route that drops unseen faces.</summary>
    public Task<ModelGenerationReadiness> RigPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(RigRoute, "Rigging a humanoid", cancellationToken);

    public Task<ModelGenerationReadiness> CullPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(CullRoute, "Dropping unseen faces", cancellationToken);

    /// <summary>The same question again, for the route that re-encodes textures.</summary>
    public Task<ModelGenerationReadiness> CompressionPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(CompressionRoute, "Re-encoding textures", cancellationToken);

    /// <summary>The same question again, for the route that changes surfaces.</summary>
    public Task<ModelGenerationReadiness> SurfacingPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(SurfacingRoute, "Changing a model's surfaces", cancellationToken);

    /// <summary>The same question again, for the route that paints a model again in sections.</summary>
    public Task<ModelGenerationReadiness> SectionsPreflightAsync(CancellationToken cancellationToken) =>
        PreflightAsync(SectionsRoute, "Painting a model in sections", cancellationToken);

    private async Task<ModelGenerationReadiness> PreflightAsync(
        string[] route, string work, CancellationToken cancellationToken, bool offerDetails = false)
    {
        var capabilities = await compiler.DescribeAsync(cancellationToken);
        // Every step, not just the last one. A route whose first stage cannot
        // run is a route that cannot run, and saying so now is the difference
        // between a refusal and a job that dies partway.
        var canRun = route.All(capabilities.CanRun);
        var missing = route
            .Select(name => capabilities.Stages.FirstOrDefault(candidate => candidate.Stage == name))
            .SelectMany((found, index) => found is null
                ? [$"{route[index]} stage"]
                : found.Missing)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ModelGenerationReadiness(
            Installed: capabilities.Installed,
            Commissioned: capabilities.Commissioned,
            CanRun: canRun && capabilities.Commissioned,
            CompilerVersion: capabilities.Version,
            Checkout: capabilities.Checkout,
            Blender: capabilities.Blender,
            Missing: capabilities.Installed ? missing : ["compiler"],
            Detail: Explain(capabilities, canRun, route, work)
                + (offerDetails && capabilities.CanRun(PixalGeometryStage) && !HasPixalChecks(capabilities)
                    ? " Update the Reference Asset Compiler to enable checked Pixal3D prop generation." : ""),
            Sizes: capabilities.Stages
                .FirstOrDefault(candidate => candidate.Stage == StageMeshStage)?.Sizes
                ?.Select(size => new ModelSizeChoice(size.Size, size.Description, size.Metres))
                .ToArray(),
            // What each stage writes, frozen into a request so a restart names
            // the same files and a finished step is recognised as finished.
            Suffixes: capabilities.Stages.ToDictionary(
                stage => stage.Stage, stage => stage.OutputSuffix, StringComparer.Ordinal),
            Colours: capabilities.Stages
                .FirstOrDefault(candidate => candidate.Stage == GlassStage)?.Colours
                ?.Select(colour => new ModelColourChoice(colour.Colour, colour.Description))
                .ToArray(),
            Details: offerDetails ? DetailChoices(capabilities) : null,
            BlenderInstall: capabilities.BlenderInstall,
            RemeshDecidesBudget: RemeshDecidesBudget(capabilities),
            PreparationKeepsPaint: capabilities.CanRun(RebakeMapsStage),
            PaintSections: capabilities.CanRun(PaintSectionsStage) ? SectionsOffer : null,
            Engines: offerDetails ? EngineChoices(capabilities) : null,
            PixalGeometryChecks: HasPixalChecks(capabilities),
            PixalSolidExport: capabilities.CanRun(RemeshStage) && capabilities.Stages.Any(stage => stage.Stage == PixalGeometryStage && stage.Available
                && stage.Options?.Contains("game_export", StringComparer.Ordinal) == true
                && stage.Options.Contains("geometry", StringComparer.Ordinal)));
    }

    /// <summary>
    /// The ways of making a shape this compiler can run, each offered only
    /// where every stage of its route can: offering Pixal3D and failing an hour
    /// in, on a stage the compiler never had, would be the worse answer.
    /// </summary>
    private static ModelEngineChoice[] EngineChoices(CompilerCapabilities capabilities)
    {
        var choices = new List<ModelEngineChoice>();
        if (ReferenceToModelRoute.All(capabilities.CanRun))
            choices.Add(new(HunyuanEngine, "Hunyuan3D: characters, animals, anything with a head",
                "The shape in about a minute; rebuilt evenly at a budget the compiler decides"));
        if (PixalRoute.All(capabilities.CanRun) && HasPixalChecks(capabilities))
            choices.Add(new(PixalEngine, "Pixal3D: hard-surface props, with rivets, straps and seams as real geometry",
                "The shape in about 3 minutes on a GPU with 9 GiB free; a hero is painted on an 8192 sheet and "
                + "its million-triangle dense mesh baked into the normal map"));
        return [.. choices];
    }

    /// <summary>
    /// What painting in sections costs, said the same way wherever it is
    /// offered. The count is not known until the compiler has cut the model, so
    /// what is promised is its ceiling and what each one takes.
    /// </summary>
    private static readonly ModelSectionsOffer SectionsOffer = new(
        SectionsMinimumLength, SectionLength, MaximumSections,
        $"A prop longer than {Metres(SectionsMinimumLength)} m is cut into sections of about {Metres(SectionLength)} m "
        + "and each is painted again on its own, so it is as sharp up close as a small prop. One painter run a section, "
        + $"roughly {MinutesPerSection} minutes each, up to {MaximumSections}, on a GPU with 10.5 GiB free; a prop shorter "
        + "than that passes straight through at no cost.");

    /// <summary>A length as a person reads one: 1.2, 0.6, 2.35.</summary>
    private static string Metres(double metres) => metres.ToString("0.0#", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether this compiler's remesh stage decides a generated model's budget
    /// itself, read from the options the stage says it takes.
    /// </summary>
    private static bool RemeshDecidesBudget(CompilerCapabilities capabilities) =>
        capabilities.Stages.FirstOrDefault(stage => stage.Stage == RemeshStage)?.Options
            ?.Contains(RemeshDecidesBudgetOption, StringComparer.Ordinal) ?? false;

    private static bool HasPixalChecks(CompilerCapabilities capabilities) =>
        capabilities.Stages.Any(stage => stage.Stage == PixalGeometryStage
            && stage.Available && stage.Options?.Contains("topology", StringComparer.Ordinal) == true);

    /// <summary>
    /// Set dressing is always offered. A hero needs the compiler to paint a
    /// head on its own and to re-encode, and is offered only where it can:
    /// offering it and then failing an hour in would be the worse answer.
    /// </summary>
    private static ModelDetailChoice[] DetailChoices(CompilerCapabilities capabilities)
    {
        // The cost is said the way it will be decided: by the compiler from the
        // model's name and size where it can, as a fixed number where it cannot.
        var decides = RemeshDecidesBudget(capabilities);
        var choices = new List<ModelDetailChoice>
        {
            new(SetDetail, "Seen from a distance: set dressing, background props",
                decides
                    ? "A triangle budget the compiler decides from its name and real size, before painting; a 2048 sheet"
                    : "20,000 triangles, a 2048 sheet"),
        };
        if (HeroExtraStages.All(capabilities.CanRun))
            choices.Add(new(HeroDetail, "Shot up close: a character, a held prop",
                (decides
                    ? "A hero budget the compiler decides from its real size, before painting; "
                    : "80,000 triangles, ")
                + "a 4096 sheet, a figure's head painted a second time on its own"
                + (capabilities.CanRun(PaintSectionsStage) ? ", a large prop painted again in sections" : "")
                + "; several minutes longer"));
        return [.. choices];
    }

    // The route is named in the artist's terms, because the two share this
    // compiler and this commissioning but are not the same offer, and "model
    // generation is ready" is the wrong sentence to read next to a button that
    // reduces something.
    private static string Explain(
        CompilerCapabilities capabilities, bool canRun, string[] route, string work)
    {
        if (!capabilities.Installed) return capabilities.Detail ?? "The Reference Asset Compiler is not available.";
        if (!canRun)
        {
            // Name the step that is the problem. "Something is missing" sends
            // an artist looking at the wrong half of an install.
            var blocked = route
                .Select(name => (Name: name, Stage: capabilities.Stages.FirstOrDefault(s => s.Stage == name)))
                .FirstOrDefault(step => step.Stage is null || !step.Stage.Available);
            if (blocked.Name is null) return "The compiler cannot run this route yet.";
            if (blocked.Stage is null) return $"This compiler does not offer the {blocked.Name} stage.";
            var sentence = $"The compiler cannot run the {blocked.Name} stage yet: {string.Join(", ", blocked.Stage.Missing)} missing.";
            // "blender missing" names the gap; this studio looked for one, and
            // what it found (or did not) is the next thing the artist needs.
            return blocked.Stage.Missing.Contains("blender", StringComparer.OrdinalIgnoreCase)
                   && capabilities.BlenderInstall is { } blender
                ? $"{sentence} {blender.Problem ?? $"Framewright passed Blender at {blender.Executable}, and the compiler did not accept it."} {blender.Override}"
                : sentence;
        }
        return capabilities.Commissioned
            ? $"Ready. {work} will run on this workstation."
            : "This route is not commissioned yet, so nothing will be submitted. Everything else about it is ready.";
    }

    /// <summary>
    /// Freezes the chosen image revision and queues the work. A capability that
    /// is missing blocks here, before anything is submitted, rather than an hour
    /// into a stage that was never going to run.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueAsync(
        CreateModelGenerationRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Image))
            return RepositoryResult<JobSummary>.Invalid("A model is generated from a reference image.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid("That reference is archived. Restore it before generating from it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("A generated model needs a name of 1 to 120 characters.");

        // A size is not optional and is not guessed. Every measurement the
        // reduction gate makes afterwards is taken against it, so a default
        // here would quietly turn a real verdict into a meaningless one.
        var size = (request.Size ?? "").Trim();
        if (size.Length == 0 && request.SizeM is null)
            return RepositoryResult<JobSummary>.Invalid(
                "Say roughly how big this is, as a height on a person — for example knee height.");
        if (request.SizeAdjust is { } adjust && (adjust < 0.25 || adjust > 4.0))
            return RepositoryResult<JobSummary>.Invalid(
                "A size adjustment that large means a different size entirely; pick the nearer one.");

        // Glass is opt-in and named, never inferred. An empty answer is the
        // ordinary one: most props have no glass at all.
        var glassColour = string.IsNullOrWhiteSpace(request.GlassColour) ? null : request.GlassColour.Trim();

        // What makes the shape. Unnamed is Hunyuan3D, the route everything was
        // made by until Pixal3D was offered; Pixal3D is asked for by name.
        var engine = string.IsNullOrWhiteSpace(request.Engine) ? HunyuanEngine : request.Engine.Trim().ToLowerInvariant();
        if (engine is not (HunyuanEngine or PixalEngine))
            return RepositoryResult<JobSummary>.Invalid(
                $"The shape is made by \"{HunyuanEngine}\" or \"{PixalEngine}\", not \"{request.Engine}\".");
        var pixal = engine == PixalEngine;
        if (request.GameExport is not null && (!pixal || request.GameExport != "solid" || request.Topology != "closed"))
            return RepositoryResult<JobSummary>.Invalid("Rebuilding a single closed volume requires a closed Pixal3D prop.");
        var customGeometry = request.Topology is not null || request.SizeM is not null || request.SizeIs is not null;
        if (customGeometry && !pixal)
            return RepositoryResult<JobSummary>.Invalid("Measured prop dimensions and shell settings require Pixal3D.");
        if (request.Topology is not (null or "closed" or "open"))
            return RepositoryResult<JobSummary>.Invalid("Choose a closed prop or a surface with intentional openings.");
        if (request.SizeM is { } metres && (!double.IsFinite(metres) || metres is < 0.01 or > 50
            || size.Length != 0 || request.SizeAdjust is not null || request.SizeIs is not ("height" or "width" or "longest")))
            return RepositoryResult<JobSummary>.Invalid("Give one measured width, height or longest side between 0.01 and 50 metres.");
        if (request.SizeIs is not null && request.SizeM is null)
            return RepositoryResult<JobSummary>.Invalid("A measured dimension needs its length in metres.");
        var up = string.IsNullOrWhiteSpace(request.Up) ? null : request.Up.Trim().ToLowerInvariant();
        if (!pixal && (request.Seed is not null || up is not null))
            return RepositoryResult<JobSummary>.Invalid(
                "A seed and how the piece stands are Pixal3D's settings. Ask for Pixal3D, or leave them out.");
        if (up is not null && !PixalUps.Contains(up, StringComparer.Ordinal))
            return RepositoryResult<JobSummary>.Invalid(
                $"A piece stands on its \"shortest\" axis (a boat, a bench), its \"longest\" (a lantern, a column) "
                + $"or its \"middle\" one (a panel on its edge), not \"{request.Up}\".");
        if (request.Seed is < 1)
            return RepositoryResult<JobSummary>.Invalid("A seed is a whole number, 1 or more.");

        var readiness = await PreflightAsync(RouteFor(glassColour, engine: engine), "Model generation",
            cancellationToken, offerDetails: true);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);
        if (request.GameExport == "solid" && !readiness.PixalSolidExport)
            return RepositoryResult<JobSummary>.Unavailable("The installed compiler does not offer closed-volume retopology.");
        if (pixal && !readiness.PixalGeometryChecks)
            return RepositoryResult<JobSummary>.Unavailable("Update the Reference Asset Compiler to use measured prop checks.");
        if (glassColour is not null
            && readiness.Colours is { Length: > 0 } offered
            && !offered.Any(colour => string.Equals(colour.Colour, glassColour, StringComparison.OrdinalIgnoreCase)))
            return RepositoryResult<JobSummary>.Invalid(
                $"The compiler does not offer {glassColour} as a glass colour. "
                + $"It offers: {string.Join(", ", offered.Select(colour => colour.Colour))}.");

        // How close the camera gets. Unnamed is set dressing, which is what
        // most generated things are; a hero is asked for, and only where the
        // compiler can actually do the extra work it stands for.
        var detail = string.IsNullOrWhiteSpace(request.Detail) ? SetDetail : request.Detail.Trim().ToLowerInvariant();
        if (detail is not (SetDetail or HeroDetail))
            return RepositoryResult<JobSummary>.Invalid(
                $"Detail is \"{SetDetail}\" or \"{HeroDetail}\", not \"{request.Detail}\".");
        var hero = detail == HeroDetail;
        // A Pixal3D hero needs no stage its route does not already have: it has
        // no head pass, and it is always re-encoded.
        if (hero && !pixal && !(readiness.Details?.Any(choice => choice.Detail == HeroDetail) ?? false))
            return RepositoryResult<JobSummary>.Invalid(
                "This compiler cannot make a hero yet: it needs the paint-head and compress-textures stages. "
                + "Generate it as set dressing, or update the compiler.");
        // Where the head is in the picture. A standing figure's is at the top;
        // an animal seen from the side has it at one end, and cutting a cat
        // at the top would paint its ears and the tip of its tail. A prop has
        // none, and says so: cutting one at the top painted a mast's top 22%
        // again as if it were a face and left a hard line around the pole.
        var headEnd = string.IsNullOrWhiteSpace(request.HeadEnd)
            ? (pixal ? NoHead : "top") : request.HeadEnd.Trim().ToLowerInvariant();
        if (!HeadEnds.Contains(headEnd, StringComparer.Ordinal))
            return RepositoryResult<JobSummary>.Invalid(
                $"The head is at the \"top\", \"left\" or \"right\" of the picture, or \"{NoHead}\" for a prop, not \"{request.HeadEnd}\".");
        if (pixal && headEnd != NoHead)
            return RepositoryResult<JobSummary>.Invalid(
                "Pixal3D makes props, and a prop has no head. Make a figure or an animal with Hunyuan3D, "
                + "which paints its head again up close.");
        var prop = headEnd == NoHead;

        // Sections are for a hero prop, on by default where the compiler can
        // paint them, and the artist's to switch off. Asking for them anywhere
        // else is refused rather than quietly ignored: set dressing is seen
        // from too far away to gain from them, and a figure keeps its head pass.
        if (request.PaintSections is true && !hero)
            return RepositoryResult<JobSummary>.Invalid(
                "Only a hero is painted again in sections. Set dressing is seen from too far away to gain from it.");
        if (request.PaintSections is true && !prop)
            return RepositoryResult<JobSummary>.Invalid(
                "Sections are for a prop. A figure or an animal has its head painted again instead; "
                + $"say it has no head (\"{NoHead}\") to paint it in sections.");
        if (request.PaintSections is true && readiness.PaintSections is null)
            return RepositoryResult<JobSummary>.Invalid(
                "This compiler cannot paint a model in sections yet: it needs the paint-sections stage. "
                + "Leave sections off, or update the compiler.");
        // Wanted unless switched off, where the compiler can; the route decides
        // whether this request is one it applies to (a hero prop, and only that).
        var sections = readiness.PaintSections is not null && request.PaintSections is not false;

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. RouteFor(glassColour, detail, headEnd, sections, engine).Select(stage => new RouteStep(
                stage,
                readiness.Suffixes?.GetValueOrDefault(stage) ?? ".glb"))],
            readiness.CompilerVersion, now, size, request.SizeAdjust ?? 1.0, glassColour,
            GenerateWork, Detail: detail, HeadEnd: headEnd,
            // Frozen, so a restart asks the remesh the same question the
            // artist was shown: the compiler's budget, or the detail's number.
            AutoTriangleBudget: readiness.RemeshDecidesBudget,
            // The hero's delivery encoding, frozen with the rest: a lossless
            // 4096 hero is sixty megabytes, and the masters stay on disk.
            // A Pixal3D model is always re-encoded: its maps are 8192 or 4096.
            ColourSize: pixal ? (hero ? PixalHeroColourSize : PixalSetColourSize) : hero ? HeroColourSize : 0,
            DataSize: pixal ? (hero ? PixalHeroDataSize : PixalSetDataSize) : hero ? HeroDataSize : 0,
            Quality: pixal || hero ? HeroTextureQuality : 0, TextureFormat: pixal || hero ? "jpeg" : null,
            Engine: engine, Seed: request.Seed ?? 0, Up: up,
            Topology: pixal && readiness.PixalGeometryChecks ? request.Topology ?? "closed" : null,
            SizeM: request.SizeM, SizeIs: request.SizeIs, GameExport: request.GameExport);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Model from reference",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen reference queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", packet.Route.Select(step => step.Stage)),
            RequestJson = requestJson,
            // The same frozen request queued twice is the same work, and the
            // job identity says so rather than two stages racing each other.
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Freezes an existing library model and queues its preparation.
    ///
    /// Nothing about the source is touched, here or later. What this produces
    /// is a new revision in the source's own stack, and the source stays the
    /// current one until a person says otherwise -- because a derivative
    /// nobody has looked at should not quietly become the thing every picker
    /// reaches for.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueuePreparationAsync(
        CreateModelPreparationRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("A derivative is prepared from a model.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid(
                "That model is archived. Restore it before preparing a derivative from it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("A prepared derivative needs a name of 1 to 120 characters.");

        // No number is Auto: the compiler decides when the stage runs. A number
        // is the artist's, and is checked here against what the reducer is
        // known to refuse, so they hear it in their own terms rather than as a
        // stage that died.
        var auto = request.TriangleBudget is null;
        var budget = request.TriangleBudget ?? 0;
        if (!auto && budget is < 1_000 or > 200_000)
            return RepositoryResult<JobSummary>.Invalid(
                "A runtime triangle budget is between 1,000 and 200,000.");

        var sourcePath = assets.StoredFilePath(source.StoragePath);
        if (sourcePath is null)
            return RepositoryResult<JobSummary>.Invalid("That model's file is missing from the asset root.");
        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        if (!inspection.Ok || inspection.Profile is null)
            return RepositoryResult<JobSummary>.Invalid(
                inspection.Error ?? "That model could not be read, so nothing can be prepared from it.");
        // A budget at or above what the source already has would ask the
        // reducer to grow a mesh, which it refuses. The number the artist
        // needs to hear is the one their model actually has.
        if (!auto && budget >= inspection.Profile.TriangleCount)
            return RepositoryResult<JobSummary>.Invalid(
                $"That model already has {inspection.Profile.TriangleCount:N0} triangles. "
                + "A derivative has to be smaller than its source; choose a lower budget.");

        var readiness = await PreparationPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        if (auto && await RefuseAutoAsync(source.DisplayName, inspection.Profile, cancellationToken) is { } refusal)
            return refusal;

        // Re-baked only where there is paint to keep and a compiler that can
        // keep it. The route is frozen with the request, so a job queued now
        // runs the same steps after a restart whatever the compiler says then.
        var plan = PreparationPlanFor(readiness.PreparationKeepsPaint && inspection.Profile.ImageCount > 0);
        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. plan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            // A preparation asks none of generation's questions: the size is
            // already the size, and the glass is already glass.
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: PrepareWork, TriangleBudget: budget, AutoTriangleBudget: auto);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Runtime derivative",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", plan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// What the compiler says this model should cost at runtime, asked with
    /// the name the artist gave it and the size measured from its own bytes.
    ///
    /// A question rather than work, so it is answered whether or not any
    /// route is commissioned. When the compiler cannot answer -- missing,
    /// older than <c>rac budget</c>, or refusing this model -- the answer says
    /// why and carries no number, and the artist chooses one.
    /// </summary>
    public async Task<RepositoryResult<ModelTriangleBudget>> TriangleBudgetAsync(
        Guid assetId, CancellationToken cancellationToken)
    {
        var profile = await assets.ModelProfileAsync(assetId, cancellationToken);
        if (profile.Kind != RepositoryResultKind.Ok || profile.Value is null)
            return new RepositoryResult<ModelTriangleBudget>(null, profile.Kind, profile.Error);
        var suggestion = await compiler.SuggestTriangleBudgetAsync(
            profile.Value.DisplayName, profile.Value.Dimensions, cancellationToken);
        return RepositoryResult<ModelTriangleBudget>.Ok(new ModelTriangleBudget(
            assetId, profile.Value.TriangleCount, suggestion.State, suggestion.Detail,
            suggestion.Role, suggestion.RoleReason, suggestion.SizeClass, suggestion.LongestMetres,
            suggestion.TriangleBudget, suggestion.MaximumP99Metres, suggestion.MaximumMaxMetres,
            suggestion.Ladder ?? [], suggestion.Summary));
    }

    /// <summary>
    /// Whether Auto can prepare this model, asked of the same compiler the
    /// stage will run on, before anything is queued. Null means it can.
    ///
    /// The number is the compiler's, and is used only to say no early in the
    /// cases its reducer is known to refuse: a model already within the budget,
    /// a character (which takes the rig route instead), or a compiler that
    /// cannot decide at all. The stage still decides for itself when it runs.
    /// </summary>
    private async Task<RepositoryResult<JobSummary>?> RefuseAutoAsync(
        string name, GlbModelProfile profile, CancellationToken cancellationToken)
    {
        var suggestion = await compiler.SuggestTriangleBudgetAsync(name, profile.Dimensions, cancellationToken);
        if (suggestion.State == TriangleBudgetState.Refused)
            return RepositoryResult<JobSummary>.Invalid(
                $"The compiler could not choose a budget for this model: {suggestion.Detail} "
                + "Choose a triangle budget yourself instead.");
        if (suggestion.State != TriangleBudgetState.Decided)
            return RepositoryResult<JobSummary>.Unavailable(
                suggestion.Detail ?? "The compiler cannot choose a budget here. Choose one yourself instead.");
        if (suggestion.TriangleBudget is not { } decided)
            return RepositoryResult<JobSummary>.Invalid(
                $"{suggestion.Summary ?? "The compiler gives this model no reduction budget."} "
                + "Choose a triangle budget yourself to reduce it anyway.");
        if (decided >= profile.TriangleCount)
            return RepositoryResult<JobSummary>.Invalid(
                $"{suggestion.Summary ?? $"Auto would allow about {decided:N0} triangles."} This model already has "
                + $"{profile.TriangleCount:N0}, so Auto has nothing to reduce. Choose a lower budget yourself to "
                + "reduce it anyway.");
        return null;
    }

    /// <summary>
    /// What this model is currently made of, part by part.
    ///
    /// Run here and now rather than queued: it reads the model and measures it,
    /// takes a couple of seconds, and produces no asset. Queueing a question
    /// would mean an artist asking what something is made of and being told to
    /// come back later.
    /// </summary>
    public async Task<RepositoryResult<ModelSurfaceSurvey>> SurveyAsync(
        Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<ModelSurfaceSurvey>.NotFound();
        if (asset.Kind != nameof(AssetKind.Model))
            return RepositoryResult<ModelSurfaceSurvey>.Invalid("Only a model is made of surfaces.");
        var path = assets.StoredFilePath(asset.StoragePath);
        if (path is null)
            return RepositoryResult<ModelSurfaceSurvey>.Invalid("That model's file is missing from the asset root.");

        var capabilities = await compiler.DescribeAsync(cancellationToken);
        if (!capabilities.CanRun(SurveySurfacesStage))
            return RepositoryResult<ModelSurfaceSurvey>.Unavailable(
                "This compiler cannot survey a model's surfaces yet.");

        // Somewhere of its own, cleared afterwards: a survey is a question, and
        // a question should not leave anything behind.
        var workspace = Path.Combine(
            StudioPaths.ResolveDataRoot(configuration, environment), "surveys", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var run = await compiler.RunStageAsync(
                SurveySurfacesStage, path,
                Path.Combine(workspace, "survey.json"), Path.Combine(workspace, "report.json"),
                cancellationToken);
            if (!run.Ok || run.ReceiptJson is null)
                return RepositoryResult<ModelSurfaceSurvey>.Invalid(
                    run.Error ?? "The compiler could not read what this model is made of.");

            using var document = JsonDocument.Parse(run.ReceiptJson);
            var root = document.RootElement;
            var parts = root.TryGetProperty("parts", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().Select(part => new ModelSurfacePart(
                    Text(part, "part"), Text(part, "colour"), Text(part, "tone"),
                    part.TryGetProperty("faces", out var faces) ? faces.GetInt32() : 0,
                    Number(part, "share") ?? 0,
                    Number(part, "roughness_median"), Number(part, "metallic_median"),
                    part.TryGetProperty("reads_as", out var reads) && reads.ValueKind == JsonValueKind.String
                        ? reads.GetString() : null,
                    part.TryGetProperty("height_range", out var band) && band.ValueKind == JsonValueKind.Array
                        ? [.. band.EnumerateArray().Select(value => value.GetDouble())] : null))
                    .ToArray()
                : [];
            var vocabulary = root.TryGetProperty("vocabulary", out var words) ? words : default;
            return RepositoryResult<ModelSurfaceSurvey>.Ok(new ModelSurfaceSurvey(
                assetId,
                root.TryGetProperty("faces_total", out var total) ? total.GetInt32() : 0,
                parts,
                Strings(vocabulary, "surfaces"),
                Strings(vocabulary, "tones")));
        }
        catch (JsonException)
        {
            return RepositoryResult<ModelSurfaceSurvey>.Invalid(
                "The compiler's survey could not be read.");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { }
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString() ?? "" : "";

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Number
            ? found.GetDouble() : null;

    /// <summary>
    /// What a re-bake pairs, named by the steps that wrote them: the adoption is
    /// the dense original the reduction was made from, the reduction's receipt
    /// binds the two by hash (the compiler refuses a pair it does not name) and
    /// carries the budget ladder to climb, and the library model is what the
    /// result has to look like. Every file is one this job wrote or froze.
    /// </summary>
    private static Dictionary<string, string> RebakeOptions(IReadOnlyList<StepOutcome> earlier, string original)
    {
        var adopted = earlier.LastOrDefault(step => step.Stage == AdoptMeshStage)
            ?? throw new InvalidOperationException("A re-bake is routed after an adoption.");
        var reduced = earlier.LastOrDefault(step => step.Stage == ReduceMeshStage)
            ?? throw new InvalidOperationException("A re-bake is routed after a reduction.");
        return new Dictionary<string, string>
        {
            ["dense"] = adopted.OutputPath,
            ["reduction-report"] = ReceiptBeside(reduced.OutputPath),
            ["appearance-reference"] = original,
        };
    }

    /// <summary>A step's receipt is named after its output: step-N-stage.json.</summary>
    private static string ReceiptBeside(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + ".json");

    /// <summary>
    /// What the re-bake concluded, in the words a person judging the result
    /// needs: that the paint was baked back, at what count, and how close the
    /// fixed views came. Absent when the route did not re-bake.
    /// </summary>
    private static string? RebakeSummary(IEnumerable<StepOutcome> steps)
    {
        var receipt = steps.LastOrDefault(step => step.Stage == RebakeMapsStage)?.ReceiptJson;
        if (string.IsNullOrWhiteSpace(receipt)) return null;
        try
        {
            using var document = JsonDocument.Parse(receipt);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "status") != "accepted"
                || !root.TryGetProperty("attempts", out var attempts) || attempts.ValueKind != JsonValueKind.Array
                || attempts.GetArrayLength() == 0)
                return null;
            var last = attempts[attempts.GetArrayLength() - 1];
            if (!last.TryGetProperty("appearance", out var appearance)
                || !appearance.TryGetProperty("summary", out var summary)
                || !summary.TryGetProperty("beauty", out var lit) || !summary.TryGetProperty("albedo", out var unlit))
                return "its paint baked back from the original";
            return string.Create(CultureInfo.InvariantCulture,
                $"its paint baked back from the original and matched to it in four fixed views (lit {Number(lit, "minimum_ssim"):0.000}, unlit {Number(unlit, "minimum_ssim"):0.000})");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a section pass's receipt says it did, read field for field.</summary>
    private sealed record SectionsOutcome(
        bool Painted, int Count, double? WholeModelMmPerPixel, double? SectionMmPerPixel, string? Note);

    /// <summary>
    /// The section pass's receipt (<c>reference-asset-compiler.section-detail-paint.v1</c>),
    /// read: whether it painted, how many sections, and the millimetres of
    /// surface a painted pixel covered for the whole model and for its coarsest
    /// section. Null when there is no such receipt or it cannot be read.
    /// </summary>
    private static SectionsOutcome? ReadSections(string? receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt)) return null;
        try
        {
            using var document = JsonDocument.Parse(receipt);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("painted", out var painted)
                || painted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;
            var note = Text(root, "note") is { Length: > 0 } said ? said : null;
            if (!root.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
                return new SectionsOutcome(painted.GetBoolean(), 0, null, null, note);
            var sections = plan.TryGetProperty("sections", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray().ToArray() : [];
            var finest = sections
                .Select(section => Number(section, "painted_mm_per_pixel"))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .ToArray();
            return new SectionsOutcome(
                painted.GetBoolean(), sections.Length, Number(plan, "whole_model_mm_per_pixel"),
                // The coarsest section, which is what the compiler's own note quotes.
                finest.Length == 0 ? null : finest.Max(), note);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The section pass's numbers in the words a revision note uses: how many
    /// sections, and the millimetres of surface a painted pixel covered before
    /// and after. Null when the receipt does not carry them.
    /// </summary>
    private static string? SectionFigures(SectionsOutcome? outcome) =>
        outcome is { Painted: true, Count: > 0, WholeModelMmPerPixel: { } whole, SectionMmPerPixel: { } section }
            ? string.Create(CultureInfo.InvariantCulture,
                $"{outcome.Count} sections, at about {section:0.0} mm of surface a painted pixel, down from {whole:0.0} mm for the whole model")
            : null;

    /// <summary>
    /// The section pass as a generated model's lineage says it: painted again
    /// and by how much, or passed through and the compiler's own reason.
    /// </summary>
    private static string? SectionsSentence(SectionsOutcome? outcome) => outcome switch
    {
        null => null,
        { Painted: true } => SectionFigures(outcome) is { } figures
            ? $" Painted again in {figures}."
            : " Painted again in sections.",
        _ => $" Not painted in sections: {(outcome.Note ?? "the compiler passed it through unchanged").TrimEnd('.')}.",
    };

    /// <summary>
    /// The budget the compiler chose, as the receipt of the stage that chose it
    /// records it -- the reduction of a preparation, or the remesh of a
    /// generated model: role, size, the number, its reasons and every rung it
    /// tried. Absent when a number was given, or the compiler wrote no decision.
    /// </summary>
    private static JsonElement? BudgetDecision(IEnumerable<StepOutcome> steps)
    {
        var receipt = steps.LastOrDefault(step => step.Stage is ReduceMeshStage or RemeshStage)?.ReceiptJson;
        if (string.IsNullOrWhiteSpace(receipt)) return null;
        try
        {
            using var document = JsonDocument.Parse(receipt);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("budget_decision", out var decision)
                   && decision.ValueKind == JsonValueKind.Object
                ? decision.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? DecisionSummary(JsonElement? decision) =>
        decision is { } found && Text(found, "summary") is { Length: > 0 } summary ? summary : null;

    private static string[] Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Array
            ? [.. found.EnumerateArray().Select(value => value.GetString() ?? "")] : [];

    /// <summary>
    /// Queues a rig for one prepared humanoid.
    ///
    /// The compiler's landmark route against the UE5 Manny browser skeleton:
    /// derive joints from the mesh, bind with heat weights, gate the rig, and
    /// bend it through five poses. What comes back is a candidate revision,
    /// never an animation-ready badge; the pose suite is the evidence, and a
    /// person accepts or refuses it the way a preparation is judged.
    /// A model that already has a skeleton is refused: rigging it again would
    /// quietly replace a rig somebody may have approved.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueRigAsync(
        CreateModelRigRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("Only a model can be rigged.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid("That model is archived. Restore it before rigging it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("This needs a name of 1 to 120 characters.");

        var sourcePath = assets.ResolveContentPath(source);
        if (!File.Exists(sourcePath))
            return RepositoryResult<JobSummary>.Unavailable("The model file is missing from the asset root.");
        var inspected = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        if (inspected.Profile is null)
            return RepositoryResult<JobSummary>.Invalid(inspected.Error ?? "That model could not be read.");
        if (inspected.Profile.Rig.HasSkeleton)
            return RepositoryResult<JobSummary>.Invalid(
                "This model already has a skeleton. Rig the unrigged revision it came from instead.");

        var readiness = await RigPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. RigPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: RigWork, TriangleBudget: 0);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Rig",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", RigPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Queues a cull of the faces nothing outside a model can see.
    ///
    /// How much this finds is a fact about the subject, not about how it was
    /// made. Measured here: a hollow lantern loses 47% of its faces, a rigged
    /// character with a body modelled under its clothing 35%, and a generated
    /// ninja -- one closed surface over everything, nothing inside it -- five
    /// faces out of eighteen thousand. Nothing is wrong in that last case;
    /// there was simply nothing sealed inside to find.
    ///
    /// The other thing worth saying about the defaults: they are guards, not
    /// dials. A model that loses more than <c>Most</c> of itself is almost
    /// always inside out rather than hollow, and a connected part larger than
    /// <c>LargestPart</c> disappearing whole is something somebody modelled
    /// rather than debris. Both refuse; neither quietly trims what it finds.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueCullAsync(
        CreateModelCullRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("Faces are culled from a model.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid(
                "That model is archived. Restore it before culling it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("This needs a name of 1 to 120 characters.");

        var directions = request.Directions ?? 64;
        if (directions is < 8 or > 512)
            return RepositoryResult<JobSummary>.Invalid(
                "Look from 8 to 512 directions. 64 is enough for anything in this library.");
        var most = request.Most ?? 0.6;
        var largest = request.LargestPart ?? 0.1;
        foreach (var (share, what) in new[] { (most, "share this may remove"), (largest, "largest part") })
            if (share is <= 0 or > 1)
                return RepositoryResult<JobSummary>.Invalid($"The {what} runs above 0 and up to 1.");

        var readiness = await CullPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. CullPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: CullWork, TriangleBudget: 0,
            Assignments: null, BakeResolution: 0, EdgeWear: 0,
            ColourSize: 0, DataSize: 0, Quality: 0, TextureFormat: null,
            Directions: directions, Most: most, LargestPart: largest,
            IgnoreTransparency: request.IgnoreTransparency ?? false);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Fewer faces",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", CullPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Queues a re-encode of one model's textures.
    ///
    /// The cheapest change available to a finished asset: nothing about the
    /// mesh, the UVs or the rig moves, and the same pictures arrive in a
    /// fraction of the bytes. It still comes back as a revision beside the
    /// source rather than in place, because "the same pictures" is a claim
    /// somebody should be able to check by looking.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueCompressionAsync(
        CreateModelCompressionRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("Textures are re-encoded on a model.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid(
                "That model is archived. Restore it before re-encoding it.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("This needs a name of 1 to 120 characters.");

        var colour = request.ColourSize ?? 2048;
        var data = request.DataSize ?? 1024;
        foreach (var (size, what) in new[] { (colour, "colour"), (data, "data") })
            if (size is not (0 or 512 or 1024 or 2048 or 4096))
                return RepositoryResult<JobSummary>.Invalid(
                    $"A {what} map is capped at 512, 1024, 2048 or 4096, or 0 to keep what came in.");
        var quality = request.Quality ?? 90;
        if (quality is < 1 or > 100)
            return RepositoryResult<JobSummary>.Invalid("Quality runs from 1 to 100.");
        var format = string.IsNullOrWhiteSpace(request.Format) ? "jpeg" : request.Format.Trim().ToLowerInvariant();
        if (format is not ("jpeg" or "webp" or "png"))
            return RepositoryResult<JobSummary>.Invalid(
                "A texture is re-encoded as jpeg, webp or png. jpeg is the one every reader understands.");

        var readiness = await CompressionPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. CompressionPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: CompressWork, TriangleBudget: 0,
            Assignments: null, BakeResolution: 0, EdgeWear: 0,
            ColourSize: colour, DataSize: data, Quality: quality, TextureFormat: format);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Smaller textures",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", CompressionPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// What the cull is told. The transparency flag is only ever sent when it
    /// is true, because an absent flag and a false one mean the same thing to
    /// the stage and sending "false" would read like a decision somebody made.
    /// </summary>
    private static List<KeyValuePair<string, string>> CullOptions(FrozenModelRequest packet)
    {
        // Hyphenated, because the gateway turns a name straight into "--name"
        // and the compiler's flags are spelled the way a person would say them.
        var options = new List<KeyValuePair<string, string>>
        {
            new("directions", (packet.Directions == 0 ? 64 : packet.Directions)
                .ToString(CultureInfo.InvariantCulture)),
            new("most", (packet.Most == 0 ? 0.6 : packet.Most)
                .ToString(CultureInfo.InvariantCulture)),
            new("largest-part", (packet.LargestPart == 0 ? 0.1 : packet.LargestPart)
                .ToString(CultureInfo.InvariantCulture)),
        };
        // A switch, so it carries no value: passing one would make the compiler
        // read the next flag as this one's argument.
        if (packet.IgnoreTransparency) options.Add(new("ignore-transparency", ""));
        return options;
    }

    /// <summary>
    /// Freezes the artist's answers about what each part should be, and queues
    /// the work. Same shape as a preparation: what comes back is a revision
    /// beside the source, and the source stays the current one until somebody
    /// has looked at both.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueSurfacingAsync(
        CreateModelSurfacingRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("Surfaces are given to a model.");
        if (source.IsArchived)
            return RepositoryResult<JobSummary>.Invalid(
                "That model is archived. Restore it before changing its surfaces.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("This needs a name of 1 to 120 characters.");

        var assignments = request.Assignments ?? [];
        if (assignments.Length == 0)
            return RepositoryResult<JobSummary>.Invalid(
                "Say which part should be which surface. Changing nothing is not a change.");
        // The same part twice would make which surface wins depend on the order
        // it happened to be listed in, which is not something an artist should
        // have to know. The compiler refuses it too; saying so here is faster.
        var duplicated = assignments.GroupBy(entry => entry.Part, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicated is not null)
            return RepositoryResult<JobSummary>.Invalid(
                $"{duplicated.Key} is named twice. Each part gets one surface.");
        if (assignments.Any(entry => string.IsNullOrWhiteSpace(entry.Part) || string.IsNullOrWhiteSpace(entry.Surface)))
            return RepositoryResult<JobSummary>.Invalid("Every assignment names a part and a surface.");

        var resolution = request.Resolution ?? 1024;
        if (resolution is not (512 or 1024 or 2048))
            return RepositoryResult<JobSummary>.Invalid("A baked map is 512, 1024 or 2048 across.");
        var edgeWear = request.EdgeWear ?? 0;
        if (edgeWear is < 0 or > 1)
            return RepositoryResult<JobSummary>.Invalid("Edge wear runs from 0 to 1.");
        // Some relief by default: the painter writes no normal map at all, so a
        // model that asks for nothing here has no fine detail whatsoever and
        // cloth reads as painted plastic at close range.
        //
        // 0.3 rather than more, chosen by looking. The derivation multiplies a
        // luminance gradient by 32, so at 0.7 the cloth picks up a specular
        // sheen from normal variation finer than the weave it is supposed to
        // be describing -- cotton that reads as satin. At 0.3 the folds gain
        // depth and the fabric stays fabric.
        var relief = request.Relief ?? 0.3;
        if (relief is < 0 or > 1)
            return RepositoryResult<JobSummary>.Invalid("Relief runs from 0 to 1.");

        var readiness = await SurfacingPreflightAsync(cancellationToken);
        if (!readiness.CanRun)
            return RepositoryResult<JobSummary>.Unavailable(readiness.Detail);

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. SurfacingPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: SurfaceWork, TriangleBudget: 0,
            Assignments: assignments, BakeResolution: resolution, EdgeWear: edgeWear,
            Relief: relief);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Surfaces",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", SurfacingPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Whether this model can be painted again in sections, and what that costs,
    /// answered before anything is queued.
    ///
    /// A question rather than work, so it is answered whether or not it can
    /// run. Every reason it cannot is a sentence the artist can act on: link
    /// the picture, pick an earlier revision, update the compiler.
    /// </summary>
    public async Task<RepositoryResult<ModelSectionPaintPlan>> SectionPaintPlanAsync(
        Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);
        if (asset is null) return RepositoryResult<ModelSectionPaintPlan>.NotFound();
        if (asset.Kind != nameof(AssetKind.Model))
            return RepositoryResult<ModelSectionPaintPlan>.Invalid("Only a model is painted again in sections.");
        var check = await CheckSectionsAsync(asset, cancellationToken);
        return check.Kind == RepositoryResultKind.Ok && check.Value is not null
            ? RepositoryResult<ModelSectionPaintPlan>.Ok(check.Value.Plan)
            : new RepositoryResult<ModelSectionPaintPlan>(null, check.Kind, check.Error);
    }

    /// <summary>
    /// What one model and this workstation make of painting it in sections:
    /// the plan the artist is shown, the picture it would paint from, and how
    /// a refusal should be reported (a missing capability is not the model's
    /// fault, and is answered as unavailable rather than as a bad request).
    /// </summary>
    private sealed record SectionsCheck(
        ModelSectionPaintPlan Plan, RepositoryResultKind RefusalKind,
        AssetRecord? Picture, ModelGenerationReadiness Readiness);

    private async Task<RepositoryResult<SectionsCheck>> CheckSectionsAsync(
        AssetRecord source, CancellationToken cancellationToken)
    {
        var path = assets.StoredFilePath(source.StoragePath);
        if (path is null)
            return RepositoryResult<SectionsCheck>.Unavailable("That model's file is missing from the asset root.");
        var inspection = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(path, cancellationToken));
        if (!inspection.Ok || inspection.Profile is null)
            return RepositoryResult<SectionsCheck>.Invalid(
                inspection.Error ?? "That model could not be read, so it cannot be painted in sections.");
        var profile = inspection.Profile;
        var longest = profile.Dimensions.Length == 0 ? 0 : profile.Dimensions.Max();
        var materials = profile.Materials.Length;

        var readiness = await SectionsPreflightAsync(cancellationToken);
        var picture = source.ReferenceAssetId is { } pictureId
            ? await db.Assets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == pictureId, cancellationToken)
            : null;
        var picturePath = picture is null ? null : assets.StoredFilePath(picture.StoragePath);

        // In the order an artist would fix them: what the workstation lacks
        // first, because linking a picture to a model the compiler cannot paint
        // is wasted effort, then what the model lacks.
        var kind = RepositoryResultKind.Invalid;
        string? refusal;
        if (source.IsArchived)
            refusal = "That model is archived. Restore it before painting it in sections.";
        else if (!readiness.CanRun)
        {
            refusal = readiness.Detail;
            kind = RepositoryResultKind.Unavailable;
        }
        else if (source.ReferenceAssetId is null)
            refusal = "This model has no linked picture. Link the picture it was made from first: "
                + "each section is cropped out of it and painted from it.";
        else if (picture is null || picture.Kind != nameof(AssetKind.Image))
            refusal = "The picture linked to this model is no longer in the library. Link the picture it was made from again.";
        else if (picturePath is null)
            refusal = "The picture linked to this model is missing from the asset root.";
        else if (materials == 0)
            refusal = "This model has no material, so there is no paint to lay sections over.";
        else if (materials > 1)
            refusal = $"This revision carries {materials} materials. Sections are painted onto one painted sheet, "
                + "so paint sections before surfacing: pick an earlier revision, from before its surfaces were changed.";
        else if (profile.ImageCount == 0)
            refusal = "This model carries no painted texture, so there is nothing to lay sections over.";
        else if (longest < SectionsMinimumLength)
            refusal = $"This model is {Metres(longest)} m at its longest, under {Metres(SectionsMinimumLength)} m: its "
                + "first paint already sees it at a small prop's scale, so painting it in sections would change nothing.";
        else refusal = null;

        var cost = $"This model is {Metres(longest)} m at its longest. It is cut into sections of about "
            + $"{Metres(SectionLength)} m, up to {MaximumSections}, and each is painted again on its own from "
            + (picture is null ? "its linked picture" : $"\u201c{picture.DisplayName}\u201d")
            + $": one painter run a section, roughly {MinutesPerSection} minutes each, on a GPU with 21 GiB free. "
            + "Its fixed views are rendered before and after, and the result arrives as a revision beside this one.";
        return RepositoryResult<SectionsCheck>.Ok(new SectionsCheck(
            new ModelSectionPaintPlan(
                source.Id, refusal is null, refusal, cost, longest, materials,
                source.ReferenceAssetId, picture?.DisplayName,
                SectionsMinimumLength, SectionLength, MaximumSections),
            kind, refusal is null ? picture : null, readiness));
    }

    /// <summary>
    /// Freezes a model and the picture linked to it, and queues painting it
    /// again in sections. Same shape as every other derivative: a revision
    /// beside the source, the source still the current one, and nobody has
    /// judged it yet.
    ///
    /// Meant for props. Every face is painted again, so a hero prop that was
    /// given a head pass loses that hard band along with the soft paint below
    /// it; a figure painted this way loses its head pass too, which is rarely
    /// what anybody wants.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> EnqueueSectionPaintAsync(
        CreateModelSectionPaintRequest request, CancellationToken cancellationToken)
    {
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == request.SourceAssetId, cancellationToken);
        if (source is null) return RepositoryResult<JobSummary>.NotFound();
        if (source.Kind != nameof(AssetKind.Model))
            return RepositoryResult<JobSummary>.Invalid("Only a model is painted again in sections.");

        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 120)
            return RepositoryResult<JobSummary>.Invalid("This needs a name of 1 to 120 characters.");

        var check = await CheckSectionsAsync(source, cancellationToken);
        if (check.Kind != RepositoryResultKind.Ok || check.Value is null)
            return new RepositoryResult<JobSummary>(null, check.Kind, check.Error);
        if (!check.Value.Plan.CanQueue || check.Value.Picture is not { } picture)
            return new RepositoryResult<JobSummary>(null, check.Value.RefusalKind, check.Value.Plan.Refusal);
        var readiness = check.Value.Readiness;

        var now = timeProvider.GetUtcNow();
        var packet = new FrozenModelRequest(
            FrozenPacketVersion, source.Id, source.ContentHash, source.DisplayName,
            source.RevisionNumber ?? 1,
            [.. SectionsPlan.Select(step => new RouteStep(
                step.Stage,
                readiness.Suffixes?.GetValueOrDefault(step.Stage) ?? ".glb",
                step.ReadsOriginal))],
            readiness.CompilerVersion, now,
            Size: "", SizeAdjust: 1.0, GlassColour: null,
            Work: SectionsWork, TriangleBudget: 0,
            // The picture is frozen by identity and by hash: re-linking the
            // model while this waits does not change what it paints from.
            PaintReferenceAssetId: picture.Id, PaintReferenceContentHash: picture.ContentHash);
        var requestJson = JsonSerializer.Serialize(packet, Json);

        var jobId = Guid.NewGuid();
        var job = new JobRecord
        {
            Id = jobId,
            ShotId = Guid.Empty,
            ShotCode = name,
            Kind = "Painted in sections",
            WorkType = ModelWorkType,
            State = JobState.Queued.ToString(),
            Progress = 0,
            Phase = "Frozen model and picture queued",
            Backend = "Reference Asset Compiler",
            AdapterId = string.Join(" then ", SectionsPlan.Select(step => step.Stage)),
            RequestJson = requestJson,
            IdempotencyKey = IdempotencyKey(requestJson, jobId),
            CreatedAt = now,
            LastHeartbeatAt = now,
            Attempt = 1,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// Jobs a restart should pick up again. Their identity is unchanged, so a
    /// job that was running before the lights went out is the same job after.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> RecoverableJobsAsync(CancellationToken cancellationToken) =>
        await db.Jobs.AsNoTracking()
            .Where(job => job.WorkType == ModelWorkType
                && (job.State == "Queued" || job.State == "Running"))
            .Select(job => job.Id)
            .ToArrayAsync(cancellationToken);

    public async Task<RepositoryResult<JobSummary>> RunAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(candidate => candidate.Id == jobId && candidate.WorkType == ModelWorkType, cancellationToken);
        if (job is null) return RepositoryResult<JobSummary>.NotFound();

        // A delivery that arrives twice does not produce two models. The job
        // already knows what it produced.
        if (job.OutputAssetId is not null)
        {
            job.DeliveryCount += 1;
            await db.SaveChangesAsync(cancellationToken);
            return RepositoryResult<JobSummary>.Ok(Map(job));
        }

        if (string.IsNullOrWhiteSpace(job.RequestJson))
            return await FailAsync(job, "This job no longer has its frozen request packet.", cancellationToken);
        FrozenModelRequest? packet;
        try { packet = JsonSerializer.Deserialize<FrozenModelRequest>(job.RequestJson, Json); }
        catch (JsonException) { packet = null; }
        if (packet is null || packet.PacketVersion != FrozenPacketVersion)
            return await FailAsync(job,
                "This job was frozen against an older route that no longer describes what it would do. "
                + "Ask for it again to queue it against the current one.", cancellationToken);
        if (packet.Route is not { Length: > 0 })
            return await FailAsync(job, "This job's frozen request names no stages to run.", cancellationToken);

        // A preparation and a surfacing both start from a model and both
        // deliver a revision beside it. What differs is the route, not what
        // happens to the answer.
        var derivative = IsDerivativeWork(packet.Work);
        var preparing = derivative;
        var sourceWord = derivative ? "model" : "reference";
        var source = await db.Assets.AsNoTracking()
            .SingleOrDefaultAsync(asset => asset.Id == packet.SourceAssetId, cancellationToken);
        if (source is null)
            return await FailAsync(job, $"The {sourceWord} this job was frozen against is no longer in the library.", cancellationToken);
        var sourcePath = assets.StoredFilePath(source.StoragePath);
        if (sourcePath is null)
            return await FailAsync(job, $"The {sourceWord} file is missing from the asset root.", cancellationToken);

        // A model painted again in sections is painted from the picture that
        // was linked to it when the work was asked for -- that row and those
        // bytes, whatever the model is linked to now.
        string? paintReference = null;
        if (packet.Work == SectionsWork)
        {
            var picture = packet.PaintReferenceAssetId is { } pictureId
                ? await db.Assets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == pictureId, cancellationToken)
                : null;
            if (picture is null)
                return await FailAsync(job, "The picture this job was frozen against is no longer in the library.", cancellationToken);
            if (!string.Equals(picture.ContentHash, packet.PaintReferenceContentHash, StringComparison.OrdinalIgnoreCase))
                return await FailAsync(job,
                    "The picture this job was frozen against is not the same bytes any more. Ask for it again.", cancellationToken);
            paintReference = assets.StoredFilePath(picture.StoragePath);
            if (paintReference is null)
                return await FailAsync(job, "The picture this job was frozen against is missing from the asset root.", cancellationToken);
        }

        var workspace = Workspace(job.Id);
        Directory.CreateDirectory(workspace);

        // Percentages here are stage boundaries, not a clock. A bar that
        // interpolates against a guessed duration is a lie, and these stages'
        // durations are genuinely unknown.
        await ProgressAsync(job, JobState.Running, 10,
            preparing ? "Reading the frozen model" : "Reading the frozen reference", cancellationToken);

        // Each step reads what the one before it wrote, starting from the
        // frozen reference image. Every step keeps its own output and receipt,
        // which is what lets an interrupted job resume at the step it reached
        // instead of starting over -- and starting over here would mean asking
        // a GPU to build the same mesh a second time.
        var steps = packet.Route;
        var stepSource = sourcePath;
        var completed = new List<StepOutcome>(steps.Length);
        var rerunAfterPartial = false;

        for (var index = 0; index < steps.Length; index++)
        {
            var stage = steps[index].Stage;
            var outputPath = Path.Combine(workspace, $"step-{index + 1}-{stage}{steps[index].OutputSuffix}");
            var receiptPath = Path.Combine(workspace, $"step-{index + 1}-{stage}.json");
            var hasOutput = Produced(outputPath);
            var hasReceipt = File.Exists(receiptPath);
            if (stage == PixalGeometryStage && packet.Topology is not null && hasReceipt)
            {
                var receipt = await File.ReadAllTextAsync(receiptPath, cancellationToken);
                if (PixalCheckError(receipt) is { } error)
                    return await FailAsync(job, $"{job.ShotCode}: {error}", cancellationToken);
            }
            // Each step gets an equal share of the span between reading the
            // reference and validating the result. Equal because the studio has
            // no honest basis for claiming one step is a given fraction of the
            // whole; a weighting invented here would be a guess wearing a number.
            var progress = 10 + (index * 70 / steps.Length);
            var describe = StagePhase.TryGetValue(stage, out var phrase) ? phrase : $"Running {stage}";
            var step = steps.Length > 1 ? $"Step {index + 1} of {steps.Length}: {describe}" : describe;

            CompilerStageRun run;
            if (hasOutput && hasReceipt)
            {
                // The ambiguous window: this job was interrupted after the step
                // wrote its answer but before the answer was recorded. Adopt
                // what is on disk rather than silently running it again.
                await ProgressAsync(job, JobState.Running, progress,
                    $"{step} — reconciling an interrupted run", cancellationToken);
                run = new CompilerStageRun(true, stage, 0, 0, outputPath,
                    await File.ReadAllTextAsync(receiptPath, cancellationToken), null,
                    ["Adopted an existing result."]);
                completed.Add(new StepOutcome(stage, outputPath, run.ReceiptJson, true, false));
            }
            else
            {
                // Half an answer is not an answer, and running a step again is
                // a decision rather than a detail: it is announced, the remains
                // of the interrupted attempt are cleared so they cannot later
                // be adopted as if they were whole, and it is recorded on the
                // delivery. Only this step is redone; steps already finished
                // keep their answers.
                var partial = hasOutput || hasReceipt;
                rerunAfterPartial |= partial;
                if (partial)
                {
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                    // The fixed views are a directory, and the compiler refuses
                    // to render into one that exists. Half of somebody's
                    // evidence is not evidence, so it goes with the receipt.
                    if (Directory.Exists(outputPath)) Directory.Delete(outputPath, recursive: true);
                    if (File.Exists(receiptPath)) File.Delete(receiptPath);
                }

                await ProgressAsync(job, JobState.Running, progress,
                    partial ? $"{step} — running again: the previous attempt left an incomplete answer" : step,
                    cancellationToken);
                var execution = compiler.RunStageAsync(
                    stage, steps[index].ReadsOriginal ? sourcePath : stepSource,
                    outputPath, receiptPath, cancellationToken,
                    StageOptions(stage, packet, job, sourcePath, completed, paintReference));
                string? previousWait = null;
                while (!execution.IsCompleted)
                {
                    if (await Task.WhenAny(execution, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)) == execution)
                        break;
                    cancellationToken.ThrowIfCancellationRequested();
                    var waitFile = receiptPath + ".gpu-wait";
                    string? waiting = null;
                    try
                    {
                        if (File.Exists(waitFile)) waiting = await File.ReadAllTextAsync(waitFile, cancellationToken);
                    }
                    catch (IOException) { continue; } // The worker just acquired the reservation.
                    if (waiting == previousWait) continue;
                    previousWait = waiting;
                    await ProgressAsync(job, JobState.Running, progress,
                        waiting is null ? step : $"{step} · {waiting}", cancellationToken);
                }
                run = await execution;
                if (!run.Ok)
                    return await FailAsync(job, $"{job.ShotCode}: "
                        + (run.Error ?? $"The {stage} stage did not produce a result."), cancellationToken);
                if (stage == PixalGeometryStage && packet.Topology is not null && PixalCheckError(run.ReceiptJson) is { } error)
                    return await FailAsync(job, $"{job.ShotCode}: {error}", cancellationToken);
                if (!Produced(outputPath))
                    return await FailAsync(job,
                        $"The {stage} stage reported success but wrote nothing.", cancellationToken);
                completed.Add(new StepOutcome(stage, outputPath, run.ReceiptJson, false, partial));
            }

            // Painting an existing model in sections exists to change its paint.
            // When the compiler passed it through instead, a revision would be
            // the same model again under a new number, so the work stops here
            // with the compiler's own reason.
            if (stage == PaintSectionsStage && packet.Work == SectionsWork
                && ReadSections(completed[^1].ReceiptJson) is { Painted: false } passed)
                return await FailAsync(job,
                    $"Nothing was painted in sections: {passed.Note ?? "the compiler passed the model through unchanged."} "
                    + "No revision was delivered, because it would have been this one again.", cancellationToken);

            // A chain by default, because that is what almost every route is.
            // A step that says it reads the original keeps reading it, which is
            // how the source's own fixed views are views of the source rather
            // than of the previous step's evidence directory.
            stepSource = outputPath;
        }

        // The last step of a preparation is evidence, not a model. The model is
        // the last step that exported one.
        // Whichever step last wrote a model. A re-encode writes one directly,
        // because it rewrites the file rather than reopening it.
        var payloadStep = completed.LastOrDefault(
            step => step.Stage is BrowserPayloadStage or CompressTexturesStage or CullUnseenStage or RigStage);
        if (payloadStep is null)
            return await FailAsync(job, "This job's route never exported a model.", cancellationToken);
        var payloadPath = payloadStep.OutputPath;
        await ProgressAsync(job, JobState.Running, 85, "Validating the candidate", cancellationToken);
        if (!File.Exists(payloadPath))
            return await FailAsync(job, "The compiler reported success but wrote no model.", cancellationToken);

        // Library, lineage and the delivery marker arrive together, or none do.
        // The files remain content-addressed, so a retry can reuse their bytes.
        await using var delivery = await db.Database.BeginTransactionAsync(cancellationToken);
        async Task<RepositoryResult<JobSummary>> RefuseDeliveryAsync(string error)
        {
            await delivery.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var restored = await db.Jobs.SingleAsync(candidate => candidate.Id == job.Id, cancellationToken);
            return await FailAsync(restored, error, cancellationToken);
        }

        RepositoryResult<AssetSummary> imported;
        await using (var payload = File.OpenRead(payloadPath))
        {
            imported = await assets.ImportGeneratedModelAsync(
                payload, $"{job.ShotCode}.glb", payload.Length, cancellationToken,
                preparing ? "Prepared derivative" : "Generated model");
        }
        // An invalid model never becomes a usable one: the import validates the
        // container before it stores anything, and a refusal fails the job.
        if (imported.Kind != RepositoryResultKind.Ok || imported.Value is null)
            return await RefuseDeliveryAsync(imported.Error ?? "The generated model could not be validated.");

        // The reference may have moved on while this ran. That does not spoil
        // the candidate; it makes it a candidate from an older source, and it
        // says so rather than implying it matches what is on screen now.
        var stale = !string.Equals(source.ContentHash, packet.SourceContentHash, StringComparison.OrdinalIgnoreCase);
        var topologyChanged = false;
        var budgetDecision = BudgetDecision(completed);
        var sectionsOutcome = ReadSections(completed.LastOrDefault(step => step.Stage == PaintSectionsStage)?.ReceiptJson);
        if (preparing)
        {
            // A derivative belongs in its source's own revision stack, citing
            // it, rather than arriving as an unrelated model that happens to
            // look similar.
            var stacked = await StackDerivativeAsync(
                source, imported.Value, packet, payloadPath, sourcePath, cancellationToken, budgetDecision,
                RebakeSummary(completed), sectionsOutcome);
            if (stacked.Kind != RepositoryResultKind.Ok)
                return await RefuseDeliveryAsync(stacked.Error ?? "The derivative could not be recorded against its source.");
            topologyChanged = stacked.Value;
        }
        await RecordLineageAsync(imported.Value.Id, packet, source, stale, cancellationToken, budgetDecision,
            sectionsOutcome);

        job.OutputAssetId = imported.Value.Id;
        job.DeliveryCount += 1;
        job.ResultJson = JsonSerializer.Serialize(new
        {
            sourceAssetId = packet.SourceAssetId,
            frozenSourceHash = packet.SourceContentHash,
            sourceHashAtDelivery = source.ContentHash,
            staleSource = stale,
            compilerVersion = packet.CompilerVersion,
            route = packet.Route.Select(step => step.Stage),
            work = packet.Work,
            size = packet.Size,
            sizeAdjust = packet.SizeAdjust,
            packet.SizeM, packet.SizeIs, packet.Topology, packet.GameExport,
            glassColour = packet.GlassColour,
            detail = packet.Work == GenerateWork ? packet.Detail : null,
            headEnd = packet.Work == GenerateWork && packet.Detail == HeroDetail ? packet.HeadEnd : null,
            engine = packet.Work == GenerateWork ? packet.Engine : null,
            seed = packet.Work == GenerateWork && packet.Seed > 0 ? packet.Seed : (int?)null,
            up = packet.Work == GenerateWork ? packet.Up : null,
            triangleBudget = packet.TriangleBudget == 0 ? (int?)null : packet.TriangleBudget,
            // Whether the artist chose the number or left it to the compiler,
            // and, when the compiler chose, its own record of what and why --
            // copied from the reduction's receipt, not restated.
            triangleBudgetMode = packet.Work switch
            {
                PrepareWork => packet.AutoTriangleBudget ? "auto" : "chosen",
                // A generated model's number is the compiler's, or the fixed
                // one its detail choice stands for on an older compiler.
                GenerateWork => packet.AutoTriangleBudget ? "auto" : "fixed",
                _ => null,
            },
            budgetDecision,
            // What the section pass did, in its receipt's own numbers: whether
            // it painted, how many sections, and the millimetres of surface a
            // painted pixel covered before and after. Absent when it did not run.
            sections = sectionsOutcome is null ? null : new
            {
                painted = sectionsOutcome.Painted,
                count = sectionsOutcome.Count,
                wholeModelMmPerPixel = sectionsOutcome.WholeModelMmPerPixel,
                sectionMmPerPixel = sectionsOutcome.SectionMmPerPixel,
                note = sectionsOutcome.Note,
            },
            topologyChanged = preparing ? topologyChanged : (bool?)null,
            rerunAfterIncompleteAnswer = rerunAfterPartial,
            // Per step, because "the route succeeded" hides which half of it
            // actually ran on this attempt and which was picked up off disk.
            steps = completed.Select((step, index) => new
            {
                stage = step.Stage,
                // Named so evidence can be fetched later without a caller
                // having to know how a workspace lays its steps out.
                step = index + 1,
                adopted = step.Adopted,
                rerunAfterIncompleteAnswer = step.RerunAfterPartial,
                receiptSha256 = step.ReceiptJson is null ? null : Sha256(step.ReceiptJson),
            }).ToArray(),
            receiptSha256 = payloadStep.ReceiptJson is null ? null : Sha256(payloadStep.ReceiptJson!),
            payloadAssetId = imported.Value.Id,
            payloadContentHash = imported.Value.ContentHash,
        }, Json);
        await ProgressAsync(job, JobState.Completed, 100,
            stale
                ? $"Delivered from an older {sourceWord}"
                : preparing ? "Delivered, and waiting to be looked at" : "Delivered",
            cancellationToken);
        await delivery.CommitAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// What each stage is told beyond its three paths.
    ///
    /// The compiler owns what a stage accepts and what each setting means.
    /// What belongs here is only what this studio knows and the compiler
    /// cannot: that it is a browser studio, and what the artist said.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> StageOptions(
        string stage, FrozenModelRequest packet, JobRecord job, string referencePath,
        IReadOnlyList<StepOutcome> earlier, string? paintReference = null) => stage switch
    {
        RebakeMapsStage => RebakeOptions(earlier, referencePath),
        GeometryStage => new Dictionary<string, string>()
        {
            ["octree-resolution"] = packet.Detail == HeroDetail ? HeroOctreeResolution : BrowserOctreeResolution,
            // Otherwise the workspace is named after the reference's content
            // hash, which is what the stored file is called.
            ["asset-name"] = job.ShotCode,
        },
        StageMeshStage => new Dictionary<string, string>()
        {
            ["size"] = packet.Size,
            ["size-adjust"] = packet.SizeAdjust.ToString(CultureInfo.InvariantCulture),
        },
        // Rebuilt on a uniform grid rather than collapsed: a generator's
        // surface has no topology worth preserving, and collapsing it keeps
        // the noise as slivers and spikes.
        //
        // The budget is decided here, before anything is painted, so the
        // painter paints the final mesh. A compiler that can decide is told
        // "auto" and the name the model was given (a hero also its role, so a
        // close-up is budgeted as one); only an older compiler is given the
        // detail choice's fixed number.
        RemeshStage when packet.AutoTriangleBudget => packet.Detail == HeroDetail
            ? new Dictionary<string, string>
            {
                ["triangle-budget"] = AutoBudgetArgument,
                ["asset-name"] = job.ShotCode,
                ["role"] = HeroRole,
                ["voxel-resolution"] = HeroVoxelResolution,
                ["smooth-iterations"] = HeroSmoothIterations,
            }
            : new Dictionary<string, string>
            {
                ["triangle-budget"] = AutoBudgetArgument,
                ["asset-name"] = job.ShotCode,
            },
        RemeshStage => packet.Detail == HeroDetail
            ? new Dictionary<string, string>
            {
                ["triangle-budget"] = HeroTriangleBudget,
                ["target-triangles"] = HeroTargetTriangles,
                ["voxel-resolution"] = HeroVoxelResolution,
                ["smooth-iterations"] = HeroSmoothIterations,
            }
            : new Dictionary<string, string> { ["triangle-budget"] = RuntimeTriangleBudget },
        // Pixal3D's shape, its game mesh and their levelling come from one
        // stage. The size is the landmark the artist chose, a height; the seed
        // and the way it stands only when they were said.
        PixalGeometryStage => PixalGeometryOptions(packet),
        // Pixal3D's game mesh is already a .blend, unfolded as it stands.
        UvUnwrapStage when packet.Engine == PixalEngine => [],
        // A generated prop is an approved static triangle mesh: it is unfolded
        // as it stands rather than welded or remeshed, which would change the
        // geometry the reduction gate already measured.
        UvUnwrapStage => new Dictionary<string, string> { ["allow-triangulated-glb"] = "" },
        // The picture the geometry was made from, laid onto the faces it shows:
        // the compiler fits Pixal3D's camera from the geometry step's report and
        // refuses any other picture. It is the frozen reference both used.
        ProjectReferenceStage => new Dictionary<string, string>
        {
            ["geometry"] = GeometryReportOf(earlier),
            ["reference"] = referencePath,
        },
        // Occlusion and curvature, and no relief from the paint: after a
        // projection it raises the picture's grain into lumps, and the dense
        // mesh's own shape follows in the normal map. Occlusion is back since
        // the compiler's unwrap stopped two faces sharing a texel, which was the
        // cause of the dark triangles it was once left out for.
        BakeDetailStage when packet.Engine == PixalEngine => new Dictionary<string, string>
        {
            ["resolution"] = PixalBakeResolution,
        },
        BakeShapeStage => new Dictionary<string, string> { ["dense"] = DenseMeshOf(earlier) },
        // The paint is conditioned on the same picture the geometry came from.
        TextureStage => new Dictionary<string, string>
        {
            ["reference"] = referencePath,
            ["views"] = PaintViews,
            ["resolution"] = PaintResolution,
            // Naming the sheet also names the runner that writes it lossless.
            // Without this the painter's maps left as JPEG and were then
            // re-encoded twice more on the way to a browser.
            ["atlas"] = packet.Engine == PixalEngine
                ? (packet.Detail == HeroDetail ? PixalHeroAtlas : PixalSetAtlas)
                : packet.Detail == HeroDetail ? HeroAtlas : SetAtlas,
        },
        // The same picture and the same painter, cropped to the head by the
        // stage itself from the figure's own silhouette.
        PaintHeadStage => new Dictionary<string, string>
        {
            ["reference"] = referencePath,
            ["views"] = PaintViews,
            ["resolution"] = PaintResolution,
            ["atlas"] = HeroAtlas,
            ["head-from"] = HeadFrom,
            ["head-end"] = packet.HeadEnd,
            ["feather"] = HeadFeather,
        },
        // The whole picture the model was painted from -- the reference itself
        // when generating, the linked picture when repainting a library model.
        // The stage works out where each section sits in it from the model's
        // own outline; nobody hands it a crop. The lengths are the ones the
        // artist was quoted before queuing.
        PaintSectionsStage => new Dictionary<string, string>
        {
            ["reference"] = paintReference ?? referencePath,
            ["views"] = PaintViews,
            ["resolution"] = PaintResolution,
            ["atlas"] = packet.Engine == PixalEngine ? PixalHeroAtlas : HeroAtlas,
            ["section"] = SectionLength.ToString(CultureInfo.InvariantCulture),
            ["overlap"] = SectionOverlap.ToString(CultureInfo.InvariantCulture),
            ["context"] = SectionContext.ToString(CultureInfo.InvariantCulture),
            ["min-length"] = SectionsMinimumLength.ToString(CultureInfo.InvariantCulture),
            ["max-sections"] = MaximumSections.ToString(CultureInfo.InvariantCulture),
        },
        GlassStage => new Dictionary<string, string> { ["colour"] = packet.GlassColour ?? "" },
        // A preparation exists to keep what a reviewed mesh already has, so a
        // mesh with no UV layer is refused by name rather than delivered as a
        // derivative nobody can paint.
        AdoptMeshStage => new Dictionary<string, string> { ["require-uvs"] = "" },
        // The detail the geometry already implies. Occlusion goes into glTF's
        // own slot rather than the packed roughness map, because the stage
        // after this one releases that map on every part it gives a surface.
        BakeDetailStage => new Dictionary<string, string>
        {
            ["resolution"] = (packet.BakeResolution == 0 ? 1024 : packet.BakeResolution)
                .ToString(CultureInfo.InvariantCulture),
            ["edge-wear"] = packet.EdgeWear.ToString(CultureInfo.InvariantCulture),
            // The painter writes no normal map, so without this a surface has
            // no relief at all and close-up cloth reads as painted plastic.
            ["relief-from-paint"] = packet.Relief.ToString(CultureInfo.InvariantCulture),
        },
        // One --assign per part, which is why a stage's options are a sequence
        // of pairs rather than a dictionary: a dictionary would keep the last
        // and silently drop the rest.
        CullUnseenStage => CullOptions(packet),
        // A colour map is looked at and a data map is read as numbers by a
        // shader, so they are two questions rather than one setting.
        CompressTexturesStage => new Dictionary<string, string>
        {
            ["colour-size"] = (packet.ColourSize == 0 ? 2048 : packet.ColourSize)
                .ToString(CultureInfo.InvariantCulture),
            ["data-size"] = (packet.DataSize == 0 ? 1024 : packet.DataSize)
                .ToString(CultureInfo.InvariantCulture),
            ["quality"] = (packet.Quality == 0 ? 90 : packet.Quality)
                .ToString(CultureInfo.InvariantCulture),
            // The compiler's own name for it. "format" is what a person would
            // say and is not what the stage answers to, and a wrong option name
            // is only found by running it.
            ["texture-format"] = string.IsNullOrWhiteSpace(packet.TextureFormat)
                ? "jpeg" : packet.TextureFormat,
        },
        AssignSurfacesStage => (packet.Assignments ?? [])
            .Select(entry => new KeyValuePair<string, string>(
                "assign", $"{entry.Part}={entry.Surface}")),
        // The collapse, judged as a derivative of something already reviewed
        // rather than as a candidate production authority. The compiler owns
        // what that changes and records every allowance it makes.
        //
        // Auto is said, not implied by leaving the flag out: a compiler too old
        // to decide would read a missing flag as its own fixed default and
        // carry on, where "auto" is refused by name. The name is the one the
        // budget was suggested for, because the stored file is called after
        // its hash and a hash says nothing about whether this is a chest.
        ReduceMeshStage => packet.AutoTriangleBudget
            ? new Dictionary<string, string>()
            {
                ["triangle-budget"] = AutoBudgetArgument,
                ["asset-name"] = packet.SourceName,
                ["runtime-derivative"] = "",
            }
            : new Dictionary<string, string>()
            {
                ["triangle-budget"] = packet.TriangleBudget.ToString(CultureInfo.InvariantCulture),
                ["runtime-derivative"] = "",
            },
        _ => [],
    };

    /// <summary>Read the compiler's verdict on the game mesh before continuing a frozen job.</summary>
    private static string? PixalCheckError(string? receipt)
    {
        try
        {
            using var doc = JsonDocument.Parse(receipt ?? "{}");
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("game_mesh_check", out var check)
                && check.ValueKind == JsonValueKind.Object && check.TryGetProperty("ok", out var ok))
            {
                if (ok.ValueKind == JsonValueKind.True) return null;
                if (root.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(note.GetString()))
                    return note.GetString();
            }
        }
        catch (JsonException) { }
        return "The compiler supplied no passing game-mesh check. Painting is held.";
    }

    /// <summary>Carry the frozen size, shell, orientation and detail to the geometry stage.</summary>
    private static Dictionary<string, string> PixalGeometryOptions(FrozenModelRequest packet)
    {
        var options = new Dictionary<string, string>
        {
            ["vertex-target"] = packet.Detail == HeroDetail ? PixalHeroVertices : PixalSetVertices,
        };
        if (packet.SizeM is { } metres)
        {
            options["size-m"] = metres.ToString("R", CultureInfo.InvariantCulture);
            options["size-is"] = packet.SizeIs!;
        }
        else
        {
            options["size"] = packet.Size;
            options["size-adjust"] = packet.SizeAdjust.ToString(CultureInfo.InvariantCulture);
        }
        if (packet.Topology is not null) options["topology"] = packet.Topology;
        if (packet.GameExport is not null) options["game-export"] = packet.GameExport;
        if (packet.Seed > 0) options["seed"] = packet.Seed.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(packet.Up)) options["up"] = packet.Up;
        return options;
    }

    /// <summary>
    /// The dense mesh Pixal3D wrote beside the game mesh, from the same
    /// generation and levelled by the same transform: what the shape bake
    /// reads its bolts and seams from.
    /// </summary>
    private static string GeometryReportOf(IReadOnlyList<StepOutcome> earlier)
    {
        var geometry = earlier.LastOrDefault(step => step.Stage == PixalGeometryStage)
            ?? throw new InvalidOperationException("The projection comes after Pixal3D's geometry, which has not run.");
        return Path.ChangeExtension(geometry.OutputPath, ".json");
    }

    private static string DenseMeshOf(IReadOnlyList<StepOutcome> earlier)
    {
        var geometry = earlier.LastOrDefault(step => step.Stage == PixalGeometryStage)
            ?? throw new InvalidOperationException("The shape bake comes after Pixal3D's geometry, which has not run.");
        var directory = Path.GetDirectoryName(geometry.OutputPath) ?? "";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(geometry.OutputPath) + "-dense.blend");
    }

    /// <summary>
    /// Puts a delivered derivative in its source's own revision stack, and
    /// leaves the source the current one.
    ///
    /// The last part is the point. A revision added to a stack ordinarily
    /// becomes the current one, which is right when a person chose it and
    /// wrong here: nobody has looked at this yet. Until somebody does, every
    /// scene and every picker should go on reaching for the mesh that was
    /// reviewed, and the derivative should sit beside it waiting.
    ///
    /// Returns whether the derivative's topology differs from its source's.
    /// </summary>
    private async Task<RepositoryResult<bool>> StackDerivativeAsync(
        AssetRecord source, AssetSummary derivative, FrozenModelRequest packet,
        string derivativePath, string sourcePath, CancellationToken cancellationToken,
        JsonElement? budgetDecision = null, string? rebake = null, SectionsOutcome? sections = null)
    {
        var before = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(sourcePath, cancellationToken));
        var after = GlbModelInspector.Inspect(await File.ReadAllBytesAsync(derivativePath, cancellationToken));
        var changed = before.Profile is null || after.Profile is null
            || before.Profile.TriangleCount != after.Profile.TriangleCount
            || before.Profile.VertexCount != after.Profile.VertexCount;
        if (string.Equals(packet.Work, RigWork, StringComparison.Ordinal))
        {
            // The compiler's gate passed, and it is checked again here from the
            // file itself: a rig that arrives without a usable skin is refused
            // before it joins the library, not discovered in a scene later.
            var rig = after.Profile?.Rig;
            if (rig is null || !rig.HasSkeleton)
                return RepositoryResult<bool>.Invalid("The rig came back without a skeleton.");
            if (!rig.SkinWeightsValid || !rig.BindPoseValid || !rig.TransformsFinite)
                return RepositoryResult<bool>.Invalid(
                    "The rig came back with a skin this studio cannot trust: " + string.Join(" ", rig.Findings));
            if (before.Profile is not null && after.Profile is not null
                && before.Profile.TriangleCount != after.Profile.TriangleCount)
                return RepositoryResult<bool>.Invalid(
                    "Rigging changed the mesh: a skeleton is bound to a model, it does not reshape it.");
        }

        var note = new StringBuilder()
            .Append(packet.Work switch
            {
                SurfaceWork => "Resurfaced from ",
                CompressWork => "Textures re-encoded from ",
                CullWork => "Unseen faces dropped from ",
                RigWork => "Rigged from ",
                SectionsWork => "Painted in sections from ",
                _ => "Runtime derivative of ",
            })
            .Append(packet.SourceName)
            .Append(" revision ").Append(packet.SourceRevisionNumber)
            .Append(" (").Append(packet.SourceContentHash[..12]).Append("…)");
        if (string.Equals(packet.Work, CullWork, StringComparison.Ordinal)
            && before.Profile is not null && after.Profile is not null)
            // "Nothing moved" is the claim worth recording, because it is the
            // one that separates this from every other way of losing triangles.
            note.Append(": ").Append(before.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" triangles down to ")
                .Append(after.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(", nothing moved");
        else if (string.Equals(packet.Work, RigWork, StringComparison.Ordinal) && after.Profile is not null)
            // What a person needs before they look: which skeleton, and that
            // the mesh under it is the same one. The bending is in the views.
            note.Append(": ").Append(after.Profile.Rig.BoneCount.ToString(CultureInfo.InvariantCulture))
                .Append(" bones on the UE5 Manny browser skeleton, ")
                .Append(after.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" triangles unchanged; a candidate until its pose suite is reviewed");
        else if (string.Equals(packet.Work, CompressWork, StringComparison.Ordinal))
            note.Append(": ").Append((source.Bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" MB down to ")
                .Append((derivative.Bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" MB, same geometry");
        else if (string.Equals(packet.Work, SurfaceWork, StringComparison.Ordinal))
            note.Append(": ").Append(string.Join(", ", (packet.Assignments ?? [])
                .Select(entry => $"{entry.Part} as {entry.Surface}")));
        else if (string.Equals(packet.Work, SectionsWork, StringComparison.Ordinal))
            // The receipt's own numbers: how many sections, and how much
            // surface a painted pixel covered before and after.
            note.Append(": ").Append(SectionFigures(sections) ?? "painted again in sections")
                .Append("; same geometry and UVs");
        else if (before.Profile is not null && after.Profile is not null)
        {
            note.Append(": ").Append(before.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" triangles reduced to ")
                .Append(after.Profile.TriangleCount.ToString("N0", CultureInfo.InvariantCulture));
            // Who chose the number matters to the person judging the result:
            // a budget they typed is theirs to change, the compiler's is the
            // compiler's, and its reasoning is quoted rather than paraphrased.
            if (packet.AutoTriangleBudget)
                note.Append(", on the compiler's own budget")
                    .Append(DecisionSummary(budgetDecision) is { } said ? $" ({said.TrimEnd('.')})" : "");
            // A reduction of something painted is only half the story without
            // this: whether the paint came along, and how closely.
            if (rebake is not null) note.Append("; ").Append(rebake);
        }
        note.Append('.');

        var added = await assets.AddRevisionAsync(source.Id,
            new AddAssetRevisionRequest(derivative.Id, note.ToString(),
                packet.CompilerVersion is null
                    ? "Reference Asset Compiler"
                    : $"Reference Asset Compiler {packet.CompilerVersion}"),
            cancellationToken, makeCurrent: false);
        if (added.Kind != RepositoryResultKind.Ok)
            return RepositoryResult<bool>.Invalid(added.Error ?? "The derivative could not join its source's revisions.");

        var stack = await db.Assets
            .Where(asset => asset.RevisionFamilyId != null
                && asset.RevisionFamilyId == db.Assets
                    .Where(parent => parent.Id == source.Id)
                    .Select(parent => parent.RevisionFamilyId).FirstOrDefault())
            .ToListAsync(cancellationToken);
        var stored = stack.SingleOrDefault(asset => asset.Id == derivative.Id);
        if (stored is not null)
        {
            stored.PreparationTopologyChanged = changed;
            // Unaccepted, explicitly. Nothing copies the source's acceptance
            // across, and a topology that moved could not carry it anyway.
            stored.PreparationAcceptedAt = null;
            stored.PreparationAcceptedBy = "";
            stored.PreparationAcceptanceNote = "";
        }
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<bool>.Ok(changed);
    }

    /// <summary>
    /// Where this model came from, written where an artist will read it rather
    /// than only in a job row.
    /// </summary>
    private async Task RecordLineageAsync(
        Guid assetId, FrozenModelRequest packet, AssetRecord source, bool stale, CancellationToken cancellationToken,
        JsonElement? budgetDecision = null, SectionsOutcome? sections = null)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);
        if (asset is null) return;
        var preparing = IsDerivativeWork(packet.Work);
        var opening = packet.Work switch
        {
            PrepareWork => "Prepared for runtime from ",
            SurfaceWork => "Resurfaced from ",
            CompressWork => "Textures re-encoded from ",
            CullWork => "Unseen faces dropped from ",
            RigWork => "Rigged from ",
            SectionsWork => "Painted in sections from ",
            _ => "Generated from ",
        };
        var lineage = new StringBuilder()
            .Append(opening).Append(packet.SourceName)
            .Append(" revision ").Append(packet.SourceRevisionNumber)
            .Append(" (").Append(packet.SourceContentHash[..12]).Append("…)")
            .Append(" by the Reference Asset Compiler")
            .Append(packet.CompilerVersion is null ? "" : " " + packet.CompilerVersion)
            .Append('.');
        if (stale)
            lineage.Append(" That reference has changed since; this model is a candidate from the older source (now ")
                .Append(source.ContentHash[..12]).Append("…).");
        if (packet.Work == GenerateWork && packet.Engine == PixalEngine)
        {
            var hero = packet.Detail == HeroDetail;
            lineage.Append(hero ? " Made as a hero prop with Pixal3D" : " Made with Pixal3D")
                .Append(packet.Seed > 0 ? $" (seed {packet.Seed})" : "")
                .Append(": its own game mesh of ").Append(hero ? "100,000" : "40,000")
                .Append(" vertices, painted onto a ").Append(hero ? PixalHeroAtlas : PixalSetAtlas)
                .Append(" sheet, the relief raised from the paint and the dense mesh's shape baked into the normal map.");
            if (hero) lineage.Append(SectionsSentence(sections));
        }
        else if (packet.Work == GenerateWork && packet.Detail == HeroDetail)
        {
            // A figure's second paint is its head; a prop has no head pass, and
            // a large one's second paint is its sections, quoted from the receipt.
            var prop = packet.HeadEnd == NoHead;
            lineage.Append(prop ? " Made as a hero prop: rebuilt at " : " Made as a hero: rebuilt at ")
                .Append(packet.AutoTriangleBudget ? "the compiler's hero budget" : "80,000 triangles")
                .Append(", painted onto a 4096 sheet, ")
                .Append(prop ? "with no head pass." : "the head painted a second time on its own.");
            lineage.Append(SectionsSentence(sections));
        }
        // Who chose the triangle count, in the compiler's own sentence.
        if (packet.Work == GenerateWork && packet.AutoTriangleBudget)
            lineage.Append(" Its triangle budget was the compiler's")
                .Append(DecisionSummary(budgetDecision) is { } said ? $": {said}" : ".");
        asset.Notes = string.IsNullOrWhiteSpace(asset.Notes) ? lineage.ToString() : asset.Notes + "\n" + lineage;
        // A generated model's revision note is this sentence, because nothing
        // else has written one. A prepared derivative already has a better one --
        // it names what the reduction cost, in triangles -- and overwriting it
        // here threw that away.
        if (!preparing)
        {
            asset.ReferenceAssetId = source.Id;
            asset.RevisionPrompt = lineage.ToString();
        }
        asset.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ProgressAsync(
        JobRecord job, JobState state, int progress, string phase, CancellationToken cancellationToken)
    {
        job.State = state.ToString();
        job.Progress = progress;
        job.Phase = phase;
        job.LastHeartbeatAt = timeProvider.GetUtcNow();
        if (state == JobState.Completed) job.CompletedAt = job.LastHeartbeatAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Queue a failed or cancelled model job again, as itself.
    ///
    /// The same job and the same frozen packet, because nothing about what was
    /// asked for has changed -- what changed is usually the workstation: the
    /// painter refuses to launch without 21 GiB of GPU free, and the first
    /// cat through this route died at its paint while another program held
    /// seven of them. The steps already on disk are adopted when it runs, so a
    /// retry after a failed paint pays for the paint and nothing before it.
    /// </summary>
    public async Task<RepositoryResult<JobSummary>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(
            candidate => candidate.Id == jobId && candidate.WorkType == ModelWorkType, cancellationToken);
        if (job is null) return RepositoryResult<JobSummary>.NotFound();
        if (job.State is not (nameof(JobState.Failed) or nameof(JobState.Cancelled)))
            return RepositoryResult<JobSummary>.Conflict("Only a failed or cancelled model job can be retried.");
        if (string.IsNullOrWhiteSpace(job.RequestJson))
            return RepositoryResult<JobSummary>.Conflict("This model job no longer has its frozen request packet.");

        var now = timeProvider.GetUtcNow();
        job.State = JobState.Queued.ToString();
        job.Progress = 0;
        job.Phase = $"Queued again after: {job.Error}";
        job.Error = null;
        job.CompletedAt = null;
        job.AcknowledgedAt = null;
        job.Attempt += 1;
        job.LastHeartbeatAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    private async Task<RepositoryResult<JobSummary>> FailAsync(
        JobRecord job, string error, CancellationToken cancellationToken)
    {
        job.State = JobState.Failed.ToString();
        job.Phase = "Stopped";
        job.Error = error;
        job.CompletedAt = timeProvider.GetUtcNow();
        job.LastHeartbeatAt = job.CompletedAt;
        await db.SaveChangesAsync(cancellationToken);
        return RepositoryResult<JobSummary>.Ok(Map(job));
    }

    /// <summary>
    /// The fixed views this job rendered, of the source and of the derivative,
    /// so a person can put them side by side.
    ///
    /// Comparing the two is the review. A derivative's views on their own show
    /// only that something rendered, which is why both are kept and why each
    /// manifest carries the hash of the exact bytes it is a picture of.
    /// </summary>
    public async Task<RepositoryResult<ModelPreparationEvidence>> PreparationEvidenceAsync(
        Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.Jobs.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId && candidate.WorkType == ModelWorkType,
                cancellationToken);
        if (job is null) return RepositoryResult<ModelPreparationEvidence>.NotFound();
        if (string.IsNullOrWhiteSpace(job.RequestJson))
            return RepositoryResult<ModelPreparationEvidence>.Invalid("This job no longer has its frozen request packet.");

        FrozenModelRequest? packet;
        try { packet = JsonSerializer.Deserialize<FrozenModelRequest>(job.RequestJson, Json); }
        catch (JsonException) { packet = null; }
        // Both routes that start from a model render the same before-and-after
        // views, and both are judged the same way.
        if (!ProducesComparisonEvidence(packet))
            return RepositoryResult<ModelPreparationEvidence>.Invalid(
                "This job did not produce a before-and-after to compare.");
        var comparisonPacket = packet!;

        var workspace = Workspace(job.Id);
        ModelPreparationViews? source = null;
        ModelPreparationViews? derivative = null;
        ModelPreparationViews? deformation = null;
        for (var index = 0; index < comparisonPacket.Route.Length; index++)
        {
            if (comparisonPacket.Route[index].Stage == RigStage)
            {
                deformation ??= ReadRigViews(job.Id, workspace, index + 1);
                continue;
            }
            if (comparisonPacket.Route[index].Stage != ReviewViewsStage) continue;
            var read = ReadViews(job.Id, workspace, index + 1);
            if (read is null) continue;
            // The first views stage reads the original; the second reads what
            // the route produced. Which is which is the route's own answer,
            // not a guess from the order they happen to appear in.
            if (comparisonPacket.Route[index].ReadsOriginal) source ??= read;
            else derivative ??= read;
        }
        return RepositoryResult<ModelPreparationEvidence>.Ok(new ModelPreparationEvidence(
            job.Id, comparisonPacket.SourceAssetId, job.OutputAssetId, source, derivative, deformation));
    }

    /// <summary>
    /// The same evidence, found from the derivative rather than from the job
    /// that made it.
    ///
    /// A comparison that exists only in the page that started it is not a
    /// review gate: the artist closes the screen, comes back to decide, and
    /// the pictures the decision is supposed to rest on are gone. The
    /// derivative knows which job delivered it, so the evidence can be reached
    /// from the thing being judged.
    /// </summary>
    public async Task<RepositoryResult<ModelPreparationEvidence>> PreparationEvidenceForAssetAsync(
        Guid assetId, CancellationToken cancellationToken)
    {
        // Ordered here rather than in the query: SQLite cannot ORDER BY a
        // DateTimeOffset, and an asset is delivered by one job, so there is
        // nothing here worth paging.
        var delivered = await db.Jobs.AsNoTracking()
            .Where(job => job.WorkType == ModelWorkType && job.OutputAssetId == assetId)
            .Select(job => new { job.Id, job.CompletedAt, job.RequestJson })
            .ToArrayAsync(cancellationToken);
        var jobId = delivered
            .OrderByDescending(job => job.CompletedAt)
            .Where(job => RequestProducesComparisonEvidence(job.RequestJson))
            .Select(job => (Guid?)job.Id)
            .FirstOrDefault();
        // Most models were never prepared from anything, and saying so is an
        // ordinary answer rather than a failure. Refusing here put a 404 in
        // the console of every model an artist opened, which is noise this
        // studio treats as a defect -- and rightly, because a console that
        // always has errors in it is a console nobody reads.
        return jobId is null
            ? RepositoryResult<ModelPreparationEvidence>.Ok(
                new ModelPreparationEvidence(Guid.Empty, assetId, null, null, null))
            : await PreparationEvidenceAsync(jobId.Value, cancellationToken);
    }

    private static bool RequestProducesComparisonEvidence(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson)) return false;
        try
        {
            return ProducesComparisonEvidence(
                JsonSerializer.Deserialize<FrozenModelRequest>(requestJson, Json));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ProducesComparisonEvidence(FrozenModelRequest? packet) =>
        packet is not null && IsDerivativeWork(packet.Work);

    /// <summary>
    /// The pose suite a rig step rendered, found through the step's own
    /// receipt. The receipt names where the evidence is; that place has to be
    /// inside this job's workspace, or it is not this job's evidence.
    /// </summary>
    private static ModelPreparationViews? ReadRigViews(Guid jobId, string workspace, int step)
    {
        var directory = RigEvidenceDirectory(workspace, step);
        if (directory is null) return null;
        var manifest = Path.Combine(directory, "views.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var views = root.TryGetProperty("views", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray()
                    .Select(view => new ModelPreparationView(
                        view.GetProperty("view").GetString() ?? "",
                        view.GetProperty("pass").GetString() ?? "",
                        $"/api/jobs/{jobId}/preparation-views/{step}/{view.GetProperty("file").GetString()}",
                        view.TryGetProperty("sha256", out var hash) ? hash.GetString() ?? "" : ""))
                    .ToArray()
                : [];
            return new ModelPreparationViews(
                $"step-{step}-{RigStage}",
                root.TryGetProperty("source_sha256", out var of) ? of.GetString() ?? "" : "",
                views);
        }
        catch (JsonException) { return null; }
    }

    private static string? RigEvidenceDirectory(string workspace, int step)
    {
        var receipt = Path.Combine(workspace, $"step-{step}-{RigStage}.json");
        if (!File.Exists(receipt)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(receipt));
            if (!document.RootElement.TryGetProperty("evidence_directory", out var named)
                || named.GetString() is not { Length: > 0 } directory) return null;
            var full = Path.GetFullPath(directory);
            var root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (JsonException) { return null; }
    }

    private static ModelPreparationViews? ReadViews(Guid jobId, string workspace, int step)
    {
        var manifest = Path.Combine(workspace, $"step-{step}-{ReviewViewsStage}", "views.json");
        if (!File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;
            var views = root.TryGetProperty("views", out var listed) && listed.ValueKind == JsonValueKind.Array
                ? listed.EnumerateArray()
                    .Select(view => new ModelPreparationView(
                        view.GetProperty("view").GetString() ?? "",
                        view.GetProperty("pass").GetString() ?? "",
                        $"/api/jobs/{jobId}/preparation-views/{step}/{view.GetProperty("file").GetString()}",
                        view.TryGetProperty("sha256", out var hash) ? hash.GetString() ?? "" : ""))
                    .ToArray()
                : [];
            return new ModelPreparationViews(
                $"step-{step}-{ReviewViewsStage}",
                root.TryGetProperty("source_sha256", out var of) ? of.GetString() ?? "" : "",
                views);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// One rendered view, read back from the workspace that produced it.
    ///
    /// The file name is checked against the manifest rather than against the
    /// filesystem: a caller asking for a name the manifest does not list is
    /// asking for something that is not evidence, whatever is on disk.
    /// </summary>
    public async Task<RepositoryResult<(string Path, string ContentType)>> PreparationViewFileAsync(
        Guid jobId, int step, string file, CancellationToken cancellationToken)
    {
        var evidence = await PreparationEvidenceAsync(jobId, cancellationToken);
        if (evidence.Kind != RepositoryResultKind.Ok || evidence.Value is null)
            return RepositoryResult<(string, string)>.NotFound();
        var wanted = $"/api/jobs/{jobId}/preparation-views/{step}/{file}";
        var listed = (evidence.Value.Source?.Views ?? [])
            .Concat(evidence.Value.Derivative?.Views ?? [])
            .Concat(evidence.Value.Deformation?.Views ?? [])
            .Any(view => string.Equals(view.Url, wanted, StringComparison.Ordinal));
        if (!listed) return RepositoryResult<(string, string)>.NotFound();

        var fromRig = evidence.Value.Deformation?.Step == $"step-{step}-{RigStage}";
        var directory = fromRig
            ? RigEvidenceDirectory(Workspace(jobId), step)
            : Path.Combine(Workspace(jobId), $"step-{step}-{ReviewViewsStage}");
        if (directory is null) return RepositoryResult<(string, string)>.NotFound();
        var path = Path.Combine(directory, file);
        return File.Exists(path)
            ? RepositoryResult<(string, string)>.Ok((path, "image/png"))
            : RepositoryResult<(string, string)>.Unavailable("That view is no longer on disk.");
    }

    /// <summary>
    /// One durable directory per job, so an interrupted run leaves its answer
    /// where the same job will look for it.
    /// </summary>
    private string Workspace(Guid jobId) => Path.Combine(
        StudioPaths.ResolveDataRoot(configuration, environment), "generated-models", jobId.ToString("N"));

    private static string IdempotencyKey(string requestJson, Guid jobId) =>
        Sha256($"model:{requestJson}:{jobId}");

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JobSummary Map(JobRecord job) => new(
        job.Id, job.ShotId, job.ShotCode, job.Kind,
        Enum.TryParse<JobState>(job.State, out var state) ? state : JobState.Queued,
        job.Progress, job.Phase, job.Backend, job.CreatedAt, job.CompletedAt, job.Error,
        job.ManifestId, job.AdapterId, job.OutputAssetId,
        job.OutputAssetId is null ? null : $"/api/assets/{job.OutputAssetId}/content",
        job.ProviderRequestId, job.Attempt, job.RetryOfJobId, job.LastHeartbeatAt, job.WorkType);
}
