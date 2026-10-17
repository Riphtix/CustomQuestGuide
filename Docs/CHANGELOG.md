# v1.3.7.1 — duty identity/level hotfix

- Duty display labels now identify treasure-map instances as `Treasure Dungeon`, e.g. `Treasure Dungeon: The Dungeons of Lyhe Ghiah - Lv. 1`, instead of falling back to generic `Duty`.
- Treasure dungeons are also grouped under a dedicated `Treasure Dungeons` duty category while retaining the shared level/type formatting used by all duties.
- Duty labels now include the duty type, name, and required character level in the canonical form `Dungeon: Mt. Gulg - Lv. 79`.
- Applies the same duty identity formatting to the Duties tab, unified target search/preview, pinned duties, duty dependencies, monster duty context, and `Run` guidance.
- Uses the class/job entry-level requirement only; item-level requirements are no longer eligible as a fallback for the displayed `Lv.` value.
- Keeps duty category filtering plural (`Dungeons`, `Trials`, `Raids`, `Alliance Raids`) while player-facing duty identity uses singular type labels.
- Release metadata and package links updated for the v1.3.7.1 hotfix.

# v1.3.7 — release-prep source cleanup

- Reorganized the flat source root into `Core`, `Domain`, `Data`, `Resolution`, `UI`, and `Infrastructure` responsibility folders.
- Consolidated small, tightly related policy/catalog partials without changing their public/internal type names or runtime behavior.
- Reduced C# source-file count from 57 to 37 and total source archive files from 69 to 49.
- Removed the stale `QuestGuidanceIndex.cs.tmp` artifact.
- Updated architecture documentation for the new layout.
- No resolver, ownership, networking, UI behavior, or cache semantics intentionally changed in this pass.

## v1.3.7 — Crafted-item drill-down breadcrumbs

- Adds session-only breadcrumbs when an ingredient is opened directly from a crafting recipe.
- Breadcrumbs show only the explicit recipe traversal path (for example `Finished Item > Subcraft > Ingredient`) and do not become a universal navigation bar.
- Earlier breadcrumb nodes are clickable, letting the player jump back to a crafted parent while keeping normal Back/Forward history independent.
- Ordinary target/search/set navigation clears the crafting trail so stale recipe ancestry cannot leak onto unrelated items.
- Direct recipe quantities remain non-recursive; breadcrumbs navigate the drill-down but do not aggregate base materials.

## v1.3.6.2 — Need/Greed nested-ULD + selected-title fallback

- Fixes target markers still failing to appear when the Need/Greed loot icon strip is nested inside component-owned ULD managers rather than the addon's root node list.
- Recursively inspects visible component ULD managers to locate the horizontal loot icon strip while remaining read-only.
- Adds a robust selected-item title fallback: if an icon slot cannot be mapped safely, a `T`/`S` marker is drawn directly beside the matching item name shown by the native Need/Greed window.
- Removes the hard dependency on `NumItems > 0` for rendering; struct/layout mismatches now fall through to the text-based marker instead of suppressing the feature entirely.
- Pinning an item after it has already dropped is reflected immediately through the live pin signature; no dungeon-entry snapshot is required.

## v1.3.6.1 — Need/Greed icon-strip geometry fix

- Fixes the first Need/Greed indicator implementation incorrectly treating the native addon as a scrolling list.
- Detects the live horizontal loot-icon strip from visible square addon nodes instead of relying on a fixed component node ID.
- Places direct-target (`T`) and set-member (`S`) markers on the matching loot icon slots.
- Keeps the overlay read-only: no Need/Greed/Pass interaction, source resolution, network work, or frame-rate ownership scanning.

## v1.3.6.0 — Need/Greed target indicators

- Adds a non-interactive CQG marker to matching rows in the native Need/Greed loot window.
- `T` marks a directly selected or pinned item target.
- `S` marks an unowned item still required by the currently selected set or a pinned set.
- Direct-target status outranks set-member status when both apply.
- Relic-set pins respect their selected job variant when deciding which loot items are still relevant.
- Marker rendering reads the native `AddonNeedGreed.Items` item IDs and list geometry only; it never clicks, rolls, passes, or fires loot callbacks.
- Target/set membership is cached and rebuilt only when target configuration changes or after a short local UI-cache interval; no network or ownership scan is triggered by the loot window.

