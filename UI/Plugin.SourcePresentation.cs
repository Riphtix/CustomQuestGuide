using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
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
    private void DrawOtherKnownSources(AcquisitionResult result)
    {
        var primary = result.Sources.FirstOrDefault();
        var primaryIsMonster = SourceClassification.IsMonster(primary);

        // When Monster Drop is the main acquisition route, every known enemy is
        // shown together in the dedicated monster list below Source. Do not count
        // those enemies again as "other sources"; that expander is for genuinely
        // different acquisition routes such as ventures, vendors, or the market.
        var primaryHasEventOccurrence = primary is not null && EventOccurrenceCatalog.TryMatch(primary, out _);
        var primaryIsCurrentGameplay = primary is not null &&
            AcquisitionRouteAvailabilityPolicy.IsCurrentGameplayRoute(result.Target, primary);
        var alternates = result.Sources
            .Skip(1)
            .Where(source => !primaryIsMonster || !SourceClassification.IsMonster(source))
            .Where(source => !primaryHasEventOccurrence ||
                (!EventSourcePolicy.IsEventDerived(source) &&
                 !OnlineStoreSourcePolicy.IsOnlineStore(source) &&
                 !source.Name.Equals("Seasonal / Collaboration Event", StringComparison.OrdinalIgnoreCase)))
            // When a permanent/current route is already primary, event rows are
            // provenance and are rendered in the Historical source block instead
            // of being counted as ordinary alternate acquisition routes.
            .Where(source => !primaryIsCurrentGameplay || !EventSourcePolicy.IsEventDerived(source))
            .ToList();

        // Levequests are one acquisition family, not N unrelated alternate
        // sources. Present them as their own collapsed group and keep the generic
        // "other known sources" count for genuinely different acquisition methods.
        var levequests = alternates
            .Where(SourceClassification.IsLevequest)
            .GroupBy(GetLevequestDisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(ParseLevequestLevel)
                .ThenBy(source => source.Priority)
                .First())
            .OrderBy(ParseLevequestLevel)
            .ThenBy(GetLevequestDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (levequests.Count > 0)
            DrawLevequestSourceGroup(result.Target.ItemId, levequests);

        var otherAlternates = alternates
            .Where(source => !SourceClassification.IsLevequest(source))
            .ToList();

        var otherCount = otherAlternates.Count;
        if (otherCount <= 0)
            return;

        // A source count should always be actionable. Keep alternate acquisition
        // routes collapsed by default so the useful repeatable primary source
        // remains prominent, but let the player expand the complete list.
        var label = $"{otherCount} other known source{(otherCount == 1 ? string.Empty : "s")}##other-sources-{result.Target.ItemId}";
        if (!ImGui.TreeNode(label))
            return;

        for (var i = 0; i < otherAlternates.Count; i++)
        {
            var source = otherAlternates[i];
            var sourceId = $"other-source-{result.Target.ItemId}-{i}";
            var duty = GetDutyFromSource(source);
            var dutyUnlocked = duty is not null && dutyUnlocks.IsDutyUnlocked(duty.Name) == true;

            ImGui.Bullet();
            ImGui.SameLine();

            if (SourceActionGuidanceResolver.TryExtractGatheringLocation(source, out var gatheringMethod, out var gatheringPlace))
            {
                ImGui.TextUnformatted($"{gatheringMethod}:");
                ImGui.SameLine();
                DrawClickableLocation(gatheringPlace, sourceId, null);
            }
            else
            {
                if (dutyUnlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                ImGui.TextUnformatted(source.Name);
                if (dutyUnlocked)
                    ImGui.PopStyleColor();
            }

            DrawMonsterLevelInline(source);

            if (!string.IsNullOrWhiteSpace(source.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— {source.Detail}");
            }

            if (source.IsUnlocked == true)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(unlocked)");
            }
            else if (source.IsUnlocked == false)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(locked)");
            }
        }

        ImGui.TreePop();
    }


    private static void DrawLevequestSourceGroup(uint itemId, IReadOnlyList<SourceEntry> levequests)
    {
        if (levequests.Count == 0)
            return;

        if (!ImGui.TreeNode($"Levequests##levequest-sources-{itemId}"))
            return;

        foreach (var source in levequests)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(GetLevequestDisplayName(source));

            var level = ParseLevequestLevel(source);
            if (level != uint.MaxValue)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— Lv. {level}");
            }
            else if (!string.IsNullOrWhiteSpace(source.Detail))
            {
                // Preserve useful non-provider detail if a source does not carry
                // the conventional Lv. N format. Internal provider provenance is
                // already removed during player-facing normalization.
                ImGui.SameLine();
                ImGui.TextDisabled($"— {source.Detail}");
            }
        }

        ImGui.TreePop();
    }

    private static string GetLevequestDisplayName(SourceEntry source)
    {
        var name = source.Name.Trim();
        return name.StartsWith("Levequest: ", StringComparison.OrdinalIgnoreCase)
            ? name["Levequest: ".Length..].Trim()
            : name;
    }

    private static uint ParseLevequestLevel(SourceEntry source)
    {
        if (string.IsNullOrWhiteSpace(source.Detail))
            return uint.MaxValue;

        var match = Regex.Match(source.Detail, @"\bLv\.?\s*(\d+)\b", RegexOptions.IgnoreCase);
        return match.Success && uint.TryParse(match.Groups[1].Value, out var level)
            ? level
            : uint.MaxValue;
    }

    private static void DrawMonsterLevelInline(SourceEntry source)
    {
        if (!SourceClassification.IsMonster(source) || !source.MonsterLevel.HasValue)
            return;

        // Match the inline level treatment used by levequest sources.
        ImGui.SameLine();
        ImGui.TextDisabled($"— Lv. {source.MonsterLevel.Value}");
    }

    private void DrawMonsterSourceList(AcquisitionResult result)
    {
        var resolvedMonsters = result.Sources
            .Where(SourceClassification.IsMonster)
            // Normal player-facing alternatives must be actionable. Legacy/stale
            // provider relationships (for example removed enemies with no current
            // location) remain available to diagnostics but do not appear as farm
            // choices simply because a provider still names them.
            .Where(MonsterResolutionPolicy.IsComplete)
            .OrderBy(source => source.MonsterLevel ?? uint.MaxValue)
            .ThenBy(source => source.MonsterZoneLevel ?? uint.MaxValue)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var monsters = resolvedMonsters.Count > 0
            ? resolvedMonsters
            : result.Sources
                .Where(SourceClassification.IsMonster)
                .OrderBy(source => source.MonsterLevel ?? uint.MaxValue)
                .ThenBy(source => source.MonsterZoneLevel ?? uint.MaxValue)
                .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .Take(1)
                .ToList();

        if (monsters.Count == 0)
            return;

        // Keep the lowest-level monster immediately actionable. Additional
        // monsters are useful alternatives, but should not bury the recommended
        // farm target in a long list (some items have dozens of valid droppers).
        DrawMonsterSourceRow(monsters[0], result.Target.ItemId, 0);

        if (monsters.Count == 1)
            return;

        var label = $"View More Monsters of Higher Level##higher-level-monsters-{result.Target.ItemId}";
        if (!ImGui.TreeNode(label))
            return;

        for (var i = 1; i < monsters.Count; i++)
            DrawMonsterSourceRow(monsters[i], result.Target.ItemId, i);

        ImGui.TreePop();
    }

    private void DrawMonsterSourceRow(SourceEntry monster, uint itemId, int monsterIndex)
    {
        var displayName = monster.Name.StartsWith("Monster: ", StringComparison.OrdinalIgnoreCase)
            ? monster.Name["Monster: ".Length..].Trim()
            : monster.Name;

        var locations = monster.MonsterLocations?
            .OrderBy(x => x.Confidence)
            .ThenBy(x => x.ZoneLevel ?? uint.MaxValue)
            .ThenBy(x => x.ZoneName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ToArray() ?? Array.Empty<MonsterSpawnLocation>();

        if (!string.IsNullOrWhiteSpace(monster.MonsterDutyName))
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(displayName);
            DrawMonsterLevelInline(monster);
            DrawMonsterDutyContext(monster, itemId, monsterIndex);
            DrawMonsterFateContext(monster);
            DrawMonsterLeveContext(monster, itemId, monsterIndex);
            return;
        }

        if (locations.Length == 0)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(displayName);
            DrawMonsterLevelInline(monster);
            DrawMonsterFateContext(monster);
            DrawMonsterLeveContext(monster, itemId, monsterIndex);
            return;
        }

        // Monster locations belong to the Monster Drop source itself. Keep the
        // representative (lowest-zone) spawn visible, then let the player expand
        // the same monster for every known coordinate. This is intentionally
        // separate from both the higher-level-monster and other-source expanders.
        var representative = locations[0];
        var nodeId = $"monster-locations-{itemId}-{monsterIndex}-{displayName}";
        var open = ImGui.TreeNode($"{displayName}##{nodeId}");
        DrawMonsterLevelInline(monster);

        ImGui.SameLine();
        ImGui.TextDisabled($"— {FormatMonsterLocation(representative)}{FormatMonsterCoordinateMarker(representative)}");
        DrawMonsterCoordinateConfidenceTooltip(representative);
        ImGui.SameLine();
        DrawMonsterLocationMapAction(representative, $"representative-{nodeId}");
        DrawMonsterFateContext(monster);
        DrawMonsterLeveContext(monster, itemId, monsterIndex);
        DrawMonsterAreaAccessGuidance(representative, nodeId);

        if (!open)
            return;

        for (var i = 0; i < locations.Length; i++)
        {
            var location = locations[i];

            // The representative location is already visible on the monster row.
            // Repeating it inside the expanded list is especially noisy when the
            // same row also carries locked-area guidance.
            if (i == 0)
                continue;

            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted($"{FormatMonsterLocation(location)}{FormatMonsterCoordinateMarker(location)}");
            DrawMonsterCoordinateConfidenceTooltip(location);
            ImGui.SameLine();
            DrawMonsterLocationMapAction(location, $"{nodeId}-{i}");
        }

        ImGui.TreePop();
    }

    private static void DrawMonsterFateContext(SourceEntry monster)
    {
        if (monster.MonsterFateNames is not { Count: > 0 })
            return;

        ImGui.Indent();
        foreach (var fateName in monster.MonsterFateNames
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.TextDisabled($"FATE: {fateName}");
        }
        ImGui.Unindent();
    }

    private static void DrawMonsterLeveContext(SourceEntry monster, uint itemId, int monsterIndex)
    {
        if (monster.MonsterLeves is not { Count: > 0 })
            return;

        var leves = monster.MonsterLeves
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.LeveId)
            .Select(group => group.First())
            .OrderBy(x => x.Level)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (leves.Length == 0)
            return;

        ImGui.Indent();
        var label = $"Levequest##monster-leves-{itemId}-{monsterIndex}";
        if (ImGui.TreeNode(label))
        {
            foreach (var leve in leves)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                var levelSuffix = leve.Level > 0 ? $" — Lv. {leve.Level}" : string.Empty;
                ImGui.TextUnformatted($"{leve.Name}{levelSuffix}");
            }

            ImGui.TreePop();
        }
        ImGui.Unindent();
    }

    private void DrawMonsterDutyContext(SourceEntry monster, uint itemId, int monsterIndex)
    {
        var dutyName = monster.MonsterDutyName;
        if (string.IsNullOrWhiteSpace(dutyName))
            return;

        ImGui.Indent();
        ImGui.TextDisabled(dutyName);

        var duty = GetDutyFromSource(new SourceEntry("Activity", dutyName));
        if (duty is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Open Duty##monster-duty-{itemId}-{monsterIndex}-{duty.ContentFinderConditionId}"))
                SelectDutyTarget(duty);
        }

        ImGui.Unindent();
    }

    private static string FormatMonsterLocation(MonsterSpawnLocation location)
    {
        var zoneLevel = location.ZoneLevel.HasValue ? $" Lv. {location.ZoneLevel.Value}" : string.Empty;
        return $"{location.ZoneName}{zoneLevel} (X: {location.X:0.0}, Y: {location.Y:0.0})";
    }


    private static string FormatMonsterCoordinateMarker(MonsterSpawnLocation location) =>
        location.Confidence == MonsterCoordinateConfidence.Unverified ? "*" : string.Empty;

    private static void DrawMonsterCoordinateConfidenceTooltip(MonsterSpawnLocation location)
    {
        if (location.Confidence == MonsterCoordinateConfidence.Unverified && ImGui.IsItemHovered())
            ImGui.SetTooltip("Unverified coordinate");
    }

    private void DrawMonsterLocationMapAction(MonsterSpawnLocation location, string id)
    {
        var lockedArea = GetLockedAreaAccessGuidance(location);
        if (lockedArea is not null)
        {
            ImGui.TextDisabled("Area locked");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Complete {lockedArea.UnlockQuestName} to access {lockedArea.AreaName}.");
            return;
        }

        if (!ImGui.SmallButton($"View Map##monster-location-{id}"))
            return;

        // MonsterSpawnLocation coordinates are normalized player-facing map X/Y.
        // Build the exact map-link payload directly from the known territory/map
        // identity instead of round-tripping the coordinates through world-space.
        // This mirrors Dalamud's public CreateMapLink(territory, map, x, y) path
        // and avoids display-name aliases for special regions such as Eureka.
        var mapId = location.MapId ?? ResolveTerritoryMapId(location.TerritoryTypeId);
        if (location.TerritoryTypeId != 0 && mapId != 0)
        {
            try
            {
                var link = SeString.CreateMapLink(
                    location.TerritoryTypeId,
                    mapId,
                    location.X,
                    location.Y);
                var payload = link.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
                if (payload is not null)
                {
                    GameGui.OpenMapWithMapLink(payload);
                    return;
                }
            }
            catch
            {
                // Fall through to the compatibility name-based path below.
            }
        }

        // Last-resort compatibility fallback for providers that lack a usable
        // territory/map identity.
        TryOpenCoordinateMapLink(location.ZoneName, location.X, location.Y);
    }


    private AreaAccessGuidance? GetLockedAreaAccessGuidance(MonsterSpawnLocation location)
    {
        var guidance = AreaAccessGuidanceCatalog.GetByZoneName(location.ZoneName);
        if (guidance is null)
            return null;

        return GetQuestState(guidance.UnlockQuestName) == QuestProgressState.Completed
            ? null
            : guidance;
    }

    private void DrawMonsterAreaAccessGuidance(MonsterSpawnLocation location, string id)
    {
        var guidance = GetLockedAreaAccessGuidance(location);
        if (guidance is null)
            return;

        ImGui.Indent();
        ImGui.TextDisabled($"Area locked — {guidance.AreaName}");

        // First resolve the explicit ordered area gates. This is what prevents a
        // late locale such as Pyros from jumping directly to its final unlock
        // quest when the player has not even entered Eureka yet.
        var gateSequence = questGuidance.ResolveSequence(
            guidance.Gates.Select(x => x.QuestName),
            GetQuestStateByRowId);

        var gateEntry = gateSequence.CurrentOrNext;
        var gate = gateEntry is null
            ? guidance.Gates.LastOrDefault()
            : guidance.Gates.FirstOrDefault(x =>
                x.QuestName.Equals(gateEntry.Name, StringComparison.OrdinalIgnoreCase));

        var gateName = gate?.QuestName ?? guidance.UnlockQuestName;
        var frontier = questGuidance.ResolvePrerequisiteFrontier(
            gateName,
            GetQuestStateByRowId);

        if (frontier.Actionable is { } actionable)
        {
            var active = frontier.ActionableState == QuestProgressState.InProgress;
            ImGui.TextUnformatted($"{(active ? "Current step" : "Next step")}: {actionable.Name}");

            if (!string.IsNullOrWhiteSpace(actionable.QuestGiver))
            {
                DrawNpcLocationLine(
                    active ? "Quest started by" : "Go to",
                    actionable.QuestGiver!,
                    actionable.Location,
                    $"area-unlock-{id}-{actionable.QuestId}",
                    actionable.IssuerNpcId);
            }
            else if (!string.IsNullOrWhiteSpace(actionable.Location))
            {
                DrawLocationOnlyLine(
                    active ? "Quest location" : "Go to",
                    actionable.Location!,
                    $"area-unlock-{id}-{actionable.QuestId}");
            }

            // Only show a gate's non-quest requirement when that gate itself is
            // actionable. If CQG has walked farther backwards to an earlier quest,
            // displaying a Pyros elemental-level requirement is noise rather than
            // useful next-step guidance.
            if (gate is not null &&
                actionable.Name.Equals(gate.QuestName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(gate.RequirementHint))
            {
                ImGui.TextWrapped($"Requirement: {gate.RequirementHint}");
            }

            if (!actionable.Name.Equals(guidance.UnlockQuestName, StringComparison.OrdinalIgnoreCase))
                ImGui.TextDisabled($"Leads to area unlock: {guidance.UnlockQuestName}");
        }
        else if (!string.IsNullOrWhiteSpace(gateName))
        {
            // Explicit registry fallback: still name the concrete gate rather than
            // emitting vague "continue the storyline" text.
            ImGui.TextUnformatted($"Next step: {gateName}");
            if (gate is not null && !string.IsNullOrWhiteSpace(gate.RequirementHint))
                ImGui.TextWrapped($"Requirement: {gate.RequirementHint}");
        }

        ImGui.Unindent();
    }

    private static uint ResolveTerritoryMapId(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
            return 0;

        try
        {
            var territory = DataManager.GetExcelSheet<TerritoryType>().GetRow(territoryTypeId);
            return ReflectionUtil.GetNestedRowId(territory, "Map");
        }
        catch
        {
            return 0;
        }
    }

    private void DrawSelectedTargetNetworkStatus(AcquisitionResult result)
    {
        var itemId = result.Target.ItemId;
        var monsterIncomplete = resolver.HasIncompleteMonsterResolution(result.Target);

        if (collectibleSources.IsTargetLoading(itemId))
        {
            ImGui.TextDisabled("Resolving community source data...");
            ImGui.Spacing();
        }

        // Cache-first revalidation must never blank or block the acquisition UI.
        // This status is deliberately informational: cached data continues to be
        // rendered below while the selected network-derived entry is checked.
        if (garlandSources.IsRequestInFlight(itemId))
        {
            ImGui.TextDisabled(monsterIncomplete
                ? "Resolving source data..."
                : "Refreshing source data...");
            ImGui.Spacing();
            return;
        }

        if (monsterIncomplete)
        {
            var monsterCandidates = resolver.GetMonsterEnrichmentCandidates(result.Target);
            if (garlandSources.HasRetryableMonsterEnrichment(itemId, monsterCandidates))
            {
                ImGui.TextDisabled("Resolving monster source data...");
                ImGui.Spacing();
                return;
            }
        }

        // If all authoritative providers were actually exhausted, preserve the
        // distinction internally but keep it out of the normal player-facing UI.
        // Detailed mode is the explicit place for diagnostics and data-quality
        // investigation.
        if (monsterIncomplete)
        {
            if (config.Detailed)
            {
                ImGui.TextDisabled("Monster source metadata unavailable from current authoritative sources.");
                ImGui.Spacing();
            }
            return;
        }

        if (!garlandSources.TryGetNoChangesNotice(itemId, out var remaining))
            return;

        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        ImGui.TextDisabled($"No new data found ({seconds} second{(seconds == 1 ? string.Empty : "s")}...)");
        ImGui.Spacing();
    }

    private void DrawTargetSummary(AcquisitionResult result)
    {
        ImGui.Spacing();
        var ownedStatus = ownership.GetItemStatus(result.Target.ItemId);
        DrawOwnershipText(result.Target.Name.ToUpperInvariant(), ownedStatus);
        ImGui.SameLine();
        DrawPinToggle("Item", result.Target.ItemId, string.Empty);
        if (ownedStatus.IsOwned)
        {
            ImGui.SameLine();
            DrawOwnershipText($"Owned — {ownedStatus.Source}", ownedStatus);
        }
        ImGui.Separator();
        ImGui.Spacing();

        DrawSelectedTargetNetworkStatus(result);

        // Presentation ordering follows currently actionable routes first. A
        // historical event can remain the provenance source without outranking a
        // permanent gameplay route that the player can actually use today.
        var knownForPresentation = resolver.GetKnownSourcesForItemId(result.Target.ItemId)
            .Concat(resolver.GetHistoricalProvenanceSourcesForItemId(result.Target.ItemId))
            .Concat(result.Sources)
            .GroupBy(source => $"{source.Kind}\u001f{source.Name}\u001f{source.Detail}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(source => source.Priority).First())
            .ToList();
        var routeSummary = AcquisitionRouteAvailabilityPolicy.Build(result.Target, knownForPresentation);

        // Online Store is a first-class acquisition family, not merely an event
        // fallback. If the selected target has no current in-game route, perform
        // the same target-scoped cached lookup used by historical event rewards.
        // This covers store-only gear pieces whose provider/game-data rows are
        // otherwise empty (for example pieces contained in a paid attire bundle).
        if (routeSummary.CurrentGameplaySource is null &&
            routeSummary.KnownOnlineStoreSource is null)
        {
            onlineStoreLookup.EnsureLookup(result.Target);
        }

        var storeStateForPresentation = onlineStoreLookup.GetState(result.Target.ItemId);
        var resolvedStoreSource = routeSummary.KnownOnlineStoreSource;
        if (resolvedStoreSource is null && storeStateForPresentation.Checked && storeStateForPresentation.IsAvailable)
        {
            resolvedStoreSource = new SourceEntry(
                "Online Store",
                "FINAL FANTASY XIV Online Store",
                string.IsNullOrWhiteSpace(storeStateForPresentation.SourceText) ||
                    storeStateForPresentation.SourceText.Equals("Online Store", StringComparison.OrdinalIgnoreCase) ||
                    storeStateForPresentation.SourceText.Equals("FINAL FANTASY XIV Online Store", StringComparison.OrdinalIgnoreCase)
                    ? "Paid optional item"
                    : storeStateForPresentation.SourceText,
                Priority: 25);
        }

        var presentationSources = result.Sources.ToList();
        if (routeSummary.CurrentGameplaySource is null &&
            presentationSources.Count == 0 &&
            resolvedStoreSource is not null)
        {
            presentationSources.Add(resolvedStoreSource);
        }
        if (routeSummary.CurrentGameplaySource is not null &&
            presentationSources.FirstOrDefault() is { } originalPrimary &&
            EventSourcePolicy.IsEventDerived(originalPrimary))
        {
            presentationSources.RemoveAll(source => SameSourceIdentity(source, routeSummary.CurrentGameplaySource));
            presentationSources.Insert(0, routeSummary.CurrentGameplaySource);
        }

        var presentationResult = result with { Sources = presentationSources };
        var primary = presentationResult.Sources.FirstOrDefault();

        if (primary is not null)
        {
            ImGui.TextUnformatted("Source:");
            ImGui.SameLine();
            var primaryDuty = GetDutyFromSource(primary);
            var primaryDutyUnlocked = primaryDuty is not null && dutyUnlocks.IsDutyUnlocked(primaryDuty.Name) == true;
            var primaryIsEventOccurrence = EventOccurrenceCatalog.TryMatch(primary, out var eventOccurrence);

            if (primaryIsEventOccurrence)
            {
                // The event occurrence is the player-facing acquisition family;
                // the provider's exact quest remains the method beneath it. This
                // prevents an ended seasonal quest from looking permanent.
                ImGui.TextUnformatted(eventOccurrence.DisplayName);

                // Keep the event occurrence as the visual parent and render the
                // concrete acquisition method beneath it. This makes the source
                // family, event status, and quest relationship readable at a
                // glance without adding an unnecessary expandable node for a
                // single method.
                ImGui.Indent();
                ImGui.TextDisabled(eventOccurrence.Classification);
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextDisabled("Quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(EventOccurrenceCatalog.GetQuestDisplayName(primary));
                ImGui.Unindent();
            }
            else if (SourceActionGuidanceResolver.TryExtractGatheringLocation(primary, out var gatheringMethod, out var gatheringPlace))
            {
                ImGui.TextUnformatted($"{gatheringMethod}:");
                ImGui.SameLine();
                DrawClickableLocation(gatheringPlace, $"source-gathering-{result.Target.ItemId}", null);
            }
            else
            {
                if (primaryDutyUnlocked)
                    ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);

                // A monster drop is one repeatable acquisition method with one
                // or more valid enemies. Keep the Source line about the method,
                // then enumerate every known enemy directly beneath it.
                ImGui.TextUnformatted(SourceClassification.IsMonster(primary) ? "Monster Drop" : primary.Name);

                if (primaryDutyUnlocked)
                    ImGui.PopStyleColor();
            }

            if (!primaryIsEventOccurrence && !SourceClassification.IsMonster(primary) && !string.IsNullOrWhiteSpace(primary.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— {primary.Detail}");
            }

            if (primary.IsUnlocked == true)
                ImGui.TextDisabled("Unlocked activity");
            else if (primary.IsUnlocked == false)
                ImGui.TextDisabled("Activity is not unlocked on this character.");

            if (SourceClassification.IsMonster(primary))
                DrawMonsterSourceList(presentationResult);

            if (result.Sources.Count > 1)
                DrawOtherKnownSources(presentationResult);
        }
        else
        {
            ImGui.TextUnformatted("Source:");
            ImGui.SameLine();
            ImGui.TextDisabled("No source resolved yet");
        }

        DrawDutyDependency(presentationResult, $"item-{result.Target.ItemId}");

        // Details belong directly under the source they explain. A crafted
        // primary source gets its recipe/components immediately below it;
        // exchange prerequisites are only relevant when the primary source is
        // actually an exchange/shop route. This prevents unrelated legacy shop
        // costs from appearing above a crafting recipe.
        if (SourceClassification.IsCrafting(primary))
            DrawCraftingRecipeControls(presentationResult.Target.ItemId);
        else if (SourceClassification.IsExchange(primary))
            DrawExchangePrerequisiteControls(presentationResult.Target.ItemId);

        var historicalEventUnavailable = primary is not null &&
            EventOccurrenceCatalog.TryMatch(primary, out var historicalEvent) &&
            historicalEvent.Availability.StartsWith("Event Ended", StringComparison.OrdinalIgnoreCase);

        if (result.Guide is null)
        {
            // Do not route the player to a seasonal quest NPC after that exact
            // event occurrence has ended. The event/quest is retained as
            // historical acquisition context, not presented as a usable action.
            if (!historicalEventUnavailable)
                DrawSourceActionGuidance(presentationResult);
        }
        else
        {
            // Guide-backed targets can still have a direct vendor/NPC acquisition
            // source. Do not suppress a useful map action merely because a guide
            // also exists for the target. Only surface the direct NPC route here;
            // quest-step guidance remains owned by the guide UI below.
            var directGuidance = actionGuidance.Resolve(presentationResult);
            if (!historicalEventUnavailable &&
                directGuidance.ActionLabel.Equals("Go to", StringComparison.OrdinalIgnoreCase))
            {
                DrawSourceActionGuidance(presentationResult);
            }
        }

        var availability = resolver.GetAvailabilityState(result.Target);
        DrawRouteAvailability(presentationResult, availability);

        var resolutionTier = resolver.GetResolutionTier(result.Target);
        ImGui.TextDisabled($"Resolution quality: {FriendlyResolutionTier(resolutionTier)}");
        ImGui.TextDisabled($"Source specificity: {resolver.GetSourceSpecificity(result.Target)}");

        if (result.Guide is not null)
        {
            var status = BuildGuideStatus(result.Guide);

            ImGui.Spacing();

            if (status.NextStep is null)
            {
                ImGui.TextUnformatted("Current Step:");
                ImGui.SameLine();
                ImGui.TextUnformatted("Complete");

                ImGui.TextUnformatted("Remaining Quests:");
                ImGui.SameLine();
                ImGui.TextUnformatted("0");
            }
            else
            {
                ImGui.TextUnformatted(status.IsActive ? "Current quest:" : "Next quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(status.NextStep.QuestName);

                ImGui.TextUnformatted("Remaining Quests:");
                ImGui.SameLine();
                ImGui.TextUnformatted(status.Remaining.ToString());

                DrawNpcLocationLine(
                    status.IsActive ? "Quest started by" : "Go to",
                    status.NextStep.QuestGiver,
                    $"{status.NextStep.Zone} (X:{status.NextStep.X:0.0}, Y:{status.NextStep.Y:0.0})",
                    $"guide-summary-{result.Target.ItemId}");
            }
        }
    }

    private static bool SameSourceIdentity(SourceEntry left, SourceEntry right) =>
        left.Kind.Equals(right.Kind, StringComparison.OrdinalIgnoreCase) &&
        left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Detail ?? string.Empty, right.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void DrawRouteAvailability(AcquisitionResult result, string fallbackAvailability)
    {
        // Route-level availability prevents an ended historical event from
        // incorrectly making the item itself look unobtainable when a permanent
        // gameplay route (crafting, vendor, Market Board, duty, etc.) still exists.
        var allKnownSources = resolver.GetKnownSourcesForItemId(result.Target.ItemId)
            .Concat(resolver.GetHistoricalProvenanceSourcesForItemId(result.Target.ItemId))
            .Concat(result.Sources)
            .GroupBy(source => $"{source.Kind}\u001f{source.Name}\u001f{source.Detail}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(source => source.Priority).First())
            .ToList();

        var routes = AcquisitionRouteAvailabilityPolicy.Build(result.Target, allKnownSources);
        var hasHistoricalContext = routes.HistoricalEventSource is not null;
        var displayedPrimaryIsHistoricalEvent = result.Sources.FirstOrDefault() is { } displayedPrimary &&
            EventSourcePolicy.IsEventDerived(displayedPrimary);

        if (routes.CurrentGameplaySource is not null)
        {
            if (routes.GameplayIsConditional)
            {
                ImGui.TextUnformatted(
                    $"Gameplay: Conditional — {AcquisitionRouteAvailabilityPolicy.DescribeGameplayRoute(routes.CurrentGameplaySource)}");
                ImGui.Indent();
                ImGui.TextDisabled("Requires the character to meet the source's prior-event/eligibility conditions.");
                ImGui.Unindent();
            }
            else if (hasHistoricalContext)
            {
                ImGui.TextUnformatted(
                    $"Gameplay: Available — {AcquisitionRouteAvailabilityPolicy.DescribeGameplayRoute(routes.CurrentGameplaySource)}");
            }
            else
            {
                // Ordinary permanent items keep the compact availability line;
                // route-level detail is only expanded when it resolves a genuine
                // event/legacy ambiguity.
                ImGui.TextDisabled($"Availability: {fallbackAvailability}");
            }

            if (hasHistoricalContext &&
                !EventSourcePolicy.IsEventDerived(routes.CurrentGameplaySource))
            {
                DrawHistoricalEventProvenance(routes);
            }

            // If gameplay is currently available, the paid store route is not
            // needed to answer availability and no network lookup is scheduled.
            return;
        }

        if (hasHistoricalContext)
        {
            var occurrenceEnded = routes.HistoricalOccurrence is not null &&
                routes.HistoricalOccurrence.Availability.StartsWith("Event Ended", StringComparison.OrdinalIgnoreCase);

            if (occurrenceEnded ||
                fallbackAvailability.Contains("Currently Unobtainable", StringComparison.OrdinalIgnoreCase) ||
                fallbackAvailability.StartsWith("Event Ended", StringComparison.OrdinalIgnoreCase) ||
                EventSourcePolicy.IsLegacyEvent(routes.HistoricalEventSource!))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, AvailabilityWarning);
                ImGui.TextUnformatted("Gameplay: Unavailable — no current in-game acquisition route found");
                ImGui.PopStyleColor();

                if (!displayedPrimaryIsHistoricalEvent)
                    DrawHistoricalEventProvenance(routes);
                DrawOnlineStoreAvailability(result.Target, routes.KnownOnlineStoreSource);
                return;
            }

            // Recurring/collaboration event context is intentionally not called
            // permanently unobtainable without a live calendar check.
            ImGui.TextDisabled($"Gameplay: {fallbackAvailability}");
            if (!displayedPrimaryIsHistoricalEvent)
                DrawHistoricalEventProvenance(routes);
            return;
        }

        // No current gameplay route and no historical event context. This can be
        // a pure paid-store item, so do not trust a generic resolver availability
        // string such as "Available / Permanent" until the store family has been
        // considered. The lookup is target-scoped and cached.
        var storeState = onlineStoreLookup.GetState(result.Target.ItemId);
        if (routes.KnownOnlineStoreSource is not null ||
            (storeState.Checked && storeState.IsAvailable))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, AvailabilityWarning);
            ImGui.TextUnformatted("Gameplay: Unavailable — no current in-game acquisition route found");
            ImGui.PopStyleColor();
            DrawOnlineStoreAvailability(result.Target, routes.KnownOnlineStoreSource);
            return;
        }

        if (storeState.IsLoading)
        {
            // A missing gameplay source may still be a resolver/data gap, so do
            // not call the item gameplay-unobtainable until the paid-store route
            // is positively identified. The lookup itself remains visible.
            ImGui.TextDisabled("Online Store: Checking...");
            return;
        }

        if (storeState.Checked && !storeState.IsAvailable &&
            (fallbackAvailability.Contains("Currently Unobtainable", StringComparison.OrdinalIgnoreCase) ||
             fallbackAvailability.StartsWith("Event Ended", StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, AvailabilityWarning);
            ImGui.TextUnformatted($"Availability: {fallbackAvailability}");
            ImGui.PopStyleColor();
        }
        else
        {
            // A negative store lookup is not proof that the target has no
            // gameplay source; preserve the resolver's ordinary availability
            // semantics rather than turning a data gap into an acquisition claim.
            ImGui.TextDisabled($"Availability: {fallbackAvailability}");
        }
    }

    private static void DrawHistoricalEventProvenance(AcquisitionRouteAvailabilityPolicy.RouteSummary routes)
    {
        if (routes.HistoricalEventSource is null)
            return;

        ImGui.TextDisabled("Historical source:");
        ImGui.Indent();

        if (routes.HistoricalOccurrence is not null)
        {
            ImGui.TextUnformatted(routes.HistoricalOccurrence.DisplayName);
            ImGui.Indent();
            ImGui.TextDisabled(routes.HistoricalOccurrence.Classification);
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextDisabled("Quest:");
            ImGui.SameLine();
            ImGui.TextUnformatted(EventOccurrenceCatalog.GetQuestDisplayName(routes.HistoricalEventSource));
            ImGui.Unindent();
        }
        else
        {
            var normalized = EventSourcePolicy.Normalize(routes.HistoricalEventSource);
            ImGui.TextUnformatted(normalized.Name);
            if (!string.IsNullOrWhiteSpace(normalized.Detail))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"— {normalized.Detail}");
            }
        }

        ImGui.Unindent();
    }

    private void DrawOnlineStoreAvailability(TargetEntry target, SourceEntry? knownStoreSource)
    {
        // Provider data that already names an Online Store route is authoritative
        // enough for presentation and avoids a redundant request.
        if (knownStoreSource is not null)
        {
            ImGui.TextUnformatted("Online Store: Available (paid optional item)");
            if (!string.IsNullOrWhiteSpace(knownStoreSource.Name) &&
                !knownStoreSource.Name.Equals("Online Store", StringComparison.OrdinalIgnoreCase) &&
                !knownStoreSource.Name.Equals("FINAL FANTASY XIV Online Store", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Indent();
                ImGui.TextDisabled(knownStoreSource.Name);
                ImGui.Unindent();
            }
            return;
        }

        onlineStoreLookup.EnsureLookup(target);
        var storeState = onlineStoreLookup.GetState(target.ItemId);

        if (storeState.IsLoading)
        {
            ImGui.TextDisabled("Online Store: Checking...");
        }
        else if (storeState.Checked && storeState.IsAvailable)
        {
            ImGui.TextUnformatted("Online Store: Available (paid optional item)");
            if (!string.IsNullOrWhiteSpace(storeState.SourceText) &&
                !storeState.SourceText.Equals("Online Store", StringComparison.OrdinalIgnoreCase) &&
                !storeState.SourceText.Equals("FINAL FANTASY XIV Online Store", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Indent();
                ImGui.TextDisabled(storeState.SourceText);
                ImGui.Unindent();
            }
        }
        else if (storeState.Checked)
        {
            ImGui.TextDisabled("Online Store: No current listing found");
        }
        else
        {
            ImGui.TextDisabled("Online Store: Check pending");
        }
    }

    private void DrawExchangePrerequisiteControls(uint targetItemId)
    {
        var dependencies = resolver.GetExchangeDependencies(targetItemId);
        if (dependencies.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.TextUnformatted(dependencies.Count == 1 ? "Prerequisite item:" : "Prerequisite items:");

        for (var i = 0; i < dependencies.Count; i++)
        {
            var dependency = dependencies[i];
            if (dependency.CostItemName.Equals("Gil", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = catalog.GetById(dependency.CostItemId);
            var owned = target is null
                ? OwnershipStatus.Missing
                : ownership.GetItemStatus(dependency.CostItemId);

            if (owned.IsOwned)
                ImGui.PushStyleColor(ImGuiCol.Text, IsGlamourDresserOwnership(owned) ? GlamourPink : OwnedGreen);
            ImGui.TextUnformatted($"{dependency.Amount:N0} {dependency.CostItemName}");
            if (owned.IsOwned)
                ImGui.PopStyleColor();

            // Tomestones are system currencies, not useful "open this item"
            // prerequisites. Show the practical repeatable earning route directly
            // in the compact summary (Detailed off or on) instead of making the
            // player open the currency target to learn how to farm it.
            if (dependency.CostItemName.StartsWith("Allagan Tomestone of ", StringComparison.OrdinalIgnoreCase))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("— Earn via eligible duties, roulettes, hunts, and other level-capped content");
                continue;
            }

            if (target is null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(not selectable as a CQG target)");
                continue;
            }

            // Keep the two prerequisite actions visually identical in size.
            // Use the wider pin/unpin label as the shared width so neither
            // button changes size when the pin state changes.
            var dependencyButtonWidth =
                Math.Max(ImGui.CalcTextSize("Pin Item").X, ImGui.CalcTextSize("Unpin Item").X) +
                ImGui.GetStyle().FramePadding.X * 2.0f;
            var dependencyButtonSize = new Vector2(dependencyButtonWidth, 0);

            ImGui.SameLine();
            if (ImGui.Button($"Open##dependency-open-{targetItemId}-{dependency.CostItemId}-{i}", dependencyButtonSize))
                SelectItemTarget(target);

            ImGui.SameLine();
            DrawPinToggle(
                "Item",
                dependency.CostItemId,
                string.Empty,
                "Pin Item",
                "Unpin Item",
                buttonSize: dependencyButtonSize);
        }
    }

    private void DrawCraftingRecipeControls(uint targetItemId)
    {
        var recipes = resolver.GetCraftingRecipes(targetItemId);
        if (recipes.Count == 0)
            return;

        ImGui.Spacing();

        for (var recipeIndex = 0; recipeIndex < recipes.Count; recipeIndex++)
        {
            var recipe = recipes[recipeIndex];
            var metadata = (recipe.Job.Length > 0, recipe.Level > 0) switch
            {
                (true, true) => $" — {recipe.Job} Lv. {recipe.Level}",
                (true, false) => $" — {recipe.Job}",
                (false, true) => $" — Lv. {recipe.Level}",
                _ => string.Empty,
            };

            var label = recipes.Count == 1
                ? $"Recipe{metadata}##craft-recipe-{targetItemId}-{recipe.RecipeId}"
                : $"Recipe {recipeIndex + 1}{metadata}##craft-recipe-{targetItemId}-{recipe.RecipeId}";

            if (!ImGui.TreeNode(label))
                continue;

            if (recipe.AmountResult > 1)
                ImGui.TextDisabled($"Makes {recipe.AmountResult:N0}");

            ImGui.TextUnformatted("Ingredients:");

            for (var ingredientIndex = 0; ingredientIndex < recipe.Ingredients.Count; ingredientIndex++)
            {
                var ingredient = recipe.Ingredients[ingredientIndex];
                var target = catalog.GetById(ingredient.ItemId);
                var owned = target is null
                    ? OwnershipStatus.Missing
                    : ownership.GetItemStatus(ingredient.ItemId);

                ImGui.Bullet();
                ImGui.SameLine();
                if (owned.IsOwned)
                    ImGui.PushStyleColor(
                        ImGuiCol.Text,
                        IsGlamourDresserOwnership(owned) ? GlamourPink : OwnedGreen);
                ImGui.TextUnformatted($"{ingredient.Amount:N0} {ingredient.Name}");
                if (owned.IsOwned)
                    ImGui.PopStyleColor();

                if (target is null)
                    continue;

                var buttonWidth =
                    Math.Max(ImGui.CalcTextSize("Pin Item").X, ImGui.CalcTextSize("Unpin Item").X) +
                    ImGui.GetStyle().FramePadding.X * 2.0f;
                var buttonSize = new Vector2(buttonWidth, 0);

                ImGui.SameLine();
                if (ImGui.Button(
                        $"Open##recipe-open-{targetItemId}-{recipe.RecipeId}-{ingredient.ItemId}-{ingredientIndex}",
                        buttonSize))
                {
                    SelectCraftingIngredientTarget(targetItemId, target);
                }

                ImGui.SameLine();
                DrawPinToggle(
                    "Item",
                    ingredient.ItemId,
                    string.Empty,
                    "Pin Item",
                    "Unpin Item",
                    buttonSize: buttonSize);
            }

            ImGui.TreePop();
        }
    }

    private void DrawDetailed(AcquisitionResult result)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted($"Item ID: {result.Target.ItemId}");
        ImGui.TextUnformatted($"Category: {result.Target.Category}");

        if (!string.IsNullOrWhiteSpace(result.Target.GameUiCategory))
            ImGui.TextUnformatted($"Game UI Category: {result.Target.GameUiCategory}");

        if (TargetCatalog.IsGearCategory(result.Target.Category) &&
            result.Target.EquipJobs.Count > 0)
        {
            ImGui.TextWrapped(
                $"Equippable by: {string.Join(", ", result.Target.EquipJobs.OrderBy(x => x))}");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Known Sources:");

        if (result.Sources.Count == 0)
        {
            ImGui.TextDisabled("No trusted source provider resolved this item yet.");
        }
        else
        {
            var hasGameplaySource = result.Sources.Any(source =>
                !(source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
                  source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase)));

            foreach (var source in result.Sources)
            {
                var suffix = source.IsUnlocked switch
                {
                    true => " — Unlocked",
                    false => " — Locked",
                    null => "",
                };

                var detail = string.IsNullOrWhiteSpace(source.Detail)
                    ? ""
                    : $" — {source.Detail}";

                var duty = GetDutyFromSource(source);
                var dutyUnlocked = duty is not null && dutyUnlocks.IsDutyUnlocked(duty.Name) == true;
                if (SourceActionGuidanceResolver.TryExtractGatheringLocation(source, out var detailedMethod, out var detailedPlace))
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"{source.Kind}: {detailedMethod}:");
                    ImGui.SameLine();
                    DrawClickableLocation(detailedPlace, $"detail-gathering-{result.Target.ItemId}-{detailedPlace}", null);
                    if (!string.IsNullOrWhiteSpace(detail) || !string.IsNullOrWhiteSpace(suffix))
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled($"{detail}{suffix}");
                    }
                }
                else
                {
                    if (dutyUnlocked)
                        ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
                    var sourceLabel = hasGameplaySource &&
                                      source.Kind.Equals("Market", StringComparison.OrdinalIgnoreCase) &&
                                      source.Name.Equals("Market Board / Player Trade", StringComparison.OrdinalIgnoreCase)
                        ? "Alternative"
                        : source.Kind;

                    ImGui.BulletText(
                        $"{sourceLabel}: {source.Name}{detail}{suffix}");
                    if (dutyUnlocked)
                        ImGui.PopStyleColor();
                }
            }
        }

        if (result.Guide is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawGuide(result.Guide);
        }
    }

    private void DrawGuide(CuratedGuide guide)
    {
        if (guide.Chains.Count == 0)
            return;

        var chainIndex = Math.Clamp(
            config.SelectedChainIndex,
            0,
            guide.Chains.Count - 1);

        var names = guide.Chains.Select(c => c.Name).ToArray();

        SetNextControlWidth(ChainComboWidth);
        if (ImGui.Combo("Prerequisite Chain", ref chainIndex, names, names.Length))
        {
            config.SelectedChainIndex = chainIndex;
            SaveConfig();
        }

        var chain = guide.Chains[chainIndex];

        ImGui.TextWrapped(chain.Description);
        ImGui.Spacing();

        var sequence = questGuidance.ResolveSequence(
            chain.Steps.Select(step => step.QuestName),
            GetQuestStateByRowId);

        var completed = sequence.Entries.Count(x => x.EffectiveState == QuestProgressState.Completed);
        ImGui.TextUnformatted(
            $"Chain Progress: {completed} / {chain.Steps.Count} quests complete");

        if (sequence.CurrentOrNext is { } actionable)
        {
            var currentStep = chain.Steps.FirstOrDefault(x =>
                x.QuestName.Equals(actionable.Name, StringComparison.OrdinalIgnoreCase));
            if (currentStep is not null)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(sequence.Active is not null ? "Current quest:" : "Next quest:");
                ImGui.SameLine();
                ImGui.TextUnformatted(currentStep.QuestName);
                if (sequence.Active is null)
                    DrawNpcLocationLine("Go to", currentStep.QuestGiver, $"{currentStep.Zone} (X:{currentStep.X:0.0}, Y:{currentStep.Y:0.0})", $"guide-current-{chainIndex}");
                else
                    DrawNpcLocationLine("Quest started by", currentStep.QuestGiver, currentStep.Zone, $"guide-active-{chainIndex}");
            }
        }

        ImGui.Spacing();

        for (var i = 0; i < chain.Steps.Count; i++)
        {
            var step = chain.Steps[i];
            var state = i < sequence.Entries.Count
                ? sequence.Entries[i].EffectiveState
                : QuestProgressState.NotStarted;
            var marker = state == QuestProgressState.Completed ? "[X]" :
                state == QuestProgressState.InProgress ? "[>]" : "[ ]";

            ImGui.TextUnformatted($"{marker} {i + 1}. {step.QuestName}");

            if (state != QuestProgressState.Completed)
            {
                ImGui.Indent();
                DrawNpcLocationLine("Go to", step.QuestGiver, $"{step.Zone} (X:{step.X:0.0}, Y:{step.Y:0.0})", $"guide-step-{chainIndex}-{i}");

                if (!string.IsNullOrWhiteSpace(step.Note))
                    ImGui.TextWrapped(step.Note);

                ImGui.Unindent();
            }
        }
    }

    private GuideStatus BuildGuideStatus(CuratedGuide guide)
    {
        if (guide.Chains.Count == 0)
            return new GuideStatus(null, false, 0);

        var chainIndex = Math.Clamp(config.SelectedChainIndex, 0, guide.Chains.Count - 1);
        var chain = guide.Chains[chainIndex];
        var sequence = questGuidance.ResolveSequence(
            chain.Steps.Select(step => step.QuestName),
            GetQuestStateByRowId);
        var actionable = sequence.CurrentOrNext is null
            ? null
            : chain.Steps.FirstOrDefault(x =>
                x.QuestName.Equals(sequence.CurrentOrNext.Name, StringComparison.OrdinalIgnoreCase));
        var remaining = sequence.Entries.Count(x => x.EffectiveState != QuestProgressState.Completed);
        return new GuideStatus(actionable, sequence.Active is not null, remaining);
    }

    private void BuildQuestCache()
    {
        foreach (var quest in DataManager.GetExcelSheet<Quest>())
        {
            var name = quest.Name.ExtractText().Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            questRowIds.TryAdd(name, quest.RowId);
            if (!questRowIdsByName.TryGetValue(name, out var ids))
            {
                ids = [];
                questRowIdsByName[name] = ids;
            }
            ids.Add(quest.RowId);
        }
    }

    private static unsafe QuestProgressState GetQuestStateByRowId(uint rowId)
    {
        var uiState = UIState.Instance();
        if (uiState == null || rowId == 0)
            return QuestProgressState.NotStarted;

        var questUnlockLink = 0x10000u + rowId;
        if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(questUnlockLink))
            return QuestProgressState.Completed;

        // Accepted quests can still be at sequence 0 immediately after pickup, so
        // checking only minimum progression misses exactly the state a quest
        // tracker needs. Ask QuestManager directly first, then retain the sequence
        // check as a fallback for live progression.
        var questManager = QuestManager.Instance();
        if (questManager != null && rowId <= ushort.MaxValue &&
            questManager->IsQuestAccepted((ushort)rowId))
            return QuestProgressState.InProgress;

        if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(questUnlockLink, 1))
            return QuestProgressState.InProgress;

        return QuestProgressState.NotStarted;
    }

    private static bool IsQuestCompleteByRowId(uint rowId) =>
        GetQuestStateByRowId(rowId) == QuestProgressState.Completed;

    private QuestProgressState GetQuestState(string questName)
    {
        if (!questRowIdsByName.TryGetValue(questName, out var rowIds) || rowIds.Count == 0)
            return QuestProgressState.NotStarted;

        var best = QuestProgressState.NotStarted;
        foreach (var rowId in rowIds)
        {
            var state = GetQuestStateByRowId(rowId);
            if (state == QuestProgressState.Completed)
                return state;
            if (state == QuestProgressState.InProgress)
                best = state;
        }
        return best;
    }

    private bool IsQuestComplete(string questName) =>
        GetQuestState(questName) == QuestProgressState.Completed;

    private QuestSequenceStatus ResolveJobQuestSequence(IReadOnlyList<JobQuestStep> steps) =>
        questGuidance.ResolveQuestIds(steps.Select(x => x.QuestId), GetQuestStateByRowId);

    private QuestSequenceStatus ResolveSourceQuestSequence(SourceEntry source) =>
        questGuidance.ResolveSequence(
            QuestGuidanceIndex.ExtractQuestNames(source),
            GetQuestStateByRowId);

    private static string FormatWhoWhere(QuestGuidanceInfo? info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.QuestGiver))
            return string.Empty;

        return string.IsNullOrWhiteSpace(info.Location)
            ? info.QuestGiver!
            : $"{info.QuestGiver} — {info.Location}";
    }

    private void DrawSourceActionGuidance(AcquisitionResult result)
    {
        var guidance = actionGuidance.Resolve(result);
        if (guidance.IsEmpty)
            return;

        ImGui.Spacing();

        // NPC/vendor guidance historically encoded "NPC — Location" in ActionText.
        // Render that through the shared clickable location path instead of as
        // plain text so vendor/field-operation destinations such as Gerolt —
        // North Shroud get the same map behavior as quest guidance.
        if (guidance.ActionLabel.Equals("Go to", StringComparison.OrdinalIgnoreCase))
        {
            DrawCombinedWhoWhereLine("Go to", guidance.ActionText, $"guidance-action-{result.Target.ItemId}", guidance.Source?.NpcId);
            return;
        }

        var runUnlockedDuty = guidance.ActionLabel.Equals("Run", StringComparison.OrdinalIgnoreCase);
        if (runUnlockedDuty)
            ImGui.PushStyleColor(ImGuiCol.Text, OwnedGreen);
        ImGui.TextUnformatted($"{guidance.ActionLabel}:");
        ImGui.SameLine();
        ImGui.TextUnformatted(guidance.ActionText);
        if (runUnlockedDuty)
            ImGui.PopStyleColor();

        if (!string.IsNullOrWhiteSpace(guidance.GoTo))
        {
            DrawCombinedWhoWhereLine("Go to", guidance.GoTo, $"guidance-{result.Target.ItemId}", guidance.Source?.NpcId);
        }
        else if (!string.IsNullOrWhiteSpace(guidance.QuestStartedBy))
        {
            DrawCombinedWhoWhereLine("Quest started by", guidance.QuestStartedBy, $"guidance-start-{result.Target.ItemId}");
        }
    }

    private void DrawCombinedWhoWhereLine(string prefix, string value, string id, uint? npcId = null)
    {
        var parts = value.Split(" — ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var npc = parts[0];
            var location = string.Join(" — ", parts.Skip(1));
            DrawNpcLocationLine(prefix, npc, location, id, npcId);
            return;
        }

        var inferred = npcLocations.GetBestLocation(value);
        if (!string.IsNullOrWhiteSpace(inferred))
        {
            DrawNpcLocationLine(prefix, value, inferred, id, npcId);
            return;
        }

        if (npcLocations.GetBestPlaceMapLocation(value) is not null)
        {
            DrawLocationOnlyLine(prefix, value, id);
            return;
        }

        ImGui.TextUnformatted($"{prefix}: {value}");
    }

    private void DrawNpcLocationLine(string prefix, string npcName, string? location, string id, uint? npcId = null)
    {
        // A player-facing instruction to visit an NPC should always attempt to be
        // navigable. Many call sites know the NPC but not its formatted location;
        // fill that gap from the shared live-game NPC placement index here rather
        // than requiring every source family to duplicate the lookup.
        var effectiveLocation = location;
        if (string.IsNullOrWhiteSpace(effectiveLocation) && npcId is > 0)
            effectiveLocation = npcLocations.GetBestLocation(npcId.Value);
        if (string.IsNullOrWhiteSpace(effectiveLocation))
            effectiveLocation = npcLocations.GetBestLocation(npcName);

        // If the destination is in a progression-gated field area the NPC marker
        // is not useful yet. Route the player to the first unmet formal quest
        // prerequisite instead of presenting a waypoint they cannot reach.
        if (!string.IsNullOrWhiteSpace(effectiveLocation) &&
            TryDrawNpcAccessPrerequisite(effectiveLocation!, id))
        {
            return;
        }

        ImGui.TextUnformatted($"{prefix}: {npcName}");
        if (!string.IsNullOrWhiteSpace(effectiveLocation))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"— {effectiveLocation}");
            ImGui.SameLine();
            DrawClickableLocation(effectiveLocation!, id, npcName, npcId, "View Map");
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(map location unavailable)");
        }
    }

    private bool TryDrawNpcAccessPrerequisite(string location, string id)
    {
        var placeName = GetDisplayedPlaceName(location);
        var guidance = AreaAccessGuidanceCatalog.GetByZoneName(placeName);
        if (guidance is null)
            return false;

        foreach (var gate in guidance.Gates)
        {
            var frontier = questGuidance.ResolvePrerequisiteFrontier(gate.QuestName, GetQuestStateByRowId);
            if (frontier.IsComplete)
                continue;

            if (frontier.Actionable is not { } actionable)
            {
                ImGui.TextWrapped($"Requires progression: {gate.QuestName}");
                if (!string.IsNullOrWhiteSpace(gate.RequirementHint))
                    ImGui.TextDisabled(gate.RequirementHint);
                return true;
            }

            var label = frontier.ActionableState == QuestProgressState.InProgress ? "Current quest" : "Required quest";
            ImGui.TextWrapped($"{label}: {actionable.Name}");
            if (!string.IsNullOrWhiteSpace(actionable.QuestGiver))
                DrawNpcLocationLineUngated("Go to", actionable.QuestGiver!, actionable.Location, $"{id}-access", actionable.IssuerNpcId);
            if (!string.IsNullOrWhiteSpace(gate.RequirementHint))
                ImGui.TextDisabled(gate.RequirementHint);
            return true;
        }

        return false;
    }

    private void DrawNpcLocationLineUngated(string prefix, string npcName, string? location, string id, uint? npcId = null)
    {
        var effectiveLocation = location;
        if (string.IsNullOrWhiteSpace(effectiveLocation) && npcId is > 0)
            effectiveLocation = npcLocations.GetBestLocation(npcId.Value);
        if (string.IsNullOrWhiteSpace(effectiveLocation))
            effectiveLocation = npcLocations.GetBestLocation(npcName);

        ImGui.TextUnformatted($"{prefix}: {npcName}");
        if (!string.IsNullOrWhiteSpace(effectiveLocation))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"— {effectiveLocation}");
            ImGui.SameLine();
            DrawClickableLocation(effectiveLocation!, id, npcName, npcId, "View Map");
        }
    }

    private void DrawLocationOnlyLine(string prefix, string location, string id)
    {
        ImGui.TextUnformatted($"{prefix}:");
        ImGui.SameLine();
        DrawClickableLocation(location, id, null);
    }

    private void DrawClickableLocation(string location, string id, string? npcName, uint? npcId = null, string? buttonLabel = null)
    {
        var displayedPlaceName = GetDisplayedPlaceName(location);
        var hasInteriorEntranceFallback = TryGetInteriorEntranceFallback(displayedPlaceName, out _, out _, out _);

        var canViewMap = hasInteriorEntranceFallback;
        if (!canViewMap && npcId is > 0)
            canViewMap = npcLocations.GetBestMapLocation(npcId.Value) is not null;
        if (!canViewMap && !string.IsNullOrWhiteSpace(npcName))
            canViewMap = npcLocations.GetBestMapLocation(npcName!) is not null;
        if (!canViewMap)
            canViewMap = npcLocations.GetBestPlaceMapLocation(displayedPlaceName) is not null;
        if (!canViewMap)
            canViewMap = TryParseLocationCoordinates(location, out _, out _, out _);

        if (!canViewMap)
        {
            ImGui.TextDisabled(location);
            return;
        }

        var label = string.IsNullOrWhiteSpace(buttonLabel) ? location : buttonLabel!;
        if (ImGui.SmallButton($"{label}##map-{id}"))
            OpenMapLocation(location, npcName, npcId);

        if (ImGui.IsItemHovered())
        {
            if (hasInteriorEntranceFallback)
                ImGui.SetTooltip($"This destination is inside {displayedPlaceName}; place a flag at its overworld entrance instead.");
            else
                ImGui.SetTooltip("Open the map and place a flag at this location.");
        }
    }

    private void OpenMapLocation(string location, string? npcName, uint? npcId = null)
    {
        try
        {
            var displayedPlaceName = GetDisplayedPlaceName(location);

            // Prefer an explicit exterior entrance for known interior-only places.
            // The interior coordinates may be perfectly valid inside that territory
            // while still being useless as a world-map navigation flag.
            if (TryGetInteriorEntranceFallback(displayedPlaceName, out var entranceZone, out var entranceX, out var entranceY))
            {
                if (TryOpenCoordinateMapLink(entranceZone, entranceX, entranceY))
                    return;
            }

            if (npcId is > 0 && npcLocations.GetBestMapLocation(npcId.Value) is { } exactNpcMap)
            {
                GameGui.OpenMapWithMapLink(exactNpcMap.TerritoryTypeId, exactNpcMap.MapId, exactNpcMap.WorldPosition);
                return;
            }

            if (!string.IsNullOrWhiteSpace(npcName) && npcLocations.GetBestMapLocation(npcName!) is { } npcMap)
            {
                GameGui.OpenMapWithMapLink(npcMap.TerritoryTypeId, npcMap.MapId, npcMap.WorldPosition);
                return;
            }

            // Explicit coordinates are stronger than a representative PlaceName
            // location. Prefer them before falling back to a generic point in the
            // zone so curated/vendor guidance flags the NPC itself rather than an
            // arbitrary location in the same map.
            if (TryParseLocationCoordinates(location, out var placeName, out var x, out var y) &&
                TryOpenCoordinateMapLink(placeName, x, y))
            {
                return;
            }

            // If no exact NPC placement or explicit coordinate is available, use
            // the representative PlaceName index as the final best-effort route.
            if (npcLocations.GetBestPlaceMapLocation(displayedPlaceName) is { } placeMap)
            {
                GameGui.OpenMapWithMapLink(placeMap.TerritoryTypeId, placeMap.MapId, placeMap.WorldPosition);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, $"Could not open map location '{location}'.");
        }
    }

    private static string GetDisplayedPlaceName(string location)
    {
        if (TryParseLocationCoordinates(location, out var placeName, out _, out _))
            return placeName;

        return (location ?? string.Empty).Trim();
    }

    private static bool TryGetInteriorEntranceFallback(
        string placeName,
        out string zone,
        out float x,
        out float y)
    {
        zone = string.Empty;
        x = 0f;
        y = 0f;

        placeName = (placeName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(placeName) ||
            !InteriorEntranceFallbacks.TryGetValue(placeName, out var fallback))
            return false;

        zone = fallback.Zone;
        x = fallback.X;
        y = fallback.Y;
        return true;
    }

    private static bool TryOpenCoordinateMapLink(string placeName, float x, float y)
    {
        var link = SeString.CreateMapLink(placeName, x, y);
        var payload = link?.Payloads.OfType<MapLinkPayload>().FirstOrDefault();
        if (payload is null)
            return false;

        GameGui.OpenMapWithMapLink(payload);
        return true;
    }

    private static bool TryParseLocationCoordinates(string location, out string placeName, out float x, out float y)
    {
        placeName = string.Empty;
        x = 0;
        y = 0;
        if (string.IsNullOrWhiteSpace(location))
            return false;

        var match = Regex.Match(
            location,
            @"^(?<place>.+?)\s*\(\s*X\s*:\s*(?<x>-?\d+(?:\.\d+)?)\s*,\s*Y\s*:\s*(?<y>-?\d+(?:\.\d+)?)\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        placeName = match.Groups["place"].Value.Trim();
        return float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
               float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y);
    }

}
