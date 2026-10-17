# Project contract

See `PROJECT_REQUIREMENTS.md` for standing behavioral, workflow, networking, and UI requirements.

# Custom Quest Guide architecture

This document is a map for future maintenance and extension. It describes ownership of behavior rather than release history.

## Core flow

1. `TargetCatalog` defines selectable item targets.
2. `SourceResolver` runs a deterministic staged pipeline: **collect provider facts -> apply cross-source policy -> normalize/merge source-family objects -> presentation ordering**. `Resolve()` performs no network I/O.
3. `SourceClassification` defines semantic source families used by both resolution and UI.
4. `SourcePresentationPolicy` owns player-facing source ordering rules. Change repeatable-source preference here rather than adding one-off UI exceptions.
5. `UI/Plugin.SourcePresentation.cs` renders resolved acquisition guidance.
6. `OwnershipIndex` owns inventory/armoury/saddlebag/retainer/glamour ownership checks. Avoid coupling source resolution to ownership polling.

## Source layout

The source tree is grouped by responsibility rather than kept as one flat directory:

- `Core/` — configuration and shared models.
- `Domain/` — target/set/job/duty catalogs and curated acquisition knowledge.
- `Data/` — live-game/community indexes, caches, ownership, locations, and supplemental data adapters.
- `Resolution/` — acquisition policies, action guidance, and the staged `SourceResolver` pipeline.
- `UI/` — Dalamud lifecycle, navigation, target management, source presentation, diagnostics, and loot indicators.
- `Infrastructure/` — generic scheduling, network bookkeeping, CSV/reflection helpers, and fingerprints.

Small policy/catalog types that change together are co-located; large indexes/providers remain separate. The goal is cohesion, not an artificially tiny file count.

## UI partial files

- `UI/Plugin.cs` — lifecycle, selection, main tabs, general target UI, configuration.
- `UI/Plugin.NavigationAndTargets.cs` — Back/Forward history and target-tab selection/breadcrumb navigation.
- `UI/Plugin.TargetManagement.cs` — sets, pinned targets, dependencies, and pin snapshot UI.
- `UI/Plugin.SourcePresentation.cs` — source, recipe, guide, event/store, and map/location presentation.
- `UI/Plugin.LootIndicators.cs` — Need/Greed target/set markers.
- `UI/Plugin.Diagnostics.cs` — debug UI, audits, exports, and diagnostic text output.

## SourceResolver files

- `Resolution/SourceResolver.Core.cs` — resolver construction, shared helpers/policy, and staged top-level pipeline.
- `Resolution/SourceResolver.GameProviders.cs` — recipes, shops, quests, gathering, ventures, and leves.
- `Resolution/SourceResolver.Fallbacks.cs` — broad metadata inference, system fallbacks, and specialized provider normalization.
- `Resolution/SourceResolver.Supplemental.cs` — duty/supplemental conversion.
- `Resolution/AcquisitionPolicies.cs` — source classification, presentation ordering, event/store/route availability, and monster completion policy.
- `Resolution/SourceActionGuidanceResolver.cs` — turns resolved sources into player-facing actions.
- `Infrastructure/KeyedWorkScheduler.cs` — reusable bounded/deduplicated/paced execution primitive for network-backed enrichment work.

## Development rules

- Prefer exact game data over curated fallbacks.
- Keep player-facing and diagnostic resolution distinct.
- Prefer useful repeatable acquisition routes in the player-facing ordering.
- Add source-family semantics to `SourceClassification`; do not duplicate string checks throughout the UI.
- Add ordering policy to `SourcePresentationPolicy`; do not encode priority exceptions in drawing code.
- Recipes list direct ingredients only. Do not recursively aggregate base materials.
- Keep ownership scans command/session driven; do not introduce background polling/network churn.
- Preserve Glamour Dresser snapshot/cache behavior.
- Comments should explain assumptions, data quirks, or policy decisions; avoid comments that merely restate a line of code.

### Monster resolution pipeline

`MonsterResolutionPolicy` owns the player-facing completion definition for a
monster source. Resolver providers contribute fields to `SourceEntry`; they do
not independently decide that the monster is complete. `SourceResolver` merges
provider rows, enriches them from local spawn indexes and Garland's global mob
cache, then evaluates the single policy.

