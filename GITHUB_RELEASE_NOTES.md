# Custom Quest Guide v1.3.7.1

## Hotfix

- Duties now show their type and required character level everywhere CQG identifies them, using labels such as `Dungeon: Mt. Gulg - Lv. 79`.
- Treasure-map instances now use the specific `Treasure Dungeon` type, e.g. `Treasure Dungeon: The Dungeons of Lyhe Ghiah - Lv. 1`, instead of generic `Duty`.
- Shared duty formatting covers the Duties tab, search/target preview, pinned duties, duty dependencies, monster duty context, and Run guidance.
- The displayed `Lv.` is the class/job entry level only; minimum item level can no longer be mistaken for character level.

## v1.3.7 release highlights

- Added crafting-only breadcrumb navigation for recipe drill-down while keeping normal Back/Forward navigation independent.
- Added Need/Greed target markers for directly targeted items and still-needed set members without automating loot decisions.
- Reworked event and Online Store acquisition handling with route-level availability, historical-event provenance, paid-store routes, outfit/bundle item handling, and automatic selected-target enrichment.
- Added progression-aware NPC map routing and explicit View Map actions for actionable NPC destinations.
- Reworked Pinned Targets into a cached 30-second snapshot view to avoid expensive resolver work at frame rate.
- Improved monster acquisition presentation with duty/FATE/levequest context, multiple spawn locations, coordinate confidence handling, and progression-gated area guidance.
- Cleaned and reorganized the source tree into clearer Core, Domain, Data, Resolution, UI, and Infrastructure responsibilities while preserving runtime behavior.

## Release metadata

- Assembly version: `1.3.7.1`
- Dalamud API level: `15`
- Expected release asset: `CustomQuestGuide_v1.3.7.1.zip`

---

## v1.3.7 — Crafted-item drill-down breadcrumbs

- Added crafting-only breadcrumbs when opening recipe ingredients.
- Breadcrumbs preserve the explicit craft traversal path and allow jumping back to an earlier crafted parent.
- Normal target navigation remains governed by Back/Forward and clears the crafting trail.
- No recursive material aggregation was added.

## v1.3.5.6

- Keeps historical seasonal-event provenance visible even when a permanent/current acquisition route is primary.
- Sea Breeze Summer Halter now remains a Market Board / Player Trade target while also showing Moonfire Faire under Historical source.

## v1.3.5.5

- Makes availability route-specific: current gameplay routes now outrank ended-event provenance.
- Keeps historical event origin visible beneath current permanent/player-economy sources.
- Separately reports gameplay unavailability and Online Store availability for event-only rewards.
- Treats Recompense Officer routes as conditional and prevents obsolete event currencies/dependency rows from becoming false permanent routes.

## v1.3.5.4

Historical seasonal rewards now distinguish **gameplay availability** from **FINAL FANTASY XIV Online Store availability**. Ended event rewards retain their original event/quest provenance, while CQG performs a cached, target-scoped check for a current paid Online Store source.

## v1.3.5.3

Availability now sits directly under the acquisition source, with ended/currently unobtainable event states highlighted using a restrained warning treatment. Resolver quality/specificity remain secondary diagnostics below it.

### v1.3.5.0

- Promotes event-derived acquisition into a first-class resolved source family even when the event is not active.
- Keeps generic Seasonal / Collaboration Event guidance visible instead of suppressing it as unresolved placeholder data.
- Adds centralized static normalization for seasonal events, recurring collaborations, Moogle Treasure Trove, and legacy-event families.
- Availability now distinguishes recurring event, legacy existing-copy, external promotion, and stronger permanent-source cases from already-known source data.
- Live event activity is intentionally not checked in this build; that remains on-demand follow-up work.

### v1.3.4.29

- Added static FATE context for monster-drop sources when Garland exposes a related FATE on the monster document.
- FATE relationships are cached per monster and never query live FATE activity.
- Locationless FATE monsters can now satisfy monster-source completion through the named FATE context.

# Custom Quest Guide v1.2.2

This release significantly expands the types of FFXIV unlocks that Custom Quest Guide can track and improves how acquisition guidance is presented.

## New target types
- Emotes
- Titles
- Hairstyles
- Fashion Accessories
- Facewear
- Chocobo Barding
- Framer's Kits
- Riding Maps

## Riding Maps & Shared FATEs
- Added Riding Map sets grouped by expansion.
- Added collection progress bars for Riding Map sets.
- Added Shared FATE progress tracking for applicable Endwalker and Dawntrail Riding Maps.
- Fixed Riding Map ownership detection for already-consumed maps.
- Shared FATE progress is read during the existing `/cqg` one-shot scan and does not introduce continuous polling.

## Progress & UI improvements
- Added reusable progress bars for measurable target progress.
- Progress bars are also shown in pinned targets where applicable.
- Standardized dropdown and control widths for a cleaner, more consistent UI.
- Improved pin layouts and reduced redundant progress text.

## Better acquisition guidance
- Player-facing sources are now required to be actionable and useful.
- Internal resolver labels such as `Item conversion` are no longer shown as acquisition instructions.
- Gameplay acquisition methods always take priority over Market Board / Player Trade.
- Market Board is shown as an alternative when a valid gameplay source exists.
- Added actionable Eureka Orthos guidance for `Modern Aesthetics - A Close Shave`.

## Existing behavior preserved
- Existing pins and configuration migrate to schema 12.
- Per-character Glamour Dresser ownership snapshots are preserved.
- Target-scoped network behavior remains unchanged.
- `/cqg` ownership scanning remains one-shot rather than continuously polling.

Blue Mage spell targeting is intentionally not included yet; dedicated limited-job support is planned for a future release where it can be properly tested.

### v1.3.4.39

- Consolidated duplicate levequest rows from local game data and Garland into one canonical player-facing leve source.
- Grouped alternate levequests under a dedicated expandable `Levequests` section instead of flooding `other known sources`.
- Hid Garland leve provenance text from normal player-facing source detail while retaining useful local leve levels.

### v1.3.4.38
- Added local battle-leve monster context with expandable levequest listings and a Debug test finder.