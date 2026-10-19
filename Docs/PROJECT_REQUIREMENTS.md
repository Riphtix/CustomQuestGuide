# Custom Quest Guide — Project Requirements

This document is the standing source of truth for Custom Quest Guide (CQG).
When code, older comments, prior experiments, or assumptions conflict with this
file, stop and reconcile the conflict before continuing implementation.

## 1. Project workflow

- Work from the latest source ZIP explicitly supplied or approved by the user.
- Return complete updated source ZIPs for testing. Do not switch to patch-only
  delivery unless the user explicitly asks for a patch.
- Do not create GitHub branches, commits, pushes, pull requests, releases, or
  otherwise modify the remote repository unless the user explicitly asks.
- Local source-control tools may be used only as an implementation aid.
- Preserve working behavior while adding features. Avoid unrelated refactors in
  feature passes unless required for correctness or maintainability.
- Keep release/version metadata unchanged unless the user asks for a version or
  release preparation change.

## 2. Maintainability and code standards

CQG must remain understandable and modifiable by its maintainer without needing
to reverse-engineer large monolithic methods.

- Prefer small, focused classes and methods with clear ownership.
- Keep source classification and player-facing source-priority policy
  centralized rather than scattering string checks and magic priority numbers.
- Reduce repeated code when a shared abstraction improves readability.
- Use descriptive names and comments that explain *why*, data quirks, invariants,
  and provider limitations. Do not add comments that merely repeat obvious code.
- Prefer explicit domain models over loosely related string conventions.
- Avoid hidden side effects in draw methods, especially network requests.
- New source/provider types should have an obvious extension point.
- **Providers contribute facts; source-family resolution owns completeness.** A
  provider finding one useful field must never independently declare a source
  complete when that source family still requires other facts. Apply this same
  completion-contract pattern to monster, vendor/exchange, duty, crafting, event,
  and future source families as they are implemented or revisited.
- Preserve `ARCHITECTURE.md` as the structural map and keep it aligned with major
  architectural changes.

## 3. Core product philosophy

CQG is a target-oriented acquisition and progression guide. Its primary answer
should be the most useful thing the player can repeatedly do to obtain or unlock
the target.

- Prefer useful repeatable acquisition routes over one-time, obsolete, legacy,
  incidental, or convenience-only relationships.
- Source-specific details belong directly beneath the source they explain.
- Do not flatten unrelated relationships into a generic prerequisite wall.
- Market Board / player trade is a convenience fallback, not the preferred
  source when a direct repeatable acquisition route is known.
- Retainer ventures and levequests are alternate sources when a concrete monster
  drop exists; they must not replace `Monster Drop` as the primary source.
- A source may be resolved even if it is not currently available (for example,
  an inactive event). Availability and source resolution are separate concepts.

## 4. Ownership and polling invariants

- Preserve the Glamour Dresser snapshot/cache fix introduced in v0.51.1.
- Items owned through supported inventories, guides, unlock state, or the
  Glamour Dresser should use the established ownership colors.
- Green = owned/unlocked.
- Blue = partial job progression where appropriate.
- Soft pink = Glamour Dresser / glam ownership state.
- Red = unresolved source, not merely temporarily unavailable.
- `/cqg` performs the intended bounded ownership scan behavior. Do not introduce
  continuous background ownership polling.
- Selected targets may be prioritized during an active scan, but the plugin must
  not become a permanent polling loop.

## 5. Network and cache policy

**1.3.4 implementation contract:** overworld monster rows merge local LuminaSupplemental `MobSpawn`/`BNpcLink` facts with cached/on-demand network mob metadata when local data is incomplete. The lowest-zone known spawn is shown on the collapsed monster row with a `View Map` action. Expanding that monster shows every known spawn coordinate, each individually viewable on the map. This location expander is part of the Monster Drop source and is separate from both `View More Monsters of Higher Level` and `X other known sources`. Location lookup must use the normal target-scoped cache policy and must not introduce persistent polling.


Network access is enrichment, not the default data path.

### 5.1 General rules

- Do not continuously poll external services.
- Do not bulk-query item-specific network endpoints at startup or merely because
  `/cqg` opened.