Garland has two explicit network phases:

1. **Item discovery** fetches one item document and publishes the complete source/monster identity list. It never waits for dedicated mob documents.
2. **Monster enrichment** walks the discovered monster list **one monster at a time**. Each monster checks `MonsterMetadataCache` first. Only a cache miss performs a dedicated mob request. A successful response is committed immediately to `monster-cache.json` before the worker advances to the next monster.

`monster-cache.json` is global by Garland monster identity. Item records do not own or duplicate resolved monster locations; the normal resolver reads the global monster cache on each pass, so a newly resolved location appears on the next UI frame if that item is still visible. Any other item that references the same monster receives the cached metadata immediately with no additional network request.

This separation is an architectural invariant. A slow monster lookup must never keep parent item discovery open, retrying a monster must never re-download the parent item, and rebuilding one item must never erase globally shared monster knowledge. Item partial level hints remain useful facts but never validate the dedicated mob document. The family-level completion view selects which monster rows still need network enrichment, so already-complete local/static monster rows do not generate redundant mob requests.

This pattern is the architectural standard for other acquisition families too:
providers are fact sources, while a source-family policy owns the definition of
complete. A provider/cache success must never substitute for family completeness.

### Coordinate normalization

World-space positions and player-facing map coordinates are different data types, and
provider models must be interpreted according to the semantics of the provider rather
than the CLR container type alone. LuminaSupplemental `MobSpawn.Position` stores its
first two components as the player-facing map X/Y values used by its generated spawn
data. CQG consumes those X/Y values directly. Explicit `WorldX`/`WorldZ` fields, when
a provider supplies them, still pass through `MapCoordinateConverter`.

LuminaSupplemental also contains a small set of verified map-centre placeholder
triples. CQG rejects only explicitly catalogued triples and scopes each one to the map
size-factor family in which it was observed; it does not infer bad data merely from
`X == Y`, a negative third component, or a coordinate being near the centre of a map.
Providers normalize or classify coordinate domains before constructing
`MonsterSpawnLocation`; presentation code never guesses.

## Exact monster identity

Monster location joins intentionally distinguish candidate identity from exact identity. `BNpcBase` is reusable and can narrow the search, but only an exact named relationship (`BNpcBase + BNpcName`, direct BNpcName data, or an equivalent exact provider record) may attach a spawn to a player-facing monster. This prevents shared NPC templates from leaking unrelated zones/coordinates into a monster's location list.

Source-family completion is based on the primary actionable route. For Monster Drop, the primary (lowest-level) recommended monster must have identity, level, and location. Optional higher-level alternatives do not keep the whole source family incomplete.

### Exact monster locations vs representative fallbacks

- Exact local `MobSpawn` coordinates are authoritative for a monster's displayed location list.
- Garland mob coordinates are representative **fallback-only** locations. They may be shown only when CQG has no exact local spawn coordinates for that monster.
- A fallback coordinate must never be merged into, counted as, or selected ahead of an exact spawn list.
- Direct BNpcName relationships are authoritative. BNpcBase-only relationships, even when uniquely bridgeable, remain fallback-only.
- Exact monster IDs must never be broadened back to same-name variants during enrichment.

- The collapsed monster row must choose its representative location from the exact list whenever exact locations exist.

### Final monster normalization

After provider rows are deduplicated, CQG runs monster enrichment once more on the merged object. Current directly identified local spawn rows are authoritative. Cached/network coordinates are fallback candidates only until no exact local spawn exists. Player-facing alternate monsters must satisfy the monster completion contract; stale/removed relationships without actionable location context remain diagnostic-only.

### Post-merge monster normalization

Monster providers contribute partial facts and may include cached fallback metadata. After duplicate monster rows are merged, the resolver runs `EnrichMonsterMetadata` again. This final normalization rebuilds exact current spawn locations from local Supplemental data and prevents provider deduplication from reintroducing stale representative coordinates.

### Monster final-normalization pass
`SourceResolver.EnrichMonsterMetadata` intentionally runs for every monster row, including rows that already look complete. This is a correctness boundary: old network/cache records may not carry modern provenance flags, so only the final identity-based rebuild can decide whether a coordinate is an authoritative local spawn or a fallback. Do not add a completeness early-return ahead of this pass.

