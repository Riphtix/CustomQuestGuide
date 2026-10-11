# Changelog

## 1.1.13

- Show repeatable Allagan Tomestone acquisition guidance directly in prerequisite summaries, including when Detailed mode is disabled.
- Prefer repeatable tomestone-system acquisition over incidental quest or item-conversion rewards.
- Match prerequisite **Open** and **Pin Item** button dimensions and preserve prerequisite Open/Pin behavior.
- Preserve real Item rows used as live SpecialShop exchange costs as selectable CQG targets, fixing Silver Chocobo Feather filtering.
- Add system-level Silver Chocobo Feather guidance for the Preferred/New World character-creation bonus.
- Prevent SpecialShop row-id 1 currency sentinels from being mislabeled as Gil; infer a named currency only when safe and otherwise omit an uncertain price.
- Treat Gil as a terminal universal currency instead of recursively resolving or enriching "where to get Gil" dependencies.
- Preserve exact shop ENpc IDs through source guidance so same-named NPCs resolve to the correct placement and map pin.
- Preserve every equally actionable duty source for exchange tokens instead of collapsing multi-duty acquisition to the first duty.
- Treat explicit named broad-system, external/contest, and curated acquisition routes as complete exact guidance when they are already authoritative.
- Add exact local fallback acquisition routes for high-frequency unresolved exchange tokens, including Clouddark Demimateria, AAC Cruiserweight books, Gelmorran/Rokkon Potsherds, and Rathalos Scales.
- Exclude intentionally unreleased/future client-data items from ambiguity-audit failures when "No published acquisition source yet" is already the truthful final answer.
- Prefer usable supplemental localized shop names when a live SpecialShop label is non-English and a localized supplemental label is available.
- Preserve target-scoped network behavior: ambiguity and target-information exports remain read-only and make zero network calls; uncached enrichment opportunities are reported as missing network data.
- Preserve the v1.1.12.7 performance fix: no automatic full-catalog resolver work is reintroduced into the live UI.
- Preserve the verified Hhihwi/Solution Nine vendor-location behavior and the existing Glamour Dresser snapshot/cache ownership behavior.

## 1.1.12.9
- Fixed the YoRHa-style duty-resolution failure by distinguishing explicit item-to-ContentFinderCondition rows from weaker duty associations inferred through shared boss/chest/territory joins.
- Explicit supplemental item->duty relationships now outrank and suppress conflicting inferred duty associations. For the YoRHa Type-51 Jacket of Fending data supplied in the diagnostic export, CFC 700 (the Copied Factory) is the direct relationship; the inferred Burn/Ridorana candidates no longer compete with it.
- Fixed `GetNonGarlandProviderSources` dropping supplemental-duty provenance, which caused conflicting inferred duties to be misclassified as exact and incorrectly disabled Garland enrichment.
- Generic duty placeholders such as `Duty Chest` and `Duty / Instance Drop` are now removed once a concrete authoritative duty is available, even when the concrete source uses a different source Kind (`Activity` vs `Duty`).
- Specificity/enrichment classification now treats multiple inferred duty associations as incomplete unless an explicit item-specific duty relationship exists.
- Ambiguity-audit diagnostics still retain suppressed candidate duties for analysis, but missing network data for losing candidates no longer causes an otherwise-resolved target to be flagged. Already-unlocked current duties also no longer report missing unlock-quest network data.

## 1.1.12.8
- Fixed a compile error introduced by the 1.1.12.7 hitch hotfix: the Debug UI no longer references the removed live `unresolvedCount` value. The unresolved export remains explicit/on-demand and therefore does not require an automatic full-catalog count.

## 1.1.12.7

- Fixed a severe render-thread hitch introduced by 1.1.12.6 when Debug was open.
- Removed automatic full-catalog resolver coverage scans from the live Debug UI.
- Made resolution-tier provenance classification non-recursive so catalog diagnostics do not follow acquisition dependency chains just to classify provider provenance.
- The one-shot `/cqg` ownership scan remains bounded per framework update and makes no network calls.
- Full-catalog ambiguity analysis remains explicit/on-demand only.

