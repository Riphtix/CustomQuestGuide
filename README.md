# Custom Quest Guide 1.2.2

Custom Quest Guide (CQG) is a target-oriented FFXIV plugin for answering a practical question: **what do I still need, and what should I do next?**

## Quick commands

- `/cqg` — open CQG and start a bounded character-state refresh.
- `/cqg <target name or item ID>` — open CQG, start the same refresh, and directly open exact item, permanent-unlock, set, job, or duty matches when possible.

## Character-state refresh model

CQG does **not** continuously poll ownership, permanent-unlock state, or duty unlock state for the whole game session. In v1.2, each use of `/cqg` starts **one complete background ownership/unlock/duty pass** with no wall-clock cutoff. The pass advances on framework updates using a small per-frame processing budget, then stops completely after the queue is exhausted. CQG does not continue ownership polling while you play. Selected targets and their related duties are promoted to the front of any still-running scan.

The permanent-ownership queue is ordered as: selected/visible targets, pinned targets, known collection/registration types, then generic ItemAction capability discovery. Irreversible permanent unlocks already confirmed owned are reused rather than re-queried.

During that window CQG samples physical inventory/Armoury/saddlebags/available retainers, Glamour Dresser/Armoire state, job unlocks, permanent collection unlocks, and duty unlock state. The UI continues using the most recently captured state after the window ends. Run `/cqg` again when you want to refresh character state.

Glamour Dresser ownership remains persisted per character using the character Content ID. Once the dresser has been opened and authoritative contents are available, those items remain known across zoning, plugin reloads, and game restarts until the next authoritative dresser snapshot replaces them.

## State colors

- **Green** — owned, complete, or unlocked. For jobs, green means the job is unlocked and all tracked formal progression is complete. Unlocked duties also use green.
- **Blue** — the job is unlocked, but CQG still detects tracked formal progression remaining.
- **Soft pink** — owned through the saved Glamour Dresser snapshot. Pink still counts as fully owned for sets, pins, and completion.
- **Red** — CQG has not resolved a trusted acquisition source for that unowned target.
- **Default / white** — not yet owned or complete with no additional highlighted state.

## v1.2 features

- **First-class Emote targets**: search the actual emote name or slash command. CQG maps item-taught emotes back through their teaching item and direct quest-reward emotes back through their quest where local game data exposes the relationship.
- **First-class Title targets**: search and pin titles, detect unlocked titles from the game's title list, and show the backing achievement/description where available.
- Dedicated item-backed categories for **Hairstyles, Fashion Accessories, Facewear, Chocobo Barding, Framer's Kits, and Riding Maps**.
- **Riding Map sets by expansion** that include all maps from that expansion, plus focused Shadowbringers/Dawntrail field-operation subsets for Bozja/Zadnor and Occult Crescent.
- **Shared FATE prerequisite progress** for Endwalker and Dawntrail normal-zone riding maps when the client exposes the persistent zone counter. Shadowbringers maps intentionally do not receive a false Rank 2 requirement.
- Reusable **progress bars** for measurable completion/requirements such as set ownership and Shared FATE counters. Exact `current / required` values remain visible.
- The existing one-shot `/cqg` refresh model is preserved: the new permanent-unlock snapshots do not introduce continuous idle polling.
- Blue Mage spells are intentionally **not** advertised as supported in 1.2; dedicated limited-job support is reserved for a future version that can be tested properly.

## Major existing features

- Ownership detection across supported physical inventory and permanent collection systems.
- Persistent per-character Glamour Dresser ownership snapshots.
- Target sets with smart actionable-frontier selection and completion-reward handling.
- Generalized quest, NPC, shop, and duty-unlock next-action guidance.
- Jobs & Progression with locked / partial / complete states.
- ARR base-class completion inference from unlocked derived jobs.
- Unified pinned targets for items, sets, duties, job unlocks, and job progression.
- External acquisition enrichment with cached/degraded operation when community services are unavailable. CQG does not refresh community HTTP sources at plugin startup or during the /cqg ownership scan.
- Detailed/debug views and CSV exports for resolver, specificity, quest, relic, and job-progression diagnostics.
- In-plugin Help tab covering commands, colors, ownership behavior, duties, map links, and troubleshooting.

