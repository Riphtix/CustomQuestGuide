# 1.1.13 release validation notes

## Release metadata

- Project package version: `1.1.13`.
- Assembly/file version: `1.1.13.0`.
- Plugin manifest `AssemblyVersion`: `1.1.13.0`.
- Configuration schema remains `11`; this release does not require a configuration migration.
- Existing pins and per-character Glamour Dresser snapshots must remain compatible.

## 1.1.13 final smoke test

- [ ] Clean Release build completes on the normal Dalamud development machine with no compile errors.
- [ ] Plugin loads with an existing configuration and `/cqg` starts the one-shot ownership/duty scan.
- [ ] Previously cached Glamour Dresser ownership remains present after reload; opening the dresser can replace the saved snapshot authoritatively.
- [ ] A normal item target can still be selected, opened, and pinned.
- [ ] An Allagan Tomestone prerequisite shows repeatable duty/system acquisition guidance instead of an **Open** button.
- [ ] The same tomestone prerequisite guidance remains visible with Detailed mode disabled.
- [ ] Repeatable tomestone-system acquisition outranks incidental one-time quest rewards.
- [ ] Prerequisite **Open** and **Pin Item** buttons have matching dimensions.
- [ ] An Arcadion chest-piece path resolves to the intended NPC/source and can be pinned.
- [ ] The Holoarmor prerequisite **Open** action works.
- [ ] Silver Chocobo Feather is a selectable/targetable item and shows Preferred/New World acquisition guidance.
- [ ] Hhihwi resolves to the intended Solution Nine placement/map pin.
- [ ] Opening Detailed > Debug does not trigger an automatic full-catalog resolver pass or major hitch.
- [ ] **Output target information** and **Export ambiguity audit** produce no new HTTP requests.
- [ ] A locked and an unlocked duty still display the correct state and pin/unlock guidance.
- [ ] At least one ordinary outdoor NPC/location still opens the correct map and flag.

## Requested v1.1 features

### Unified Target control
- Replaced the separate search and target selection flow with one `Target` combo.
- The Target selector can search items, jobs, and duties, while sets are selected from the separate Set dropdown.
- Empty search acts as a browser for the current tab/category.
- Existing ownership visual states remain visible inside the selector.

### First-class duties
- Added a Duties tab backed by `ContentFinderCondition` data.
- Duties can be selected directly, searched globally, opened from pins, and pinned/unpinned with the existing priority queue.
- Unlocked duties use the established green completion/unlock color.
- Locked duties show the best resolved unlock quest/contact/location currently available.
- Duty unlock state is sampled incrementally during the same command-triggered full polling pass; polling stops when the complete queue has been checked and is not continuous while the UI is open.

### Duty dependencies on acquisition targets
- Item/source guidance detects named trial/raid/alliance-raid/dungeon sources as duty dependencies.
- If the dependency is unlocked, the duty name/source is rendered green.
- If the dependency is locked, CQG shows the resolved unlock quest and contact/location when available.
- A dependent duty can be pinned directly from the item/set guidance using the same `Duty` pin target type.

### Clickable map locations
- NPC placement data now retains TerritoryType, Map, and world position where available.
- Supported displayed locations are clickable.
- Clicking opens the in-game map and places a flag through `IGameGui.OpenMapWithMapLink`.
- Explicit `Zone (X:n.n, Y:n.n)` strings are also converted into map links where Dalamud can resolve the place name.
- If CQG lacks enough authoritative location data, text remains non-clickable instead of inventing a pin.

## Compatibility / persistence
- Config schema is 11.
- Existing v1.0 pins and per-character Glamour Dresser snapshots are preserved.
- Existing color semantics remain unchanged: green complete/unlocked, blue partial job progression, soft pink Glamour Dresser ownership, red unresolved acquisition.
- The current one-shot scan has no wall-clock cutoff, advances only after `/cqg`, is frame-budgeted, and stops completely when its one-shot queue finishes.

## Source-level validation performed here
- Release metadata is set to `1.1.13` / `1.1.13.0` in the project and plugin manifest.
- `System.Action` remains explicitly qualified in `OwnershipIndex` to avoid the prior Lumina ambiguity.
- New `Duty` pin kind is accepted by configuration migration.
- No GitHub repository operations are performed by this build workspace.

## Build caveat
The current packaging environment does not contain the .NET/Dalamud SDK toolchain, so a real Release compile must still be performed on the normal development machine before publishing v1.1.13.
## v1.1.3 regression checks
- Verify Set selector is above Target selector on Items.
- Verify a normal locked dungeon/trial with `ContentFinderCondition.UnlockType == 1` shows its direct quest unlock route.
- Re-test Aloalo Island, Aglaia, Advanced Mahjong, and A Relic Reborn: The Hydra.
- Verify Levinsilk -> `Harvesting: Everkeep Ward` renders a clickable location and opens/flags the map.



## 1.1.4 regression targets
- Levinsilk (44028): primary `Harvesting: Everkeep Ward` should render Everkeep Ward as a clickable map button and place a flag in Heritage Found.
- Locked ordinary dungeon/trial/raid: if ContentFinderCondition UnlockType is quest-based, CQG should resolve UnlockCriteria/UnlockCriteria2 even when those fields are scalar quest IDs.
- Re-check Aloalo Island, Aglaia, Advanced Mahjong (Full Ranked Match), and A Relic Reborn: The Hydra curated routes.