### Target-scoped source repair

The Items UI exposes `Rebuild selected source` as the deterministic recovery path for one stale/incomplete target. On a manual press, the action removes only that item's persisted target-scoped external acquisition answer, resets selected-target recovery state, and forces one fresh selected-item network request before normal resolution continues. Global monster metadata is deliberately preserved because it belongs to the monster, not to the item. The broad `Clear network caches` diagnostic action is the explicit way to discard the global monster cache. Ownership state and Glamour Dresser data remain independent.


## Monster identity and location invariant

Monster location resolution is identity-first. The concrete identity is the
`BNpcBase + BNpcName` pair used by Garland; `BNpcName` by itself is only a name
row and may be shared by multiple battle-NPC bases. `BNpcName`, `BNpcBase`, and
territory relationships used to attach a spawn must be read by exact member/column
identity; permissive substring matching is not allowed for those joins. A localized
display name or BNpcName-only relationship cannot be used to union locations from
multiple bases. When the pair is ambiguous, the monster remains incomplete and
target-scoped enrichment resolves the exact identity. This prevents valid
coordinates from a different same-name NPC variant from becoming bogus pins.


## Selected-target enrichment state

Selected-target enrichment is modeled separately from rendering. UI drawing only reads state. The framework update loop may reconcile the currently selected target when its source-family completion contract is still incomplete and an authoritative enrichment path remains. Recovery is deliberately bounded (currently two automatic recovery attempts for one continuously selected target) so transient failures self-heal without becoming persistent polling. Provider-exhausted incomplete data remains available to Detailed/debug diagnostics but is not treated as a normal player-facing warning.

## Adding a new resolution/enrichment feature

Use the narrowest stage that owns the behavior:

- **New local/static provider:** index its facts into `gameSources` or add one collection step in `CollectSourceFacts`.
- **New source-family completion rule:** add a family policy analogous to `MonsterResolutionPolicy`; do not make providers decide completeness.
- **New network enrichment:** give it a stable key and a `KeyedWorkScheduler`; keep discovery, enrichment, retry/backoff, and cache mutation inside the provider index. `Resolve()` remains IO-free.
- **New presentation preference:** change `SourcePresentationPolicy`, not provider resolution.
- **New UI affordance:** consume normalized `SourceEntry` data; do not infer provider-specific semantics in drawing code.

The intended consequence is that a new feature should normally touch one provider/index, one family policy if needed, and presentation only when the data model genuinely needs a new affordance. It should not require selection, navigation, cache, and resolver changes simultaneously.
## MobSpawn compatibility audit

CQG treats confirmed bad LuminaSupplemental MobSpawn coordinates as bundled compatibility data, not as a runtime heuristic. The Debug-only `Export MobSpawn blacklist audit` utility scans the loaded MobSpawn dataset on demand and reports heavily reused exact position tuples plus current blacklist matches. Runtime MobSpawn indexing and the audit must use the same raw Position decoder so a serialized `X;Y;Z` value cannot be visible to one path and invisible to the other. The export records scan/parse counts, CLR assembly metadata, CQG's compiled package baseline, and a normalized MobSpawn SHA-256 fingerprint. The package version and CLR assembly version are different version domains and must not be directly compared. When LuminaSupplemental changes, run the audit, compare the dataset fingerprint, manually verify candidates, then update the hardcoded territory+tuple blacklist in a normal CQG release. Runtime resolution must not automatically promote audit candidates into the blacklist.

Debug tooling is grouped under collapsible sections so adding future diagnostics does not turn the main Debug panel into an unbounded horizontal button strip.



### MobSpawn coordinate confidence

MobSpawn coordinates use explicit compatibility classifications tied to the audited LuminaSupplemental dataset fingerprint. `Suppress` entries are confirmed placeholders and never become runtime locations. `Unverified` entries remain available but are marked in the UI and always sort below verified coordinates; a verified coordinate must be the representative location whenever one exists. Absence from the override table means normal/verified behavior. The runtime must not infer confidence from broad coordinate heuristics.


### Duty-only monster completion