- Prefer local Lumina/LuminaSupplemental data whenever it is sufficient.
- When a selected target requires missing external enrichment, make only the
  required target-scoped request through the existing rate-limited queue.
- Cache both successful external results and confirmed misses where appropriate.
- Repeated normal use of a validated cached result should require zero network
  requests.
- If an item document identifies a creature/dropper but does not include the
  creature level, request the authoritative creature/mob document only when that
  selected target needs it. Cache creature metadata globally by creature ID so
  every future item can reuse it without another network request.

### 5.2 Cache-first revalidation after game updates

Network-derived cached data is persistent and remains immediately usable.

- Associate persisted network-derived item enrichment with a local game-data
  fingerprint/version marker representing the game data it was last validated
  against.
- A meaningful game-data change (for example a major patch or expansion) marks
  network-derived cached entries as due for revalidation; it does **not** delete
  or hide them.
- When the user selects an affected target, display its cached result immediately
  and queue one controlled revalidation request.
- After successful validation, store the current game-data fingerprint so future
  selections perform zero requests until the fingerprint changes again.
- Transient network/schema failures must not overwrite good cached data or turn
  into long-lived false negative results.

### 5.3 Revalidation UI

Cached data must never disappear while it is being checked.

- If cached information is currently displayed and its network revalidation is
  queued or in flight, show a non-blocking status: `Refreshing source data...`.
- If the request completes successfully and the external source data is
  unchanged, show:
  `No new data found (10 seconds...)`
- Count that message down once per second (`9 seconds...`, etc.).
- Remove the message when the 10-second timer reaches zero.
- The status is informational only and must not block interaction with the cached
  acquisition information.

## 6. Source presentation rules

- The primary source line represents the acquisition *method*, not every provider
  record independently.
- Alternate acquisition methods belong under an expandable `other known sources`
  row. A count such as `+49 other known sources` must never be a dead-end label;
  it must be expandable.
- Provider duplicates should be merged/deduplicated while preserving useful
  metadata from every provider.
- Sorting/presentation policy must not accidentally discard richer metadata from
  a secondary provider.

## 7. Crafting requirements

- A normally craftable item should present `Source: Crafted` when crafting is the
  useful primary repeatable route.
- Show direct recipe ingredients/components and quantities exactly as the recipe
  defines them.
- Do not recursively aggregate total base materials across nested recipes.
- Recipe/component details belong beneath the Crafted source.
- Ingredients should support Open / Pin where applicable.
- Retired catalyst/exchange relationships such as legacy Grade 2 Glamour Prism
  requirements must not leak into the modern recipe as irrelevant prerequisite
  clutter.
- Crafted-item breadcrumbs are enabled for explicit recipe ingredient drill-down.
  They show the current craft traversal path and allow jumping back to an earlier
  crafted parent. They are not a universal navigation system and must not appear
  for ordinary search/target navigation.

## 8. General navigation

- Back and Forward navigation are supported for explicit target navigation.
- Plain top-level tab switching should not pollute history.
- General Back/Forward history and optional crafted-item breadcrumbs are separate
  concepts.

## 9. Monster-drop requirements

When concrete monster drops are known, `Monster Drop` is the preferred primary
repeatable source over retainer ventures, levequests, and Market Board.

### 9.0 Monster completion contract

- A concrete overworld `Monster Drop` is not complete until CQG has the monster
  identity, monster level, and at least one usable zone/coordinate.
- Local and network providers contribute facts to one merged monster object. No
  provider-specific cache flag, known name, or known level may bypass this
  family-level completion check.
- Monster identity joins must be exact. A concrete monster identity is the
  `BNpcBase + BNpcName` pair; `BNpcName` alone is not sufficient because multiple
  battle-NPC bases can share the same name row/display text. Critical
  `BNpcName` / `BNpcBase` / territory IDs may not be discovered with
  substring/contains matching, and a display name may not be broadened across all
  same-name variants. Ambiguous identity remains incomplete until an exact
  provider resolves it. Never solve a bad pin by hard-coding or blacklisting a
  coordinate.