## v1.1.9 interior-location regression
- Select guidance whose NPC is inside The Rising Stones.
- The Rising Stones location should remain clickable even though the interior itself is not a useful world-map flag destination.
- Clicking it should open Mor Dhona and flag the Rising Stones entrance around X:21.9, Y:8.8.
- Normal outdoor NPC/location pins should retain their existing behavior.

- Verify representative interior fallbacks: The Waking Sands, The Rising Stones, Fortemps Manor, The Ocular, The Pendants, Andron, The Omphalos, Meghaduta Guest Chambers, The For'ard Cabins, and The Backroom.

- [ ] Debug > Clear cached items clears runtime ownership state and the current character saved Glamour Dresser snapshot, then `/cqg` completes a fresh full pass and writes the profile TXT.

- Verify a `/cqg` scan performs one transient snapshot only, checks each queued permanent item/duty once, and goes fully idle when complete.
- During a running scan, select a new item/set/duty and confirm its remaining related checks are promoted without restarting or duplicating the scan.

## Network safety
- [ ] Fresh plugin startup performs no FFXIV Collect or Garland item bulk HTTP refresh.
- [ ] Running `/cqg` performs the one-shot local ownership/duty scan without increasing community HTTP request counters.
- [ ] Selecting a single item that requires Garland enrichment causes at most one target-scoped Garland item request.
- [ ] Rapidly selecting multiple unresolved items does not create concurrent Garland requests; suppressed attempts are visible in the profiler.
- [ ] Re-selecting a cached Garland hit/miss does not issue another HTTP request while the cache is fresh.
- [ ] HTTP 429 produces a 30-minute backoff; other transient Garland failures produce a 5-minute backoff.
- [ ] Refresh acquisition sources is manual, has a 5-minute cooldown, refreshes FFXIV Collect only, and does not bulk-query Garland.
- [ ] Ownership profile TXT clearly separates local ownership/duty checks from plugin-session network activity.

- [ ] Debug > Clear network caches deletes Community Duty/Garland disk caches, clears FFXIV Collect session data, resets network telemetry, and refuses while related network work is active.

## v1.1.12.4 validation focus
- Ownership scan no longer writes profiler/TXT files or retains scan timing/split history.
- Functional cache files remain unchanged.
- Generic NPC/vendor guidance routes location text through the shared clickable map renderer (regression: Abyss Burgeonet -> Gerolt -> North Shroud).
- Manual Debug CSV exports remain opt-in only and are never written automatically.


## v1.1.12.7 diagnostics validation

- [ ] Pressing **Output target information** causes zero new HTTP requests and includes already-cached Garland/collectible/duty data.
- [ ] Pressing **Export ambiguity audit** causes zero new HTTP requests even when network caches are empty.
- [ ] Ambiguity audit rows marked **missing network data** remain unresolved rather than starting enrichment.
- [ ] Cached community data is still represented in both outputs when available.
- [ ] Output target information includes all resolved sources, exchange dependencies, supplemental duty/source rows, duty IDs/states/unlock guidance, ownership, action guidance, and cache state.

## v1.1.12.7 ambiguity-audit remediation

- Verify a generic supplemental `Duty Chest` source is classified as incomplete, remains eligible for target-scoped Garland enrichment, and does not claim exact item specificity.
- Verify YoRHa Type-51 Jacket of Fending resolves to an exact duty after its target-scoped cached/network enrichment completes; conflicting supplemental duty candidates must remain suppressed from the primary answer.
- Verify a named SpecialShop/GilShop can gain an NPC from the retail ENpc fallback only when LuminaSupplemental has no shop-NPC link.
- Verify exchange currencies that are valid Item rows but absent from the user target catalog no longer produce a false `missing from catalog` ambiguity.
- Verify short nested exchange chains surface an actionable dependency source without looping beyond the depth cap.
- Verify selecting an unresolved exchange target queues only bounded related Garland lookups and that the queue remains serialized/paced/cached.
- Verify market-only items are not reported as resolver contradictions merely because Market Board is displayed as a low-priority fallback.
- Verify ambiguity and target-information exports make no network requests and include suppressed supplemental duty candidates plus cached-only duty guidance.


## 1.1.13 resolver cleanup regression targets
- Augmented Zwill Crossblades: no false `600 Gil` SpecialShop cost; Poetics wording when safely inferable.
- Abyss Burgeonet: Gerolt guidance uses the shop-linked ENpc placement rather than arbitrary same-name Gerolt placement.
- Light-heavy Coat of Striking: Light-heavy Holoarmor dependency retains M1-M4 as valid alternatives.
- Bluebird Earring / Honorbound: no false exact-specificity ambiguity solely because the route is external/curated.
- Clouddark Demimateria I, AAC Illustrated: CW Edition IV, Gelmorran Potsherd, Rathalos Scale(+), Rokkon Potsherd: named activity fallback available without requiring network enrichment.