## v1.3.5.6 — Historical provenance survives current-route ranking

- Added a dedicated historical-provenance channel so event origin is preserved even when a stronger current route such as Market Board / Player Trade wins source ranking.
- Route-level availability now merges current acquisition routes with historical event provenance before presentation.
- Added curated legacy provenance for Sea Breeze Summer Halter / Pareo, whose current actionable route is player trade while their original source was Moonfire Faire.
- Historical provenance remains context only and never outranks an actionable current gameplay source.

## v1.3.5.5 — Route-level event availability

- Availability is now evaluated per acquisition route instead of treating an item's historical event state as one global availability flag.
- Current in-game routes (vendor, duty, crafting, Market Board/player trade, etc.) outrank historical event provenance in the player-facing Source line.
- Historical event sources are retained in a dedicated `Historical source` block when a permanent/current route exists.
- Historical event items with no current gameplay route now explicitly show `Gameplay: Unavailable — no current in-game acquisition route found`, followed by the cached Online Store status.
- Market Board/player trade counts as a current gameplay route only for tradable items.
- Recompense Officer-style routes are presented as conditional rather than universally available because character eligibility can depend on prior event completion.
- Resolver-only dependency rows such as `Item Conversion` / `Currency Source` cannot masquerade as current gameplay acquisition routes.
- Obsolete event-currency-looking routes are not treated as permanent gameplay sources.
- Online Store checking is only scheduled when no current in-game route exists; it does not add network traffic to items already obtainable through gameplay/player economy.
- Exact Online Store matching remains conservative: CQG does not claim that a differently named `Authentic`/equivalent reissue is the same item unless an exact source relationship exists.

## v1.3.5.4 — Historical event Online Store availability

- Historical event rewards now separate gameplay availability from paid Online Store availability.
- Selecting an ended event reward starts one target-scoped FFXIV Collect lookup for its relevant collection endpoint, then persists both positive and negative results for seven days.
- The UI now states `Gameplay: Unavailable — original event has ended` independently of the store result.
- If a current Online Store source exists, CQG shows `Online Store: Available (paid optional item)`; otherwise it shows that no current listing was found.
- Event occurrence/quest provenance remains the primary source even when a paid Online Store route also exists.
- Online Store lookups are included in `Clear network caches`; transient provider failures back off for five minutes and are never cached as confirmed misses.

## v1.3.5.3 — Availability hierarchy polish

- Moves `Availability` immediately below the player-facing acquisition/source block so obtainability is communicated before resolver diagnostics.
- Ended/currently unobtainable event availability is shown with a restrained warning color instead of disabled gray text.
- Keeps `Resolution quality` and `Source specificity` as secondary diagnostic lines beneath availability.
- Does not change event resolution, source selection, or acquisition logic.

## v1.3.5.2 — Event source visual hierarchy

- Keeps the specific event occurrence as the top-level player-facing source.
- Indents the event classification beneath the source instead of presenting it as a peer line.
- Renders the concrete event quest as a bulleted child acquisition method, with a dimmed `Quest:` label and the quest name in normal text.
- Does not add an expand/collapse interaction for a single acquisition method, keeping the event summary immediately readable.

## v1.3.5.1 — Event occurrence authority

- Exact seasonal-event quests can now be associated with the specific event occurrence that owned them instead of being presented as ordinary permanent quests.
- `Earning Your Water Wings (Lv. 30)` is presented under `Moonfire Faire 2026` as a historical seasonal-event acquisition method.
- Historical occurrence knowledge now overrides the generic `Quest` kind for availability, so ended event rewards report `Event Ended / Currently Unobtainable` rather than `Available / Permanent`.
- The event is the player-facing acquisition family and the quest is shown beneath it as the acquisition method.
- Redundant generic seasonal-event fallbacks are suppressed when the primary source is already tied to an exact event occurrence.
- CQG does not offer quest-NPC routing for an event occurrence known to have ended.
- This remains static/local classification; no event-calendar network polling was added.

## v1.3.5.0 — Static event-source classification