- If all currently available providers have been checked and required facts are
  still unavailable, keep the source explicitly incomplete rather than reporting
  the item as exact/complete or displaying `No new data found`.

### 9.1 Monster list

- Resolve and show all distinct known monsters that drop the item.
- Merge duplicate monster records across providers rather than choosing one and
  losing metadata from another.
- Display monster level inline in the same style as levequest levels, for example:
  `Banemite — Lv. 25`.
- Show the lowest-level known monster directly beneath `Source: Monster Drop` as
  the default/recommended farm target.
- Collapse every additional monster beneath an expander labeled
  `View More Monsters of Higher Level`.
- This higher-level-monster expander is part of the primary `Monster Drop` source
  and is completely separate from `other known sources`. Higher-level monsters must
  never increase that alternate-source count or appear inside that alternate-source
  expander; `other known sources` is reserved for different acquisition methods such
  as levequests, retainer ventures, vendors, quests, and Market Board.
- Order the monster list by monster level first, then zone level, then name so the
  immediately visible monster is the easiest known concrete target. Zone level
  remains a fallback/tie-breaker and will also drive the later location UI.
- Unknown zone/monster levels sort after known values.

### 9.2 Overworld monster locations

Planned monster-location work should:

- Give each monster an expandable coordinate/location list.
- Allow the collapsed row to show a representative location and Pin action.
- Show all known spawn locations/coordinates when expanded.
- Make each coordinate individually pinnable.

### 9.3 Duty/FATE/leve monsters

- Duty-only monster: show the monster, duty name as subtext, and `Open Duty`
  where supported.
- FATE association: static source information only. Do not attempt to indicate
  whether the FATE is currently active.
- Leve association: present as an expandable `Levequest` group containing all
  possible leves that include the enemy.

## 10. Duties, currencies, and source actions

- Duties tab supports Trial / Dungeon / Alliance selection and unlocked state.
- Locked duties should identify the unlock quest and support Pin when possible.
- Clicking a usable location should place a map pin. Interior-only locations may
  fall back to a useful entrance/exterior pin.
- Tomestones should prefer an Open/shop action where that is the practical source
  behavior, including in compact mode.
- Special-currency exchanges should show the relevant NPC/location plus useful
  information about where that currency comes from.
- Open and Pin controls that are peers should use consistent sizing.

## 11. Relics and progression

- Preserve smart relic/progression behavior, including current-step awareness.
- Job unlock ownership and further job progression are separate states: an
  unlocked job may be green while later progression remains incomplete.
- Avoid treating a single unlock as completion of an entire progression chain.

## 12. Event sources

**v1.3.5.0 static event contract:**

- Event-derived items are resolved event sources even when the event is inactive.
- Generic `Seasonal / Collaboration Event` data is valid player-facing system context; inactivity must not make the target unresolved/red.
- When an exact reward quest belongs to a specific seasonal/collaboration event occurrence, present the event occurrence as the acquisition family and the quest beneath it as the method; do not misclassify an ended event quest as a permanent quest.
- An event occurrence known statically to have ended should report `Event Ended / Currently Unobtainable` and should not offer an invalid quest-NPC route.
- Normalize concrete seasonal/collaboration source names centrally without discarding exact provider text.
- Availability is supplemental state rather than resolution state. Static classification may report `Limited / Recurring Event`, `Legacy / Existing Copies`, or a stronger permanent/external state from already-known acquisition sources.

Remaining live-event work should:

- Check current event availability on demand for the selected target rather than with continuous polling.
- Cache live event availability sensibly under the general network policy above.
- Never make live availability a prerequisite for resolving the underlying event source.

## 13. Diagnostics and safety rails

- Diagnostics/audits explicitly documented as zero-network must remain zero-network.
- Debug exports should distinguish missing network enrichment from genuinely
  unresolved data.
- `Clear network caches` is a diagnostic/manual operation; ordinary users should
  not need it for normal freshness.