## External acquisition sources

CQG primarily uses local game/client data. When that data is incomplete, it can supplement acquisition guidance using LuminaSupplemental, FFXIV Collect, Garland Tools, and the cached community duty index. The /cqg ownership scan itself performs no community HTTP refresh. FFXIV Collect is refreshed only through the explicit Refresh acquisition sources button (5-minute cooldown). Garland item documents are requested only for an actively selected item that still needs enrichment, one request at a time with a 2-second pace and failure backoff. Duty fallbacks are likewise on-demand and cached.

## Configuration resilience

Configuration schema version: **12**. v1.2 adds saved Emote/Title selection and the `Emote`/`Title` pin kinds while preserving existing pins and per-character Glamour Dresser snapshots.

## Version

Assembly/file version: **1.2.2.0**  
Release: **1.2.2**

See `CHANGELOG.md` and `CREDITS.md` for release history and data/tool attribution.


## Duty unlock fallback

CQG first resolves duty unlocks from local game-sheet relationships and curated special cases. When a duty is still unresolved, CQG first uses the cached community duty index; only after that can it make a target-scoped Garland Tools instance lookup for the script-derived `unlockedByQuest` relationship. Results and confirmed misses are cached. Garland duty requests are serialized/paced and transient failures back off before retrying.

## Interior-only navigation
When quest guidance points into an interior map that cannot accept a useful world-map flag, CQG redirects the click to a known exterior entrance. v1.1.9 seeds this table with major story hubs and inn/private-room interiors from ARR through Dawntrail so users should not need to discover these one at a time.

### Fresh ownership profiling
In **Detailed > Debug**, **Clear cached items** resets CQG's current-character ownership cache, including the saved Glamour Dresser snapshot. Run `/cqg` afterward to start a cold full ownership pass. Acquisition-source and duty resolver caches are intentionally left intact. **Clear network caches** is a separate diagnostic control that deletes persisted Community Duty/Garland caches, clears FFXIV Collect session data, and resets network telemetry; normal users should leave those caches intact because they reduce outbound HTTP traffic.


## One-shot ownership scan

`/cqg` starts one complete background ownership/permanent-unlock/duty scan with no wall-clock cutoff. Each queued permanent item, first-class unlock, and duty is checked at most once, then CQG stops all ownership checking until `/cqg` is run again. Inventory/Armoury/saddlebags/available retainers, Glamour Dresser cache, Armoire, and job state are snapshotted once at the beginning of the scan. If a target or set is selected while the scan is still running, its remaining item and related duty checks are promoted to the front of the queue without repeating anything already checked.


### Acquisition guidance quality

CQG ranks actionable, item-specific acquisition routes above broad associations. Special/currency-shop targets can expose the source of required exchange tokens, and ambiguous supplemental duty associations are not presented as authoritative until an item-specific source confirms them. Target-scoped community lookups remain cached and network-limited.


## Debug exports

- **Output target information** writes a read-only TXT snapshot of everything CQG currently knows about the selected item/set. It never starts network requests; already-cached community data is included.
- **Export ambiguity audit** scans ambiguous/incomplete item resolutions and writes a comprehensive CSV. It makes **zero network calls**; uncached enrichment opportunities are marked **missing network data**.


### Debug performance
Catalog-wide resolver coverage is never recomputed from the live render loop. The full Ambiguity Audit is explicit/on-demand only; opening Debug or running `/cqg` must not trigger a catalog-wide acquisition resolver pass.


### 1.1.13 resolver quality pass
The 1.1.13 resolver quality pass prefers complete actionable acquisition paths over first-found source rows, preserves multiple legitimate routes, and avoids presenting uncertain SpecialShop currency sentinels or ambiguous same-name NPC placements as authoritative.
