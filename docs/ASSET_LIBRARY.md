# Framewright Asset Library

## Product decision

Framewright uses a production media-pool pattern rather than exposing filesystem folders. One canonical, content-addressed asset can be tagged, placed in a manual collection, found through smart views, attached to shots, and archived without copying or moving its source bytes.

This combines established production patterns:

- DaVinci Resolve's Media Pool uses bins, Smart Bins, metadata, search, and multiple views while keeping source media distinct from organization.
- Adobe Premiere organizes source items in bins and exposes metadata for discovery rather than requiring destructive file operations.
- Frame.io keeps historical versions available and makes selection/review explicit instead of silently replacing evidence.

Primary references: [DaVinci Resolve Media](https://www.blackmagicdesign.com/products/davinciresolve/media), [DaVinci Resolve Edit](https://www.blackmagicdesign.com/products/davinciresolve/edit), [Adobe Premiere bins](https://helpx.adobe.com/ca/premiere/desktop/organize-media/file-organization/organize-bins.html), [Adobe metadata](https://helpx.adobe.com/premiere/desktop/organize-media/edit-metadata/metadata-in-premiere.html), and [Frame.io version stacking](https://help.frame.io/en/articles/9101068-version-stacking).

## Artist workflow

The Assets rail opens a project-level workspace; it does not require a selected shot.

1. Use a smart view for All assets, Images, Music & audio, Video takes, Authorities, or Archived.
2. Search name, tags, notes, or provenance. Sort by newest, name, or type and switch between grid and list views.
3. Create optional collections for scene, location, episode, editorial purpose, or any other artist-owned grouping. On desktop, drag an asset onto a collection. On touch, use the collection field in Asset details.
4. Import supported media or create it in context:
   - Image creation launches the same composition workspace used by shots, with its subject set to `Asset`. The artist can begin from text alone, draw on the pencil canvas, add text, use an existing image as an underlay, or combine those inputs. The result returns to the library and never occupies or advances a shot approval slot.
   - Existing shot frames, authority images, and ordinary library images appear in one visual-reference picker. A shot is therefore reusable as generation context without being converted into an asset or mutating its history.
   - Music creation happens in Sequence → Music as `intent → composition → ABC → YuE2 render`. Lyrics, structure, tempo, key, chords, melody intent, and advanced ABC remain editable. Every save creates a revision, and every render stays associated with that exact revision before entering the audio asset library.
   - Video files can be imported, previewed, organized, and attached to a shot as reviewable takes. Video generation still begins from a ratified final in the Shot workflow.
5. Edit display name, collection, tags, and notes in the inspector. Source filename, size, dimensions/duration, provider source, and content identifier remain provenance facts.
6. Attach an asset to a named shot as an Image guide, Video take, or Audio cue. The Shot Media tab shows those placements and can remove them without deleting the asset.
7. Archive unused media non-destructively. Archived media remains recoverable and searchable through the Archived smart view.

Authority images appear in the same workspace for discovery, but continue to use the immutable authority version editor. A normal image guide cannot silently become character, wardrobe, location, prop, or style canon.

## Domain and trust rules

- `AssetRecord` is the canonical project-scoped media object and stores editable library metadata alongside immutable source facts.
- `AssetCollectionRecord` is an optional manual bin. Deleting it requires that it be empty and never deletes asset bytes.
- `AssetPlacementRecord` joins one asset to one shot and role. The service constrains roles by media kind.
- Smart views are computed queries. They do not duplicate records.
- Archive is reversible. Physical content remains in the content-addressed store because another manifest, shot, or historical version may cite it.
- Provider generation remains server-side. The explicit Generate click authorizes that image call; no secondary feature toggle or packet confirmation is required. The browser receives no API key, ComfyUI graph, or arbitrary filesystem path.
- `GenerateAssetImageRequest` is an asset-scoped command over the same curated `IGenerationAdapter` boundary used by shots. It carries a name, creative brief, route, optional composition image, and up to eight reference asset IDs; it deliberately carries no shot ID.
- The browser owns only ephemeral editing state. The API validates adapter capability, reads content-addressed inputs, performs the provider call server-side, imports the result, and returns an `AssetSummary`.
- GPT Image receives selected reference images as image inputs. ComfyUI reference capacity is discovered from the selected allowlisted workflow: Framewright binds the supported direct image slots, prioritizes character identity and spatial pins, and carries remaining authorities as locked text instead of pretending every graph has identical conditioning support.
- Tests use isolated data roots and the local proof boundary. They never submit to the artist's ComfyUI queue.

## Shared generation architecture

The reusable abstraction is the generation subject, not a generic form or a fake shot:

- `Shot` supplies version, approval, continuity, authority, and candidate-promotion semantics.
- `Asset` supplies library name, optional composition, selected visual references, and asset-import semantics.
- Both subjects use the same canvas, text tools, provider-route selector, workflow identity, progress treatment, and generation adapters.

React keeps the active asset draft in `App`, the nearest common owner of the Asset Library and composition workspace. The library launcher creates a typed `AssetGenerationDraft`; the shared editor consumes it; the API returns the created asset. This avoids parallel copies of prompt, route, and reference state while keeping the two domain outcomes explicit.

This direction follows established patterns: Adobe Firefly Boards lets artists combine canvas content and select existing canvas images as generation references; Autodesk Flow Production Tracking links reviewable Versions to either Shots or Assets instead of flattening both entity types; React recommends one owner for shared state. See [Firefly Boards](https://helpx.adobe.com/firefly/web/create-mood-boards/firefly-boards/create-mood-boards.html), [Flow Production Tracking entities](https://help.autodesk.com/view/SGSUB/ENU/?guid=SG_Producer_pr_project_tracking_pr_entities_html), and [React: Sharing State Between Components](https://react.dev/learn/sharing-state-between-components).

## Responsive behavior

Desktop uses a three-pane layout: navigation/bins, media browser, and inspector. Tablet turns the bins into a horizontally scrollable filter strip and the inspector into a dismissible sheet. Every operation available through drag-and-drop also has an explicit control for pencil, touch, keyboard, and accessibility use.

## Deliberate boundaries

- No generated background music is embedded in image or video prompts.
- The Asset Library is not a replacement for the sequence audio editor; it supplies reusable source media to it.
- Filesystem folder mirroring, automatic duplicate cleanup, AI auto-tagging, proxy generation, and waveform background indexing are future capabilities, not shipped behavior.
- Voice-profile consent and reusable voice identity remain in the separate voice domain; importing an audio file does not create a clone profile.