- The normal Items UI must provide a target-scoped repair action (`Rebuild selected source`) for stale or incomplete acquisition data. A manual press must first delete the selected item's persisted target-scoped external acquisition-source cache plus the related cached monster metadata used by that target, then force one fresh selected-item network request before rebuilding the result. It must not accept the just-invalidated answer merely because local data currently looks complete. Unrelated source caches, ownership data, and the Glamour Dresser snapshot must remain untouched.
- Target-scoped repair is distinct from the five-minute broad community-source refresh cooldown. It may run whenever that selected target has no request already pending/in flight because it cannot fan out across the catalog.
- Do not silently introduce network requests into UI draw loops, startup, audits,
  or ownership scans.

## 14. Deferred / explicitly skipped work

- Crafted-item breadcrumbs were resumed in v1.3.7 after the user explicitly
  asked to continue through the remaining 1.3.x backlog. Keep them scoped only to
  recipe ingredient drill-down.

---

When adding a new requirement, update this document in the same source pass so
future work can be checked against one stable written contract.

### Selected-target enrichment lifecycle
- A target that is already selected when the plugin/window is opened must be evaluated once for newly-required enrichment; the user must not have to re-select it manually after an update.
- This evaluation remains selected-target-only and must not become a background sweep or polling loop.
- Dedicated monster metadata is cached globally by exact BNpcBase + BNpcName identity so level/location data can be reused by every item from the same monster.
- When a cache schema gains a new required network-derived field, older cached values remain usable immediately but their validation state must not suppress the one controlled revalidation needed to populate the new field.

## Monster source completion contract (v1.3.4+)

Monster Drop resolution is a single coordinated pipeline, not separate competing
"name", "level", and "location" processes.

For an overworld monster source, CQG must not consider the source complete until
it has resolved all of the following:

1. Concrete monster identity/name.
2. Monster level.
3. At least one actionable location (zone + coordinates).

Local Lumina/LuminaSupplemental data and cached/network Garland data are inputs to
the same monster object. They should merge their useful fields rather than each
provider independently deciding that the source is complete.

A level embedded in a Garland item partial is a useful hint, but it must never
mark the dedicated mob metadata as validated because the mob document owns the
location (`coords` / `zoneid`) as well. When a selected item's monster object is
incomplete, CQG should fetch only the missing required network metadata, cache it
by exact monster identity, merge it with local spawn data, and then reassess the
same completion contract.

If the dedicated provider has been checked for the current game-data version but
still cannot supply a required field, CQG should retain the best cached/local
answer and remain visibly incomplete rather than repeatedly requesting the same
unchanged document on every selection.

### Map-coordinate normalization
- Source-provider coordinates must be classified/normalized at the provider boundary before presentation code uses them.
- LuminaSupplemental `MobSpawn.Position` values are world-space X/Y/Z. Use Position.X and Position.Z, convert them once to player-facing map coordinates, and preserve world X/Z for precise pinning. Position.Y is elevation and must not be used as map Y.
- Raw/world-space positions must never be displayed or passed to a map-coordinate API as if they were player-facing X/Y.
- Only provider fields that explicitly identify themselves as world X/Z should use the centralized Map/SizeFactor/offset conversion, retaining the original world position for precise pin opening.
- Pinning should prefer TerritoryType + Map + original world position only when genuine world-space data exists; already-normalized map coordinates should use the ordinary coordinate map-link path.
- Coordinate-domain handling must remain centralized and reusable so future source families do not independently guess coordinate semantics.

### Monster identity and completion semantics

- `BNpcBase` is a reusable battle-NPC template and is **not** sufficient by itself to prove that a spawn belongs to a named monster. It may only be used to discover candidates.
- Player-facing spawn locations must be tied to an exact named monster identity (`BNpcBase + BNpcName`) or another equivalently exact provider relationship.
- If local Supplemental data cannot disambiguate a shared `BNpcBase`, CQG must prefer the exact cached/network mob identity instead of attaching every base-level spawn to every linked name.
- A Monster Drop source is player-actionable/complete when its **primary recommended monster** has identity + level + at least one exact location. Higher-level alternative monsters may continue to enrich, but missing optional alternative metadata must not keep the primary source labeled incomplete.
- The same source-family principle applies elsewhere: optional alternatives/details must not block a complete primary actionable route, while required fields for that primary route must all be satisfied before the family is considered complete.

