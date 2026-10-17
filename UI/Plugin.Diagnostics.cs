using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed partial class Plugin
{
    private sealed record FateMonsterTestCase(
        string Kind,
        uint MonsterBaseId,
        uint MonsterNameId,
        string MonsterName,
        IReadOnlyList<string> FateNames,
        uint ExampleItemId,
        bool HasVerifiedLocation);

    private sealed record LeveMonsterUiTestCase(
        uint MonsterBaseId,
        uint MonsterNameId,
        string MonsterName,
        IReadOnlyList<MonsterLeveContext> Leves,
        uint ExampleItemId);

    private IReadOnlyList<MobSpawnConfidenceTestCase>? mobSpawnConfidenceTests;
    private IReadOnlyList<FateMonsterTestCase>? fateMonsterTests;
    private IReadOnlyList<LeveMonsterUiTestCase>? leveMonsterTests;

    private void DrawDebugPanel(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        if (!config.Detailed)
            return;

        ImGui.Spacing();
        ImGui.Separator();

        var debug = config.Debug;
        if (ImGui.Checkbox("Debug", ref debug))
        {
            config.Debug = debug;
            SaveConfig();
        }

        if (!config.Debug)
            return;

        ImGui.Spacing();

        // Never scan/resolve the full catalog from Draw(). v1.1.12.6 made
        // provenance resolution more thorough, which turned the old automatic
        // two-second coverage refresh into a very expensive render-thread task.
        // Full-catalog analysis now happens only through the explicit Ambiguity
        // Audit export; normal Debug rendering stays O(1).
        ImGui.TextDisabled($"Catalog targets: {catalog.Targets.Count:N0}");
        ImGui.TextDisabled($"Target sets: {setCatalog.Sets.Count:N0}");
        ImGui.TextDisabled(
            $"Job progression targets: {jobCatalog.Jobs.Count:N0} " +
            $"({jobCatalog.Jobs.Count(j => j.UnlockQuest is not null):N0} with unlock quest; " +
            $"{jobCatalog.Jobs.Sum(j => j.Progression.Count):N0} formal quest nodes)");
        ImGui.TextDisabled($"Pinned targets: {config.PinnedTargets.Count:N0}");
        ImGui.TextDisabled(
            $"Ownership scan: {(ownershipPollingActive ? "active (one-shot full pass)" : "idle")}; " +
            $"permanent={ownership.PermanentCandidateCursor:N0}/{ownership.PermanentCandidateCount:N0}; " +
            $"duties={dutyUnlocks.PollingCursor:N0}/{dutyUnlocks.PollingCandidateCount:N0}");
        ImGui.TextDisabled("Catalog-wide resolver coverage is computed only by the on-demand Ambiguity Audit (never during UI rendering).");
        if (!string.IsNullOrWhiteSpace(releaseDiagnosticsStatus))
            ImGui.TextDisabled($"Configuration diagnostic: {releaseDiagnosticsStatus}");

        ImGui.TextDisabled(
            $"Supplemental data: Store={supplemental.StoreItems.Count:N0}, " +
            $"Unobtainable={supplemental.UnobtainableItems.Count:N0}, " +
            $"GenericSources={supplemental.ItemSources.Count:N0}, " +
            $"DutyItems={supplemental.DutyReferencesByItem.Count:N0}");
        ImGui.TextDisabled(
            $"Supplemental rows: StoreItem={supplemental.StoreItemRows:N0}, " +
            $"UnobtainableItem={supplemental.UnobtainableItemRows:N0}");
        ImGui.TextDisabled(
            $"Supplemental shop metadata: ShopNames={supplemental.ShopNames.Count:N0}, " +
            $"ShopNpcLinks={supplemental.ShopNpcIds.Sum(x => x.Value.Count):N0}");

        ImGui.TextDisabled(
            $"Community collectible sources: {collectibleSources.Status}");
        ImGui.TextDisabled(
            $"Garland fallback: {garlandSources.Status}; " +
            $"item work={garlandSources.PendingItemRequestCount:N0}, " +
            $"monster work={garlandSources.PendingMonsterRequestCount:N0}, " +
            $"monster cache={garlandSources.CachedMonsterCount:N0}");

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Game providers: Craft={resolver.CraftingSourceItems:N0}, " +
            $"GilShop={resolver.GilShopSourceItems:N0}, " +
            $"SpecialShop={resolver.SpecialShopSourceItems:N0}, " +
            $"GCShop={resolver.GrandCompanySourceItems:N0}");
        ImGui.TextDisabled(
            $"Game providers: Quest={resolver.QuestSourceItems:N0}, " +
            $"Achievement={resolver.AchievementSourceItems:N0}, " +
            $"Gathering={resolver.GatheringSourceItems:N0}, " +
            $"Leve={resolver.LeveSourceItems:N0}");
        ImGui.TextDisabled(
            $"Game providers: CompanyCraft={resolver.CompanyCraftSourceItems:N0}");

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Curated source fallbacks: total={resolver.CuratedFallbackItemCount:N0}, " +
            $"fallback-only={resolver.CuratedFallbackOnlyCount:N0}, " +
            $"superseded={resolver.CuratedFallbackSupersededCount:N0}");
        ImGui.TextDisabled(
            "Curated fallbacks are hidden whenever generic or metadata-driven data can replace them.");
        ImGui.TextDisabled(
            $"Catalog exclusions: StoreData={catalog.SupplementalStoreExclusionCount:N0}, " +
            $"SupplementalUnobtainable={catalog.SupplementalUnobtainableExclusionCount:N0}, " +
            $"LiveUnobtainable={catalog.LiveUnobtainableExclusionCount:N0}, " +
            $"Curated={catalog.CuratedExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Non-item virtual progression rows excluded: " +
            $"{catalog.VirtualProgressionExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Ephemeral task/turn-in items excluded: " +
            $"{catalog.EphemeralTaskItemExclusionCount:N0}");
        ImGui.TextDisabled(
            $"Explicit retired-system rows excluded: " +
            $"{catalog.RetiredSystemExclusionCount:N0}");

        if (selectedSet is not null)
        {
            ImGui.TextDisabled($"Selected Set ID: {selectedSet.Id}");
            ImGui.TextDisabled($"Selected Set Members: {selectedSet.ItemIds.Count}");
            var selectedMembers = selectedSet.ItemIds
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .ToArray();
            ImGui.TextDisabled($"Selected Set Owned: {selectedMembers.Count(x => ownership.GetItemStatus(x.ItemId).IsOwned)} / {selectedMembers.Length}");
            foreach (var member in selectedMembers.Where(x => !ownership.GetItemStatus(x.ItemId).IsOwned))
            {
                var advice = GetDutyUnlockAdvice(selectedSet, member);
                if (advice is null)
                    continue;
                ImGui.TextDisabled($"Unlock trace: {advice.DutyName} => unlocked={advice.IsUnlocked?.ToString() ?? "unknown"}, quest={(string.IsNullOrWhiteSpace(advice.QuestName) ? "?" : advice.QuestName)}, contact={advice.Contact ?? "?"}, location={advice.Location ?? "?"}");
                break;
            }
        }
        else if (selectedResult is not null)
        {
            var owned = ownership.GetItemStatus(selectedResult.Target.ItemId);
            ImGui.TextDisabled($"Selected Item ID: {selectedResult.Target.ItemId}");
            ImGui.TextDisabled($"Selected UI Category: {selectedResult.Target.GameUiCategory}");
            ImGui.TextDisabled($"Selected Filter Group: {selectedResult.Target.FilterGroup}");
            ImGui.TextDisabled($"Selected ownership: {owned.IsOwned} {(owned.IsOwned ? $"({owned.Source})" : string.Empty)}");
            ImGui.TextDisabled(
                $"Selected direct source known: {resolver.HasDirectSource(selectedResult.Target)}");
        }

        ImGui.Spacing();

        DrawDebugCacheControls();
        DrawDebugTargetTools(selectedResult, selectedSet);
        DrawDebugCatalogAuditTools();
        DrawDebugSupplementalTools();

        if (!string.IsNullOrWhiteSpace(exportStatus))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(exportStatus);
        }
    }

    private void DrawDebugCacheControls()
    {
        if (!ImGui.CollapsingHeader("Cache & refresh controls"))
            return;

        ImGui.Indent();

        if (ImGui.Button("Clear cached items"))
        {
            ownershipPollingActive = false;
            var removedDresserSnapshot = ownership.ClearCachedOwnershipForCurrentCharacter();
            exportStatus = removedDresserSnapshot
                ? "Cleared cached ownership for this character, including its saved Glamour Dresser snapshot. Run /cqg to start a fresh full ownership scan."
                : "Cleared cached ownership for this character. Run /cqg to start a fresh full ownership scan.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Clears CQG's cached item/job ownership state for the current character. " +
                "This also clears the saved Glamour Dresser ownership snapshot, but does not clear acquisition-source or duty resolver caches. " +
                "Run /cqg afterward for a fresh one-shot ownership scan.");
        }

        ImGui.SameLine();

        if (ImGui.Button("Clear network caches"))
        {
            var collectibleCleared = collectibleSources.ClearSessionCache();
            var onlineStoreCleared = onlineStoreLookup.ClearCache();
            var garlandItemsCleared = garlandSources.ClearCache();
            var garlandDutiesCleared = garlandDutyUnlocks.ClearCache();
            var communityDutiesCleared = communityDutyUnlocks.ClearCache();

            if (collectibleCleared && onlineStoreCleared && garlandItemsCleared && garlandDutiesCleared && communityDutiesCleared)
            {
                networkRequests.Reset();
                nextManualSourceRefreshUtc = DateTime.MinValue;
                coverageMetricsExpiresUtc = DateTime.MinValue;
                exportStatus =
                    "Cleared persisted/community network caches and FFXIV Collect session data. " +
                    "The next unresolved target may perform fresh, rate-limited network lookups.";
            }
            else
            {
                exportStatus =
                    "Could not clear every network cache because a network lookup/refresh is currently active. " +
                    "Wait for it to finish and try again.";
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Deletes CQG's persisted Community Duty, Garland Item, Monster, and Garland Duty caches, " +
                "clears FFXIV Collect data loaded in this plugin session, and resets network diagnostics. " +
                "This does not clear item ownership or Glamour Dresser ownership. " +
                "Use this only for diagnostics/fresh-network testing; normal users should leave network caches intact to minimize HTTP traffic.");
        }

        ImGui.Unindent();
    }

    private void DrawDebugTargetTools(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        if (!ImGui.CollapsingHeader("Selected target diagnostics"))
            return;

        ImGui.Indent();

        if (ImGui.Button("Output target information"))
            OutputTargetInformation(selectedResult, selectedSet);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Writes everything CQG currently knows about the selected target to a text file. " +
                "This is a read-only snapshot: pressing this button never starts a network request. " +
                "Already-cached community data is included when present.");
        }

        if (selectedResult is not null || selectedSet is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Export quest guidance trace"))
                ExportSelectedQuestGuidanceCsv(selectedResult, selectedSet);
        }

        if (selectedSet is not null && IsRelicSet(selectedSet))
        {
            ImGui.SameLine();
            if (ImGui.Button("Export relic trace"))
                ExportRelicProgressionTraceCsv(selectedSet);
        }

        ImGui.Unindent();
    }

    private void DrawDebugCatalogAuditTools()
    {
        if (!ImGui.CollapsingHeader("Catalog audits & exports"))
            return;

        ImGui.Indent();

        if (ImGui.Button("Export ambiguity audit"))
            ExportAmbiguityAuditCsv();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Audits ambiguous/incomplete item resolutions using only local game data and already-cached community data. " +
                "The audit makes ZERO network calls. Cases that could normally use uncached community enrichment are marked 'missing network data'.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Export unresolved CSV"))
            ExportUnresolvedCsv();

        ImGui.SameLine();
        if (ImGui.Button("Export resolver audit CSV"))
            ExportResolverAuditCsv();

        if (ImGui.Button($"Export specificity backlog ({cachedSpecificityEnrichmentCandidates:N0})"))
            ExportSpecificityBacklogCsv();

        ImGui.SameLine();
        if (ImGui.Button($"Export exact-source backlog ({cachedExactSpecificityBacklog:N0})"))
            ExportExactSpecificityBacklogCsv();

        ImGui.SameLine();
        if (ImGui.Button("Export job progression audit"))
            ExportJobProgressionAuditCsv();

        ImGui.Unindent();
    }

    private void DrawDebugSupplementalTools()
    {
        if (!ImGui.CollapsingHeader("Supplemental data tools"))
            return;

        ImGui.Indent();
        ImGui.TextDisabled($"LuminaSupplemental assembly: {supplemental.SupplementalAssemblyVersion}");
        ImGui.TextDisabled($"LuminaSupplemental informational version: {supplemental.SupplementalAssemblyInformationalVersion}");
        ImGui.TextDisabled(
            $"CQG MobSpawn coordinate overrides were audited against package {SupplementalCsvIndex.BundledMobSpawnBlacklistSourceVersion}. " +
            "Package and CLR assembly versions are intentionally not compared.");

        if (ImGui.Button("Export MobSpawn coordinate audit"))
            ExportMobSpawnBlacklistAuditCsv();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Scans the currently loaded LuminaSupplemental MobSpawn dataset on demand and exports suspicious, highly reused exact Position tuples. " +
                "The report includes territory, map-size, reuse counts, and CQG's bundled classification for the exact territory/tuple (Suppress, Unverified, or Normal). " +
                "It does not modify the blacklist or perform network requests.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Find FATE monster tests"))
        {
            fateMonsterTests = supplemental.BuildFateMonsterContextCandidates()
                .Where(x => catalog.GetById(x.ExampleItemId) is not null)
                .Select(x => new FateMonsterTestCase(
                    x.Kind,
                    x.MonsterBaseId,
                    x.MonsterNameId,
                    x.MonsterName,
                    x.FateNames,
                    x.ExampleItemId,
                    x.HasVerifiedLocation))
                .ToArray();

            exportStatus = fateMonsterTests.Count > 0
                ? $"Found {fateMonsterTests.Count} local FATE-monster test case(s)."
                : "No conservative item-scoped FATE/monster test cases were found in the loaded supplemental data.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Finds zero-network test cases from LuminaSupplemental FateItem + MobDrop data. " +
                "CQG only attaches FATE context when an item has explicit FATE evidence and exactly one concrete monster, so this helper tests the same conservative production rule rather than probing Garland mob documents.");
        }

        if (fateMonsterTests is { Count: > 0 })
        {
            foreach (var test in fateMonsterTests)
            {
                ImGui.PushID($"fate-monster-test-{test.Kind}-{test.MonsterBaseId}-{test.MonsterNameId}");
                ImGui.TextUnformatted(test.Kind);
                ImGui.SameLine();
                ImGui.TextDisabled($"{test.MonsterName} — FATE: {string.Join(" / ", test.FateNames)}");

                if (catalog.GetById(test.ExampleItemId) is { } testTarget)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Test"))
                        SelectItemTarget(testTarget);
                    if (ImGui.IsItemHovered())
                    {
                        var expectation = test.Kind switch
                        {
                            "FATE + verified location" => "The verified spawn should remain representative while the item-scoped FATE appears as static subtext beneath the monster.",
                            "FATE context only" => "The single monster should resolve without an overworld coordinate and show the named FATE as its static context.",
                            _ => "The single monster should show every explicit FATE source attached to this item; CQG does not imply that any FATE is currently active.",
                        };
                        ImGui.SetTooltip($"Select item: {testTarget.Name}\n{expectation}");
                    }
                }

                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Find levequest monster tests"))
        {
            var tests = new List<LeveMonsterUiTestCase>();
            foreach (var candidate in resolver.GetLeveMonsterTestCandidates())
            {
                var exampleItemId = supplemental.FindExampleMobDropItem(
                    candidate.MonsterBaseId, candidate.MonsterNameId);
                if (exampleItemId == 0 || catalog.GetById(exampleItemId) is null)
                    continue;

                string monsterName;
                try
                {
                    monsterName = DataManager.GetExcelSheet<BNpcName>()
                        .GetRow(candidate.MonsterNameId).Singular.ExtractText().Trim();
                }
                catch
                {
                    monsterName = string.Empty;
                }
                if (string.IsNullOrWhiteSpace(monsterName))
                    monsterName = $"BNpcName {candidate.MonsterNameId}";

                tests.Add(new LeveMonsterUiTestCase(
                    candidate.MonsterBaseId,
                    candidate.MonsterNameId,
                    monsterName,
                    candidate.Leves,
                    exampleItemId));

                // One single-leve and one multi-leve example are enough for fast UI testing.
                if (tests.Any(x => x.Leves.Count == 1) && tests.Any(x => x.Leves.Count > 1))
                    break;
            }

            leveMonsterTests = tests
                .OrderByDescending(x => x.Leves.Count > 1)
                .Take(3)
                .ToArray();
            exportStatus = leveMonsterTests.Count > 0
                ? $"Found {leveMonsterTests.Count} local levequest-monster test case(s)."
                : "No targetable battle-leve monster test cases were found in local game/supplemental data.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Finds zero-network test items whose monster sources are referenced by the game's Leve -> BattleLeve data. " +
                "Use these to verify the expandable Levequest context without manually searching monster drops.");
        }

        if (leveMonsterTests is { Count: > 0 })
        {
            foreach (var test in leveMonsterTests)
            {
                ImGui.PushID($"leve-monster-test-{test.MonsterBaseId}-{test.MonsterNameId}");
                ImGui.TextUnformatted(test.Leves.Count > 1 ? "Multiple levequests" : "Levequest");
                ImGui.SameLine();
                ImGui.TextDisabled($"{test.MonsterName} — {string.Join(" / ", test.Leves.Select(x => x.Name))}");

                if (catalog.GetById(test.ExampleItemId) is { } testTarget)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Test"))
                        SelectItemTarget(testTarget);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Select item: {testTarget.Name}\nExpand Levequest beneath the monster to verify every linked battle leve is shown.");
                }

                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Find coordinate confidence tests"))
        {
            mobSpawnConfidenceTests = supplemental.BuildMobSpawnConfidenceTestCases();
            exportStatus = mobSpawnConfidenceTests.Count > 0
                ? $"Found {mobSpawnConfidenceTests.Count} coordinate-confidence test case(s)."
                : "No coordinate-confidence test cases with a targetable MobDrop item were found.";
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Finds real monster-drop targets that exercise Unverified + verified, Unverified-only, and Suppressed coordinate behavior. " +
                "This scans only local LuminaSupplemental data and performs no network requests.");
        }

        if (mobSpawnConfidenceTests is { Count: > 0 })
        {
            foreach (var test in mobSpawnConfidenceTests)
            {
                ImGui.PushID($"mobspawn-confidence-{test.Kind}-{test.MonsterBaseId}-{test.MonsterNameId}-{test.TerritoryTypeId}");
                ImGui.TextUnformatted(test.Kind);
                ImGui.SameLine();
                ImGui.TextDisabled(
                    $"{test.MonsterName} — {test.TerritoryName} " +
                    $"(X: {test.X:0.##}, Y: {test.Y:0.##}, Z: {(test.Z.HasValue ? test.Z.Value.ToString("0.###", CultureInfo.InvariantCulture) : "?")})");

                if (catalog.GetById(test.ExampleItemId) is { } testTarget)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Test"))
                        SelectItemTarget(testTarget);
                    if (ImGui.IsItemHovered())
                    {
                        var expectation = test.Kind switch
                        {
                            "Unverified + verified" => "The verified coordinate should remain representative; expand the monster to find the unverified coordinate at the bottom with an * tooltip.",
                            "Unverified only" => "The coordinate should remain visible as the representative, marked with * and the 'Unverified coordinate' tooltip.",
                            _ => "The listed placeholder coordinate should not appear anywhere for this monster.",
                        };
                        ImGui.SetTooltip($"Select item: {testTarget.Name}\n{expectation}");
                    }
                }
                else
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"Item {test.ExampleItemId} is not targetable");
                }

                ImGui.PopID();
            }
        }

        ImGui.TextDisabled(
            "Use the confidence tests for quick UI verification. Use the audit after a LuminaSupplemental update to recollect candidates before updating CQG's hardcoded coordinate overrides.");
        ImGui.Unindent();
    }

    private void ExportMobSpawnBlacklistAuditCsv()
    {
        try
        {
            var report = supplemental.BuildMobSpawnCoordinateAudit();
            var entries = report.Entries;
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-MobSpawnBlacklistAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var csv = new StringBuilder();

            csv.AppendLine($"# LuminaSupplemental assembly version,{Csv(supplemental.SupplementalAssemblyVersion)}");
            csv.AppendLine($"# LuminaSupplemental informational version,{Csv(supplemental.SupplementalAssemblyInformationalVersion)}");
            csv.AppendLine($"# LuminaSupplemental file version,{Csv(supplemental.SupplementalAssemblyFileVersion)}");
            csv.AppendLine($"# CQG compiled LuminaSupplemental package version,{Csv(SupplementalCsvIndex.BundledMobSpawnBlacklistSourceVersion)}");
            csv.AppendLine($"# MobSpawn rows scanned,{report.RawRowCount.ToString(CultureInfo.InvariantCulture)}");
            csv.AppendLine($"# Rows with parsed Position,{report.ParsedPositionRowCount.ToString(CultureInfo.InvariantCulture)}");
            csv.AppendLine($"# Rows with parsed territory + Position,{report.ParsedTerritoryPositionRowCount.ToString(CultureInfo.InvariantCulture)}");
            csv.AppendLine($"# Unique exact Position tuples,{report.UniqueExactPositionTupleCount.ToString(CultureInfo.InvariantCulture)}");
            csv.AppendLine($"# Candidate territory/tuple rows,{entries.Count.ToString(CultureInfo.InvariantCulture)}");
            csv.AppendLine($"# MobSpawn normalized-data SHA256,{Csv(report.DataFingerprintSha256)}");
            csv.AppendLine($"# Generated,{Csv(DateTime.Now.ToString("O", CultureInfo.InvariantCulture))}");
            csv.AppendLine("Territory Type ID,Territory Name,Map Size Factor,X,Y,Z,Rows In Territory,Distinct Monsters In Territory,Global Rows For Exact Tuple,Global Territory Count,Global Distinct Monsters,X Equals Y,Bundled Classification,Candidate Reason");

            foreach (var entry in entries)
            {
                csv.Append(Csv(entry.TerritoryTypeId.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.TerritoryName)); csv.Append(',');
                csv.Append(Csv(entry.MapSizeFactor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)); csv.Append(',');
                csv.Append(Csv(entry.X.ToString("R", CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.Y.ToString("R", CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.Z?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty)); csv.Append(',');
                csv.Append(Csv(entry.TerritoryRowCount.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.TerritoryMonsterCount.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.GlobalRowCount.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.GlobalTerritoryCount.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.GlobalMonsterCount.ToString(CultureInfo.InvariantCulture))); csv.Append(',');
                csv.Append(Csv(entry.XEqualsY.ToString())); csv.Append(',');
                csv.Append(Csv(entry.BundledClassification)); csv.Append(',');
                csv.Append(Csv(entry.CandidateReason));
                csv.AppendLine();
            }

            File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
            exportStatus =
                $"MobSpawn audit: {report.RawRowCount:N0} rows scanned, " +
                $"{report.ParsedPositionRowCount:N0} positions parsed, " +
                $"{entries.Count:N0} candidates.\n{path}";
            Log.Information(
                $"Exported CQG MobSpawn blacklist audit: rows={report.RawRowCount:N0}, " +
                $"parsedPositions={report.ParsedPositionRowCount:N0}, " +
                $"parsedTerritories={report.ParsedTerritoryPositionRowCount:N0}, " +
                $"candidates={entries.Count:N0}, fingerprint={report.DataFingerprintSha256}, path={path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"MobSpawn coordinate audit failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG MobSpawn coordinate audit.");
        }
    }

    private void ExportSelectedQuestGuidanceCsv(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-QuestGuidanceTrace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var csv = new StringBuilder();
            csv.AppendLine("Target Type,Target ID,Target Name,Selected Variant,Source Kind,Source Name,Quest Name,Quest Row ID,Raw State,Quest Giver,Location,Previous Quest IDs");

            void AppendTarget(string targetType, TargetEntry target, string variant)
            {
                var result = resolver.Resolve(target);
                foreach (var source in result.Sources)
                {
                    var questNames = QuestGuidanceIndex.ExtractQuestNames(source);
                    foreach (var questName in questNames)
                    {
                        var infos = questGuidance.GetByName(questName);
                        if (infos.Count == 0)
                        {
                            csv.Append(Csv(targetType)); csv.Append(',');
                            csv.Append(Csv(target.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(target.Name)); csv.Append(',');
                            csv.Append(Csv(variant)); csv.Append(',');
                            csv.Append(Csv(source.Kind)); csv.Append(',');
                            csv.Append(Csv(source.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(",0,Unresolved,,,\n");
                            continue;
                        }

                        foreach (var info in infos)
                        {
                            csv.Append(Csv(targetType)); csv.Append(',');
                            csv.Append(Csv(target.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(target.Name)); csv.Append(',');
                            csv.Append(Csv(variant)); csv.Append(',');
                            csv.Append(Csv(source.Kind)); csv.Append(',');
                            csv.Append(Csv(source.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(',');
                            csv.Append(Csv(info.QuestId.ToString())); csv.Append(',');
                            csv.Append(Csv(GetQuestStateByRowId(info.QuestId).ToString())); csv.Append(',');
                            csv.Append(Csv(info.QuestGiver ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(info.Location ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(string.Join("|", info.PreviousQuestIds)));
                            csv.AppendLine();
                        }
                    }
                }
            }

            if (selectedResult is not null)
                AppendTarget("Item", selectedResult.Target, string.Empty);

            if (selectedSet is not null)
            {
                var variant = IsRelicSet(selectedSet) ? config.SelectedRelicJob ?? string.Empty : string.Empty;
                foreach (var target in selectedSet.ItemIds
                    .Select(catalog.GetById)
                    .Where(x => x is not null)
                    .Cast<TargetEntry>()
                    .Where(x => string.IsNullOrWhiteSpace(variant) || x.EquipJobs.Contains(variant)))
                {
                    AppendTarget("Set member", target, variant);
                }
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            exportStatus = $"Exported selected quest guidance trace to:\n{path}";
            Log.Information($"Exported CQG selected quest guidance trace to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Quest guidance trace export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG selected quest guidance trace.");
        }
    }

    private void ExportJobProgressionAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-JobProgressionAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "ClassJob ID,Name,Abbreviation,Category,Parent ClassJob ID,Unlock Resolution,ClassJob UnlockQuest ID,Resolved Unlock Quest ID,Resolved Unlock Quest,Unlock Quest Giver,Unlock Location,Unlock State,Formal Quest Count,Formal Effective Complete Count,Current/Next State,Current/Next Quest ID,Current/Next Quest,Current/Next Quest Giver,Current/Next Location");

            foreach (var job in jobCatalog.Jobs)
            {
                var unlockState = job.UnlockQuest is null
                    ? QuestProgressState.NotStarted
                    : GetQuestStateByRowId(job.UnlockQuest.QuestId);
                var progression = ResolveJobQuestSequence(job.Progression);
                var completed = progression.Entries.Count(x =>
                    x.EffectiveState == QuestProgressState.Completed);
                var nextEntry = progression.CurrentOrNext;
                var next = nextEntry?.Info is { } nextInfo
                    ? job.Progression.FirstOrDefault(x => x.QuestId == nextInfo.QuestId)
                    : nextEntry is not null
                        ? job.Progression.FirstOrDefault(x => x.Name.Equals(nextEntry.Name, StringComparison.OrdinalIgnoreCase))
                        : null;

                csv.Append(Csv(job.ClassJobId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.Name));
                csv.Append(',');
                csv.Append(Csv(job.Abbreviation));
                csv.Append(',');
                csv.Append(Csv(job.Category));
                csv.Append(',');
                csv.Append(Csv(job.ParentClassJobId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.UnlockResolution));
                csv.Append(',');
                csv.Append(Csv(job.ClassJobUnlockQuestId.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.QuestId.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.Name ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.QuestGiver ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest?.Location ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(job.UnlockQuest is null ? "Unknown" : unlockState.ToString()));
                csv.Append(',');
                csv.Append(Csv(job.Progression.Count.ToString()));
                csv.Append(',');
                csv.Append(Csv(completed.ToString()));
                csv.Append(',');
                csv.Append(Csv(nextEntry?.RawState.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.QuestId.ToString() ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.Name ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.QuestGiver ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(next?.Location ?? string.Empty));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus =
                $"Exported {jobCatalog.Jobs.Count:N0} class/job progression target(s) to:\n{path}";
            Log.Information(
                $"Exported {jobCatalog.Jobs.Count} CQG class/job progression targets to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Job progression export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG class/job progression audit.");
        }
    }

    private void ExportRelicProgressionTraceCsv(TargetSet set)
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-RelicTrace-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var selectedJob = config.SelectedRelicJob ?? string.Empty;

            var csv = new StringBuilder();
            csv.AppendLine("Set ID,Set Name,Selected Job,Weapon Type,Stage,Item ID,Item Name,Quest Name,Quest Row IDs,Raw Quest State,Inferred Stage Unlocked,Stage Frontier,Computed Route,Resolver Primary Source,Resolver Primary Detail");

            var members = set.ItemIds
                .Select(catalog.GetById)
                .Where(x => x is not null)
                .Cast<TargetEntry>()
                .Where(x => string.IsNullOrWhiteSpace(selectedJob) || x.EquipJobs.Contains(selectedJob))
                .ToArray();

            if (set.Id.Equals("relic-phantom", StringComparison.OrdinalIgnoreCase))
            {
                var progress = GetPhantomProgress();
                foreach (var stageState in progress.StageStates)
                {
                    var stage = stageState.Stage;
                    var stageMembers = members.Where(x => x.Name.EndsWith(" " + stage.Suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var member in stageMembers)
                    {
                        var primary = resolver.Resolve(member).Sources.FirstOrDefault();
                        for (var i = 0; i < stage.QuestChain.Count; i++)
                        {
                            var questName = stage.QuestChain[i];
                            questRowIdsByName.TryGetValue(questName, out var ids);
                            var rawState = GetQuestState(questName);
                            var route = stageState.Unlocked ? stage.RepeatRoute :
                                stageState.IsFrontier ? $"Frontier: {stageState.ActiveQuest}{(string.IsNullOrWhiteSpace(stageState.ActiveQuest) ? stageState.NextQuest : string.Empty)}" :
                                "Locked by earlier one-time Phantom progression";
                            csv.Append(Csv(set.Id)); csv.Append(',');
                            csv.Append(Csv(set.Name)); csv.Append(',');
                            csv.Append(Csv(selectedJob)); csv.Append(',');
                            csv.Append(Csv(RelicWeaponType(selectedJob))); csv.Append(',');
                            csv.Append(Csv(stage.Label)); csv.Append(',');
                            csv.Append(Csv(member.ItemId.ToString())); csv.Append(',');
                            csv.Append(Csv(member.Name)); csv.Append(',');
                            csv.Append(Csv(questName)); csv.Append(',');
                            csv.Append(Csv(ids is null ? string.Empty : string.Join("|", ids))); csv.Append(',');
                            csv.Append(Csv(rawState.ToString())); csv.Append(',');
                            csv.Append(Csv(stageState.Unlocked ? "Yes" : "No")); csv.Append(',');
                            csv.Append(Csv(stageState.IsFrontier ? "Yes" : "No")); csv.Append(',');
                            csv.Append(Csv(route)); csv.Append(',');
                            csv.Append(Csv(primary?.Name ?? string.Empty)); csv.Append(',');
                            csv.Append(Csv(primary?.Detail ?? string.Empty));
                            csv.AppendLine();
                        }
                    }
                }
            }
            else
            {
                foreach (var member in members)
                {
                    var primary = resolver.Resolve(member).Sources.FirstOrDefault();
                    csv.Append(Csv(set.Id)); csv.Append(',');
                    csv.Append(Csv(set.Name)); csv.Append(',');
                    csv.Append(Csv(selectedJob)); csv.Append(',');
                    csv.Append(Csv(RelicWeaponType(selectedJob))); csv.Append(',');
                    csv.Append(Csv(string.Empty)); csv.Append(',');
                    csv.Append(Csv(member.ItemId.ToString())); csv.Append(',');
                    csv.Append(Csv(member.Name)); csv.Append(",,,,,");
                    csv.Append(Csv(primary?.Name ?? string.Empty)); csv.Append(',');
                    csv.Append(Csv(primary?.Detail ?? string.Empty));
                    csv.AppendLine();
                }
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            exportStatus = $"Exported relic progression trace to:\n{path}";
            Log.Information($"Exported CQG relic progression trace to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Relic trace export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG relic progression trace.");
        }
    }

    private void ExportUnresolvedCsv()
    {
        try
        {
            var unresolved = catalog.Targets
                .Where(t => !resolver.HasDirectSource(t))
                .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.ItemId)
                .ToArray();

            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-Unresolved-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";

            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();

            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Equip Jobs,Tradable,Resolution Status");

            foreach (var target in unresolved)
            {
                var jobs = target.EquipJobs.Count == 0
                    ? string.Empty
                    : string.Join(
                        "; ",
                        target.EquipJobs.OrderBy(
                            x => x,
                            StringComparer.OrdinalIgnoreCase));

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(jobs));
                csv.Append(',');
                csv.Append(Csv(target.IsUntradable ? "No" : "Yes"));
                csv.Append(',');
                csv.Append(Csv("No direct source resolved"));
                csv.AppendLine();
            }

            // Emit a UTF-8 BOM so Excel opens localized item names cleanly.
            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus =
                $"Exported {unresolved.Length:N0} unresolved item(s) to:\\n{path}";

            Log.Information(
                $"Exported {unresolved.Length} unresolved CQG targets to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Export failed: {ex.Message}";
            Log.Error(ex, "Failed to export unresolved CQG targets.");
        }
    }


    private void ExportSpecificityBacklogCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-SpecificityBacklog-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Resolution Tier,Source Specificity,Primary Source Kind,Primary Source,Primary Source Detail,Garland Enrichment Eligible,Known Source Count");

            foreach (var target in catalog.Targets
                         .Where(resolver.NeedsSpecificityEnrichment)
                         .OrderBy(t => resolver.GetSourceSpecificity(t), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (result.Guide is not null ? result.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(result.Sources.Count.ToString()));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported specificity backlog to:\n{path}";
            Log.Information($"Exported CQG specificity backlog to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Specificity backlog export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG specificity backlog.");
        }
    }


    private void ExportExactSpecificityBacklogCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-ExactSourceBacklog-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Resolution Tier,Source Specificity,Primary Source Kind,Primary Source,Primary Source Detail,Garland Enrichment Eligible,Known Source Count");

            foreach (var target in catalog.Targets
                         .Where(resolver.NeedsExactItemSpecificity)
                         .OrderBy(t => resolver.GetSourceSpecificity(t), StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();

                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (result.Guide is not null ? result.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(result.Sources.Count.ToString()));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported exact-source backlog to:\n{path}";
            Log.Information($"Exported CQG exact-source backlog to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Exact-source backlog export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG exact-source backlog.");
        }
    }

    private void ExportResolverAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);

            var fileName =
                $"CustomQuestGuide-ResolverAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);

            var csv = new StringBuilder();
            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Owned,Ownership Source,Resolution Tier,Source Specificity,Availability Class,Direct Source Known,Curated Fallback Only,Primary Source Kind,Primary Source,Primary Source Detail,Next Action Label,Next Action,Next Action NPC/Location,Needs Specificity Enrichment,Needs Exact Item Specificity,Garland Enrichment Eligible,Known Source Count,Contradiction Check");

            foreach (var target in catalog.Targets
                         .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                csv.Append(Csv(target.ItemId.ToString()));
                csv.Append(',');
                csv.Append(Csv(target.Name));
                csv.Append(',');
                csv.Append(Csv(target.Category));
                csv.Append(',');
                csv.Append(Csv(target.GameUiCategory));
                csv.Append(',');
                csv.Append(Csv(target.FilterGroup.ToString()));
                csv.Append(',');
                var auditResult = resolver.Resolve(target);
                var primary = auditResult.Sources.FirstOrDefault();
                var ownedStatus = ownership.GetItemStatus(target.ItemId);
                var action = actionGuidance.Resolve(auditResult);
                var directSourceKnown = resolver.HasDirectSource(target);
                var availability = resolver.GetAvailabilityState(target);
                var contradiction = DescribeResolverContradiction(
                    auditResult, action, ownedStatus, directSourceKnown, availability);

                csv.Append(Csv(ownedStatus.IsOwned ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(ownedStatus.Source));
                csv.Append(',');
                csv.Append(Csv(resolver.GetResolutionTier(target)));
                csv.Append(',');
                csv.Append(Csv(resolver.GetSourceSpecificity(target)));
                csv.Append(',');
                csv.Append(Csv(availability));
                csv.Append(',');
                csv.Append(Csv(directSourceKnown ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(
                    resolver.IsCuratedFallbackOnly(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(primary?.Kind ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(primary?.Name ??
                    (auditResult.Guide is not null ? auditResult.Guide.Name : string.Empty)));
                csv.Append(',');
                csv.Append(Csv(primary?.Detail ?? string.Empty));
                csv.Append(',');
                csv.Append(Csv(action.ActionLabel));
                csv.Append(',');
                csv.Append(Csv(action.ActionText));
                csv.Append(',');
                csv.Append(Csv(!string.IsNullOrWhiteSpace(action.GoTo) ? action.GoTo : action.QuestStartedBy));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsSpecificityEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsExactItemSpecificity(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No"));
                csv.Append(',');
                csv.Append(Csv(auditResult.Sources.Count.ToString()));
                csv.Append(',');
                csv.Append(Csv(contradiction));
                csv.AppendLine();
            }

            File.WriteAllText(
                path,
                csv.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            exportStatus = $"Exported resolver audit to:\\n{path}";
            Log.Information($"Exported CQG resolver audit to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Resolver audit export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG resolver audit.");
        }
    }

    private void OutputTargetInformation(
        AcquisitionResult? selectedResult,
        TargetSet? selectedSet)
    {
        try
        {
            if (selectedResult is null && selectedSet is null)
            {
                exportStatus = "No item or set target is currently selected.";
                return;
            }

            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, $"CustomQuestGuide-TargetInformation-{stamp}.txt");
            var output = new StringBuilder();
            output.AppendLine("Custom Quest Guide — selected target information");
            output.AppendLine("READ-ONLY SNAPSHOT: this export does not start network requests.");
            output.AppendLine("Cached community data is included when already present.");
            output.AppendLine();

            if (selectedSet is not null)
            {
                output.AppendLine("TARGET TYPE: Set");
                output.AppendLine($"Set ID: {selectedSet.Id}");
                output.AppendLine($"Group: {selectedSet.Group}");
                output.AppendLine($"Name: {selectedSet.Name}");
                output.AppendLine($"Description: {selectedSet.Description}");
                output.AppendLine($"Members: {selectedSet.ItemIds.Count}");
                output.AppendLine($"Completion reward IDs: {string.Join(", ", selectedSet.CompletionRewardItemIds)}");
                output.AppendLine();

                foreach (var itemId in selectedSet.ItemIds)
                {
                    var target = catalog.GetById(itemId);
                    if (target is null)
                    {
                        output.AppendLine($"[{itemId}] <catalog row missing>");
                        continue;
                    }

                    output.AppendLine(new string('-', 80));
                    AppendTargetInformation(output, target, selectedSet.Id);
                }
            }
            else if (selectedResult is not null)
            {
                output.AppendLine("TARGET TYPE: Item");
                AppendTargetInformation(output, selectedResult.Target, FindContainingSet(selectedResult.Target.ItemId)?.Id);
            }

            File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            exportStatus = $"Output target information to:\n{path}";
            Log.Information($"Output CQG target information to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Target information output failed: {ex.Message}";
            Log.Error(ex, "Failed to output CQG target information.");
        }
    }

    private void AppendTargetInformation(StringBuilder output, TargetEntry target, string? containingSetId)
    {
        // IMPORTANT: ResolveDiagnostic() consumes only local/live data plus caches.
        // It intentionally includes internal resolver relationships because this
        // export is a Debug diagnostic; player-facing UI uses Resolve(), which
        // filters those records out. The cached-only action/duty methods below
        // explicitly suppress every EnsureLoaded/EnsureRequested network path.
        var result = resolver.ResolveDiagnostic(target);
        var primary = result.Sources.FirstOrDefault();
        var owned = ownership.GetItemStatus(target.ItemId);
        var action = actionGuidance.Resolve(result, allowNetworkRequests: false);
        var availability = resolver.GetAvailabilityState(target);
        var direct = resolver.HasDirectSource(target);
        var contradiction = DescribeResolverContradiction(result, action, owned, direct, availability);
        var collectCached = collectibleSources.GetSources(target.ItemId);
        var garlandCached = garlandSources.GetSources(target.ItemId);
        var dependencies = resolver.GetExchangeDependencies(target.ItemId);

        output.AppendLine($"Item ID: {target.ItemId}");
        output.AppendLine($"Name: {target.Name}");
        output.AppendLine($"Category: {target.Category}");
        output.AppendLine($"Game UI Category: {target.GameUiCategory}");
        output.AppendLine($"Filter Group: {target.FilterGroup}");
        output.AppendLine($"Equip Jobs: {(target.EquipJobs.Count == 0 ? "<none>" : string.Join(", ", target.EquipJobs.OrderBy(x => x)))}");
        output.AppendLine($"Untradable: {target.IsUntradable}");
        output.AppendLine($"Owned: {owned.IsOwned}");
        output.AppendLine($"Ownership Source: {owned.Source}");
        output.AppendLine($"Containing Set: {containingSetId ?? "<none>"}");
        output.AppendLine();

        output.AppendLine("RESOLUTION");
        output.AppendLine($"Resolution Tier: {resolver.GetResolutionTier(target)}");
        output.AppendLine($"Source Specificity: {resolver.GetSourceSpecificity(target)}");
        output.AppendLine($"Availability: {availability}");
        output.AppendLine($"Direct Source Known: {direct}");
        output.AppendLine($"Curated Fallback Only: {resolver.IsCuratedFallbackOnly(target)}");
        output.AppendLine($"Needs Specificity Enrichment: {resolver.NeedsSpecificityEnrichment(target)}");
        output.AppendLine($"Needs Exact Item Specificity: {resolver.NeedsExactItemSpecificity(target)}");
        output.AppendLine($"Garland Enrichment Eligible: {resolver.NeedsGarlandEnrichment(target)}");
        output.AppendLine($"Contradiction Check: {(string.IsNullOrWhiteSpace(contradiction) ? "<none>" : contradiction)}");
        output.AppendLine();

        output.AppendLine("CURRENT PRIMARY / ACTION");
        output.AppendLine($"Primary: {(primary is null ? "<none>" : FormatSourceForDiagnostic(primary))}");
        output.AppendLine($"Action Label: {action.ActionLabel}");
        output.AppendLine($"Action Text: {action.ActionText}");
        output.AppendLine($"Action GoTo: {action.GoTo}");
        output.AppendLine($"Action QuestStartedBy: {action.QuestStartedBy}");
        output.AppendLine($"Action Source: {(action.Source is null ? "<none>" : FormatSourceForDiagnostic(action.Source))}");
        output.AppendLine();

        output.AppendLine($"ALL CURRENT RESOLVED SOURCES ({result.Sources.Count})");
        if (result.Sources.Count == 0)
            output.AppendLine("  <none>");
        for (var i = 0; i < result.Sources.Count; i++)
            output.AppendLine($"  [{i}] {FormatSourceForDiagnostic(result.Sources[i])}");
        output.AppendLine();

        output.AppendLine("CACHED COMMUNITY DATA");
        output.AppendLine($"FFXIV Collect cached source count: {collectCached.Count}");
        foreach (var source in collectCached)
            output.AppendLine($"  Collect: {FormatSourceForDiagnostic(source)}");
        output.AppendLine($"Garland item checked/cached: {garlandSources.IsChecked(target.ItemId)}");
        output.AppendLine($"Garland item cached miss: {garlandSources.IsCachedMiss(target.ItemId)}");
        output.AppendLine($"Garland item request pending: {garlandSources.IsRequestInFlight(target.ItemId)}");
        output.AppendLine($"Garland monster enrichment pending: {garlandSources.IsMonsterEnrichmentInFlight(target.ItemId)}");
        output.AppendLine($"Global monster cache entries: {garlandSources.CachedMonsterCount}");
        output.AppendLine($"Global monster cache file present: {garlandSources.MonsterCacheFilePresent}");
        output.AppendLine($"Garland cached source count: {garlandCached.Count}");
        foreach (var source in garlandCached)
            output.AppendLine($"  Garland: {FormatSourceForDiagnostic(source)}");
        if (resolver.NeedsGarlandEnrichment(target) && !garlandSources.IsChecked(target.ItemId))
            output.AppendLine("  Network status: missing network data (eligible item has no cached Garland lookup result)");
        output.AppendLine();

        output.AppendLine($"EXCHANGE DEPENDENCIES ({dependencies.Count})");
        if (dependencies.Count == 0)
            output.AppendLine("  <none>");
        foreach (var dependency in dependencies)
        {
            output.AppendLine(
                $"  CostItemId={dependency.CostItemId}; CostItem={dependency.CostItemName}; Amount={dependency.Amount}; " +
                $"Shop={dependency.ShopName}; NPCs={(dependency.NpcNames.Count == 0 ? "<none>" : string.Join(" | ", dependency.NpcNames))}");

            var costSources = resolver.GetKnownSourcesForItemId(dependency.CostItemId);
            output.AppendLine($"    Cost item current sources ({costSources.Count}):");
            foreach (var source in costSources)
                output.AppendLine($"      {FormatSourceForDiagnostic(source)}");

            var costTarget = catalog.GetById(dependency.CostItemId);
            if (costTarget is not null &&
                resolver.NeedsGarlandEnrichment(costTarget) &&
                !garlandSources.IsChecked(costTarget.ItemId))
            {
                output.AppendLine("      Network status: missing network data (cost item is eligible for uncached Garland enrichment)");
            }
            else if (costSources.Count == 0 && !garlandSources.IsChecked(dependency.CostItemId))
            {
                output.AppendLine("      Network status: missing network data (dependency has no cached exact source)");
            }
        }
        output.AppendLine();

        output.AppendLine("SUPPLEMENTAL RAW RELATIONSHIPS");
        if (supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs) && dutyRefs.Count > 0)
        {
            output.AppendLine($"  Duty references ({dutyRefs.Count}): {string.Join(" | ", dutyRefs.Select(x => $"{x.Kind}:{x.Id}"))}");
        }
        else
        {
            output.AppendLine("  Duty references: <none>");
        }

        if (supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalRows) && supplementalRows.Count > 0)
        {
            output.AppendLine($"  Supplemental source rows ({supplementalRows.Count}):");
            foreach (var row in supplementalRows)
            {
                var fields = string.Join("; ", row.Row.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"));
                output.AppendLine($"    Dataset={row.Dataset}; Kind={row.Kind}; {fields}");
            }
        }
        else
        {
            output.AppendLine("  Supplemental source rows: <none>");
        }
        output.AppendLine();

        var resolvedDuties = result.Sources
            .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supplementalDutyCandidates = resolver.GetSupplementalDutyCandidateNames(target.ItemId);
        var duties = resolvedDuties
            .Concat(supplementalDutyCandidates)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        output.AppendLine($"DUTY CANDIDATES ({duties.Length})");
        if (duties.Length == 0)
            output.AppendLine("  <none>");
        foreach (var duty in duties)
        {
            var provenance = resolvedDuties.Contains(duty, StringComparer.OrdinalIgnoreCase)
                ? "resolved/current"
                : "supplemental candidate (suppressed from current answer)";
            output.AppendLine($"  [{provenance}]");
            AppendDutyDiagnostic(output, duty, containingSetId, "    ");
        }

        if (result.Guide is not null)
        {
            output.AppendLine();
            output.AppendLine($"CURATED GUIDE: {result.Guide.Name}");
            foreach (var chain in result.Guide.Chains)
            {
                output.AppendLine($"  Chain: {chain.Name} — {chain.Description}");
                foreach (var step in chain.Steps)
                    output.AppendLine($"    {step.QuestName} | {step.QuestGiver} | {step.Zone} ({step.X:F1},{step.Y:F1}) | {step.Note}");
            }
        }
    }

    private void AppendDutyDiagnostic(StringBuilder output, string dutyName, string? setId, string indent)
    {
        var cfcId = dutyUnlocks.GetContentFinderConditionId(dutyName);
        var instanceId = dutyUnlocks.GetInstanceContentId(dutyName);
        var advice = string.IsNullOrWhiteSpace(setId)
            ? dutyUnlocks.GetAdviceCachedOnly(dutyName)
            : dutyUnlocks.GetAdviceCachedOnly(setId, dutyName);
        var communityQuest = communityDutyUnlocks.GetCachedQuestId(dutyName);
        var garlandQuest = instanceId == 0 ? null : garlandDutyUnlocks.GetCachedQuestId(instanceId);
        var garlandChecked = instanceId != 0 && garlandDutyUnlocks.IsChecked(instanceId);

        output.AppendLine($"{indent}{dutyName}");
        output.AppendLine($"{indent}  ContentFinderConditionId: {cfcId}");
        output.AppendLine($"{indent}  InstanceContentId: {instanceId}");
        output.AppendLine($"{indent}  Unlock snapshot: {dutyUnlocks.IsDutyUnlocked(dutyName)?.ToString() ?? "unknown"}");
        output.AppendLine($"{indent}  Cached community quest ID: {(communityQuest?.ToString() ?? "<none>")}");
        output.AppendLine($"{indent}  Cached Garland checked: {garlandChecked}");
        output.AppendLine($"{indent}  Cached Garland quest ID: {(garlandQuest?.ToString() ?? "<none>")}");
        if (advice is null)
        {
            output.AppendLine($"{indent}  Cached/local advice: <none>");
            return;
        }

        output.AppendLine($"{indent}  Advice quest: {advice.QuestName}");
        output.AppendLine($"{indent}  Advice contact: {advice.Contact}");
        output.AppendLine($"{indent}  Advice location: {advice.Location}");
        output.AppendLine($"{indent}  Advice note: {advice.Note}");
    }

    private static string FormatSourceForDiagnostic(SourceEntry source)
    {
        var locations = source.MonsterLocations ?? Array.Empty<MonsterSpawnLocation>();
        var exactLocationCount = locations.Count(location => !location.IsFallback);
        var fallbackLocationCount = locations.Count - exactLocationCount;

        return
            $"Kind={source.Kind}; Name={source.Name}; Detail={source.Detail ?? string.Empty}; " +
            $"Unlocked={(source.IsUnlocked.HasValue ? source.IsUnlocked.Value.ToString() : "unknown")}; " +
            $"Priority={source.Priority}; SpecificityHint={source.SpecificityHint ?? string.Empty}; " +
            $"MonsterBaseId={source.MonsterBaseId?.ToString() ?? string.Empty}; " +
            $"MonsterNameId={source.MonsterNameId?.ToString() ?? string.Empty}; " +
            $"MonsterLevel={source.MonsterLevel?.ToString() ?? string.Empty}; " +
            $"MonsterZoneLevel={source.MonsterZoneLevel?.ToString() ?? string.Empty}; " +
            $"EnrichmentKey={source.EnrichmentKey ?? string.Empty}; " +
            $"MonsterLocations={locations.Count} (exact={exactLocationCount}, fallback={fallbackLocationCount})";
    }

    private void ExportAmbiguityAuditCsv()
    {
        try
        {
            var directory = PluginInterface.GetPluginConfigDirectory();
            Directory.CreateDirectory(directory);
            var fileName = $"CustomQuestGuide-AmbiguityAudit-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";
            var path = Path.Combine(directory, fileName);
            var csv = new StringBuilder();

            csv.AppendLine(
                "Item ID,Name,Category,Game UI Category,Filter Group,Equip Jobs,Owned,Ownership Source," +
                "Ambiguity Reasons,Missing Network Data,Resolution Tier,Source Specificity,Availability," +
                "Direct Source Known,Curated Fallback Only,Needs Specificity Enrichment,Needs Exact Item Specificity," +
                "Garland Enrichment Eligible,Garland Cached Checked,Garland Cached Miss," +
                "Primary Source,Current Action Label,Current Action Text,Current Action Location," +
                "All Current Sources,Cached FFXIV Collect Sources,Cached Garland Sources," +
                "Exchange Dependencies,Supplemental Duty References,Supplemental Duty Candidate Names,Supplemental Raw Rows,Duty Diagnostics,Contradiction Check");

            var exported = 0;
            foreach (var target in catalog.Targets
                         .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var result = resolver.Resolve(target);
                var primary = result.Sources.FirstOrDefault();
                var owned = ownership.GetItemStatus(target.ItemId);
                var action = actionGuidance.Resolve(result, allowNetworkRequests: false);
                var availability = resolver.GetAvailabilityState(target);
                var direct = resolver.HasDirectSource(target);
                var contradiction = DescribeResolverContradiction(result, action, owned, direct, availability);
                var dependencies = resolver.GetExchangeDependencies(target.ItemId);
                var reasons = new List<string>();
                var missingNetwork = new List<string>();

                // A client-data item with explicitly unknown/future availability is
                // already being described truthfully. It is not a resolver ambiguity
                // and should not pollute the actionable acquisition audit.
                if (availability.Equals("Future / Unknown", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (result.Sources.Count == 0 && result.Guide is null)
                    reasons.Add("No source resolved");
                if (resolver.NeedsSpecificityEnrichment(target))
                    reasons.Add("Source specificity incomplete");
                if (resolver.NeedsExactItemSpecificity(target))
                    reasons.Add("Exact item-specific source missing");
                if (!string.IsNullOrWhiteSpace(contradiction))
                    reasons.Add("Resolver contradiction");
                if (primary is not null &&
                    (primary.Name.Contains("exact duty unresolved", StringComparison.OrdinalIgnoreCase) ||
                     primary.Detail?.Contains("exact duty unresolved", StringComparison.OrdinalIgnoreCase) == true ||
                     primary.Detail?.Contains("exact item-specific duty", StringComparison.OrdinalIgnoreCase) == true ||
                     primary.Name.Equals("Duty Chest", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty Boss", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty Boss Chest", StringComparison.OrdinalIgnoreCase) ||
                     primary.Name.Equals("Duty / Instance Drop", StringComparison.OrdinalIgnoreCase)))
                {
                    reasons.Add("Duty family known but exact duty unresolved");
                }

                var resolvedDuties = result.Sources
                    .Select(DutyUnlockGuidanceIndex.ExtractDutyName)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var supplementalDutyCandidates = resolver.GetSupplementalDutyCandidateNames(target.ItemId);

                // Multiple exact/current duties can be legitimate for materials or
                // currencies. Flag only unresolved supplemental ambiguity, where CQG
                // lacks an authoritative item-specific duty and therefore cannot say
                // which candidate is the intended acquisition route.
                if (supplementalDutyCandidates.Count > 1 &&
                    resolvedDuties.Length == 0 &&
                    !garlandSources.HasSource(target.ItemId))
                {
                    reasons.Add($"Conflicting supplemental duty candidates ({supplementalDutyCandidates.Count})");
                }

                var dutiesForDiagnostics = resolvedDuties
                    .Concat(supplementalDutyCandidates)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var dutyDiagnostics = new List<string>();
                var containingSetId = FindContainingSet(target.ItemId)?.Id;
                foreach (var duty in dutiesForDiagnostics)
                {
                    var cfcId = dutyUnlocks.GetContentFinderConditionId(duty);
                    var instanceId = dutyUnlocks.GetInstanceContentId(duty);
                    var advice = string.IsNullOrWhiteSpace(containingSetId)
                        ? dutyUnlocks.GetAdviceCachedOnly(duty)
                        : dutyUnlocks.GetAdviceCachedOnly(containingSetId, duty);
                    var communityQuest = communityDutyUnlocks.GetCachedQuestId(duty);
                    var garlandQuest = instanceId == 0 ? null : garlandDutyUnlocks.GetCachedQuestId(instanceId);
                    var garlandChecked = instanceId != 0 && garlandDutyUnlocks.IsChecked(instanceId);
                    var note = advice?.Note ?? string.Empty;
                    var isCurrent = resolvedDuties.Contains(duty, StringComparer.OrdinalIgnoreCase);

                    if (note.Contains("Missing network data", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep suppressed candidates in the diagnostic trail, but
                        // do not classify their missing enrichment as an active
                        // problem once CQG has a different authoritative duty.
                        // An already-unlocked current duty also does not need an
                        // unlock-quest network lookup.
                        if (isCurrent && dutyUnlocks.IsDutyUnlocked(duty) != true)
                            missingNetwork.Add($"Duty {duty}: {note}");
                    }
                    else if (isCurrent &&
                             advice is not null &&
                             advice.IsUnlocked != true &&
                             string.IsNullOrWhiteSpace(advice.QuestName))
                    {
                        reasons.Add($"Locked duty has no resolved unlock quest: {duty}");
                    }

                    dutyDiagnostics.Add(
                        $"{duty} [provenance={(isCurrent ? "current" : "supplemental-candidate")}; CFC={cfcId}; Instance={instanceId}; unlocked={dutyUnlocks.IsDutyUnlocked(duty)?.ToString() ?? "unknown"}; " +
                        $"quest={advice?.QuestName ?? string.Empty}; contact={advice?.Contact ?? string.Empty}; location={advice?.Location ?? string.Empty}; " +
                        $"communityQuest={communityQuest?.ToString() ?? string.Empty}; garlandChecked={garlandChecked}; garlandQuest={garlandQuest?.ToString() ?? string.Empty}; note={note}]");
                }

                if (resolver.NeedsGarlandEnrichment(target) && !garlandSources.IsChecked(target.ItemId))
                    missingNetwork.Add("Item-specific Garland enrichment is eligible but not cached");

                if (dependencies.Count > 0)
                {
                    foreach (var dependency in dependencies)
                    {
                        // Gil is a universal terminal currency, not an acquisition
                        // dependency that needs its own resolver chain. Asking CQG to
                        // explain where Gil comes from adds noise rather than guidance.
                        if (dependency.CostItemName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var costSources = resolver.GetKnownSourcesForItemId(dependency.CostItemId);
                        if (costSources.Count == 0)
                        {
                            reasons.Add($"Exchange dependency source unresolved: {dependency.CostItemName}");
                            if (!garlandSources.IsChecked(dependency.CostItemId))
                                missingNetwork.Add($"Exchange cost item {dependency.CostItemName}: missing network data");
                        }

                        var costTarget = catalog.GetById(dependency.CostItemId);
                        if (costTarget is not null &&
                            resolver.NeedsGarlandEnrichment(costTarget) &&
                            !garlandSources.IsChecked(costTarget.ItemId))
                        {
                            missingNetwork.Add($"Exchange cost item {dependency.CostItemName} is eligible for uncached Garland enrichment");
                        }
                    }
                }

                if (IsShopDiagnostic(primary) && action.IsEmpty)
                    reasons.Add("Shop/exchange source lacks actionable vendor/location guidance");

                if (reasons.Count == 0 && missingNetwork.Count == 0)
                    continue;

                exported++;
                var collectCached = collectibleSources.GetSources(target.ItemId);
                var garlandCached = garlandSources.GetSources(target.ItemId);
                supplemental.DutyReferencesByItem.TryGetValue(target.ItemId, out var dutyRefs);
                supplemental.ItemSources.TryGetValue(target.ItemId, out var supplementalRows);

                var allSources = string.Join(" || ", result.Sources.Select(FormatSourceForDiagnostic));
                var collectSources = string.Join(" || ", collectCached.Select(FormatSourceForDiagnostic));
                var garlandSourcesText = string.Join(" || ", garlandCached.Select(FormatSourceForDiagnostic));
                var dependencyText = string.Join(" || ", dependencies.Select(x =>
                    $"CostItemId={x.CostItemId}; CostItem={x.CostItemName}; Amount={x.Amount}; Shop={x.ShopName}; NPCs={string.Join(" | ", x.NpcNames)}"));
                var dutyRefText = dutyRefs is null
                    ? string.Empty
                    : string.Join(" || ", dutyRefs.Select(x => $"{x.Kind}:{x.Id}"));
                var supplementalDutyCandidateText = string.Join(" || ", supplementalDutyCandidates);
                var supplementalText = supplementalRows is null
                    ? string.Empty
                    : string.Join(" || ", supplementalRows.Select(row =>
                        $"Dataset={row.Dataset}; Kind={row.Kind}; " +
                        string.Join("; ", row.Row.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"))));

                var fields = new[]
                {
                    target.ItemId.ToString(), target.Name, target.Category, target.GameUiCategory, target.FilterGroup.ToString(),
                    string.Join(" | ", target.EquipJobs.OrderBy(x => x)), owned.IsOwned ? "Yes" : "No", owned.Source,
                    string.Join("; ", reasons.Distinct()), missingNetwork.Count == 0 ? "No" : string.Join("; ", missingNetwork.Distinct()),
                    resolver.GetResolutionTier(target), resolver.GetSourceSpecificity(target), availability,
                    direct ? "Yes" : "No", resolver.IsCuratedFallbackOnly(target) ? "Yes" : "No",
                    resolver.NeedsSpecificityEnrichment(target) ? "Yes" : "No",
                    resolver.NeedsExactItemSpecificity(target) ? "Yes" : "No",
                    resolver.NeedsGarlandEnrichment(target) ? "Yes" : "No",
                    garlandSources.IsChecked(target.ItemId) ? "Yes" : "No",
                    garlandSources.IsCachedMiss(target.ItemId) ? "Yes" : "No",
                    primary is null ? string.Empty : FormatSourceForDiagnostic(primary),
                    action.ActionLabel, action.ActionText,
                    !string.IsNullOrWhiteSpace(action.GoTo) ? action.GoTo : action.QuestStartedBy,
                    allSources, collectSources, garlandSourcesText, dependencyText, dutyRefText, supplementalDutyCandidateText, supplementalText,
                    string.Join(" || ", dutyDiagnostics), contradiction,
                };
                csv.AppendLine(string.Join(",", fields.Select(Csv)));
            }

            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            exportStatus = $"Exported ambiguity audit ({exported:N0} entries) to:\n{path}";
            Log.Information($"Exported {exported:N0} CQG ambiguity audit entries to {path}");
        }
        catch (Exception ex)
        {
            exportStatus = $"Ambiguity audit export failed: {ex.Message}";
            Log.Error(ex, "Failed to export CQG ambiguity audit.");
        }
    }

    private static bool IsShopDiagnostic(SourceEntry? source)
    {
        if (source is null)
            return false;
        return source.Kind.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
               source.Kind.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
               source.Name.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
               source.Name.Contains("Vendor", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeResolverContradiction(
        AcquisitionResult result,
        SourceActionGuidance action,
        OwnershipStatus ownedStatus,
        bool directSourceKnown,
        string availability)
    {
        var issues = new List<string>();

        if (directSourceKnown && result.Sources.Count == 0 && result.Guide is null)
            issues.Add("Direct source flag without a source/guide");
        var hasMeaningfulResolvedSource = result.Sources.Any(source =>
            !source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase));
        if (!directSourceKnown && (hasMeaningfulResolvedSource || result.Guide is not null))
            issues.Add("Actionable source/guide exists while direct-source flag is false");
        if (!action.IsEmpty && action.Source is null)
            issues.Add("Next action has no source provenance");
        if (action.Source is not null && !result.Sources.Contains(action.Source))
            issues.Add("Next action source is absent from resolved sources");
        if (availability.Equals("Legacy / Existing Copies", StringComparison.OrdinalIgnoreCase) &&
            !action.IsEmpty &&
            (action.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase) ||
             action.ActionLabel.Equals("Unlock content", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add("Legacy-only item has live duty action");
        }

        // Owned targets may still show acquisition provenance in Detailed mode;
        // that is informational and intentionally is not treated as a contradiction.
        _ = ownedStatus;

        return string.Join("; ", issues);
    }

    private IReadOnlyList<TargetEntry> GetGlobalSearchResults(string query)
    {
        if (query.Equals(cachedSearchQuery, StringComparison.Ordinal))
            return cachedSearchResults;

        var hasItemId = uint.TryParse(query, out var queryItemId);

        cachedSearchResults = catalog.Targets
            .Where(t =>
                t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (hasItemId && t.ItemId == queryItemId))
            .OrderByDescending(t =>
                t.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                (hasItemId && t.ItemId == queryItemId))
            .ThenByDescending(t =>
                t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToArray();

        cachedSearchQuery = query;
        return cachedSearchResults;
    }

    private void RefreshCoverageMetrics(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < coverageMetricsExpiresUtc)
            return;

        cachedResolutionTiers.Clear();
        cachedSpecificity.Clear();
        cachedAvailability.Clear();
        var unresolved = 0;
        var specificityCandidates = 0;
        var exactSpecificityBacklog = 0;

        foreach (var target in catalog.Targets)
        {
            if (!resolver.HasDirectSource(target))
                unresolved++;

            var tier = resolver.GetResolutionTier(target);
            cachedResolutionTiers[tier] =
                cachedResolutionTiers.TryGetValue(tier, out var count)
                    ? count + 1
                    : 1;

            var specificity = resolver.GetSourceSpecificity(target);
            cachedSpecificity[specificity] =
                cachedSpecificity.TryGetValue(specificity, out var specificityCount)
                    ? specificityCount + 1
                    : 1;

            var availability = resolver.GetAvailabilityState(target);
            cachedAvailability[availability] =
                cachedAvailability.TryGetValue(availability, out var availabilityCount)
                    ? availabilityCount + 1
                    : 1;

            if (resolver.NeedsSpecificityEnrichment(target))
                specificityCandidates++;

            if (resolver.NeedsExactItemSpecificity(target))
                exactSpecificityBacklog++;
        }

        cachedUnresolvedCount = unresolved;
        cachedDirectCount = catalog.Targets.Count - unresolved;
        cachedSpecificityEnrichmentCandidates = specificityCandidates;
        cachedExactSpecificityBacklog = exactSpecificityBacklog;
        coverageMetricsExpiresUtc = now.AddSeconds(2);
    }

    private static string FriendlyResolutionTier(string tier) => tier switch
    {
        "Live/Game-sheet" => "Exact live game data",
        "FFXIV Collect" => "Exact collectible/community data",
        "LuminaSupplemental" => "Supplemental game-data relationship",
        "Garland" => "Item-specific community data",
        "Broad system" => "System-level acquisition source",
        "Curated fallback" => "Curated acquisition fallback",
        "Curated guide" => "Curated prerequisite guide",
        _ => tier,
    };

    private static string Csv(string value)
    {
        value ??= string.Empty;

        if (value.Contains('"'))
            value = value.Replace("\"", "\"\"");

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value}\""
            : value;
    }

}
