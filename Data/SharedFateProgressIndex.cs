using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomQuestGuide;

/// <summary>
/// One-shot snapshot of the game's own Travel -> Shared FATE data. This never
/// counts FATEs itself; it reads the same rank/current/needed values the client
/// already uses for the Shared FATE window.
/// </summary>
public sealed class SharedFateProgressIndex
{
    private readonly IPluginLog log;
    private readonly Dictionary<string, SharedFateProgressSnapshot> byZone =
        new(StringComparer.OrdinalIgnoreCase);

    public bool HasSnapshot { get; private set; }
    public bool InitializationAttempted { get; private set; }

    public SharedFateProgressIndex(IPluginLog log) => this.log = log;

    public unsafe void CaptureSnapshot()
    {
        byZone.Clear();
        HasSnapshot = false;
        InitializationAttempted = false;

        try
        {
            var agent = AgentFateProgress.Instance();
            if (agent == null)
                return;

            // FateProgress is an on-demand UI agent. On a fresh login its tab/zone
            // backing data can still be empty even for a character with extensive
            // Shared FATE progress. Because /cqg is an explicit user-triggered
            // one-shot scan, briefly activating the agent here is preferable to
            // background polling or making the user manually open Travel -> Shared
            // FATE before every scan. Show/Hide occurs in the same scan and we never
            // close an agent that was already active.
            var wasActive = agent->IsAgentActive();
            if (!HasUsableZoneData(agent) && !wasActive && agent->IsActivatable())
            {
                InitializationAttempted = true;
                try
                {
                    agent->Show();
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "Could not initialize the Shared FATE agent for the current /cqg scan.");
                }
            }

            ReadAgentZones(agent);

            if (!wasActive && InitializationAttempted)
            {
                try
                {
                    agent->Hide();
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "Could not hide the Shared FATE agent after snapshot initialization.");
                }
            }

            HasSnapshot = byZone.Count > 0;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Could not snapshot Shared FATE progress.");
        }
    }

    private static unsafe bool HasUsableZoneData(AgentFateProgress* agent)
    {
        foreach (var tab in agent->Tabs)
        {
            foreach (var zone in tab.Zones)
            {
                if (zone.TerritoryTypeId != 0)
                    return true;
            }
        }
        return false;
    }

    private unsafe void ReadAgentZones(AgentFateProgress* agent)
    {
        foreach (var tab in agent->Tabs)
        {
            foreach (var zone in tab.Zones)
            {
                if (zone.TerritoryTypeId == 0)
                    continue;

                var zoneName = zone.ZoneName.ToString().Trim();
                if (string.IsNullOrWhiteSpace(zoneName))
                    continue;

                byZone[zoneName] = new SharedFateProgressSnapshot(
                    zone.TerritoryTypeId,
                    zoneName,
                    zone.CurrentRank,
                    zone.MaxRank,
                    zone.FateProgress,
                    zone.NeededFates);
            }
        }
    }

    public SharedFateProgressSnapshot? GetByZoneName(string zoneName)
    {
        if (string.IsNullOrWhiteSpace(zoneName))
            return null;
        return byZone.TryGetValue(zoneName.Trim(), out var progress) ? progress : null;
    }

    public SharedFateProgressSnapshot? GetForRidingMap(string itemName)
    {
        const string suffix = " Riding Map";
        if (string.IsNullOrWhiteSpace(itemName) || !itemName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var zoneName = itemName[..^suffix.Length].Trim();
        return GetByZoneName(zoneName);
    }
}