Monster completion is context-aware. Overworld monsters require exact actionable spawn data. A locationless monster may instead be completed by a single unambiguous named duty attached from the item's complete source fact set. The resolver never guesses when multiple concrete duties are present. The UI presents the duty as the monster's location context and exposes `Open Duty` where ContentFinder support exists.


## Monster FATE context (v1.3.4.29)

Monster acquisition context is owned by the monster rather than by the item that references it. Dedicated Garland monster documents may expose related FATE partials; CQG stores those names in the global monster cache alongside level/location metadata. The relationship is static only: it never represents live FATE activity. A named FATE is an alternate actionable context for a locationless monster, analogous to a duty context, while exact overworld locations remain the normal representative when available.

## Debug FATE relationship verification

FATE-monster test discovery is intentionally Debug-only and on-demand. Candidate identities come from local LuminaSupplemental `MobDrop` joins; the finder checks the global monster cache first and may inspect a bounded number of Garland mob documents sequentially with pacing. It never performs a persistent/background catalog scan. Results point back to real targetable items so production presentation is exercised directly.


### FATE monster context

FATE context is currently item-scoped. LuminaSupplemental provides `FateItem` (item -> FATE) and `MobDrop` (item -> monster), but no authoritative global monster -> FATE relationship. CQG therefore attaches FATE names only when a target item has explicit FATE evidence and exactly one concrete monster identity. Multi-monster items are deliberately left unattached. This relationship must not be written to the global monster cache.


## Special-area access guidance (v1.3.4.35)
`AreaAccessGuidanceCatalog` is a deliberately small compatibility/presentation catalog for areas where the client cannot display the destination map until an unlock quest has been completed. Monster-location presentation checks the catalog before exposing `View Map`. If the quest is incomplete, CQG leaves the source/location intact but substitutes explicit unlock guidance and reuses `QuestGuidanceIndex` for the actionable quest giver/location. This belongs at the presentation/access boundary; it does not alter source resolution or monster caches.

## Locked-area access guidance (v1.3.4.36)
Special field-operation maps use an explicit `AreaAccessGuidanceCatalog` entry containing the locale's final unlock quest and any non-quest requirement text. Runtime presentation does not hardcode prerequisite chains. `QuestGuidanceIndex.ResolvePrerequisiteFrontier` walks the game's formal `PreviousQuest` relationships backwards and returns the first actionable unmet quest (preferring an in-progress branch, then the deepest known branch). This keeps “what do I do next?” as shared quest-resolution behavior rather than locale-specific UI logic.

The explicit registry currently covers Eureka Anemos/Pagos/Pyros/Hydatos, the Bozjan Southern Front, Zadnor, and Occult Crescent South/North Horn. If a future special locale needs this behavior, add its final unlock quest to the registry; do not add a bespoke progression walker.

## Ordered special-area access gates (v1.3.4.37)
Special-area access is represented as ordered `AreaAccessGate` data rather than prose hints attached only to the final unlock quest. The area resolver first selects the first unmet gate from the explicit sequence, then asks `QuestGuidanceIndex.ResolvePrerequisiteFrontier` for the concrete formal prerequisite of that gate. Non-quest gate requirements are shown only when that gate is itself actionable. This prevents late-area targets from skipping earlier system-entry quests.

## Battle-leve monster context (v1.3.4.38)

Battle leve membership is modeled as local relationship data, not network enrichment. `LeveMonsterIndex` reverses the game's `Leve.DataId -> BattleLeve.LeveData -> BNpcName/BNpcBase` relationship into monster -> levequest contexts during resolver construction. The resolver attaches those contexts after provider deduplication, and presentation renders them as an expandable `Levequest` group beneath the monster.

This follows the source-ownership rule used elsewhere in CQG: Garland continues to own only the monster facts it actually provides, while battle-leve membership stays in the local game-data index. A named levequest is an actionable alternate context for a monster that has no overworld spawn; it must not cause CQG to invent coordinates or repeatedly request monster location enrichment.

## Static event-source policy (v1.3.5.0)

`EventSourcePolicy` is the single static normalization layer for event-derived acquisition facts. Provider rows remain source facts; the policy only classifies/cleans event-family presentation and never performs network IO. Event inactivity does not invalidate the acquisition relationship. Current event activity is intentionally separate state and must be queried only by a future explicit/on-demand availability component.
