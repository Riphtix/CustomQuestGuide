# CQG verified progression catalog — offline authoring

The in-game plugin accepts only verified, nonempty quest-ID sequences. Run this generator **offline**, never in the Dalamud game process. There are deliberately no assumed entries in the template.

1. Establish complete class/job membership with official Lodestone class/job quest categories; independently confirm ordering and starting-class alternatives using additional quest lists and game data. Record exact Lumina `Quest.RowId` values, not names, plus an HTTPS reference URL for *each* row.
2. Put one *applicable progression* quest per row in `quest-catalog-review-template.csv`, use 1-based contiguous order per `classJobId`. Exclude unlock/prelude/relic/MSQ quests. Add verified rows **only after the entire chain is reviewed**. Any starting-class variant requiring separate character eligibility must be modeled and tested before that class is activated—this schema doesn't yet encode variants.
3. Run `python tools/catalog/build_verified_catalog.py reviewed.csv Data/Verified/job-progression.json`.
4. Run `python -m unittest discover -s tests/catalog`. Build locally with .NET and run the in-game job progression audit. Reject a chain whose quest IDs disagree with Lumina or whose quest count/next quest disagrees with trusted quest lists.

The generator validates IDs, uniqueness, order and required provenance. It **cannot independently prove** the supplied IDs are correctly assigned or that no quests were omitted. Existing `VerifiedJobQuestCatalog` checks that quest IDs exist in game data. An independent complete-chain review is a prerequisite to setting `verifiedComplete=true`.

This offline authoring tooling introduces no plugin startup, gameplay, network or scanning costs. The bundled JSON remains intentionally empty pending fully validated data.