- Event-derived acquisition routes are now a first-class resolved source family even when the event is not currently active.
- The generic `Seasonal / Collaboration Event` broad source is now allowed into player-facing results instead of being suppressed as unresolved placeholder data.
- Added centralized `EventSourcePolicy` normalization for seasonal events, recurring collaborations, Moogle Treasure Trove, and known legacy event families.
- FFXIV Collect-style `Event: <source>` rows are normalized for cleaner player-facing presentation while preserving the provider's concrete source text.
- Availability now follows the best already-known actionable source: event routes report `Limited / Recurring Event`, historical legacy-event routes report `Legacy / Existing Copies`, and a stronger permanent source keeps `Available / Permanent`.
- This build intentionally does not perform a live event-calendar/network check. Current event activity remains the next event-availability feature and must stay on-demand.

## v1.3.4.40 — NPC routing consistency and pinned-target render caching

- Centralized NPC destination rendering so any player-facing NPC direction now attempts to infer and expose an actionable map location even when the individual source family only supplied the NPC name.
- Known progression-gated special-area NPC destinations now resolve to the first unmet/current access quest instead of presenting an unusable NPC waypoint.
- Added a short-lived derived-view cache for the Pinned Targets tab. Expensive source resolution and set-frontier selection are no longer repeated every UI frame while the tab is open.
- The cache is presentation-only, is invalidated on saved configuration changes, and does not add ownership polling or background acquisition work.

## v1.3.4.39 — Levequest source normalization

- Canonicalized player-facing levequest sources by leve name so local game-data level rows and Garland provenance rows merge instead of appearing twice.
- Suppressed `Garland Tools leve data` from normal player-facing source detail.
- Moved alternate levequest acquisition rows into one expandable `Levequests` family, sorted by level then name.
- `other known sources` now counts only genuinely different acquisition families, so levequests no longer flood that list.

## v1.3.4.37 — Ordered locked-area progression gates
- Replaces late-area "final unlock quest + prose prerequisite" guidance with ordered area progression gates.
- Eureka Pyros/Hydatos/Pagos now walk back through the earlier Eureka entry quests before presenting the later-zone unlock.
- Each gate may own a non-quest requirement; CQG only displays that requirement when the gate itself is the actionable step.
- The selected gate still uses the generic quest prerequisite frontier, so formal MSQ/quest predecessors can be resolved to an earlier concrete next action.
- Removes the duplicate representative monster-location row from the expanded coordinate list.

## v1.3.4.35

- Added explicit access guidance for locked Eureka exploration zones from monster-location rows.
- When the zone unlock quest is incomplete, CQG no longer offers a map action that the game cannot display; it shows `Area locked`, names the unlock quest, and routes the player to the quest giver/location when local quest metadata is available.
- Added the four Eureka access gates (Anemos, Pagos, Pyros, Hydatos) as a small conservative catalog rather than inferring arbitrary zone locks.


## v1.3.4.33
- Monster coordinate actions are labeled **View Map** instead of **Pin** so they are not confused with persistent CQG target pins.
- Monster map opening now creates an exact Dalamud map-link payload from TerritoryType ID + Map ID + normalized displayed map X/Y. It no longer round-trips LuminaSupplemental map coordinates through world-space before opening the map.
- Special-region name lookup remains only as a last-resort compatibility fallback.
## v1.3.4.32 - exact-territory monster map pins
- Fixed monster-location Pin actions for special/aliased regions such as Eureka.
- Monster pins now preserve the exact TerritoryType ID from the resolved spawn instead of reconstructing a map link from the displayed zone name.
- Added inverse map-coordinate conversion so player-facing MobSpawn X/Y can be converted back to the world X/Z position required by Dalamud's exact territory/map link API.
- Kept the old place-name map-link path only as a last-resort fallback when exact territory/map identity cannot be resolved.

## v1.3.4.31 - conservative local FATE context

- Replaced the failed Garland mob-document FATE inference with a zero-network, item-scoped relationship derived from LuminaSupplemental `FateItem` + `MobDrop`.
- CQG only attaches FATE context when the item has explicit FATE evidence and exactly one concrete monster identity; multi-monster items remain unattached rather than guessed.
- FATE context is no longer promoted into the global monster cache because the available supplemental evidence is item-specific, not a universal monster-to-FATE mapping.
- `Find FATE monster tests` is now an immediate local scan of the exact production rule and makes no Garland requests.

## v1.3.4.30 - Debug FATE-monster test finder

