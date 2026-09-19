using StoryboardStudio.Core;

namespace StoryboardStudio.Api.Services;

/// <summary>
/// Which project the current unit of work belongs to.
///
/// Every project-scoped entity carries a global query filter keyed off this, so a
/// read that forgets to scope itself is not possible rather than merely reviewed.
/// A request resolves it once from <see cref="ActiveProjectRegistry"/>; the
/// generation worker runs outside any request and binds the job's own project
/// explicitly, so switching projects mid-render cannot make a running job vanish.
/// </summary>
public interface IProjectScope
{
    Guid ProjectId { get; }

    /// <summary>Pins this unit of work to one project, ignoring the active selection.</summary>
    void Bind(Guid projectId);
}

public sealed class ProjectScope(ActiveProjectRegistry registry) : IProjectScope
{
    // Snapshot the active selection when the request scope is constructed. If a
    // second browser or tablet activates another project while this request is
    // between two awaited queries, the unit of work must not change tenants
    // underneath EF. Workers and the activation endpoint may still deliberately
    // rebind the scope before doing project-scoped work.
    private Guid bound = registry.Current;

    public Guid ProjectId => bound;

    public void Bind(Guid projectId) => bound = projectId;
}

/// <summary>
/// Server-side "current project", per the multi-project plan's step 3.
///
/// This is a single-user local-first app, so the active project is process state
/// rather than a header or a route prefix: the existing <c>GET /api/studio</c>
/// snapshot shape stays intact and the ~250 query sites need no route change.
/// The choice is mirrored onto <see cref="Persistence.ProjectRecord.IsActive"/> so
/// it survives a restart. Guid is not word-sized, so reads are locked rather than
/// volatile — a torn read here would silently address the wrong project.
/// </summary>
public sealed class ActiveProjectRegistry
{
    private readonly object gate = new();
    private Guid current = StudioDefaults.ProjectId;

    public Guid Current
    {
        get { lock (gate) return current; }
    }

    public void Set(Guid projectId)
    {
        lock (gate) current = projectId;
    }
}
