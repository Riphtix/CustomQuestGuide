# 1.0 release validation notes

This file tracks the P0-P2 stabilization work applied before the 1.0 version stamp.

## P0

### Compile/runtime safety
- Removed the prior `Action` ambiguity by explicitly using `System.Action` in `OwnershipIndex`.
- Performed source-level scans for stale pre-release version strings and ambiguous standalone `Action` type references.
- Added guarded configuration load/save paths so configuration failures do not crash plugin construction or later saves.
- The packaging environment used for this source bundle does not contain the .NET SDK/Dalamud build chain, so a real Dalamud compile still must be performed in the normal development environment.

### Ownership/state regression matrix
Expected state semantics were reviewed across:
- item: unowned / physical / Glamour Dresser / Armoire / registered collection
- jobs: locked / unlocked-partial / complete / ARR derived-job inference
- pins: item / set / job unlock / job progression
- sets: incomplete prerequisites / actionable member / completion reward available / complete
- duties: locked / unlock guidance / unlocked-run guidance

### Character-safe Glamour snapshots
- Glamour snapshots are keyed by character Content ID, not character name.
- Live dresser data replaces the saved snapshot only when the game reports that dresser data is authoritatively loaded.
- Unloaded dresser state preserves the last known snapshot instead of treating the dresser as empty.

### Resolver contradiction audit
- Resolver audit CSV now records ownership, next-action provenance, direct-source state, and a contradiction-check column.

### Config migration/corruption handling
- Schema bumped to 10.
- Null/invalid navigation values and pins are normalized.
- Glamour snapshot item lists are de-duplicated and sorted.
- If config loading throws, CQG uses safe defaults and disables config writes for that session.

## P1

### Network/API failure handling
- Community refreshes remain asynchronous and serialized.
- Existing session/cache data is retained on partial failures.
- Garland cancellation on plugin disposal is handled separately from ordinary failures.
- Manual refreshes have a UI cooldown and Garland concurrency is reduced.

### Main-thread/performance
- `/cqg` starts work after the command returns.
- Ownership work is split across framework updates.
- Permanent collection checks use both a count limit and per-slice time budget.
- Full-catalog debug coverage remains cached instead of recalculated every frame.
- Debug displays the last ownership polling slice duration.

### Search/empty states
- Global searches remain capped at 500 results.
- Exact item ID/name and prefix matches sort ahead of broad contains matches.
- Empty target/category/pin/job states display explicit messages rather than blank UI.

### Startup message lifecycle
- Startup help is plain chat text with no custom chat-color payload.
- The plugin-instance guard prevents repeated startup messages during normal character transitions.

### Help/README
- Quick commands are at the top.
- Red ownership-polling warning is immediately below the commands.
- Colors, dresser behavior, partial jobs, NPC/duty guidance, completion rewards, source refreshes, pins, and debug reporting are documented.

## P2

### Attribution/changelog/packaging
- Version strings moved to 1.0.0.0 / 1.0.0.
- Added README, CHANGELOG, CREDITS, and this validation document.
- Kept author/repository metadata conservative rather than inventing identity or repository URLs that were not supplied.