### Exact monster locations vs representative fallbacks

- Exact local `MobSpawn` coordinates are authoritative for a monster's displayed location list.
- Garland mob coordinates are representative **fallback-only** locations. They may be shown only when CQG has no exact local spawn coordinates for that monster.
- A fallback coordinate must never be merged into, counted as, or selected ahead of an exact spawn list.
- Monster metadata must be normalized **after provider deduplication** as well as during provider ingestion, so merging cached/network rows cannot reintroduce stale fallback locations after exact local spawns were resolved.
- A MobSpawn row that names BNpcName directly is exact. A location inferred only through a unique BNpcBase -> BNpcName bridge is fallback-only and must disappear whenever directly named spawn rows exist.
- Once a monster has an exact BNpcName ID, CQG must not broaden the lookup to every BNpcName record sharing the same display name.

- The collapsed monster row must choose its representative location from the exact list whenever exact locations exist.

### Final monster normalization invariant
- Every player-facing Monster Drop row must pass through final monster normalization on every resolver pass, even if cached/provider metadata already appears complete.
- Do not short-circuit normalization based on cached `MonsterLevel`, `MonsterZoneLevel`, or apparently exact locations. Older cache records may predate provenance flags and can otherwise preserve stale representative coordinates.
- Current exact local `MobSpawn` locations are rebuilt from the exact monster identity and are authoritative. Cached/network coordinates survive only when no exact local spawn can be established.
### MobSpawn blacklist maintenance

- Confirmed LuminaSupplemental MobSpawn placeholder coordinates are packaged with CQG as hardcoded compatibility data.
- Placeholder filtering must remain explicit and conservative; do not infer arbitrary bad coordinates from generic rules such as negative Z or X == Y alone.
- Provide a Debug utility that re-audits the currently loaded MobSpawn dataset after LuminaSupplemental changes and exports candidate repeated tuples for manual verification.
- The audit must be read-only and network-free. It must record the loaded LuminaSupplemental version/baseline information needed to know when the bundled blacklist should be reviewed.
- Debug controls should be grouped/collapsible as the diagnostic surface grows.


- MobSpawn confirmed placeholders must not be shown or cached as usable locations. Suspicious audited coordinates remain visible as unverified, show `*` with tooltip `Unverified coordinate`, and sort below verified locations.
- MobSpawn coordinate classifications are explicit territory+exact-position compatibility data regenerated/reviewed after LuminaSupplemental dataset updates.

- FATE-associated monsters: show the associated FATE name as static monster context. Do not query or display whether the FATE is currently active. FATE relationship metadata belongs to the global monster cache, not an item cache.


- FATE-monster context must remain static and must not imply live FATE state. Where only item-scoped FateItem + MobDrop evidence exists, attach it only when exactly one concrete monster can be identified; do not guess across multiple monster sources.

### Action terminology
- **View Map** means open the FFXIV map / place a temporary map flag.
- **Pin / Unpin** is reserved for CQG persistent targets (items, duties, sets, job progression, etc.).
- Never label a map-opening/map-flag action as **Pin**.


## Locked special-area map guidance (v1.3.4.35+)
When a monster coordinate belongs to a conservatively cataloged special area whose unlock quest is not complete, do not offer a map action that the game cannot display. Show the area as locked, name the unlock quest, and use normal quest guidance to direct the player to the quest giver/location. Once the unlock quest is complete, restore the ordinary `View Map` action. The initial explicit catalog covers Eureka Anemos, Pagos, Pyros, and Hydatos; do not infer arbitrary locked areas without a known unlock relationship.

### Locked/special locale guidance
- When a source points into a known locked special locale, CQG must not stop at “unlock quest unavailable” or generic storyline advice.
- Resolve the final locale unlock quest through the shared quest prerequisite graph and show the first concrete unmet/current quest with its NPC/location.
- Preserve explicit notes for non-quest gates (field-operation rank/elemental/story requirements) that cannot be inferred from normal quest completion bits.
- `View Map` is shown only after the locale's final unlock quest is complete; otherwise show actionable access guidance.
- Keep special-locale recognition explicit/conservative rather than guessing arbitrary map failures are access locks.

