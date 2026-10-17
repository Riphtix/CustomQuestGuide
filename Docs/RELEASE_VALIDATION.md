## v1.3.7 validation

- Open a normally crafted item and expand its recipe.
- Open an ingredient: verify `Crafting path:` appears with parent > current item.
- From that ingredient, open a craftable sub-ingredient and verify the breadcrumb extends by one level.
- Click an earlier breadcrumb and verify CQG returns to that item without duplicating or corrupting Back/Forward history.
- Select an unrelated item from search/target dropdown and verify the crafting breadcrumb disappears.
- Verify recipe ingredient quantities remain the direct recipe quantities rather than recursively aggregated totals.

## v1.3.5.6 historical provenance retention

- [ ] Select `Sea Breeze Summer Halter`. `Source` remains `Market Board / Player Trade`.
- [ ] Confirm the availability block reads `Gameplay: Available — Market Board / Player Trade`.
- [ ] Confirm a separate `Historical source:` block shows `Moonfire Faire` and does not replace the current source.
- [ ] Select `Inflatable Mograft`. Its event occurrence remains the primary historical source and it still reports no current gameplay route.
- [ ] Confirm event provenance does not appear inside `other known sources` when a current gameplay route is primary.

## v1.3.5.5 route-level availability

- [ ] Historical event-only item (Inflatable Mograft): source remains the event occurrence; shows gameplay unavailable and an Online Store result, without duplicating the historical block.
- [ ] Tradable old seasonal item (Sea Breeze Summer Halter): current Source remains Market Board / Player Trade; gameplay is shown as available and any known seasonal provenance appears under `Historical source` rather than making the item unobtainable.
- [ ] Event item with a permanent vendor/Gold Saucer/crafting route: permanent route is the current Source; event appears only as historical provenance.
- [ ] Recompense Officer source: show gameplay as conditional with prior-event/eligibility wording, not universally available.
- [ ] Ended event item with no gameplay route but an Online Store source: show gameplay unavailable and Online Store available as separate route states.
- [ ] Ended event item with neither gameplay nor Online Store source: show gameplay unavailable and no current store listing.
- [ ] Generic dependency rows (`Item Conversion`, `Currency Source`) never cause an ended event reward to be labeled gameplay-available.
- [ ] An `Authentic X`/equivalent store reissue does not make differently named historical item X claim exact store availability unless provider data explicitly relates them.

## v1.3.5.4 historical event Online Store availability

- [ ] Select an ended event reward such as Inflatable Mograft and confirm `Gameplay: Unavailable — original event has ended` is visible.
- [ ] On first selection, confirm the Online Store line briefly shows `Checking...` and does not repeatedly issue requests while the target remains selected.
- [ ] For an ended event reward currently sold on the Online Store, confirm `Online Store: Available (paid optional item)` is shown while the historical event remains the primary source.
- [ ] For an ended event reward not currently sold there, confirm `Online Store: No current listing found` is shown.
- [ ] Reopen/reselect the same target and confirm the cached result is reused without a new request.
- [ ] Confirm `Clear network caches` also clears the Online Store lookup cache.
- [ ] Confirm transient FFXIV Collect failures do not become false `No current listing found` results and back off before retrying.

## v1.3.5.3 availability hierarchy polish

- [ ] Select `Inflatable Mograft`. Confirm `Availability: Event Ended / Currently Unobtainable` appears directly below the event/quest source block.
- [ ] Confirm the ended/unobtainable availability line uses the warning treatment and is more visually prominent than diagnostics.
- [ ] Confirm `Resolution quality` and `Source specificity` remain below availability.
- [ ] Confirm available/permanent targets still render availability without a warning treatment.
- [ ] Confirm source resolution and event quest hierarchy are unchanged from v1.3.5.2.

## v1.3.5.2 event source visual hierarchy

### Event occurrence presentation
- [ ] Select Inflatable Mograft.
- [ ] Confirm `Source: Moonfire Faire 2026` remains the parent source line.
- [ ] Confirm `Historical Seasonal Event` appears indented beneath the event source and in disabled/dim text.
- [ ] Confirm `Quest:` is shown as a bulleted child row beneath the event classification.
- [ ] Confirm `Earning Your Water Wings (Lv. 30)` remains normal-brightness text beside the dimmed `Quest:` label.
- [ ] Confirm availability still reads `Event Ended / Currently Unobtainable`.
- [ ] Confirm no expired event NPC/map action appears.

### Regression
- [ ] Non-event item source presentation is unchanged.
- [ ] Monster, levequest, NPC map, and pinned-target presentation remain unchanged.

## v1.3.5.1 event occurrence authority