- Treat unresolved duty-family placeholders (for example `Duty Chest`) as incomplete rather than exact item-specific answers.
- Automatically keep Garland target enrichment eligible until an exact actionable duty/shop/source is known.
- Added a local retail ENpc shop-contact fallback for SpecialShop/GilShop rows missing supplemental NPC links.
- Improved exchange dependency resolution to use every already-known provider, follow short nested token chains, and avoid false `missing from catalog` diagnostics for non-target currency items.
- User-selected exchange targets can queue a small bounded set of unresolved cost-item lookups through the existing serialized/cached Garland request queue.
- Garland target requests are now queued and processed one at a time with the existing two-second pacing instead of dropping related requests while another lookup is in flight.
- Garland trade shops now emit one source per NPC so map/location resolution receives a real NPC name rather than a comma-joined pseudo-name.
- Resolution-tier diagnostics now describe the provider that supplied the winning answer rather than whichever provider happened to have any row.
- Fixed ambiguity-audit false positives for market-only items and non-user-trackable exchange currencies.
- Ambiguity diagnostics now retain suppressed supplemental duty candidates, distinguish cached-missing-network unlock data, and do not treat missing network data as a local unlock-quest contradiction.
- `Output target information` now includes suppressed/raw duty candidates and dependency-source details even when a cost item is not exposed as a normal CQG target.

- Added **Output target information** under Detailed > Debug. It dumps all currently available resolver, ownership, source, cached-community, exchange-chain, supplemental, duty, quest/contact, and action data for the selected target without starting any network request.
- Added **Export ambiguity audit**. The audit writes comprehensive diagnostics for every ambiguous/incomplete item resolution while making **ZERO network calls**. Cached community data is used when present; uncached data that would ordinarily be eligible for enrichment is marked **missing network data**.
- Added cached-only duty/action resolver paths so diagnostic exports cannot accidentally trigger Community Duty or Garland HTTP fallbacks.
- Added exchange-dependency and cache-state detail to diagnostics to support resolver fixes without requiring repeated in-game screenshots.

## 1.1.12.4

- Adds structured SpecialShop exchange dependencies so token/currency items can lead to their own best acquisition source and related duty.
- Treats named shop entries without an NPC/location as incomplete and allows target-scoped Garland enrichment to fill the missing contact.
- Prefers actionable named Garland trade-shop NPCs over anonymous SpecialShop labels.
- Suppresses conflicting supplemental duty associations instead of presenting an arbitrary duty as the answer; item-specific Garland duty relations replace them when available.
- Source ordering now respects route priority before unlocked-state convenience.
- Keeps the one-shot local ownership scan and network-safe target-scoped cache model.

## 1.1.12.3
- Removed ownership scan profiler/TXT output and scan timing history.
- Kept only functional cache files; ownership scans no longer write diagnostic files.
- Fixed generic NPC/vendor guidance so resolved locations use the shared clickable map-location renderer.

## 1.1.12.2
- Added **Debug > Clear network caches** for fresh-network diagnostics. It clears persisted Community Duty/Garland caches, FFXIV Collect session data, and network telemetry without touching ownership caches.
- Network-cache clearing refuses to run while a related request/refresh is active, avoiding races with in-flight writes.

- Added explicit network-cache diagnostics to ownership profile reports so fresh-install and warm-cache behavior can be distinguished.
- Reports persisted community-duty, Garland item, and Garland duty cache presence/counts/age/freshness plus FFXIV Collect session state.
- No change to the one-shot local ownership scan or network-safety behavior from 1.1.12.

## 1.1.12