- Added **Find FATE monster tests** under Debug -> Supplemental data tools.
- The finder builds a bounded list of real targetable MobDrop monsters from local LuminaSupplemental data, checks the global monster cache first, and only then probes Garland mob documents one at a time with pacing when needed.
- Stops early once representative cases are found for **FATE + verified location**, **FATE context only**, and **Multiple FATE relationships**.
- Each result includes a **Test** button that selects a real dropped item so the normal production monster-source UI can be verified directly.
- The finder is explicit/on-demand only, is capped at 80 candidate monsters per run, can be cancelled, and preserves the normal global monster cache so discovered FATE relationships are reused later.

## v1.3.4.29 - FATE-associated monster context

- Added static FATE context to monster-drop sources when the dedicated Garland monster document exposes related FATE partials.
- FATE association is cached globally with the monster in `monster-cache.json`; it is not tied to any one item.
- FATE context can satisfy the actionable-context portion of monster completion when an exact overworld coordinate is unavailable.
- FATE context is presentation-only/static: CQG does not attempt to detect whether the FATE is currently active.
- Monsters with normal spawn coordinates keep those coordinates as their primary row and show associated FATE names as indented subtext.
- Schema-1 monster cache entries keep their level/location hints but are revalidated lazily once so FATE metadata can be learned without a blanket cache wipe.

## v1.3.4.28 - Duty-only monster context

- Treat a concrete monster as complete when it has exact identity + level and either an actionable overworld spawn or one unambiguous named duty context.
- Locationless monsters inherit a duty context only when the complete item source set resolves to exactly one concrete duty; ambiguous duty sets remain unresolved instead of guessing.
- Duty-only monsters no longer request location enrichment solely to satisfy an overworld coordinate contract.
- Player-facing duty-only monster rows show the duty name as subtext and an `Open Duty` action when the duty exists in the duty catalog.
- Provider fallback coordinates are hidden for duty-context monsters; the duty is the actionable location.

## v1.3.4.27 - Stable monster-enrichment status and confidence-test targets

- Keeps the selected-target monster status stable across bounded retry/backoff windows instead of flickering between resolving and unavailable after each request.
- Coordinate-confidence test discovery now prefers monsters that are already locally complete (level + surviving coordinate data), reducing unrelated network enrichment during UI verification.

## v1.3.4.25

- Replaced runtime MobSpawn blacklist heuristics with an explicit territory+exact-position override table generated from the audited LuminaSupplemental snapshot.
- Confirmed placeholder coordinates are suppressed completely.
- Suspicious coordinates are retained as Unverified, sorted below verified locations, and rendered with an `*` tooltip: `Unverified coordinate`.
- Verified coordinates always win representative/collapsed display priority when available.
- Debug MobSpawn audit now reports the bundled classification (`Suppress`, `Unverified`, `Normal`) for each candidate.

# v1.3.4.24 - MobSpawn audit parser fix

- Fixed the Debug `Export MobSpawn blacklist audit` utility returning zero candidates even while runtime monster resolution could read MobSpawn coordinates. LuminaSupplemental 5.x commonly exposes `Position` as one serialized vector value (`X;Y;Z`), while the first audit implementation only inspected flattened `Position.X/Y/Z` fields.
- Runtime MobSpawn parsing and the Debug audit now share one raw-position decoder, preventing the audit and live resolver from interpreting the same upstream row differently.
- The shared decoder preserves the third Position component so explicit blacklist matching can work even when LuminaSupplemental exposes Position as one serialized value.
- Audit exports now include rows scanned, rows with parsed positions, rows with territory+position, unique exact tuples, candidate count, and a normalized MobSpawn SHA-256 fingerprint. An empty report can no longer look successful without showing what was actually scanned.
- Removed the invalid package-version-vs-assembly-version comparison (`5.1.5` vs `1.0.0.0`). Debug output now reports package baseline and CLR assembly metadata separately; the MobSpawn data fingerprint is the stable comparison point for future upstream re-audits.
- No new blacklist coordinates were promoted in this build. The explicit territory/tuple blacklist will be generated from a successful audit and reviewed separately.

## v1.3.4.23 — MobSpawn blacklist audit + wrapped debug tools