- [ ] Select `Inflatable Mograft`. Source should be `Moonfire Faire 2026`, not the quest name alone.
- [ ] The source block should show `Historical Seasonal Event` and `Quest: Earning Your Water Wings (Lv. 30)`.
- [ ] Availability should read `Event Ended / Currently Unobtainable`, never `Available / Permanent`.
- [ ] The ended event should not offer a quest-NPC/map action as if the quest were currently available.
- [ ] A target with a stronger permanent source should still keep that permanent route/availability.

## v1.3.5.0 static event-source classification

- [ ] Select an item with a concrete FFXIV Collect event source. The source should remain resolved even if the event is not active, and `Event: ` should not be redundantly prefixed in the displayed source name.
- [ ] Select a `Seasonal Miscellany` item that has only generic event-family context. It should show `Seasonal / Collaboration Event` rather than disappearing from the player-facing source list / appearing unresolved.
- [ ] A recurring event/collaboration source (for example Moogle Treasure Trove or Yo-kai Watch data when present) should show `Availability: Limited / Recurring Event`.
- [ ] A legacy historical event item should show `Availability: Legacy / Existing Copies`.
- [ ] An item that also has a stronger permanent acquisition source should remain `Availability: Available / Permanent`.
- [ ] Confirm selecting event items makes no new event-calendar request; v1.3.5.0 is static classification only.

## v1.3.4.40 NPC routing / Pinned Targets performance

- [x] Static review: Pins-tab subtitle and location/frontier derivation is cached for one second instead of recomputed at frame rate.
- [x] Cache is cleared on configuration mutations; it does not introduce a timer, background task, ownership scan, or network polling loop.
- [x] NPC rows with a missing formatted location now consult the shared NPC placement index before giving up on map navigation.
- [x] NPC destinations in explicitly cataloged locked special areas route to the first unmet/current access quest before exposing the destination waypoint.
- [ ] In-game: compare frame rate on Pinned Targets against v1.3.4.39 with the same pin list.
- [ ] In-game: verify item, duty, job, emote, set, and quest NPC directions expose a clickable map location whenever live-game placement data exists.
- [ ] In-game: verify a gated special-area NPC destination shows the required/current access quest until that area is unlocked.
- [ ] Runtime compile/load verification still requires the local Dalamud/.NET build environment.

## v1.3.4.30 Debug FATE-monster test finder

- [x] FATE test discovery is explicit Debug-only work; no background catalog scan was added.
- [x] Candidate discovery uses local MobDrop data and targetable item IDs.
- [x] Garland mob probes are sequential, paced, bounded to 80 candidates, and cancellable.
- [x] Cached monster FATE metadata is reused before any network request.
- [x] Test results select normal item targets rather than rendering a mock source UI.
- [ ] Runtime compile/load verification still requires the local Dalamud/.NET build environment.

## v1.3.4.28 duty-only monster validation

- Static review: duty context attaches only to locationless concrete monsters when exactly one concrete duty name survives source normalization.
- Completion policy accepts named duty context as the alternate location contract while retaining identity and monster-level requirements.
- Monster enrichment candidates use the same duty-context rule as the player-facing resolver, preventing false-negative retry loops.
- UI renders duty-only monsters without coordinates and offers `Open Duty` only when ContentFinder resolution succeeds.
- Ambiguous/multiple duty candidates remain unresolved.

## v1.3.4.24 MobSpawn audit parser fix

- [x] Audit and runtime MobSpawn parsing share `TryReadRawMobSpawnPosition`.
- [x] Serialized `Position` values retain X, Y, and the optional third component for blacklist matching.
- [x] Audit output contains row/parse totals and a deterministic normalized-data SHA-256 fingerprint.
- [x] Removed misleading NuGet package-version vs CLR assembly-version equality check.
- [x] No automatic candidate-to-blacklist promotion was added.
- [ ] Runtime audit export to be re-run in game; expected result is non-zero parsed positions and candidate rows for known repeated tuples.

## v1.3.4.23 validation notes

- Static checked the new MobSpawn audit path as on-demand only; it is not invoked from startup, normal resolver execution, or per-frame rendering.
- Audit consumes the already loaded/cached LuminaSupplemental dataset and contains no HTTP/network operations.
- Debug actions are now separated into collapsible groups; existing cache/export handlers remain unchanged behind the new grouping helpers.
- Confirmed source-file delimiter balance after edits and checked for duplicate helper definitions.
- Runtime compilation could not be performed in this environment because the .NET SDK/Dalamud build toolchain is unavailable.

# v1.3.4.20 sequential monster-cache validation

Recommended runtime checks:

1. Clear network caches, select Diremite Web, and confirm the item/monster identity list appears before all monster locations finish.
2. Confirm monster enrichment proceeds one monster at a time and each completed monster gains location/level data without waiting for the remaining monsters.
3. Navigate away while enrichment is running, then return. Completed monsters should remain cached and no completed monster should be fetched again.
4. Select another item that drops from a monster already resolved in step 2. Its monster metadata should appear from cache with no new Garland mob request.
5. Press Rebuild selected source. The selected item document should refresh, but previously resolved monster metadata should remain available.
6. Clear network caches. Both `garland-source-cache.json` and `monster-cache.json` should be removed; subsequent selection should rebuild them on demand.
7. Verify crafting, vendors/exchanges, gathering, duties, ownership colors, navigation, and Glamour Dresser ownership behavior remain unchanged.

Static validation in the build environment should include: no references to the retired combined-cache monster dictionaries, balanced C# delimiters, and ZIP integrity. A real Dalamud/.NET compile and in-game runtime test are still required because this environment does not include the project SDK/toolchain.


## v1.3.4.22 targeted validation

- Verify Miteling resolves North Shroud around `(24.3, 27.5)` and South Shroud around `(25.3, 18.3)` without the `(21.5, 21.5)` placeholder.
- Verify legitimate rows with negative third components remain visible.
- Verify size-factor 95 and 200 placeholder families are omitted only when the complete explicit tuple matches.
- Verify Pin falls back through normalized displayed map X/Y for MobSpawn rows (no fabricated world coordinates).

- [ ] Confirm a monster with verified + unverified coordinates chooses a verified representative and places unverified coordinates last.
- [ ] Confirm unverified coordinates display `*` and tooltip `Unverified coordinate`.
- [ ] Confirm confirmed placeholder tuples do not appear or pin.

### v1.3.4.26 coordinate-confidence debug verification
1. Enable Detailed + Debug and expand Supplemental data tools.
2. Click `Find coordinate confidence tests`.
3. For `Unverified + verified`, click Test and confirm the verified location is representative; expand the monster and confirm the unverified location is last, marked `*`, with tooltip `Unverified coordinate`.
4. For `Unverified only`, click Test and confirm the unverified coordinate remains visible as representative with `*` and tooltip.
5. For `Suppressed placeholder`, click Test and confirm the listed raw placeholder coordinate is absent.
6. The finder itself must issue zero network requests.


### v1.3.4.29 FATE monster checks

- Select an item with a monster known to participate in a FATE and allow one monster enrichment pass.
- Confirm the monster row shows `FATE: <name>` as indented static subtext.
- Confirm no active/inactive/current FATE state is shown or queried.
- Re-select another item that references the same monster and confirm the FATE context comes from the monster cache without another mob request.
- Confirm a monster with both verified coordinates and FATE context keeps the verified coordinate as the collapsed representative.
- Confirm a locationless FATE monster is treated as complete once its exact identity, level, and FATE context are known.


## v1.3.4.31 checks
- Confirm `Find FATE monster tests` performs no network requests.
- Confirm a single-monster item with FateItem evidence shows static FATE subtext.
- Confirm multi-monster FateItem targets do not assign that FATE to every monster.
- Confirm overworld location remains representative when present.
- Confirm locationless single-monster FATE context satisfies monster completeness without creating a duty context.


## v1.3.4.32 checks
- Monster-location pins prefer provider world/map identity when available.
- Map-space-only monster coordinates use TerritoryType -> Map plus inverse map conversion before opening the map.
- Name-based map-link construction is only a compatibility fallback.
- This specifically avoids special-region aliases producing "Unable to display area." when an exact TerritoryType ID is already known.

- v1.3.4.33: Verify monster location buttons read `View Map`; verify ordinary overworld maps and special regions such as Eureka Pyros open from exact territory/map IDs without relying on display-name lookup.

### v1.3.4.34 terminology audit
- Audited all user-visible `Pin` strings in C# source. Remaining Pin/Unpin labels route through persistent-target `DrawPinToggle` behavior; monster map actions say `View Map`.
- Audited all `OpenMapWithMapLink` call sites for conflicting `Pin` button labels.

### v1.3.4.35 locked-area guidance
- With Eureka Pyros not unlocked, select a monster located in Eureka Pyros (for example Ying-Yang): the coordinate row should show `Area locked` instead of `View Map`.
- The row should show `Area locked — unlock quest: And We Shall Call It Pyros` and route to Rodney in Kugane when quest guidance is available.
- After the corresponding Eureka unlock quest is complete, the normal `View Map` action should return.
- Ordinary overworld monster locations must remain unchanged.