### Network-safety pass
- Removed automatic FFXIV Collect and Garland source refreshes from plugin startup.
- The `/cqg` ownership/duty scan is local-only and never starts community HTTP refreshes.
- FFXIV Collect refresh is now explicit/manual only with a 5-minute UI cooldown.
- Garland item enrichment is target-scoped: selecting one unresolved item can request at most that one item document.
- Garland item and duty requests are serialized/paced to at most one new request every 2 seconds, cache positive results/misses, and back off transient failures (30 minutes for HTTP 429, 5 minutes for other transient failures).
- Bulk Garland refresh was removed from the user-facing refresh button.
- Ownership profiler now reports outbound community-data HTTP request totals, successes/failures, cache hits/misses, and suppressed/backoff attempts separately from local ownership checks.

- Reworked the one-shot `/cqg` ownership scan for much higher throughput without reintroducing command-frame hitches.
- Removed the old 250 ms artificial polling interval; the scan now advances on framework updates under a strict per-frame processing budget.
- Raised the per-slice candidate ceiling so the elapsed-time budget, rather than a small fixed count, controls frame cost.
- Increased duty-state batch size; duty scans remain one-shot and stop when complete.
- Added session-local ItemAction capability learning: items the game reports as having no permanent unlock status are skipped on later `/cqg` scans in the same plugin session.
- Prioritizes known collection/registration ItemAction types ahead of generic unlock-capability discovery.
- Reuses previously confirmed irreversible permanent unlocks instead of re-querying them every command. Physical inventory/storage is still freshly snapshotted once per command, so deleted/moved ordinary items can disappear correctly.
- Permanent ownership snapshots are now character-safe when switching characters without reloading the plugin.
- Expanded profiler output with skipped-known-nonunlockable, newly learned nonunlockable, and previously-owned permanent counts.
- `Clear cached items` also clears the session ItemAction capability cache so the next scan can be used as a true cold-profile run.

## 1.1.11

- Replaced repeated transient sampling with a single transient snapshot at the start of each `/cqg` scan.
- Removed the wall-clock cutoff: the command starts one complete pass and every queued permanent item/duty is checked at most once.
- CQG performs no ownership checks after the queue completes until `/cqg` is run again.
- Selecting an item, set, or duty during an active scan promotes its remaining item/duty checks to the front of the queue without repeating completed checks.
- Profiler output now records dynamic queue-promotion events.

- Profiling build: removed the 30-second cutoff; `/cqg` now runs one complete incremental ownership/duty-state pass and stops when the queue is exhausted.
- Permanent ownership candidates are split into selected/visible, pinned, previously unowned/unknown, and previously owned revalidation tiers.
- Writes an `ownership-poll-profile-*.txt` timing report after each completed pass so full-scan duration and split timings can be measured.
- Fix mount/minion/other permanent collection ownership appearing white in sets when the bounded ownership scan had not reached those items yet.
- The 30-second `/cqg` ownership polling window now checks the currently selected set/item and pinned item/set targets before scanning the rest of the catalog.
- Preserves split set-row coloring: owned item names are green/pink; unlocked duty text is green independently.

- Green now applies only to the unlocked duty/action text until the actual set member is owned.
- Owned members still use the existing green (or Glamour Dresser pink) ownership state.

## 1.1.9
- Added navigation fallback for interior-only destinations: clicking a location that cannot accept a useful world-map flag can route to its overworld entrance instead.
- Added The Rising Stones as the first explicit interior entrance route, flagging its entrance in Mor Dhona rather than the non-world-map interior coordinates.
- Normalized displayed location labels before representative PlaceName lookup so labels containing `(X:, Y:)` can still resolve to a pinnable landmark.

### Interior-navigation coverage expansion
- Expanded the interior-only map fallback table across ARR through Dawntrail instead of handling only The Rising Stones.
- Includes major Scion headquarters, expansion quest hubs, inn-room interiors, Fortemps Manor, The Ocular, The Pendants, Kienkan, The Omphalos, Endwalker private rooms, and Dawntrail private hubs.
- Interior destinations continue to display their real interior name while clicking them places the flag at the useful exterior entrance.
- Debug includes **Clear cached items** to reset the current-character ownership cache, including the saved Glamour Dresser snapshot, before a cold full-pass ownership profile.