## Ordered locked-area guidance (v1.3.4.37+)
- Special/locked locales must resolve to the first actionable unmet progression gate, not merely name the locale's final unlock quest.
- Area progression may define explicit ordered quest gates when game `PreviousQuest` metadata is insufficient.
- Each gate may define a non-quest requirement; show that requirement only when the gate itself is the current/next actionable step.
- Continue to use the generic quest-prerequisite resolver inside the selected gate so formal predecessors can lead to an earlier concrete quest.
- Do not display vague "continue the storyline" fallback text when an explicit gate is known.

### Levequest monsters
- Resolve battle-leve enemy membership from local game data (`Leve -> BattleLeve`) with zero network traffic.
- Present monster leve relationships as an expandable `Levequest` group showing every known leve containing that enemy.
- Sort levequests deterministically by level then name.
- A locationless monster with concrete levequest context is actionable and must not be treated as missing an overworld coordinate.
- Keep levequest relationships out of the network monster cache; they belong to the local game-data relationship index.

## NPC destination routing (v1.3.4.40+)
- Whenever player-facing guidance tells the user to visit/talk to an NPC, route that line through the shared NPC location/map path; source-specific UIs should not emit a dead plain-text NPC destination when a live-game placement can be resolved.
- If the destination is in an explicitly known progression-gated locale that the character cannot yet access, do not present the unreachable NPC waypoint. Resolve the access chain to the first current/unmet quest and show that quest/NPC instead.
- Missing NPC placement data must be represented honestly as unavailable; never fabricate coordinates.

## Pinned Targets performance
- Drawing the Pinned Targets tab must be presentation-cheap. Do not perform source resolution, set-frontier selection, network requests, ownership scans, or other expensive acquisition work at UI frame rate.
- Derived pin guidance may be cached/invalidation-driven. Such caching is UI state only and must not become background polling.

## Loot-window target indicator (v1.3.6.0+)
- When an item shown in the FFXIV Need/Greed loot window is a directly targeted/pinned item, or is still needed by an active targeted/pinned set, show a small CQG visual indicator on that loot row.
- Use `T` for a direct item target and `S` for a still-needed set member; direct targeting wins if both apply.
- The indicator is informational only: CQG must not automate Need/Greed/Pass decisions, fire loot callbacks, or interact with loot on the player's behalf.
- The loot overlay must remain presentation-cheap: use native loot item IDs plus cached target membership and do not trigger source resolution, network requests, or ownership scans at frame rate.
## Event presentation hierarchy (v1.3.5.2)

- A specific event occurrence is the top-level acquisition source.
- Event classification/status is subordinate visual metadata and should be indented/dimmed.
- The concrete quest or other acquisition method is rendered as a child beneath the event occurrence rather than as a peer source.
- Avoid expandable UI when an event has only one known acquisition method; the relationship should be immediately visible.
## Availability presentation hierarchy (v1.3.5.3)

- Present availability immediately after the acquisition/source block and before resolver-quality diagnostics.
- Treat `Event Ended / Currently Unobtainable` and equivalent currently-unobtainable states as player-action information, not diagnostic metadata.
- Use a restrained warning treatment for currently unobtainable states; do not use the unresolved/error red reserved for resolution failures.
- Keep resolution quality and source specificity visually secondary.



## Historical event Online Store availability (v1.3.5.4)

- An ended event reward must report gameplay availability separately from any paid alternate source.
- When gameplay acquisition has ended, CQG should check whether the selected target currently has an Online Store source before presenting the final availability state.
- Show `Gameplay: Unavailable — original event has ended` even when the item is purchasable.
- When a paid source exists, show `Online Store: Available (paid optional item)` without replacing the original event/quest provenance.
- Store checks are target-scoped, asynchronous, positive/negative cached, and must never create per-frame or persistent polling.
- Provider/network failures must not be cached as confirmed store misses.

## Route-level availability policy (v1.3.5.5)