## v1.3.4.36 validation targets
- With Eureka Pyros locked before any Eureka progress, the row should identify the earliest unmet formal Eureka unlock prerequisite (normally `And We Shall Call it Eureka` once Stormblood is complete), not tell the user to “continue the storyline.”
- As prerequisite quests are completed/accepted, locked-area guidance should advance to the next/current quest automatically.
- Pyros/Pagos/Hydatos should retain their explicit elemental-level/story requirement note because Eureka's internal quasi-quests are not normal journal quests.
- Bozjan Southern Front, Zadnor, Occult Crescent South Horn, and North Horn should use the same prerequisite-frontier logic when their map source appears while locked.
- Completing the locale's final unlock quest should restore `View Map` without cache clearing.

## v1.3.4.37 validation targets
- With no Eureka progression, a Pyros monster should lead to the Eureka entry gate rather than `And We Shall Call It Pyros`.
- After the first Eureka gate is complete, the next explicit gate should advance in order.
- A gate-specific elemental/job/item-level requirement should appear only when that gate is itself actionable.
- Formal prerequisites of the selected gate should still resolve through the shared quest prerequisite frontier.
- Expanding a monster's locations should not duplicate the representative location already shown on the collapsed row.

## v1.3.4.39 Levequest source-family validation
- Select Diremite Web (or another monster-drop item with multiple levequest relationships).
- Verify alternate leves appear once each under a collapsed `Levequests` group, sorted by level then name.
- Verify no player-facing row displays `Garland Tools leve data`.
- Verify `other known sources` excludes levequests and counts only other acquisition families such as Treasure Hunt or Retainer Venture.
- Expand monster-specific `Levequest` context and verify the v1.3.4.38 monster relationship presentation still works independently.

## v1.3.4.38 Levequest monster validation
- Use Debug -> Supplemental data tools -> `Find levequest monster tests` to select a real item/monster pair without manually searching the catalog.
- Verify the monster row shows expandable `Levequest` context and that expansion lists every linked battle leve.
- Verify multiple leves are ordered by level then name and are not duplicated.
- Verify a leve-context-only monster does not remain in a false "monster metadata unavailable" state solely because it lacks an overworld coordinate.
- Verify the helper and production path make no network request for leve membership.

## v1.3.4.41 targeted validation

- Open an item whose primary shop/vendor source names an NPC (for example an Eirene exchange). Confirm a visible `Go to: <NPC> — <location> [View Map]` action is present and the button opens/flags the NPC location.
- Confirm inaccessible progression-gated NPC destinations still redirect to the required/current quest instead of showing an unusable NPC waypoint.
- Open Pinned Targets with a representative multi-pin queue and compare FPS to Items/Duties. After the initial snapshot build, leaving the tab open should not repeatedly resolve sources/frontiers or quest sequences.
- Leave and re-enter Pinned Targets after game/config state changes; cached presentation should refresh on re-entry.

## v1.3.4.42 validation
- Open Pinned Targets and verify the upper-right countdown reads `X Seconds Until Refresh` and counts down from 30.
- Verify opening/re-entering the tab produces a fresh snapshot immediately.
- Leave the tab open past zero and verify progress/source/completion state refreshes once, then the countdown resets to 30.
- Confirm FPS remains stable between refreshes and no resolver/frontier work repeats every frame.
- Reorder/remove/add a pin and verify the visible pin data updates immediately and the countdown restarts.

### v1.3.4.43 NPC map action
- Select an Archeo Kingdom augmentation/exchange item that directs the player to Eirene. Confirm `Go to: Eirene — Solution Nine (X:8.1, Y:14.0) [View Map]` appears.
- Click `View Map` and verify the flag is placed at Eirene rather than an arbitrary representative point in Solution Nine.

## v1.3.5.7 validation
- Select a pure Online Store gear piece such as `The Warden's Thighboots` with no gameplay source.
- Confirm the initial target-scoped lookup does not run continuously and settles into the persistent cache.
- Confirm the target no longer remains `No source resolved yet` after a positive store result.
- Confirm the visible source is the FINAL FANTASY XIV Online Store and, when supplied by the outfit endpoint, shows `The Warden's Attire` as the paid bundle.
- Confirm route availability says gameplay is unavailable while Online Store availability is available.
- Confirm historical-event items continue to retain their event/quest provenance and current gameplay routes continue to outrank store routes.
- Confirm clearing network caches clears the Online Store schema-v2 cache.


## v1.3.5.8 validation

- Select a target such as **Valentione Acacia Attire (Dress)** with no prior broad FFXIV Collect refresh in the current session.
- Confirm the UI briefly reports `Resolving community source data...` and then updates automatically to the FFXIV Collect event/store source without pressing **Refresh acquisition sources**.
- Confirm switching away and back within 30 minutes does not repeat the target-scoped requests.
- Confirm manual **Refresh acquisition sources** still performs its existing broad refresh behavior.