## 1.1.8
- Fixed item-source duty promotion so exact live `Activity` sources (for example Containment Bay P1T6 (Extreme)) resolve to a real ContentFinderCondition and expose **Pin Duty**.
- Reorganized the Items UI into a clearer flow: Set Target, Browse Filters (Category / Class-Job), then Target.

## 1.1.7
- Fixed duty pinning from item targets when acquisition-source duty names do not exactly match the first-class duty catalog.
- Added a ContentFinderCondition-ID fallback so recognized locked duties can still be pinned and use the normal unlock-quest guidance path.
- Normalized provider-decorated duty source names before lookup.

## 1.1.6
- Added a one-shot, asynchronously cached community duty-unlock fallback based on the MIT-licensed FFXIV Todo generated content index.
- The community fallback is consulted after local sheet/curated resolution and before the per-duty Garland fallback.
- The full community index is requested at most once when needed, reduced to a local duty->quest cache, and reused for seven days to limit network traffic.
- Added Akadaemia Anyder as a regression target for script-only duty unlock resolution.

## 1.1.0

### Unified target selection
- Replaced the separate item search + target dropdown with one Target control that combines searching and selection.
- Unified search can route to items, sets, jobs, or duties.

### Duties
- Added a first-class Duties tab sourced from Content Finder data.
- Added duty pins to the shared pinned-target queue.
- Unlocked duties use the existing green visual language.
- Locked duties show the best known unlock quest/contact route and can be pinned directly from dependent targets.
- Duty unlock state is refreshed incrementally during the bounded `/cqg` polling window instead of being queried continuously by the UI.

### Map guidance
- Added clickable location controls.
- Clicking a supported location opens the in-game map and places a flag using NPC Level placement data or explicit map coordinates.
- Applied clickable location guidance to duty, item/source, set, job, curated-guide, and pinned-target routes where sufficient data exists.

### Configuration
- Bumped config schema to 11 for duty selection and duty pins.
- Preserved v1.0 Glamour Dresser snapshots and existing pin migrations.

## 1.0.0

### Ownership and state
- Finalized command-triggered, 30-second background ownership polling.
- Added per-framework work budgeting for permanent-unlock checks to reduce hitch risk.
- Preserved persistent per-character Glamour Dresser snapshots and soft-pink ownership.
- Preserved green complete and blue partial job states.
- Preserved ARR base-class completion inference from unlocked derived jobs.

### Guidance and resolver
- Preserved generalized NPC, quest, vendor, and duty-unlock guidance.
- Preserved smart set frontier and completion-reward behavior.
- Expanded resolver audit exports with ownership, next-action provenance, and contradiction checks.

### Stability and performance
- Hardened configuration migration to schema 10.
- Added safe fallback behavior for configuration-load failures and guarded config saves.
- Rate-limited manual external source refreshes.
- Reduced Garland concurrent requests and improved cancellation behavior.
- Exposed ownership polling timing in Debug for performance diagnosis.

### UX and release polish
- Finalized Help content and 1.0 version labeling.
- Kept `/cqg` and `/cqg <item name or item ID>` at the top of Help.
- Kept the ownership-polling warning prominently displayed in red in Help.
- Added 1.0 README, changelog, credits, and validation notes.

## Pre-1.0 series
- 0.99.3: command-triggered background ownership polling.
- 0.53: Help tab.
- 0.52: stabilization/refactor and external-source hardening.
- 0.51.1: persistent Glamour Dresser cache fix.
- 0.51: soft-pink Glamour Dresser ownership state.
- 0.50: smart set frontier and completion rewards.
- 0.49.x: generalized guidance, blue partial jobs, and ARR base-class completion fix.

