# Multi-project, asset library, and AI project setup

Implementation plan. Written after measuring the real surface, because the first
estimate ("mostly mechanical scoping") was wrong and the correction matters.

Status: **complete** (2026-08-17). All six steps landed, gated by the leak test in
step 2 and verified against a copy of the live database. See "What actually landed"
below for the places reality differed from this plan.

---

## Why this is not a small change

| Fact | Measured |
|---|---|
| `StudioRepository.cs` | 1408 lines |
| Query sites needing a project filter | ~250 across 12 files — the ~140 first measured were `StudioRepository` alone |
| Records that already carry `ProjectId` | `AssetRecord`, `AssetCollectionRecord`, `ReferenceRecord` |
| Records that do **not** | `ShotRecord`, `CommentRecord`, `JobRecord`, `ShotVersionRecord`, `SketchDocumentRecord`, `GenerationManifestRecord`, `ReferenceVersionRecord`, `CandidateVersionRecord`, `FrameMarkupRecord`, `TimelineClipRecord`, `VoiceProfileRecord` |
| Schema mechanism | `EnsureCreatedAsync` — **does not alter existing tables** |

`AssetStore` already filters on `ProjectId` in six places, which is why this
looked half-done. It is not: the entity tables that carry production evidence
are entirely unscoped.

### The failure mode to design against

A partially scoped build **silently mixes two projects' data**. Because approval
evidence is append-only and manifests cite exact authority versions, a shot in
project B binding an authority version from project A is corruption that may go
unnoticed for a long time and cannot be cleanly unwound.

Therefore: **do not ship a partial scoping pass.** Steps 0, 1 and 2 are one
commit, gated by the test in step 2.

---

## What actually landed

Four things this plan got wrong or missed. Recorded because each one would have
been a silent failure, and the last one nearly was.

**1. The scoping surface was ~250 sites, not ~140.** The original count measured
`StudioRepository.cs` (136, accurate) and stopped there. The rest: `AssetStore`
37, `GenerationOrchestrator` 17, `StudioDatabaseInitializer` 16, `TimelineService`
13, `ProductionExportService` 9, `AudioMasteringService` 7, `VoiceSynthesisService`
4, `QwenVoiceService` 4, `ContinuityService` 3, `AssetImageGenerationService` 2,
`BackupService` 1.

**2. Scoping is done with EF global query filters, not by hand.** `StudioDbContext`
carries `HasQueryFilter(x => x.ProjectId == ActiveProjectId)` on all fourteen
scoped entities, keyed off a scoped `IProjectScope`. This is stronger than the
plan's "a method that forgets it should fail to compile", because there is nothing
to forget: a read cannot omit the filter. `SaveChanges` stamps the active project
onto new rows, so writes cannot land unscoped either. What remains per-site is
only the deliberate exceptions, all marked `IgnoreQueryFilters()`: the startup
backfills, the job worker's recovery sweep, the project switcher's counts, the
backup status, and project deletion.

`Projects`, `AuditEvents`, and `AssetPlacements` are deliberately unfiltered — the
tenant table, a trail that must outlive its project, and a join between two
already-scoped rows.

**3. Two unique indexes and one primary key blocked a second project outright.**
`Shots.Code` and `VoiceProfiles.Name` were unique on their own, so project B could
never hold an `SH-010` or a duplicate voice name; both are now `(ProjectId, …)`.
`References.Id` was a bare slug PK (`ennix`), so two projects could not both have
that character — it is now a composite `(ProjectId, Id)`, which SQLite cannot do
by ALTER, so `EnsureReferenceIdentityAsync` rebuilds the table behind a
`pragma table_info` guard. Reference slugs are therefore **per-project**: a bare
`/api/references/{slug}` URL means "in the active project", and the same slug
legitimately names a different authority in each project.