- Added an on-demand Debug utility, **Export MobSpawn blacklist audit**, that scans the currently loaded LuminaSupplemental `MobSpawn` dataset and exports suspicious exact `Position` tuples with territory, territory name, map-size factor, local/global reuse counts, distinct monster counts, X==Y signal, and whether CQG's bundled blacklist currently matches the tuple.
- The audit is diagnostic-only: it performs no network calls and never mutates the runtime blacklist. It is intended to be run after LuminaSupplemental updates so candidate placeholder coordinates can be recollected and manually verified before updating CQG's hardcoded compatibility data.
- Added the LuminaSupplemental assembly version and the blacklist baseline package version (`5.1.5`) to the Debug supplemental section and audit export. A baseline mismatch is surfaced as `RE-AUDIT RECOMMENDED`.
- Wrapped the growing Debug button collection into collapsible sections: **Cache & refresh controls**, **Selected target diagnostics**, **Catalog audits & exports**, and **Supplemental data tools**.
- No monster-resolution, network scheduling, ownership, or source-presentation behavior changed in this revision.

## v1.3.4.22 — explicit MobSpawn placeholder blacklist

- Reverted the v1.3.4.21 MobSpawn X/Z interpretation after validating LuminaSupplemental's generated data against known map locations: `Position.X`/`Position.Y` are consumed directly as displayed map coordinates.
- Added an explicit, narrowly scoped blacklist for the three verified placeholder families seen in the upstream MobSpawn dataset: `(21.48, 21.48, -1)` on size-factor 100 maps, `(11.24, 11.24, -2)` on size-factor 200 maps, and `(22.557896, 22.557896, -0.95)` on size-factor 95 maps.
- The blacklist requires the full observed triple and the matching map-size family. It does not reject generic `X == Y`, negative third-component values, or arbitrary centre-map coordinates.
- Blacklisted rows are simply omitted from local monster locations; legitimate rows for the same monster/territory continue to populate normally.
- No changes to the v1.3.4.20 sequential monster-cache/resolution flow.

## v1.3.4.21 — MobSpawn coordinate-domain correction

- Corrected LuminaSupplemental `MobSpawn.Position` handling: Position is world-space, so CQG now converts Position.X/Position.Z through `MapCoordinateConverter`.
- Position.Y is treated as elevation and is no longer eligible to become displayed map Y.
- Preserves world X/Z alongside converted map coordinates so Pin can use the exact spawn position.
- Removes the permissive "looks like 0-60, therefore it must already be map-space" interpretation for structured MobSpawn Position data.
- No resolver, sequential monster-cache, source-order, or presentation-lifecycle changes in this hotfix.

## v1.3.4.20 — sequential global monster cache

- Reworked monster enrichment to the simpler cache-first sequence: discover the full item monster list, render identities immediately, then resolve one monster at a time in deterministic order.
- Added `MonsterMetadataCache` backed by `monster-cache.json`. Monster level/location/validation data is keyed globally by monster identity instead of copied into item cache records.
- Every successful monster lookup is persisted before the worker advances to the next monster. Future items referencing the same monster read the cache first and skip the network request.
- Removed live monster-to-item source mutation and the associated cache-write coalescing path. The resolver now reads global monster metadata directly on normal UI frames.
- `Rebuild selected source` now rebuilds only the selected item source record and preserves global monster metadata. `Clear network caches` still clears both item and monster caches.
- Preserved the exact Garland provider key separately from normalized BNpc identity so the dedicated mob endpoint is always queried with the identity discovered by the item document.
- Migrates legacy monster metadata from older combined `garland-source-cache.json` files into the new global monster cache when available.

## v1.3.4.26 - Debug coordinate-confidence test finder
- Added an on-demand Debug utility that finds real monster-drop targets exercising coordinate-confidence presentation.
- Finds examples for Unverified + verified, Unverified-only, and Suppressed placeholder cases using only loaded LuminaSupplemental data.
- Each example includes a Test button that selects a real target item so the production monster UI can be verified directly.
- No network requests are performed by the finder.

## v1.3.4.34
- Standardized navigation language: map/flag actions use **View Map**; **Pin/Unpin** is reserved for CQG persistent targets.
- Renamed monster map-action helpers and internal map-action state to match the user-facing semantics.
- Updated Help text to document the distinction.