- Availability belongs to acquisition routes, not to the item globally.
- Prefer currently actionable in-game acquisition routes over historical event provenance.
- Preserve historical event provenance beneath the current route when both are known.
- If no current gameplay route exists for an ended historical event, explicitly report gameplay as unavailable and separately report Online Store availability.
- Market Board/player trade is a valid current route only when the target item is tradable.
- Recompense Officer/event-reclaim routes are conditional on character eligibility and must not be presented as universally available.
- Obsolete event currencies and resolver dependency/provenance rows must not be promoted as permanent current routes.
- Do not infer that differently named `Authentic` or equivalent Online Store reissues are the exact target item without an explicit relationship.
## Historical provenance retention (v1.3.5.6)

- Current acquisition ranking and historical provenance are independent dimensions. A stronger current route must not erase a known historical event origin.
- Historical event provenance is presentation context only; it must never outrank an actionable current gameplay route.
- When provider/client data no longer carries a legacy event relationship, CQG may use a small curated historical-provenance catalog for verified legacy cases.
- Route-level availability must merge current routes with historical provenance before rendering `Gameplay` and `Historical source` blocks.


### v1.3.5 Online Store acquisition-family rule
- Treat the FINAL FANTASY XIV Online Store as a first-class acquisition family, not only as an ended-event fallback.
- For a selected target with no current in-game/gameplay acquisition route, CQG may perform the target-scoped cached store lookup even when the item has no historical-event provenance.
- When a paid outfit/bundle contains the selected item, the bundle is a valid store acquisition route for that contained item; preserve the bundle name when available.
- Keep gameplay availability separate from paid-store availability: a store-only item should state that no in-game acquisition route exists and that the Online Store route is available.
- Do not infer differently named equivalent/reissued items as exact store availability unless an explicit equivalence relationship is implemented.


### Selected-target asynchronous enrichment
- When a selected target needs FFXIV Collect event/store/collectible source data, CQG must request the relevant collection family on demand and update the visible target automatically when the request completes.
- A user must not have to press the broad `Refresh acquisition sources` button merely to make an already-selected target consume the async result.
- Keep this target-scoped and cached; do not restore continuous/global background polling.


## Duty identity presentation (v1.3.7.1+)
- Every player-facing duty reference should identify the duty type and required character level when the Content Finder row provides one.
- Canonical form: `Dungeon: Mt. Gulg - Lv. 79`; equivalent types are `Trial`, `Raid`, and `Alliance Raid`.
- The displayed `Lv.` value is character/class-job entry level only. Never substitute minimum item level into that field.
- Reuse the duty domain object's canonical display label across search, selected duty, pinned targets, dependencies, monster duty context, and action guidance rather than rebuilding strings ad hoc in UI code.

## Object-oriented architecture (standing requirement)
- Future CQG work must use proper object-oriented design rather than accumulating patchwork conditionals or oversized partial classes.
- Prefer cohesive domain objects, encapsulated responsibilities, composition/strategy interfaces where appropriate, dependency injection/testable seams, and small focused classes.
- Avoid god classes, excessive partial-class sprawl, duplicated presentation policy, and file-count reduction achieved by merging unrelated concerns.
- Refactors must preserve validated runtime behavior while improving maintainability, ownership boundaries, and extensibility.


## Treasure-dungeon duty identity (v1.3.7.1+)

- Treasure-map instances must be classified and displayed as `Treasure Dungeon`, never the generic `Duty` label.
- Canonical player-facing formatting is `Treasure Dungeon: <Name> - Lv. <required level>`.
- This classification must flow through the shared `DutyTarget.DisplayLabel` so Duties, item dependencies, pins, Run guidance, and other duty surfaces stay consistent.


## v1.4.0.0 development — pin capacity (task 1)
- Maximum of 500 pinned targets. The existing saved list must never be truncated during migration.
- Show `current / 500` at the top of Pinned Targets and in the main tab label.
- Disable new Pin actions once the cap is reached, with an explanatory tooltip; existing pins remain removable.
- The limit counts each actual pin entry, including variants, not target quantity goals.
- Enforcement is centralized through the existing DrawPinToggle insertion path; future programmatic insertion paths must use the same policy.
- This change must not add per-frame ownership scans, additional network requests, or background polling.