**4. The Guid text-casing trap.** EF stores a Guid as **upper-case** TEXT and
SQLite compares TEXT case-sensitively. The first version of the upgrade backfilled
`ProjectId` with `ALTER TABLE … DEFAULT '<lower-case guid>'`. Every value looked
correct in the database file and matched no query filter at all — eleven tables of
production evidence silently invisible to every project. `GET /api/studio` failed
with `KeyNotFoundException: (aerie, 7)` because the authority existed and its
versions did not resolve. The fix is to never hand-write the literal: the column
defaults to `''` and the backfill binds a real `Guid` parameter so the provider
writes exactly the form EF later reads. The `WHERE` clause matches
`ProjectId NOT IN (SELECT Id FROM Projects)` rather than a list of sentinels, which
also repairs a wrongly-encoded id, since that will not join either.

**This was caught only by running against a copy of the real database.** Every
unit test passed throughout, because tests use fresh databases where EF writes
every row itself and the casing is self-consistent. The upgrade path only executes
on a database that predates it.

### Verified against the live data

A copy of `src/StoryboardStudio.Api/App_Data/storyboard-studio.db` upgraded
cleanly and preserved everything: 11 shots `SH-010`–`SH-110` with SH-020 still at
v23, 12 authorities, 32 comments, 90 assets, 5 clips, 4 voice profiles, and
SH-020's 14 manifests / 20 candidates / 4 ratified versions. A second project
created in that upgraded database saw none of it, successfully reused the code
`SH-020` and the slug `ennix`, and left project A byte-identical. The active
project survived a process restart.

---

## Step 0 — Schema upgrade path

`EnsureCreatedAsync` creates missing tables but never alters existing ones, so
adding `ProjectId` to eleven tables will not reach the live database. There is
already a hand-rolled precedent in `StudioDatabaseInitializer.EnsureAuthorityTableAsync`.

Two options:

1. **Hand-rolled `ALTER TABLE` upgrade** (matches existing precedent, no new
   dependency). For each table: check `pragma table_info(<table>)`, and if
   `ProjectId` is absent, `ALTER TABLE <t> ADD COLUMN ProjectId TEXT NOT NULL
   DEFAULT '<StudioDefaults.ProjectId>'`.
2. **Adopt EF Core migrations** (the handoff's stated Phase 1 intent, better long
   term, larger change and needs a baseline migration for the existing schema).

Recommendation: **option 1 now**, option 2 as its own separate piece of work.
Mixing a first migration into this change doubles the risk surface.

Landed as option 1, in `StudioDatabaseInitializer.EnsureProjectScopeColumnsAsync`.

**`scripts/restore-backup.ps1` does not cover the dev database.** Confirmed: lines
19–21 reject any `-DataRoot` outside `%LOCALAPPDATA%`, and the working database
lives at `src/StoryboardStudio.Api/App_Data`. It also only accepts a ZIP produced
by `BackupService`, which is a download endpoint — nothing writes one to disk on a
schedule. So there was no working restore path for the database being developed
against. Until that is fixed, take a plain file copy of the `App_Data` folder
before any schema change.

## Step 1 — Add `ProjectId` to the eleven records

Default every existing row to `StudioDefaults.ProjectId` so current data becomes
"the first project" and nothing is orphaned. Index `(ProjectId)` on the tables
that are queried by it most: `Shots`, `CandidateVersions`, `References`,
`Comments`, `GenerationManifests`.

## Step 2 — Scope every query, and prove it

Roughly 140 sites. The counts, so nothing is missed:

```
23 db.Shots          21 db.CandidateVersions   16 db.References
13 db.Comments       12 db.GenerationManifests 12 db.Assets
11 db.ReferenceVersions  6 db.SketchDocuments   6 db.ShotVersions
 5 db.Jobs            5 db.FrameMarkups         4 db.Projects
 2 db.AuditEvents
```

Do **not** rely on reviewing 140 call sites by eye. Two structural safeguards:

1. **Resolve the active project once per request** and pass it explicitly, rather
   than letting each method decide. A method that forgets it should fail to
   compile, not silently return everything.
2. **A leak test.** Create two projects, populate both, then assert that every
   list/read endpoint returns only the active project's rows. This is the gate
   for the whole change:

```
Two_projects_never_see_each_others_entities()
  - create project A, add shot + authority + comment + manifest + timeline clip
  - create project B, same
  - for each read endpoint: assert counts and ids are disjoint
```

Write that test **first**. It should fail before the scoping work and pass after.