## v1.3.4.36 — General locked-area progression guidance
- Replaced the vague locked-area fallback (“continue the preceding storyline”) with a generic quest-prerequisite frontier resolver.
- Locked special locales now walk the formal `PreviousQuest` graph and show one concrete current/next quest plus its quest giver/location.
- Added explicit non-quest requirement notes for field-operation progression gates that normal quest metadata cannot express (for example Eureka elemental-level/story requirements).
- Expanded locked-area handling beyond Eureka to the Bozjan Southern Front, Zadnor, Occult Crescent: South Horn, and Occult Crescent: North Horn.
- Kept the locale registry explicit and conservative; normal zones are not guessed to be locked.

## v1.3.4.38 — Levequest monster context

- Added a zero-network local `LeveMonsterIndex` built from the game's `Leve -> BattleLeve -> LeveData` relationship.
- Monster sources now carry every battle levequest known to contain that enemy.
- Added expandable `Levequest` context beneath monster rows, ordered by leve level and name.
- Levequest context can satisfy a locationless monster's actionable-context contract without inventing an overworld coordinate.
- Added Debug -> Supplemental data tools -> `Find levequest monster tests` for one-click production-UI verification.
- Leve relationships are local game data and are not added to the network monster cache.

## v1.3.4.41 — NPC map-action visibility and pinned-tab render caching

- NPC guidance now renders an explicit `View Map` button rather than making the location text itself the only map affordance.
- Guide-backed targets no longer suppress a direct vendor/NPC map action when the acquisition source points at an NPC.
- The Pinned Targets tab now caches title/completion/progress/location-derived state until an explicit invalidation (pin/config mutation or re-entering the tab), instead of rebuilding expensive resolver/frontier/quest state every second and repeating completion/progress walks every frame.
- This remains UI caching only: no background polling or persistent acquisition scans were added.

## v1.3.4.42 — Pinned Targets 30-second snapshot refresh

- Pinned Targets now takes one fresh derived-state snapshot when the tab is opened.
- While the tab remains open, that snapshot is invalidated only once every 30 seconds instead of doing expensive derived work at frame rate.
- Added a top-right `X Seconds Until Refresh` countdown so the refresh cadence is visible to the player.
- Pin/config mutations still invalidate the snapshot immediately, then restart the 30-second refresh window.
- No background ownership polling or acquisition scanning was added.

## v1.3.4.43 — NPC map action completion

- Fixed `Go to: <NPC>` guidance paths that dropped the resolved source NPC ID before map rendering.
- Added a verified Eirene fallback at Solution Nine (X:8.1, Y:14.0) for clients where her placement is not exposed by the generated Level/ENpc relationship.
- Explicit coordinates now win over generic PlaceName representative points when opening a map flag.

## v1.3.5.7 — Online Store as a first-class acquisition source

- Online Store lookup is no longer limited to historical-event fallback. A selected item with no current gameplay route can now perform the same target-scoped, cached store lookup.
- Pure store-only targets can promote the Online Store into the visible `Source:` field once the lookup resolves, instead of remaining `No source resolved yet`.
- Store-only targets now explicitly distinguish `Gameplay: Unavailable` from `Online Store: Available (paid optional item)`.
- FFXIV Collect outfit bundles can now match their contained gear pieces. When an outfit record supplies the store route, CQG preserves the parent bundle name (for example, an individual Warden gear piece can resolve through `The Warden's Attire`).
- The Online Store cache schema was bumped so older negative results produced before contained-outfit matching are not retained.
- The lookup remains selected-target only, persists positive/negative results, and does not add background polling.

## v1.3.5.8 — Selected-target community source auto-refresh

- Added target-scoped FFXIV Collect enrichment for the actively selected item.
- Event/store/collectible source data now merges into the resolver as soon as the async lookup completes; users no longer need to press the broad **Refresh acquisition sources** button to make a selected target update.
- The selected-target request is limited to the relevant FFXIV Collect collection families and is session-cached for 30 minutes.
- Added `Resolving community source data...` while that target-scoped request is in flight.
- Transient failures remain retryable and are not turned into permanent negative source data.

## v1.3.6.3 — Loot marker placement/readability
- Fixed the Need/Greed title fallback marker using the full text-node width, which could place the badge over the window close button.
- The fallback now anchors from the actual rendered item-name width inside the centered text node.
- Reworked T/S markers to use a dark badge with a colored target/set ring and white glyph for much stronger contrast.
