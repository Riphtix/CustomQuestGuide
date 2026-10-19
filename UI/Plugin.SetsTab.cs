using System;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace CustomQuestGuide;

public sealed partial class Plugin
{
    private string setsExpansionFilter = "All Expansions";
    private string setsTypeFilter = "All Types";

    private static readonly string[] SetExpansions =
    [
        "All Expansions", "A Realm Reborn", "Heavensward", "Stormblood",
        "Shadowbringers", "Endwalker", "Dawntrail", "Other"
    ];
    private static readonly string[] SetTypes =
        ["All Types", "Mounts", "Riding Maps", "Relics", "Relic Tools"];

    private static string GetSetExpansion(TargetSet set)
    {
        var id = set.Id.ToLowerInvariant();
        if (id == "relic-zodiac") return "A Realm Reborn";
        if (id == "relic-anima") return "Heavensward";
        if (id == "tool-skysteel") return "Shadowbringers";
        if (id == "relic-eureka") return "Stormblood";
        if (id == "relic-resistance") return "Shadowbringers";
        if (id == "relic-manderville" || id == "tool-splendorous") return "Endwalker";
        if (id == "relic-phantom" || id == "tool-cosmic") return "Dawntrail";
        if (id.EndsWith("-arr") || id.Contains("-arr-")) return "A Realm Reborn";
        if (id.EndsWith("-hw") || id.Contains("-hw-")) return "Heavensward";
        if (id.EndsWith("-sb") || id.Contains("-sb-")) return "Stormblood";
        if (id.EndsWith("-shb") || id.Contains("-shb-")) return "Shadowbringers";
        if (id.EndsWith("-ew") || id.Contains("-ew-")) return "Endwalker";
        if (id.EndsWith("-dt") || id.Contains("-dt-")) return "Dawntrail";
        foreach (var expansion in SetExpansions.Skip(1).Where(expansion => expansion != "Other"))
            if (set.Name.Contains(expansion, StringComparison.OrdinalIgnoreCase)) return expansion;
        return "Other";
    }

    private static string GetSetType(TargetSet set) => set.Group switch
    {
        "Mount Sets" => "Mounts",
        "Riding Map Sets" => "Riding Maps",
        "Relic Sets" => "Relics",
        "Relic Tool Sets" => "Relic Tools",
        _ => "Other"
    };

    private void DrawSetsNavigation()
    {
        // Filters belong only to the browser, not the selected collection detail page.
        if (setCatalog.Get(config.SelectedSetId) is not null)
            return;

        ImGui.TextUnformatted("Browse Collections");
        var expansion = Array.IndexOf(SetExpansions, setsExpansionFilter);
        if (expansion < 0) expansion = 0;
        SetNextControlWidth(StandardComboWidth);
        if (ImGui.Combo("Expansion##sets-expansion", ref expansion, SetExpansions, SetExpansions.Length))
            setsExpansionFilter = SetExpansions[expansion];
        ImGui.SameLine();
        var type = Array.IndexOf(SetTypes, setsTypeFilter);
        if (type < 0) type = 0;
        SetNextControlWidth(StandardComboWidth);
        if (ImGui.Combo("Type##sets-type", ref type, SetTypes, SetTypes.Length))
            setsTypeFilter = SetTypes[type];
    }

    private static string GetSetBrowserName(TargetSet set, string expansion)
    {
        var name = set.Name;
        if (name.StartsWith(expansion + " ", StringComparison.OrdinalIgnoreCase))
            name = name[(expansion.Length + 1)..];
        return name;
    }

    private void DrawSetBrowserRow(TargetSet set, string expansion)
    {
        var selected = config.SelectedSetId.Equals(set.Id, StringComparison.OrdinalIgnoreCase);
        var name = GetSetBrowserName(set, expansion);
        var typeSuffix = setsTypeFilter == "All Types" ? $"  ({GetSetType(set)})" : string.Empty;
        var label = $"{name}{typeSuffix}##set-browser-{set.Id}";
        if (ImGui.Selectable(label, selected))
            SelectSetTarget(set, selected ? config.SelectedRelicJob : "");

        // Relic sets represent a chosen weapon's progression, not one binary item collection.
        // Do not show their catalog item count as an ownership percentage.
        if (IsRelicSet(set))
        {
            ImGui.Indent();
            ImGui.TextDisabled("Choose a weapon to view its stages");
            ImGui.Unindent();
            return;
        }

        // Do not query ownership across *every member of every set* on each ImGui frame.
        // Full live ownership/progress remains available when the collection is opened.
        ImGui.Indent();
        ImGui.TextDisabled($"{set.ItemIds.Count} members");
        ImGui.Unindent();
    }

    private void DrawSetsTab()
    {
        // Collection selection is a navigation destination, not a panel appended
        // below the entire browser. This also prevents background rendering of
        // dozens of set rows while the user is inspecting a single collection.
        var selectedSet = setCatalog.Get(config.SelectedSetId);
        if (selectedSet is not null)
        {
            if (ImGui.Button("< All Collections##set-browser-back"))
                NavigateTo(() => config.SelectedSetId = string.Empty);
            ImGui.Spacing();
            DrawCommonTargetControls();
            DrawSetSummary(selectedSet);
            if (config.Detailed && !IsRelicSet(selectedSet))
                DrawSetDetailed(selectedSet);
            DrawDebugPanel(null, selectedSet);
            return;
        }

        var matching = setCatalog.Sets.Where(s =>
            (setsExpansionFilter == "All Expansions" || GetSetExpansion(s) == setsExpansionFilter) &&
            (setsTypeFilter == "All Types" || GetSetType(s) == setsTypeFilter)).ToArray();
        ImGui.TextDisabled($"{matching.Length} collections");

        if (matching.Length == 0)
            ImGui.TextDisabled("No collections match these filters.");

        // Group the complete catalog by expansion instead of producing one long, flat list.
        // With an expansion filter, its heading remains visible but avoids an extra click.
        foreach (var expansion in SetExpansions.Skip(1))
        {
            var inExpansion = matching.Where(set => GetSetExpansion(set) == expansion)
                .OrderBy(set => GetSetType(set), StringComparer.OrdinalIgnoreCase)
                .ThenBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (inExpansion.Length == 0) continue;

            // Make the expansion a distinct section heading; individual sets sit one
            // indentation level below it. Keep the rows compact and selectable.
            ImGui.Spacing();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            // Section headings use the normal theme text color; blue is reserved for partial progression.
            ImGui.TextUnformatted($"{expansion.ToUpperInvariant()}  ({inExpansion.Length})");
            ImGui.Spacing();
            ImGui.Indent();
            foreach (var set in inExpansion)
            {
                DrawSetBrowserRow(set, expansion);
                ImGui.Spacing();
            }
            ImGui.Unindent();
        }

    }
}