Landed as `tests/StoryboardStudio.Api.Tests/ProjectIsolationTests.cs`, three tests.
Confirmed to be a real gate: commenting out `ApplyProjectScope` makes
`Two_projects_never_see_each_others_entities` fail. It asserts both directions, not
just "B is empty" — a one-directional check would pass a build that scoped reads to
whichever project was created last. Each test builds its own factory, because the
active project is process state and a shared server would let one test decide what
the next one sees.

One correction to the plan's shape: id-level disjointness does not hold for
references, by design. The composite key makes the slug per-project, so the same
`ennix` is a legitimate id in both. The test asserts instead that the *version rows*
behind a shared slug differ, which is the property that actually matters.

## Step 3 — Active project selection

Options considered: a header, a route prefix (`/api/projects/{id}/...`), or
server-side "current project" state.

Recommendation: **server-side current project**, switched by
`POST /api/projects/{id}/activate`. Reasons: this is a single-user local-first
app, it keeps ~140 call sites from needing a route change, and the existing
`GET /api/studio` snapshot shape stays intact so the whole front end keeps
working. Revisit if the app ever becomes multi-user.

Endpoints:

```
GET    /api/projects                 list, with shot/authority counts
POST   /api/projects                 create
POST   /api/projects/{id}/activate   switch
PUT    /api/project                  (existing) edit the active project
DELETE /api/projects/{id}            refuse while it is the active project
```

Landed as specified. Three details worth knowing:

- The choice is mirrored onto `ProjectRecord.IsActive` and restored on startup, so
  a restart does not silently drop the artist back into the first project.
- **The generation worker binds the scope explicitly.** It runs outside any request,
  so `RunAsync` looks up the job's own `ProjectId` first and pins the scope to it.
  Without that, switching projects while a render was in flight would hide the job
  from its own completion path and it would stall with no error. The restart
  recovery sweep likewise ignores the filters, so a render is not abandoned merely
  because the artist had switched away before shutting down.
- Creating a project deliberately does **not** activate it — a new project is empty,
  so switching immediately would replace the artist's board with a blank one.
  Deletion refuses the active project, refuses the last remaining project, and needs
  the same explicit acceptance as a shot deletion when ratified evidence exists. It
  removes rows but never stored asset files, because the store is content-addressed
  and shared across projects.

## Step 4 — Front end switcher

The context bar already shows `production · name`. Make it a menu: recent
projects, counts, "New project", "Manage". No new surface required.

Landed as `src/storyboard-studio-web/src/components/ProjectSwitcher.tsx`. The
identity block became the menu trigger; the counts come from `GET /api/projects`
and are re-read on every open rather than cached, because they go stale the moment
a shot is added.

Three behaviours worth keeping:

- **Switching clears shot-scoped state.** Selected shot, selected authority, review
  candidate, pending comment pin, and any in-flight generation draft are dropped and
  the board is shown. Carrying a selected shot id into a project that does not
  contain it would leave the workspace pointed at nothing.
- **The active project has no delete control at all**, rather than a control that
  returns 409. The refusal still exists server-side; the UI simply never aims at it.
- **Deleting asks for the project name typed out**, plus a separate acceptance
  checkbox when ratified evidence would be destroyed.

### One pre-existing bug found here

`<input type="number" min={180} step={16}>` on delivery height rejects every legal
value: with `min` not itself a multiple of `step`, the accepted heights either side
of 960 are 948 and 964, so the browser blocked submission — silently, because
`requestSubmit()` fires no event when constraint validation fails. The same pair was
already in the Settings drawer's project contract form, so saving a 960-high canvas
from Settings had been quietly impossible. Both are now `min={192}`, the smallest
multiple of 16 at or above the server's 180 floor.

---

## Step 5 — Asset library and cross-project reuse

Design settled in conversation; recorded here so it is not re-litigated.

**Copy on import, with provenance — never a live cross-project link.**

This follows from `IMPLEMENTATION_HANDOFF.md` §10: *"a version cites exact
authority versions, never a mutable latest."* A live link would mean editing a
character in the library silently changes a frame already delivered in another
project.

Three tiers:

1. **Library** — global, project-independent. Characters, props, locations, styles.
2. **Project** — imports selected library authorities **at a specific version**,
   recording `originLibraryId` + `originVersion`.
