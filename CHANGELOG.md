# Changelog

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
