using Microsoft.EntityFrameworkCore;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Persistence;

public sealed class StudioDbContext(DbContextOptions<StudioDbContext> options, IProjectScope scope) : DbContext(options)
{
    /// <summary>
    /// The project every scoped read is filtered to.
    ///
    /// EF re-evaluates this per query and passes it as a parameter, so one context
    /// instance still honours a rebind. Exposed as a property rather than captured
    /// in the filter closure for exactly that reason.
    /// </summary>
    public Guid ActiveProjectId => scope.ProjectId;

    public DbSet<ShotRecord> Shots => Set<ShotRecord>();
    public DbSet<CommentRecord> Comments => Set<CommentRecord>();
    public DbSet<AssetReviewNoteRecord> AssetReviewNotes => Set<AssetReviewNoteRecord>();
    public DbSet<ShotVisualAuditRecord> ShotVisualAudits => Set<ShotVisualAuditRecord>();
    public DbSet<JobRecord> Jobs => Set<JobRecord>();
    public DbSet<ShotVersionRecord> ShotVersions => Set<ShotVersionRecord>();
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();
    public DbSet<SketchDocumentRecord> SketchDocuments => Set<SketchDocumentRecord>();
    public DbSet<PosePresetRecord> PosePresets => Set<PosePresetRecord>();
    public DbSet<GenerationManifestRecord> GenerationManifests => Set<GenerationManifestRecord>();
    public DbSet<AssetRecord> Assets => Set<AssetRecord>();
    public DbSet<AssetCollectionRecord> AssetCollections => Set<AssetCollectionRecord>();
    public DbSet<AssetPlacementRecord> AssetPlacements => Set<AssetPlacementRecord>();
    public DbSet<ReferenceRecord> References => Set<ReferenceRecord>();
    public DbSet<ReferenceVersionRecord> ReferenceVersions => Set<ReferenceVersionRecord>();
    public DbSet<CandidateVersionRecord> CandidateVersions => Set<CandidateVersionRecord>();
    public DbSet<FrameMarkupRecord> FrameMarkups => Set<FrameMarkupRecord>();
    public DbSet<TimelineClipRecord> TimelineClips => Set<TimelineClipRecord>();
    public DbSet<VoiceProfileRecord> VoiceProfiles => Set<VoiceProfileRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<LibraryAuthorityRecord> LibraryAuthorities => Set<LibraryAuthorityRecord>();
    public DbSet<LibraryAuthorityVersionRecord> LibraryAuthorityVersions => Set<LibraryAuthorityVersionRecord>();
    public DbSet<ShotRevisionProposalRecord> ShotRevisionProposals => Set<ShotRevisionProposalRecord>();
    public DbSet<MusicCompositionRecord> MusicCompositions => Set<MusicCompositionRecord>();
    public DbSet<MusicCompositionRevisionRecord> MusicCompositionRevisions => Set<MusicCompositionRevisionRecord>();
    public DbSet<MusicRenderRecord> MusicRenders => Set<MusicRenderRecord>();
    public DbSet<SceneRecord> Scenes => Set<SceneRecord>();
    public DbSet<SceneInstanceRecord> SceneInstances => Set<SceneInstanceRecord>();
    public DbSet<SceneAnnotationRecord> SceneAnnotations => Set<SceneAnnotationRecord>();
    public DbSet<SceneProposalRecord> SceneProposals => Set<SceneProposalRecord>();
    public DbSet<SceneBlockoutPlanRecord> SceneBlockoutPlans => Set<SceneBlockoutPlanRecord>();
    public DbSet<SceneBlockoutItemRecord> SceneBlockoutItems => Set<SceneBlockoutItemRecord>();
    public DbSet<SceneShotBindingRecord> SceneShotBindings => Set<SceneShotBindingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShotRecord>().HasKey(x => x.Id);
        // Widened from Code alone: a second project must be able to hold its own
        // SH-010. The same applies to VoiceProfiles.Name below.
        modelBuilder.Entity<ShotRecord>().HasIndex(x => new { x.ProjectId, x.Code }).IsUnique();
        modelBuilder.Entity<CommentRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<CommentRecord>().HasIndex(x => new { x.ShotId, x.Version });
        modelBuilder.Entity<AssetReviewNoteRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AssetReviewNoteRecord>().HasIndex(x => new { x.AssetId, x.CreatedAt });
        modelBuilder.Entity<ShotVisualAuditRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ShotVisualAuditRecord>().HasIndex(x => new { x.ShotId, x.ShotVersion, x.AssetId, x.ContractHash });
        modelBuilder.Entity<JobRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<JobRecord>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<JobRecord>().HasIndex(x => x.ManifestId);
        modelBuilder.Entity<JobRecord>().HasIndex(x => new { x.State, x.NextAttemptAt, x.CreatedAt });
        modelBuilder.Entity<JobRecord>().HasIndex(x => x.IdempotencyKey);
        modelBuilder.Entity<ShotVersionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ShotVersionRecord>().HasIndex(x => new { x.ShotId, x.Version }).IsUnique();
        modelBuilder.Entity<AuditEventRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AuditEventRecord>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<SketchDocumentRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SketchDocumentRecord>().HasIndex(x => x.ShotId).IsUnique();
        modelBuilder.Entity<SketchDocumentRecord>().Property(x => x.Revision).IsConcurrencyToken();
        modelBuilder.Entity<PosePresetRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<PosePresetRecord>().Property(x => x.Name).UseCollation("NOCASE");
        modelBuilder.Entity<PosePresetRecord>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        modelBuilder.Entity<GenerationManifestRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<GenerationManifestRecord>().HasIndex(x => new { x.ShotId, x.CreatedAt });
        modelBuilder.Entity<GenerationManifestRecord>().HasIndex(x => x.ManifestHash).IsUnique();
        modelBuilder.Entity<AssetRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AssetRecord>().HasIndex(x => new { x.ProjectId, x.ContentHash }).IsUnique();
        modelBuilder.Entity<AssetRecord>().HasIndex(x => new { x.RevisionFamilyId, x.RevisionNumber });
        modelBuilder.Entity<AssetCollectionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AssetCollectionRecord>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        modelBuilder.Entity<AssetPlacementRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<AssetPlacementRecord>().HasIndex(x => new { x.AssetId, x.ShotId, x.Role }).IsUnique();
        // Reference ids are readable slugs ("ennix"), so identity has to be
        // per-project or two projects can never both hold a character of that
        // name. Versions key off the same pair for the same reason.
        modelBuilder.Entity<ReferenceRecord>().HasKey(x => new { x.ProjectId, x.Id });
        modelBuilder.Entity<ReferenceRecord>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        modelBuilder.Entity<ReferenceVersionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ReferenceVersionRecord>().HasIndex(x => new { x.ProjectId, x.ReferenceId, x.Version }).IsUnique();
        modelBuilder.Entity<CandidateVersionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<CandidateVersionRecord>().HasIndex(x => new { x.ShotId, x.Version }).IsUnique();
        modelBuilder.Entity<FrameMarkupRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<FrameMarkupRecord>().HasIndex(x => new { x.ShotId, x.Version }).IsUnique();
        modelBuilder.Entity<FrameMarkupRecord>().Property(x => x.Revision).IsConcurrencyToken();
        modelBuilder.Entity<TimelineClipRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<TimelineClipRecord>().HasIndex(x => new { x.Track, x.StartFrame });
        modelBuilder.Entity<TimelineClipRecord>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<VoiceProfileRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<VoiceProfileRecord>().HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        modelBuilder.Entity<ProjectRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ProjectRecord>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<LibraryAuthorityRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<LibraryAuthorityRecord>().HasIndex(x => x.Slug).IsUnique();
        modelBuilder.Entity<LibraryAuthorityRecord>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<LibraryAuthorityRecord>().Property(x => x.UpdatedAt).IsConcurrencyToken();
        modelBuilder.Entity<LibraryAuthorityVersionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<LibraryAuthorityVersionRecord>().HasIndex(x => new { x.LibraryAuthorityId, x.Version }).IsUnique();
        modelBuilder.Entity<ShotRevisionProposalRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ShotRevisionProposalRecord>().HasIndex(x => new { x.ProjectId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<ShotRevisionProposalRecord>().HasIndex(x => new { x.ShotId, x.State, x.CreatedAtUnixMs });
        modelBuilder.Entity<MusicCompositionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<MusicCompositionRecord>().HasIndex(x => new { x.ProjectId, x.UpdatedAt });
        modelBuilder.Entity<MusicCompositionRevisionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<MusicCompositionRevisionRecord>().HasIndex(x => new { x.CompositionId, x.RevisionNumber }).IsUnique();
        modelBuilder.Entity<MusicRenderRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<MusicRenderRecord>().HasIndex(x => new { x.CompositionRevisionId, x.CreatedAt });
        modelBuilder.Entity<SceneRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneRecord>().Property(x => x.Version).IsConcurrencyToken();
        modelBuilder.Entity<SceneRecord>().HasIndex(x => new { x.ProjectId, x.UpdatedAt });
        modelBuilder.Entity<SceneInstanceRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneInstanceRecord>().HasIndex(x => new { x.SceneId, x.SortOrder });
        modelBuilder.Entity<SceneAnnotationRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneAnnotationRecord>().HasIndex(x => new { x.SceneId, x.InstanceId, x.State });
        modelBuilder.Entity<SceneProposalRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneProposalRecord>().HasIndex(x => new { x.ProjectId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<SceneBlockoutPlanRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneBlockoutPlanRecord>().HasIndex(x => new { x.ProjectId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<SceneBlockoutItemRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneBlockoutItemRecord>().HasIndex(x => new { x.PlanId, x.SortOrder });
        modelBuilder.Entity<SceneShotBindingRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneShotBindingRecord>().HasIndex(x => new { x.SceneId, x.CreatedAt });
        modelBuilder.Entity<SceneShotBindingRecord>().HasIndex(x => new { x.ShotId, x.ShotVersion }).IsUnique();

        ApplyProjectScope(modelBuilder);
    }

    /// <summary>
    /// The structural safeguard for the ~250 query sites the multi-project plan
    /// measured. Scoping by hand at each site would make a single omission a
    /// silent cross-project leak; here a read cannot omit it at all.
    ///
    /// Deliberately excluded:
    /// <list type="bullet">
    /// <item>Projects — the tenant table itself; the switcher must see them all.</item>
    /// <item>LibraryAuthorities and their versions — the library is global by
    /// definition. Scoping it would defeat the entire point of a shared library.</item>
    /// <item>AssetPlacements — a pure join between an Asset and a Shot that are
    /// both already scoped, so it inherits isolation without a column.</item>
    /// <item>AuditEvents — an append-only trail that has to stay readable across
    /// projects, including for rows whose project has since been deleted.</item>
    /// </list>
    ///
    /// Raw SQL and <c>DbSet.Find</c> bypass query filters; the initializer and the
    /// backup path are the only callers that do either, and both are deliberate.
    /// </summary>
    private void ApplyProjectScope(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShotRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<CommentRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<AssetReviewNoteRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<ShotVisualAuditRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<JobRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<ShotVersionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SketchDocumentRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<PosePresetRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<GenerationManifestRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<AssetRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<AssetCollectionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<ReferenceRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<ReferenceVersionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<CandidateVersionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<FrameMarkupRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<TimelineClipRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<VoiceProfileRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<ShotRevisionProposalRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<MusicCompositionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<MusicCompositionRevisionRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<MusicRenderRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneInstanceRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneAnnotationRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneProposalRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneBlockoutPlanRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneBlockoutItemRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
        modelBuilder.Entity<SceneShotBindingRecord>().HasQueryFilter(x => x.ProjectId == ActiveProjectId);
    }

    /// <summary>
    /// Stamps the active project onto new rows so an insert cannot land unscoped.
    ///
    /// The query filter makes reads safe; this makes writes safe. Without it a
    /// forgotten assignment would write ProjectId = Guid.Empty and the row would
    /// then be invisible to every project, which is a subtler failure than a leak
    /// but just as damaging to append-only evidence.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampNewRows();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampNewRows();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampNewRows()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;
            var property = entry.Metadata.FindProperty(nameof(ShotRecord.ProjectId));
            if (property is null || entry.Metadata.ClrType == typeof(ProjectRecord)) continue;
            if (entry.Property(property.Name).CurrentValue is Guid existing && existing != Guid.Empty) continue;
            entry.Property(property.Name).CurrentValue = ActiveProjectId;
        }
    }
}

public sealed class ProjectRecord
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Production { get; set; }
    public required string SequenceCode { get; set; }
    public required string SequenceName { get; set; }
    public int FramesPerSecond { get; set; }
    public required string AspectRatio { get; set; }
    public int DeliveryWidth { get; set; }
    public int DeliveryHeight { get; set; }
    public string ColorSpace { get; set; } = "Rec.709";
    public int AudioSampleRate { get; set; } = 48000;
    public required string VisualStyle { get; set; }
    public required string WorldCanon { get; set; }
    public required string PromptDirectives { get; set; }
    public required string NegativeDirectives { get; set; }

    /// <summary>
    /// Mirrors the in-memory active selection so it survives a restart. Exactly
    /// one project carries it; the activate endpoint clears the others in the same
    /// transaction.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ShotRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Stage { get; set; }
    public required string Approval { get; set; }
    public int Version { get; set; }
    public int DurationFrames { get; set; }
    public int SortOrder { get; set; }
    public int VisualVariant { get; set; }
    public required string ContinuityState { get; set; }
    public required string Camera { get; set; }
    public required string Action { get; set; }
    public required string ReferenceIdsJson { get; set; }
    public required string ConstraintsJson { get; set; }
    public Guid? CurrentAssetId { get; set; }
    public Guid? VideoFirstFrameCandidateId { get; set; }
    public Guid? VideoLastFrameCandidateId { get; set; }
    public Guid? ProductionVideoJobId { get; set; }
    public Guid? ProductionVideoAssetId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CommentRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int Version { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public required string Body { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReferenceId { get; set; }
    public int? ReferenceVersion { get; set; }
}

public sealed class AssetReviewNoteRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AssetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public required string Body { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ShotRevisionProposalRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int BaseVersion { get; set; }
    public DateTimeOffset BaseUpdatedAt { get; set; }
    public required string CreativeDirection { get; set; }
    public required string Rationale { get; set; }
    public required string DesiredMediaType { get; set; }
    public required string AuthorityIdsJson { get; set; }
    public required string NoteIdsJson { get; set; }
    public required string State { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    /// <summary>When the artist pushed this accepted direction into the revision surface. Applying twice replays this one.</summary>
    public DateTimeOffset? AppliedAt { get; set; }
    /// <summary>The director-context token the agent had read when it proposed. A view that moved on invalidates the proposal.</summary>
    public string? ObservedStateToken { get; set; }
    /// <summary>Constraints the proposal promises not to touch. Always a subset of the shot's own rules and locked authorities.</summary>
    public string PreservedConstraintsJson { get; set; } = "[]";
}

public sealed class ShotVisualAuditRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int ShotVersion { get; set; }
    public Guid AssetId { get; set; }
    public required string AssetHash { get; set; }
    public required string ContractHash { get; set; }
    public required string State { get; set; }
    public required string GateState { get; set; }
    public required string Summary { get; set; }
    public required string FindingsJson { get; set; }
    public required string DecisionsJson { get; set; }
    public string? AdoptedShotProposalJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class JobRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public required string ShotCode { get; set; }
    public required string Kind { get; set; }
    public required string State { get; set; }
    public int Progress { get; set; }
    public required string Phase { get; set; }
    public required string Backend { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>
    /// When the artist acknowledged a terminal failure. A dismissal that lived
    /// only in the page came back on every reload, so a job that failed once
    /// went on interrupting for ever. Seen is a fact about the job, not about
    /// the tab it was seen in.
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? Error { get; set; }
    public Guid? ManifestId { get; set; }
    public string? AdapterId { get; set; }
    public Guid? OutputAssetId { get; set; }
    public string? ProviderRequestId { get; set; }
    public int Attempt { get; set; } = 1;
    public Guid? RetryOfJobId { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string WorkType { get; set; } = "Shot";
    public string? RequestJson { get; set; }
    public string? ResultJson { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int DeliveryCount { get; set; }
}

public sealed class ShotVersionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int Version { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Stage { get; set; }
    public required string Approval { get; set; }
    public int DurationFrames { get; set; }
    public int VisualVariant { get; set; }
    public required string Camera { get; set; }
    public required string Action { get; set; }
    public required string ReferenceIdsJson { get; set; }
    public required string ConstraintsJson { get; set; }
    public required string ManifestHash { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? VideoFirstFrameCandidateId { get; set; }
    public Guid? VideoLastFrameCandidateId { get; set; }
    public Guid? ProductionVideoJobId { get; set; }
    public Guid? ProductionVideoAssetId { get; set; }
    public DateTimeOffset RatifiedAt { get; set; }
}

public sealed class AuditEventRecord
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SketchDocumentRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int Revision { get; set; }
    public required string CreativeBrief { get; set; }
    public required string ContentJson { get; set; }
    public Guid? UnderlayAssetId { get; set; }
    public Guid? CompositionAssetId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PosePresetRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string JointsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class FrameMarkupRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int Version { get; set; }
    public int Revision { get; set; }
    public required string StrokesJson { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TimelineClipRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Track { get; set; }
    public required string Label { get; set; }
    public int StartFrame { get; set; }
    public int DurationFrames { get; set; }
    public double TrimStartSeconds { get; set; }
    public double Volume { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? VoiceProfileId { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class VoiceProfileRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string Provider { get; set; }
    public required string ProviderVoiceId { get; set; }
    public required string CharacterName { get; set; }
    public string? CharacterReferenceId { get; set; }
    public Guid? SampleAssetId { get; set; }
    public required string ConsentAttestation { get; set; }
    public DateTimeOffset? ConsentedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class GenerationManifestRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public required string ShotCode { get; set; }
    public int ShotVersion { get; set; }
    public Guid SketchId { get; set; }
    public int SketchRevision { get; set; }
    public required string Route { get; set; }
    public required string Purpose { get; set; }
    public required string State { get; set; }
    public required string CreativeBrief { get; set; }
    public required string AuthoritiesJson { get; set; }
    public required string ConstraintsJson { get; set; }
    public required string ManifestJson { get; set; }
    public required string ManifestHash { get; set; }
    public bool ProviderCallMade { get; set; }
    public Guid? CompositionAssetId { get; set; }
    public string? CompositionAssetHash { get; set; }
    public Guid? LastFrameAssetId { get; set; }
    public string? LastFrameAssetHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AssetRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Kind { get; set; }
    public required string OriginalFileName { get; set; }
    public required string MimeType { get; set; }
    public long Bytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public required string ContentHash { get; set; }
    public required string StoragePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string DisplayName { get; set; } = "";
    public Guid? CollectionId { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string Notes { get; set; } = "";
    public string Source { get; set; } = "Imported";
    public bool IsArchived { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? RevisionFamilyId { get; set; }
    public int? RevisionNumber { get; set; }
    public bool IsCurrentRevision { get; set; }
    public Guid? ParentAssetId { get; set; }
    public string RevisionPrompt { get; set; } = "";
    public string RevisionEngine { get; set; } = "";

    /// <summary>
    /// When a person accepted this asset, and who said so.
    ///
    /// Acceptance lives on the asset that was accepted, and that is the whole
    /// mechanism: a prepared derivative is a new revision and therefore a new
    /// row, so it starts unaccepted however long its parent has been approved.
    /// Nothing has to remember to clear anything.
    /// </summary>
    public DateTimeOffset? PreparationAcceptedAt { get; set; }
    public string PreparationAcceptedBy { get; set; } = "";
    public string PreparationAcceptanceNote { get; set; } = "";

    /// <summary>
    /// Set when a derivative's triangle or vertex count differs from the
    /// revision it was prepared from. A rig or an anchor agreed against the
    /// old surface cannot follow it across that, so it is recorded on the
    /// asset rather than left for a reader to derive from two profiles.
    /// </summary>
    public bool PreparationTopologyChanged { get; set; }
}

public sealed class AssetCollectionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string Color { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AssetPlacementRecord
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public Guid ShotId { get; set; }
    public required string Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A reusable authority that belongs to no project.
///
/// The library is a separate table rather than a flag on <see cref="ReferenceRecord"/>
/// because every entity table is project-scoped by a query filter: a "global row" in
/// a filtered table would be invisible to every project. Identity is a Guid here,
/// not a slug, because the library is one global namespace while reference slugs are
/// deliberately per-project.
/// </summary>
public sealed class LibraryAuthorityRecord
{
    public Guid Id { get; set; }
    /// <summary>Readable id carried into a project on import, when it is free there.</summary>
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public int CurrentVersion { get; set; }
    public int LastIssuedVersion { get; set; }
    public required string Accent { get; set; }
    public int VisualVariant { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One immutable version of a library authority.
///
/// The image is stored as a self-sufficient descriptor rather than a foreign key to
/// <see cref="AssetRecord"/>. Asset rows are project-scoped, so a key would point at
/// a row invisible to any other project — and would dangle entirely once the
/// originating project was deleted, since deletion removes asset rows while
/// deliberately keeping the content-addressed files. A descriptor lets any project
/// materialise its own row over the same bytes.
/// </summary>
public sealed class LibraryAuthorityVersionRecord
{
    public Guid Id { get; set; }
    public Guid LibraryAuthorityId { get; set; }
    public int Version { get; set; }
    public required string Description { get; set; }
    public required string LockedConstraint { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset RatifiedAt { get; set; }
    /// <summary>Which project this version was promoted from, for the trail.</summary>
    public Guid? OriginProjectId { get; set; }
    public string? ImageContentHash { get; set; }
    public string? ImageStoragePath { get; set; }
    public string? ImageMimeType { get; set; }
    public long? ImageBytes { get; set; }
    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }
}

public sealed class ReferenceRecord
{
    public required string Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>
    /// Provenance for an imported authority: which library authority it came from
    /// and the exact version taken. Never a live link — the row is a copy, so
    /// editing the library cannot alter a frame already delivered here. Comparing
    /// OriginVersion to the library head is what surfaces "library is at v7, you
    /// have v4".
    /// </summary>
    public Guid? OriginLibraryId { get; set; }
    public int? OriginVersion { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public int CurrentVersion { get; set; }
    public int LastIssuedVersion { get; set; }
    public required string Status { get; set; }
    public required string Accent { get; set; }
    public int VisualVariant { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ReferenceVersionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string ReferenceId { get; set; }
    public int Version { get; set; }
    public required string Description { get; set; }
    public required string LockedConstraint { get; set; }
    public Guid? ImageAssetId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset RatifiedAt { get; set; }
}

public sealed class CandidateVersionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ShotId { get; set; }
    public int Version { get; set; }
    public required string Stage { get; set; }
    public required string Approval { get; set; }
    public bool IsCurrent { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? SourceManifestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}

/// <summary>
/// The editable working state of one small scene: where the inspection camera
/// sits, how it is lit, and a monotonic version so a save built on a stale read
/// is refused instead of erasing newer work.
/// </summary>
public sealed class SceneRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public int Version { get; set; }
    public double CameraYaw { get; set; }
    public double CameraPitch { get; set; }
    public double CameraDistance { get; set; }
    public double CameraTargetX { get; set; }
    public double CameraTargetY { get; set; }
    public double CameraTargetZ { get; set; }
    public double CameraFieldOfView { get; set; }
    public double KeyLightIntensity { get; set; }
    public double KeyLightYaw { get; set; }
    public double KeyLightPitch { get; set; }
    public double AmbientLightIntensity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One placed object. Two instances may reference the same model revision and
/// still be moved independently, which is why the transform lives here and not
/// on the asset.
/// </summary>
public sealed class SceneInstanceRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    /// <summary>The exact model revision this instance is pinned to, or null while it is a placeholder.</summary>
    public Guid? AssetId { get; set; }
    /// <summary>Simple geometry drawn in place of a model. Null once a model is pinned.</summary>
    public string? PlaceholderShape { get; set; }
    public double PlaceholderSizeX { get; set; }
    public double PlaceholderSizeY { get; set; }
    public double PlaceholderSizeZ { get; set; }
    /// <summary>What this object is for, when a blockout plan put it here.</summary>
    public string? Role { get; set; }
    /// <summary>The construction plan this object came from, so its reasoning stays inspectable.</summary>
    public Guid? SourcePlanId { get; set; }
    /// <summary>The clip bound to this one object, with its own playback settings.</summary>
    public Guid? ClipAssetId { get; set; }
    public string? ClipName { get; set; }
    public double ClipStart { get; set; }
    public double ClipEnd { get; set; }
    public double ClipSpeed { get; set; } = 1;
    public double ClipTime { get; set; }
    public bool ClipLoop { get; set; }
    /// <summary>How this object's clip moves its root: Hold or Offset, never both.</summary>
    public string? ClipRootMotion { get; set; }
    /// <summary>A rigid part's declared pivot and swing, for an object with no skeleton.</summary>
    public string? MotionAxis { get; set; }
    public double MotionPivotX { get; set; }
    public double MotionPivotY { get; set; }
    public double MotionPivotZ { get; set; }
    public double MotionFrom { get; set; }
    public double MotionTo { get; set; }
    public double MotionSeconds { get; set; }
    public bool MotionPingPong { get; set; }
    public required string Name { get; set; }
    public int SortOrder { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double ScaleX { get; set; }
    public double ScaleY { get; set; }
    public double ScaleZ { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A note left on one object in a scene.
///
/// The anchor is a point in the instance's own local space, recorded together
/// with the model revision it was placed against. If that instance is later
/// pinned to a different revision the geometry underneath has changed, so the
/// note is reported stale rather than being silently moved to a point that
/// means something else now.
/// </summary>
public sealed class SceneAnnotationRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public Guid InstanceId { get; set; }
    /// <summary>The exact model revision the anchor was measured against.</summary>
    public Guid AssetId { get; set; }
    public double AnchorX { get; set; }
    public double AnchorY { get; set; }
    public double AnchorZ { get; set; }
    /// <summary>The view the artist was looking from when the note was placed.</summary>
    public double CameraYaw { get; set; }
    public double CameraPitch { get; set; }
    public double CameraDistance { get; set; }
    public double CameraTargetX { get; set; }
    public double CameraTargetY { get; set; }
    public double CameraTargetZ { get; set; }
    public required string Body { get; set; }
    public required string State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A staged change to exactly one scene instance. It records the scene version
/// and director-context token it was built on, so a proposal made against a
/// view that has since moved is refused rather than applied to different work.
/// </summary>
public sealed class SceneProposalRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public Guid InstanceId { get; set; }
    public int BaseSceneVersion { get; set; }
    public required string ObservedStateToken { get; set; }
    public required string Direction { get; set; }
    public required string Rationale { get; set; }
    /// <summary>Null means "leave this alone"; only named axes are proposed.</summary>
    public string? PositionJson { get; set; }
    public string? RotationJson { get; set; }
    public string? ScaleJson { get; set; }
    public required string State { get; set; }
    public required string IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

/// <summary>
/// A construction plan read off one reference image: what objects the scene
/// needs, which of them the library already has, what stands in for the rest,
/// and what the plan is unsure about. It creates nothing until the artist
/// approves it, and approving it never dispatches a provider.
/// </summary>
public sealed class SceneBlockoutPlanRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ReferenceAssetId { get; set; }
    /// <summary>The exact reference bytes the plan was read from.</summary>
    public required string ReferenceContentHash { get; set; }
    public required string Title { get; set; }
    public required string Summary { get; set; }
    public required string State { get; set; }
    public double CameraYaw { get; set; }
    public double CameraPitch { get; set; }
    public double CameraDistance { get; set; }
    public double CameraTargetX { get; set; }
    public double CameraTargetY { get; set; }
    public double CameraTargetZ { get; set; }
    public double CameraFieldOfView { get; set; }
    /// <summary>What the plan took for granted, kept verbatim so it can be argued with.</summary>
    public required string AssumptionsJson { get; set; }
    /// <summary>What the reference did not show: occlusions, guesses, and unreadable areas.</summary>
    public required string UncertaintiesJson { get; set; }
    public required string IdempotencyKey { get; set; }
    /// <summary>The scene this plan built, once the artist approved it.</summary>
    public Guid? SceneId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }
}

/// <summary>
/// One object in a construction plan. It either matches a model the project
/// already holds or describes simple geometry to stand in for one, never both,
/// and it records which scene object it became.
/// </summary>
public sealed class SceneBlockoutItemRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PlanId { get; set; }
    public int SortOrder { get; set; }
    public required string Role { get; set; }
    public Guid? MatchAssetId { get; set; }
    public string? PlaceholderShape { get; set; }
    public double PlaceholderSizeX { get; set; }
    public double PlaceholderSizeY { get; set; }
    public double PlaceholderSizeZ { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double PositionZ { get; set; }
    public double RotationX { get; set; }
    public double RotationY { get; set; }
    public double RotationZ { get; set; }
    public double ScaleX { get; set; }
    public double ScaleY { get; set; }
    public double ScaleZ { get; set; }
    public required string MotionIntent { get; set; }
    public required string Confidence { get; set; }
    public required string Note { get; set; }
    /// <summary>The scene object this item became, once the plan was applied.</summary>
    public Guid? InstanceId { get; set; }
}

/// <summary>
/// One immutable scene-to-shot snapshot. The editable scene may continue to
/// change; this row and its rendered still continue to describe the exact
/// source that entered candidate review.
/// </summary>
public sealed class SceneShotBindingRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public required string SceneName { get; set; }
    public int SceneVersion { get; set; }
    public Guid ShotId { get; set; }
    public required string ShotCode { get; set; }
    public int ShotVersion { get; set; }
    public required string CameraJson { get; set; }
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public double StillTime { get; set; }
    public int DeliveryWidth { get; set; }
    public int DeliveryHeight { get; set; }
    public int FramesPerSecond { get; set; }
    public required string ColorSpace { get; set; }
    public required string SnapshotJson { get; set; }
    public required string SnapshotHash { get; set; }
    public Guid StillAssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MusicCompositionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public Guid CurrentRevisionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MusicCompositionRevisionRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CompositionId { get; set; }
    public int RevisionNumber { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public required string EditSummary { get; set; }
    public required string CompositionJson { get; set; }
    public required string AbcNotation { get; set; }
    public required string ContentHash { get; set; }
    public required string PlanArtifactManifestJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MusicRenderRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid CompositionRevisionId { get; set; }
    public Guid JobId { get; set; }
    public Guid AssetId { get; set; }
    public required string Renderer { get; set; }
    public required string SettingsJson { get; set; }
    public required string ArtifactManifestJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