3. **Project-local authorities** — created in-project, with a **promote-up** path
   into the library once they prove reusable.

Tier 3 matters more than it looks: most characters start inside one project and
you only learn they are reusable later. Without promote-up, the library only ever
receives what someone predicted in advance.

Provenance buys the good version of "port quickly": a project can surface
*"library is at v7, you have v4 — pull it?"* as an explicit act. Selective import
falls out naturally — take Remora and the style authority, leave the rest.

Free win from an existing decision: assets are already **content-addressed by
hash**, so a shared asset store deduplicates automatically. The same character
reference image across five projects stores one file.

Industry precedent for the shape: ShotGrid/ftrack/Kitsu use a library or shared
project plus publish→import-a-version. Unreal's *Migrate* copies an asset and its
dependency tree. Blender's **Link vs Append** is exactly this choice; we are
choosing Append, for the reason above.

Landed as `AuthorityLibraryService` plus `LibraryAuthorities` /
`LibraryAuthorityVersions`, deliberately outside `ApplyProjectScope`. Endpoints:

```
GET    /api/library                                     list, annotated for the active project
GET    /api/library/{id}/versions                       the library version stack
GET    /api/library/{id}/versions/{version}/image        preview without a project copy
POST   /api/library/{id}/import                          copy in at the library head
DELETE /api/library/{id}                                 remove; imported copies are untouched
POST   /api/references/{id}/promote-to-library           promote-up
POST   /api/references/{id}/pull-library-update          append the newer version
```

### The asset-row problem this design had to solve

"Assets are content-addressed, so a shared store deduplicates automatically" is true
of the *files* and false of the **rows**: `AssetRecord` is project-scoped, so a
library authority holding an asset id would point at a row invisible to every other
project — and one that vanishes entirely when the originating project is deleted,
since deletion removes asset rows while deliberately keeping the files.

So a library version stores a self-sufficient image **descriptor** (content hash,
storage path, mime type, byte count, dimensions) rather than a foreign key. On
import the target project materialises its own asset row over the same bytes. The
result is the dedupe the plan promised — verified on real data as one file and two
rows, both serving the identical 1,924,562 bytes — and a library that outlives the
production it was promoted from.

### Where the version numbers come from

An import starts the project's own stack at **v1** even when the library is at v7,
because a manifest cites the project's version numbers and those have to be its own.
`OriginVersion` records what was taken. Pulling a later library version **appends**
to the project stack rather than editing the imported version, so a manifest frozen
against v1 still means v1 afterwards — this is the whole reason provenance was chosen
over linkage, and there is a test whose only job is to hold it.

Promoting an authority that already came from the library adds a version to that
library entry instead of creating a duplicate, so refinements made inside a
production flow back up. Deleting a library entry clears provenance on every
imported copy but leaves the copies working, because they were always copies.

One thing steps 0–3 changed for this step: reference slugs are now per-project, so
a library import can keep the readable slug it had in its source project without
colliding. `originLibraryId` + `originVersion` still need adding to
`ReferenceRecord`.

## Step 6 — Codex project interview

`POST /api/projects` now exists, so the interview has somewhere to land. World
settings on that endpoint are **optional** and fall back to `StudioDefaults`,
precisely so the interview can fill them afterwards rather than the artist being
made to write four essays to create a project. The proposals-must-be-reviewable
rule below is unchanged.

Good fit because
`ProjectRecord` already models the fields an interview would fill:
`VisualStyle`, `WorldCanon`, `PromptDirectives`, `NegativeDirectives`,
`AspectRatio`, `FramesPerSecond`, `DeliveryWidth/Height`.

Flow: ask three or four plain-language questions — what is this (30-second spot?
short? series?), what is the look, who is in it, anything locked — then have
Codex **propose** those fields plus a starter style authority and a suggested
aspect/fps.

**Proposals must be reviewable, never auto-applied.** Every other Codex
integration in this codebase advises while the human ratifies; a wizard that
silently writes world canon would be the single place that boundary breaks.

Landed as `POST /api/codex/project-interview` plus an optional panel inside the
create-project dialog. The endpoint **writes nothing**: the proposal fills the same
editable fields the manual path uses, and the same button creates the project.
Starter authorities are listed but deliberately not created — the panel says so.

