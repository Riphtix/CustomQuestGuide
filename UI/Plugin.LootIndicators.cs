using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomQuestGuide;

public sealed partial class Plugin
{
    private enum LootTargetKind
    {
        None = 0,
        SetMember = 1,
        Direct = 2,
    }

    private readonly Dictionary<uint, LootTargetKind> lootTargetKinds = new();
    private DateTime lootTargetSnapshotExpiresUtc = DateTime.MinValue;
    private string lootTargetSnapshotSignature = string.Empty;

    private readonly record struct LootSlotAnchor(float X, float Y, float Width, float Height);
    private readonly record struct LootTextAnchor(float X, float Y, float Width, float Height, string Text);

    /// <summary>
    /// Draws a non-interactive CQG marker for targeted loot in the native Need/Greed
    /// window. The preferred presentation is attached to the loot icon. A text-title
    /// fallback is also rendered when the current ULD nests the icon strip too deeply
    /// to map safely. Nothing here changes loot state or fires addon callbacks.
    /// </summary>
    private unsafe void DrawLootTargetIndicators()
    {
        AddonNeedGreed* addon;
        try
        {
            addon = GameGui.GetAddonByName<AddonNeedGreed>("NeedGreed");
        }
        catch
        {
            return;
        }

        if (addon is null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return;

        RefreshLootTargetSnapshotIfNeeded();
        if (lootTargetKinds.Count == 0)
            return;

        var draw = ImGui.GetForegroundDrawList();
        var markedItemIds = new HashSet<uint>();

        // Prefer the native item array + visual icon strip when the current client
        // exposes both consistently. Clamp all struct-derived values defensively so
        // a patch/layout mismatch simply falls through to the title-text fallback.
        var rawCount = addon->NumItems;
        var itemCount = rawCount is > 0 and <= 16
            ? Math.Min(rawCount, addon->Items.Length)
            : 0;

        if (itemCount > 0)
        {
            var slots = FindLootSlotAnchorsDeep(&addon->AtkUnitBase, itemCount);
            if (slots.Count >= itemCount)
            {
                for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                {
                    ref var item = ref addon->Items[itemIndex];
                    var itemId = NormalizeLootItemId(item.ItemId);
                    if (itemId == 0 || !lootTargetKinds.TryGetValue(itemId, out var kind) || kind == LootTargetKind.None)
                        continue;

                    DrawLootBadge(draw, slots[itemIndex], kind);
                    markedItemIds.Add(itemId);
                }
            }
        }

        // Reliable fallback: the selected loot item's full name is a visible text node
        // in the Need/Greed addon. Match that player-facing text against CQG's target
        // catalog rather than depending on a particular ULD node ID/shape. This also
        // means pinning an item after it has already dropped works immediately.
        DrawLootTitleFallbacks(&addon->AtkUnitBase, draw, markedItemIds);
    }

    private static void DrawLootBadge(ImDrawListPtr draw, LootSlotAnchor slot, LootTargetKind kind)
    {
        var minSide = Math.Max(1f, Math.Min(slot.Width, slot.Height));
        var markerRadius = Math.Clamp(minSide * 0.18f, 6f, 9f);
        var center = new Vector2(
            slot.X + slot.Width - markerRadius - 2f,
            slot.Y + markerRadius + 2f);

        DrawLootBadgeAt(draw, center, markerRadius, kind);
    }

    private static void DrawLootBadgeAt(ImDrawListPtr draw, Vector2 center, float markerRadius, LootTargetKind kind)
    {
        // Keep the marker readable against FFXIV's highly saturated loot borders.
        // The target type is expressed by the ring color while the badge itself stays
        // near-black with white lettering. This is substantially clearer than a filled
        // green/blue circle, especially on selected loot icons.
        var accent = kind == LootTargetKind.Direct ? OwnedGreen : PartialBlue;
        var accentPacked = ImGui.GetColorU32(accent);
        var shadowPacked = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.95f));
        var fillPacked = ImGui.GetColorU32(new Vector4(0.055f, 0.055f, 0.065f, 0.98f));
        var whitePacked = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));

        draw.AddCircleFilled(center, markerRadius + 2.5f, shadowPacked);
        draw.AddCircleFilled(center, markerRadius + 1.5f, accentPacked);
        draw.AddCircleFilled(center, markerRadius - 1.0f, fillPacked);

        var label = kind == LootTargetKind.Direct ? "T" : "S";
        var textSize = ImGui.CalcTextSize(label);
        var textPos = center - (textSize * 0.5f);

        // Tiny text shadow keeps the glyph legible even if the UI is scaled down.
        draw.AddText(textPos + new Vector2(1f, 1f), shadowPacked, label);
        draw.AddText(textPos, whitePacked, label);
    }

    private unsafe void DrawLootTitleFallbacks(AtkUnitBase* addon, ImDrawListPtr draw, HashSet<uint> alreadyMarked)
    {
        var targetNames = new Dictionary<string, (uint ItemId, LootTargetKind Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in lootTargetKinds)
        {
            if (alreadyMarked.Contains(pair.Key))
                continue;

            var target = catalog.GetById(pair.Key);
            if (target is null || string.IsNullOrWhiteSpace(target.Name))
                continue;

            targetNames[target.Name.Trim()] = (pair.Key, pair.Value);
        }

        if (targetNames.Count == 0)
            return;

        var textNodes = FindVisibleLootTextAnchorsDeep(addon);
        foreach (var textNode in textNodes)
        {
            var visibleText = textNode.Text.Trim();
            if (!targetNames.TryGetValue(visibleText, out var target))
                continue;

            // Text nodes often span the entire Need/Greed window even though the
            // visible item name is centered inside that rectangle. Using node.Width as
            // the rendered text width therefore placed the badge over the window's X
            // button. Anchor to the actual glyph width instead.
            var radius = 8f;
            var renderedTextWidth = ImGui.CalcTextSize(visibleText).X;
            var nodeWidth = Math.Max(renderedTextWidth, Math.Max(0f, textNode.Width));
            var visibleTextLeft = textNode.X + Math.Max(0f, (nodeWidth - renderedTextWidth) * 0.5f);
            var center = new Vector2(
                visibleTextLeft + renderedTextWidth + radius + 6f,
                textNode.Y + Math.Max(14f, textNode.Height) * 0.5f);

            DrawLootBadgeAt(draw, center, radius, target.Kind);
            alreadyMarked.Add(target.ItemId);
        }
    }

    private static unsafe List<LootSlotAnchor> FindLootSlotAnchorsDeep(AtkUnitBase* addon, int itemCount)
    {
        var candidates = new List<LootSlotAnchor>();
        var visitedManagers = new HashSet<nint>();
        CollectSquareLootNodes(&addon->UldManager, candidates, visitedManagers, 0);

        if (candidates.Count < itemCount)
            return new List<LootSlotAnchor>();

        // Collapse multiple visual layers (frame/icon/collision) belonging to one slot.
        var deduped = new List<LootSlotAnchor>();
        foreach (var candidate in candidates.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            var existingIndex = deduped.FindIndex(existing =>
                Math.Abs(existing.X - candidate.X) <= 7f &&
                Math.Abs(existing.Y - candidate.Y) <= 7f);

            if (existingIndex < 0)
            {
                deduped.Add(candidate);
                continue;
            }

            var existing = deduped[existingIndex];
            if ((candidate.Width * candidate.Height) > (existing.Width * existing.Height))
                deduped[existingIndex] = candidate;
        }

        // The item strip is the horizontal group that can account for NumItems.
        var rows = new List<List<LootSlotAnchor>>();
        foreach (var candidate in deduped.OrderBy(c => c.Y).ThenBy(c => c.X))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(r[0].Y - candidate.Y) <= 12f);
            if (row is null)
            {
                row = new List<LootSlotAnchor>();
                rows.Add(row);
            }

            row.Add(candidate);
        }

        var best = rows
            .Select(r => r.OrderBy(x => x.X).ToList())
            .Where(r => r.Count >= itemCount)
            .OrderBy(r => Math.Abs(r.Count - itemCount))
            .ThenBy(r => r.Max(x => x.X) - r.Min(x => x.X))
            .ThenBy(r => r.Average(x => x.Y))
            .FirstOrDefault();

        return best is null
            ? new List<LootSlotAnchor>()
            : best.Take(itemCount).ToList();
    }

    private static unsafe void CollectSquareLootNodes(
        AtkUldManager* manager,
        List<LootSlotAnchor> output,
        HashSet<nint> visitedManagers,
        int depth)
    {
        if (manager is null || depth > 8 || !visitedManagers.Add((nint)manager))
            return;

        var nodes = manager->NodeList;
        var nodeCount = manager->NodeListCount;
        if (nodes is null || nodeCount <= 0 || nodeCount > 2048)
            return;

        for (var i = 0; i < nodeCount; i++)
        {
            var node = nodes[i];
            if (node is null || (node->NodeFlags & NodeFlags.Visible) == 0)
                continue;

            var width = node->Width;
            var height = node->Height;
            if (width is >= 28 and <= 80 && height is >= 28 and <= 80 && Math.Abs(width - height) <= 14)
            {
                var x = node->ScreenX;
                var y = node->ScreenY;
                if (float.IsFinite(x) && float.IsFinite(y))
                    output.Add(new LootSlotAnchor(x, y, width, height));
            }

            // Component nodes own another ULD manager; the loot icon strip is nested
            // under components on current clients, so looking only at the addon's root
            // NodeList misses it.
            if ((ushort)node->Type < 1000)
                continue;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
                CollectSquareLootNodes(&componentNode->Component->UldManager, output, visitedManagers, depth + 1);
        }
    }

    private static unsafe List<LootTextAnchor> FindVisibleLootTextAnchorsDeep(AtkUnitBase* addon)
    {
        var output = new List<LootTextAnchor>();
        var visitedManagers = new HashSet<nint>();
        CollectLootTextNodes(&addon->UldManager, output, visitedManagers, 0);
        return output;
    }

    private static unsafe void CollectLootTextNodes(
        AtkUldManager* manager,
        List<LootTextAnchor> output,
        HashSet<nint> visitedManagers,
        int depth)
    {
        if (manager is null || depth > 8 || !visitedManagers.Add((nint)manager))
            return;

        var nodes = manager->NodeList;
        var nodeCount = manager->NodeListCount;
        if (nodes is null || nodeCount <= 0 || nodeCount > 2048)
            return;

        for (var i = 0; i < nodeCount; i++)
        {
            var node = nodes[i];
            if (node is null || (node->NodeFlags & NodeFlags.Visible) == 0)
                continue;

            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = textNode->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    output.Add(new LootTextAnchor(
                        node->ScreenX,
                        node->ScreenY,
                        node->Width,
                        node->Height,
                        text));
                }
            }

            if ((ushort)node->Type < 1000)
                continue;

            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component is not null)
                CollectLootTextNodes(&componentNode->Component->UldManager, output, visitedManagers, depth + 1);
        }
    }

    private void RefreshLootTargetSnapshotIfNeeded()
    {
        var signature = BuildLootTargetSnapshotSignature();
        var now = DateTime.UtcNow;
        if (signature.Equals(lootTargetSnapshotSignature, StringComparison.Ordinal) &&
            now < lootTargetSnapshotExpiresUtc)
        {
            return;
        }

        lootTargetKinds.Clear();
        lootTargetSnapshotSignature = signature;
        lootTargetSnapshotExpiresUtc = now.AddSeconds(5);

        // The actively selected item is a direct target even if it is not pinned.
        AddLootTarget(config.SelectedItemId, LootTargetKind.Direct);

        // The actively selected set contributes only still-needed members.
        if (!string.IsNullOrWhiteSpace(config.SelectedSetId) &&
            setCatalog.Get(config.SelectedSetId) is { } selectedSet)
        {
            AddNeededSetMembers(selectedSet, config.SelectedRelicJob);
        }

        foreach (var pin in config.PinnedTargets)
        {
            if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase))
            {
                AddLootTarget(pin.NumericId, LootTargetKind.Direct);
                continue;
            }

            if (!pin.Kind.Equals("Set", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(pin.StringId) ||
                setCatalog.Get(pin.StringId) is not { } set)
            {
                continue;
            }

            AddNeededSetMembers(set, pin.Variant);
        }
    }

    private void AddNeededSetMembers(TargetSet set, string? variant)
    {
        foreach (var itemId in set.ItemIds)
        {
            if (itemId == 0 || ownership.GetItemStatus(itemId).IsOwned)
                continue;

            if (!string.IsNullOrWhiteSpace(variant))
            {
                var target = catalog.GetById(itemId);
                if (target is null || !target.EquipJobs.Contains(variant, StringComparer.OrdinalIgnoreCase))
                    continue;
            }

            AddLootTarget(itemId, LootTargetKind.SetMember);
        }
    }

    private void AddLootTarget(uint itemId, LootTargetKind kind)
    {
        itemId = NormalizeLootItemId(itemId);
        if (itemId == 0)
            return;

        if (!lootTargetKinds.TryGetValue(itemId, out var existing) || kind > existing)
            lootTargetKinds[itemId] = kind;
    }

    private string BuildLootTargetSnapshotSignature()
    {
        var pins = string.Join(";", config.PinnedTargets.Select(pin =>
            $"{pin.Kind}:{pin.NumericId}:{pin.StringId}:{pin.Variant}"));
        return $"{config.SelectedItemId}|{config.SelectedSetId}|{config.SelectedRelicJob}|{pins}";
    }

    private static uint NormalizeLootItemId(uint itemId)
    {
        // Inventory-style HQ IDs may carry the standard +1,000,000 modifier.
        // LootItemInfo normally exposes base IDs, but normalize defensively.
        return itemId >= 1_000_000 && itemId < 2_000_000
            ? itemId - 1_000_000
            : itemId;
    }
}
