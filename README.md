# Custom Quest Guide 1.0.0

Custom Quest Guide (CQG) is a target-oriented FFXIV plugin for answering a practical question: **what do I still need, and what should I do next?**

## Quick commands

- `/cqg` — open CQG and start a bounded ownership refresh.
- `/cqg <item name or item ID>` — open CQG, start the same refresh, and search/select that item directly when possible.

## Ownership refresh model

CQG does **not** continuously poll ownership for the whole game session. Each use of `/cqg` starts a **30-second background ownership polling window**, then polling stops. Work is split across later framework updates so the slash-command frame does not perform one large synchronous ownership scan.

During that window CQG samples physical inventory/Armoury/saddlebags/available retainers, Glamour Dresser/Armoire state, job unlocks, and permanent collection unlocks. The UI continues using the most recently captured state after the window ends. Run `/cqg` again when you want to refresh ownership.

Glamour Dresser ownership is persisted per character using the character Content ID. Once the dresser has been opened and authoritative contents are available, those items remain known across zoning, plugin reloads, and game restarts until the next authoritative dresser snapshot replaces them.

## State colors

- **Green** — owned or complete. For jobs, green means the job is unlocked and all tracked formal progression is complete.
- **Blue** — the job is unlocked, but CQG still detects tracked formal progression remaining.
- **Soft pink** — owned through the saved Glamour Dresser snapshot. Pink still counts as fully owned for sets, pins, and completion.
- **Red** — CQG has not resolved a trusted acquisition source for that unowned target.
- **Default / white** — not yet owned or complete with no additional highlighted state.

## Major 1.0 features

- Global target search by name or item ID.
- Ownership detection across supported physical inventory and permanent collection systems.
- Persistent per-character Glamour Dresser ownership snapshots.
- Target sets with smart actionable-frontier selection.
- Completion-reward handling for collection/meta rewards.
- Generalized quest, NPC, shop, and duty-unlock next-action guidance.
- Jobs & Progression with locked / partial / complete states.
- ARR base-class completion inference from unlocked derived jobs.
- Unified pinned targets for items, sets, job unlocks, and job progression.
- External acquisition enrichment with cached/degraded operation when community services are unavailable.
- Detailed/debug views and CSV exports for resolver, specificity, quest, relic, and job-progression diagnostics.
- In-plugin Help tab covering commands, colors, ownership behavior, and troubleshooting.

## External acquisition sources

CQG primarily uses live game data. When that data is incomplete, it can supplement acquisition guidance using LuminaSupplemental, FFXIV Collect, and Garland Tools data. External refreshes are asynchronous, serialized, cached where applicable, and manually rate-limited in the UI.

If an external service fails, CQG keeps usable cached/session data where possible rather than wiping working guidance.

## Troubleshooting

If ownership looks stale, run `/cqg` again and allow the 30-second refresh window to complete. If a Glamour Dresser item has never appeared pink, open the dresser once on that character so the game exposes authoritative dresser contents.

For incorrect acquisition guidance, enable **Detailed**, then **Debug**, and use the resolver/quest/relic/job CSV exports. The resolver audit includes ownership state, source provenance, next-action provenance, and contradiction checks.

## Configuration resilience

Configuration schema version: **10**. CQG normalizes old/pre-release settings, preserves valid pins and per-character Glamour Dresser snapshots, and falls back to safe defaults if Dalamud cannot deserialize the configuration. If config loading fails, CQG disables config writes for that session so it does not immediately overwrite the unreadable file.

## Version

Assembly/file version: **1.0.0.0**  
Release: **1.0.0**

See `CHANGELOG.md` and `CREDITS.md` for release history and data/tool attribution.
