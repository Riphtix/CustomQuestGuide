# Custom Quest Guide v1.4.0.0

This release adds expanded Watchlist and progression workflows, a dedicated Sets browsing experience, and optional Focused Tracking alongside FFXIV's quest list.

## Highlights
- Focused Tracking shows up to three Watchlist targets in one compact overlay, with native FFXIV icons, position and alignment controls, and context-sensitive target progress.
- Focused targets update without opening `/cqg`, using bounded target-specific refreshes rather than full catalog polling.
- Native focused icons use retained asynchronous texture leases, avoiding expensive texture acquisition during every draw. This was confirmed in-game to restore expected FPS for three icon-bearing targets.
- Improved class/job quest progression, unlock/prerequisite guidance, and Watchlist completion behavior.
- Dedicated Sets tab with expansion and type filtering; additional collection targets and progress presentation.
- Maintains existing Glamour Dresser snapshot caching, on-demand enrichment/network requests, and manual `/cqg` ownership-scan policy.

## Validation notes
The most recent icon-performance change was confirmed in-game by the maintainer. This package was prepared without compiling the plugin; perform a Release build and verify the resulting artifact before publishing.
