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
