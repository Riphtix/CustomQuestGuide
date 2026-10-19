# Verified class/job quest catalog (development)

The gameplay plugin never contacts Lodestone. Quest membership must be checked
outside the game against the official [Lodestone quest database](https://na.finalfantasyxiv.com/lodestone/playguide/db/quest/), including per-quest **Starting Class** restrictions and follow-up relations. Map each Lodestone quest to an exact **Lumina quest RowId**; the Lodestone URL hash is **not** a Lumina RowId.

`Data/Verified/job-progression.json` is deliberately empty until complete, reviewed chains are generated. The runtime treats any missing entry as **unknown** (never `0/0 complete`). It must NOT fall back to class restrictions, English title patterns, or partial prerequisite chains.

Schema v1 example (illustrative IDs only; do not paste into the runtime manifest):

```json
{"schemaVersion":1,"jobs":[{"classJobId":999,"verifiedComplete":true,"progressionQuestIds":[123456,123457]}]}
```

Only publish `verifiedComplete:true` when all required quests and applicable variants have been validated. The validator checks quest IDs exist and are unique, but **cannot attest that the chain is complete**; that requires external verification. Starting-class-specific variants require a richer manifest schema and eligibility selection before any affected chain is marked verified.

## Gates before release
1. Validate a source-backed catalog for every supported class/job, including branching paths and starting-class variants.
2. Run deterministic regression tests for Gladiator, Archer, Thaumaturge, Rogue/Ninja, Conjurer/White Mage, Arcanist/Summoner/Scholar, Paladin relic exclusion, Miner MSQ exclusion, and a future job.
3. Ensure unlocks stay authoritative (class level / job stone), and unverified progressions never complete or advance pinned stages.
4. Compile locally and compare UI against audits; only then remove temporary raw-quest diagnostics.

### Task #10 data-recovery checkpoint

`tools/catalog/stage_from_quest_audit.py` extracts uniquely identified *candidates*
from CQG's in-game QuestFlags CSV, retaining distinct quest IDs even if their
localized titles match. This is an offline staging tool, never a source of
verified membership. The staged THM CSV demonstrates an unresolved pair of
level-10 `The Threat of Paucity` records (65884 and 65885). These MUST be
reviewed for first-class eligibility and quest branch before activating THM.

The public community wiki provides a helpful ordered list, while the official
Lodestone provides independently checkable quest membership. Neither a title
match nor the current character's quest-completion flag alone establishes
which variant ID is applicable. Keep `verifiedComplete` false/unset until all
IDs and variants are reconciled, then generate the embedded catalog offline.

### Task #10 — Thaumaturge variant review checkpoint

`tools/catalog/thaumaturge-variant-review.csv` is a source-backed **candidate**
listing, not an enabled runtime chain. It distinguishes the class unlock from
formal progression, excludes Black Mage unlock, and describes the *My First
Scepter* non-starting-class condition. The duplicate Level 10 quest IDs
65884/65885 have **provisional path assignment** until independently matched
against their exact prerequisite quest IDs in current Lumina data. The CSV
must not be promoted to `verifiedComplete` on title matching alone.

Run `python tools/catalog/validate_quest_variants.py
 tools/catalog/thaumaturge-variant-review.csv` before review. The check catches
incorrectly modeled duplicate steps and missing provenance; it cannot establish
which game quest ID belongs to each alternative branch. The runtime remains
strict and unverified progression stays unavailable rather than showing false
completion or auto-advancing pinned jobs.

Next integration gate: verify both level-10 variant IDs against actual game
prerequisites, extend the bundled runtime schema with starting-class
eligibility, and test both starting-THM and later-unlocked-THM scenarios before
activating this catalog entry.

### Runtime schema v2 (first validated chain)

The runtime manifest uses ordered `stages`. Each stage may contain `always`,
`startingClass`, or `laterClass` quest IDs; variant applicability is selected
using `IPlayerState.FirstClass` and never inferred from English quest names.
Only game-data IDs present in the current quest sheet are accepted. Unavailable
character data fails closed. All quest states are still read from the game.

The first production entry is Thaumaturge (`ClassJob` 7). Its level-10 variants
are 65884 (predecessor 65881, original starting-class route) and 65885
(predecessor 65882, later-unlock route). Sources:
- https://ff14.huijiwiki.com/wiki/Data:Quest/65884.json
- https://ff14.huijiwiki.com/wiki/Data:Quest/65885.json
- https://ffxiv.consolegameswiki.com/wiki/List_of_thaumaturge_quests

The offline CSV compiler currently emits legacy schema v1 and must be upgraded
before using it to regenerate this schema-v2 production manifest. Do not
replace the manifest with its output until upgraded and validated.

## Activated review batch — 2026-10-18

Rogue (29), Archer (5), and Gladiator (1) have been added alongside Thaumaturge (7).
Quest ID evidence comes from the in-game raw QuestFlags export, with class-chain membership/order
cross-checked against published class quest tables; see tools/catalog/verified-chain-provenance.json.
Character-first-class eligibility is encoded through schema v2 stages (startingClass/laterClass).
Arcanist, other classes, jobs, and crafting/gathering chains remain unavailable pending review.
This batch has not been compiled or tested in-game; validate progression counts and next-step NPCs before considering the additions release-ready.
