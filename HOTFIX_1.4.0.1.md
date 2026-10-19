# Custom Quest Guide v1.4.0.1 — Hotfix

Changes since v1.4.0.0:
- Focused Tracking responds to quest completion and relevant unlock/inventory events without opening the CQG window. Event handlers coalesce focused-target refresh requests; rendering remains snapshot-based and event-driven.
- Relic progress recognizes earlier consumed weapon/tool upgrade stages when a later stage in the same verified lineage is owned. This applies across supported relic families, rather than only Phantom weapons.

Release tag: `v1.4.0.1`
Upload asset: `CustomQuestGuide_v1.4.0.1.zip` (compiled Dalamud plugin ZIP, **not** this source archive).

Build the plugin locally with Dalamud.NET.Sdk 15, create the compiled release ZIP, upload it to the tagged GitHub release, then update repository `pluginmaster.json` using `pluginmaster.release.json`.

C# compilation and in-game verification were not possible in this environment.