Two properties the tests hold, because both are easy to lose:

- **A proposal must be one the create endpoint would accept.** Frame rate, aspect
  ratio, both delivery dimensions, every authority category and accent are clamped
  to the same contract `POST /api/projects` enforces. A proposal the artist cannot
  save would surface as a validation error on their own edit.
- **A canvas that is already valid is returned untouched.** The first version
  re-derived a "tidier" width, so Codex proposed 2048×864 and the form showed 2064
  while the rationale still said 2048 — the numbers contradicted the prose
  explaining them.

### The Codex layer was silently dead before this

Every `codex exec` call was reaching its timeout and falling back, while
`codex login status` reported Connected — so the UI said "Codex was unavailable" for
`/api/codex/assist`, `shot-intent`, and `improve-generation-direction` alike. This
matches the handoff note that Codex was the one generation path still unexercised.

Two causes, both in process invocation rather than in Codex:

1. **Inherited stdin.** `RunProcessAsync` redirected stdout and stderr but not stdin,
   so the child inherited the service's own — and under a host that never closes it,
   an interactive-capable CLI blocks forever waiting to read. The same invocation
   from a shell completed in 4.5 seconds. Both `IntegrationDiscoveryService` and
   `CodexRuntime` now redirect stdin and close it immediately, handing the child an
   EOF. This is the same reason ffmpeg is invoked with `-nostdin` in
   `AudioMasteringService`; Codex just never got the equivalent.
2. **Console-codepage decoding.** A redirected stream is decoded with the console
   codepage on Windows, so the CLI's UTF-8 became mojibake: `1920×800` arrived as
   `1920Ã—800`, and every dash, curly quote, and accented name was corrupted.
   `StandardOutputEncoding` and `StandardErrorEncoding` are now UTF-8.

With both fixed, the interview returns in about 17 seconds and shot-intent correctly
picks up existing canon — it recovered Remora's amber piping and the cut through her
anatomical left eyebrow from the authority packet. Note that the API test suite is
now ~60s rather than ~10s, because the Codex tests make real calls instead of timing
out into the fallback.

---

## Suggested order

0–2 as one commit (gated by the leak test) → 3 → 4 → 5 → 6.

Step 6 last: it writes into fields that steps 0–4 may still reshape.

Steps 0–3 landed together rather than 0–2, because the query-filter approach needs
an active project resolved per unit of work, which is step 3's mechanism. Splitting
them would have meant building a temporary stand-in and then replacing it.

Step 6 landed before step 5 rather than after. Its stated reason for going last —
that it writes into fields steps 0–4 might reshape — expired once those steps were
done, and it does not touch authority identity, which is the part step 5 changes.

### Ideas deliberately not built

- **A library browser outside the dialog.** The library is reached from the authority
  board, where authorities already live. A workspace of its own would be a second
  place to look for the same thing.
- **Bulk import.** Selective import falls out of the per-row action; a "import all"
  would mostly be a way to fill a project with authorities nobody chose.
- **Automatic pulls.** The staleness signal is deliberately a prompt, not a sync. An
  authority that updated itself is the live link this design exists to avoid.

### Follow-ups from this work

Both resolved:

- `scripts/restore-backup.ps1` now accepts a data root outside `%LOCALAPPDATA%` when
  it is demonstrably already a Framewright data root — it must contain
  `storyboard-studio.db`. Drive roots, `%WINDIR%`, `%ProgramFiles%`, and the profile
  root are refused outright, and a present `-wal` file is refused because it means a
  process still has the database open (the existing `Framewright` process check does
  not see a dev server, which runs the apphost under `dotnet run`).
- `scripts/create-backup.ps1` is new, and gives the restore script an input. It asks
  the running service for a backup, because `BackupService` uses SQLite's online
  backup API and is safe while the app is open; it verifies the archive opens and
  carries both required entries before reporting success. With no service running it
  falls back to a hash-verified folder copy, refusing to copy a database that still
  has a write-ahead log.

Note: this checkout has no `pwsh` on PATH. Both scripts run under Windows
PowerShell 5.1.
