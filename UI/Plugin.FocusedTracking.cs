using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomQuestGuide;

public sealed partial class Plugin
{
    // A single HUD presentation over existing, cached Watchlist guidance.
    // Normal mode is transparent and click-through; Edit Mode exposes drag,
    // resizing and per-entry order/removal controls without a second resolver.
    private readonly List<(Vector2 Min, Vector2 Max)> focusedClickRegions = new();
    private string focusedGuidanceSelection = string.Empty;
    private DateTime nextFocusedGuidanceRefreshUtc = DateTime.MinValue;
    // Native quest-list geometry is not quest guidance. Sample it at a modest
    // rate rather than recursively walking UI nodes on every rendered frame.
    private static readonly TimeSpan FocusedQuestAnchorSampleInterval = TimeSpan.FromMilliseconds(250);
    private DateTime nextFocusedQuestAnchorSampleUtc = DateTime.MinValue;
    private Vector2? cachedFocusedQuestAnchor;
    // Text layout is immutable until guidance, width, or font metrics change.
    // Avoid word/character-level ImGui measurements on every game frame.
    private readonly Dictionary<(string Text, int Width, bool WithIcon, float FontSize), string[]> focusedWrappedLines = new();
    // Resolve expensive set/relic representative-item icons only when the
    // focused selection changes. The actual texture wrap remains frame-scoped.
    private readonly Dictionary<string, uint?> focusedIconIds = new(StringComparer.Ordinal);
    // Rent focused icons once on startup/selection changes. The lease stays valid
    // across frames and is disposed when no longer needed or on plugin unload.
    private readonly Dictionary<uint, Task<IDalamudTextureWrap>> focusedIconRentals = new();
    // Presentation snapshots are rebuilt on the target-scoped refresh schedule,
    // never by the ImGui draw loop. This prevents resolver/catalog/ownership
    // work from leaking into high-frequency rendering.
    private readonly Dictionary<string, PinnedUiDerivedCache> focusedDisplaySnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint?> focusedDisplayIcons = new(StringComparer.Ordinal);

    private void DrawFocusedTrackingOverlays()
    {
        var allFocused = config.PinnedTargets.Where(pin => pin.FocusedTrackingEnabled).ToArray();
        if (allFocused.Length > MaxFocusedTargets)
        {
            foreach (var extra in allFocused.Skip(MaxFocusedTargets))
                extra.FocusedTrackingEnabled = false;
            SaveConfig();
        }
        var focused = allFocused.Take(MaxFocusedTargets).ToArray();
        if (focused.Length == 0)
        {
            focusedGuidanceSelection = string.Empty;
            focusedIconIds.Clear();
            ReleaseUnusedFocusedIconRentals(new HashSet<uint>());
            focusedDisplaySnapshots.Clear();
            focusedDisplayIcons.Clear();
            nextFocusedGuidanceRefreshUtc = DateTime.MinValue;
            RefreshFocusedTargetStates(Array.Empty<PinnedTargetConfig>());
            return;
        }

        // The HUD can be opened while the main Watchlist has not been visited.
        // Prime just this new selection once, then use the normal timed/event-
        // invalidated Watchlist snapshot; do not resolve anything each frame.
        var selection = string.Join("|", focused.Select(p =>
            $"{PinnedTargetCacheKey(p)}\u001f{p.FocusedTrackingId}"));
        // Refresh only the up-to-three selected HUD targets, even when the user
        // has never opened /cqg. Do not trigger the command's full ownership scan
        // or clear every Watchlist row's guidance cache.
        var now = DateTime.UtcNow;
        if (!string.Equals(selection, focusedGuidanceSelection, StringComparison.Ordinal) ||
            now >= nextFocusedGuidanceRefreshUtc)
        {
            if (!string.Equals(selection, focusedGuidanceSelection, StringComparison.Ordinal))
                focusedIconIds.Clear();
            focusedGuidanceSelection = selection;
            RefreshFocusedTargetStates(focused);
            // Build the entire visual snapshot after state refresh. Neither
            // GetPinnedUiDerived nor ResolveFocusedGameIcon is called in Draw.
            focusedDisplaySnapshots.Clear();
            focusedDisplayIcons.Clear();
            foreach (var pin in focused)
            {
                var key = PinnedTargetCacheKey(pin);
                focusedDisplaySnapshots[key] = GetPinnedUiDerived(pin);
                focusedDisplayIcons[key] = config.FocusedTrackingShowIcons ? GetCachedFocusedGameIcon(pin) : null;
            }
            PrimeFocusedIconRentals();
            nextFocusedGuidanceRefreshUtc = now.Add(PinnedUiRefreshInterval);
        }
        var edit = config.FocusedTrackingEditMode;
        var flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;
        // Preserve gameplay clicks everywhere except directly over an actionable
        // HUD text line. Hit regions are captured from the previous render.
        var mouse = ImGui.GetMousePos();
        var overAction = focusedClickRegions.Any(r => mouse.X >= r.Min.X && mouse.X <= r.Max.X && mouse.Y >= r.Min.Y && mouse.Y <= r.Max.Y);
        if (!edit)
        {
            flags |= ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (!overAction)
                flags |= ImGuiWindowFlags.NoInputs;
        }
        focusedClickRegions.Clear();

        var width = Math.Clamp(config.FocusedTrackingWidth, 170f, 850f);
        var height = Math.Clamp(config.FocusedTrackingHeight, 90f, 900f);
        ImGui.SetNextWindowSize(new Vector2(width, height), edit ? ImGuiCond.Once : ImGuiCond.Always);
        // Follow only the visible native quest text. Never change the saved manual
        // coordinates, so toggling attachment off restores the user's HUD layout.
        var attachedPosition = config.FocusedTrackingAttachToQuestList
            ? TryGetNativeQuestListBottom(out var bottom)
                ? new Vector2(
                    Math.Clamp(bottom.X - width + Math.Clamp(config.FocusedTrackingQuestListHorizontalOffset, -250f, 250f),
                        ImGui.GetMainViewport().Pos.X,
                        Math.Max(ImGui.GetMainViewport().Pos.X, ImGui.GetMainViewport().Pos.X + ImGui.GetMainViewport().Size.X - width)),
                    bottom.Y + Math.Clamp(config.FocusedTrackingQuestListGap, 0f, 32f))
                : (Vector2?)null
            : null;
        if (attachedPosition is { } nativePosition)
            ImGui.SetNextWindowPos(nativePosition, ImGuiCond.Always);
        else if (config.FocusedTrackingX >= 0f && config.FocusedTrackingY >= 0f)
            ImGui.SetNextWindowPos(new Vector2(config.FocusedTrackingX, config.FocusedTrackingY), edit ? ImGuiCond.Once : ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(edit ? 0.30f : 0f);
        var visible = ImGui.Begin("Focused Tracking###CQGFocusedCombined", flags);
        if (visible)
        {
            if (edit)
            {
                ImGui.TextDisabled(attachedPosition.HasValue
                    ? "Focused Tracking - attached to quest list; resize from corner"
                    : "Focused Tracking - drag to move; resize from corner");
                if (ImGui.SmallButton(config.FocusedTrackingLeftAligned ? "Align: Left" : "Align: Right"))
                {
                    config.FocusedTrackingLeftAligned = !config.FocusedTrackingLeftAligned;
                    SaveConfig();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Done##FocusDone"))
                {
                    config.FocusedTrackingEditMode = false;
                    SaveConfig();
                }
                ImGui.Separator();
            }

            foreach (var pin in focused)
            {
                var key = PinnedTargetCacheKey(pin);
                if (!focusedDisplaySnapshots.TryGetValue(key, out var derived))
                    continue;
                var destination = derived.Location;
                // The gold title is the map action; blue objective text is informational.
                DrawFocusedAlignedText(FormatFocusedTitle(pin, derived.Title), new Vector4(0.95f, 0.79f, 0.46f, 1f), destination,
                    config.FocusedTrackingShowIcons && focusedDisplayIcons.TryGetValue(key, out var icon) ? icon : null);
                if (!string.IsNullOrWhiteSpace(derived.Subtitle))
                    DrawFocusedAlignedText(derived.Subtitle, new Vector4(0.55f, 0.82f, 1f, 1f));
                if (derived.Progress is { } progress)
                    DrawFocusedAlignedText($"{progress.Current} / {progress.Total} {progress.Unit}", new Vector4(0.55f, 0.82f, 1f, 1f));
                if (derived.Complete)
                    DrawFocusedAlignedText("Complete", new Vector4(0.35f, 1f, 0.45f, 1f));

                if (edit)
                {
                    ImGui.PushID(pin.FocusedTrackingId);
                    if (ImGui.SmallButton("Open"))
                    {
                        OpenMainWindow();
                        OpenPinnedTarget(pin);
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Remove"))
                    {
                        pin.FocusedTrackingEnabled = false;
                        SaveConfig();
                    }
                    ImGui.PopID();
                }
                ImGui.Dummy(new Vector2(1f, 8f));
            }

            if (edit)
            {
                // Save only when the user releases the mouse, not every frame.
                var position = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                    (!attachedPosition.HasValue || Math.Abs(size.X - config.FocusedTrackingWidth) > 0.5f || Math.Abs(size.Y - config.FocusedTrackingHeight) > 0.5f) &&
                    (Math.Abs(position.X - config.FocusedTrackingX) > 0.5f ||
                     Math.Abs(position.Y - config.FocusedTrackingY) > 0.5f ||
                     Math.Abs(size.X - config.FocusedTrackingWidth) > 0.5f ||
                     Math.Abs(size.Y - config.FocusedTrackingHeight) > 0.5f))
                {
                    if (!attachedPosition.HasValue)
                    {
                        config.FocusedTrackingX = position.X;
                        config.FocusedTrackingY = position.Y;
                    }
                    config.FocusedTrackingWidth = size.X;
                    config.FocusedTrackingHeight = size.Y;
                    SaveConfig();
                }
            }
        }
        ImGui.End();
    }

    /// <summary>
    /// Locate the bottom of the *visible* quest tracker content, not the addon's
    /// whole allocated rectangle. Read-only; unavailable or invalid structures
    /// never change saved manual placement. The node walk is bounded by the
    /// existing safe ULD traversal limits and performs no network requests.
    /// </summary>
    private unsafe bool TryGetNativeQuestListBottom(out Vector2 bottom)
    {
        var now = DateTime.UtcNow;
        if (now < nextFocusedQuestAnchorSampleUtc)
        {
            bottom = cachedFocusedQuestAnchor ?? default;
            return cachedFocusedQuestAnchor.HasValue;
        }
        nextFocusedQuestAnchorSampleUtc = now.Add(FocusedQuestAnchorSampleInterval);
        cachedFocusedQuestAnchor = null;
        bottom = default;
        try
        {
            var addon = GameGui.GetAddonByName<AtkUnitBase>("_ToDoList");
            if (addon is null || !addon->IsReady || !addon->IsVisible)
                return false;

            var texts = FindVisibleLootTextAnchorsDeep(addon);
            var viewport = ImGui.GetMainViewport();
            var viewportMax = viewport.Pos + viewport.Size;
            var bestY = float.MinValue;
            var right = float.MinValue;
            foreach (var entry in texts)
            {
                if (string.IsNullOrWhiteSpace(entry.Text) || entry.Width <= 0f || entry.Height <= 0f)
                    continue;
                if (entry.X < viewport.Pos.X || entry.Y < viewport.Pos.Y ||
                    entry.X + entry.Width > viewportMax.X || entry.Y + entry.Height > viewportMax.Y)
                    continue;
                var endY = entry.Y + entry.Height;
                if (endY > bestY)
                    bestY = endY;
                right = Math.Max(right, entry.X + entry.Width);
            }
            if (bestY == float.MinValue || right == float.MinValue)
                return false;
            bottom = new Vector2(right, bestY);
            cachedFocusedQuestAnchor = bottom;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DrawFocusedAlignedText(string value, Vector4 color, PinnedLocationHint? destination = null, uint? categoryIcon = null)
    {
        var availableWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X - (categoryIcon is null ? 0f : 20f));
        // Integer width makes keys stable across floating-point positioning jitter.
        var key = (value, (int)MathF.Floor(availableWidth), categoryIcon.HasValue, ImGui.GetFontSize());
        if (!focusedWrappedLines.TryGetValue(key, out var lines))
        {
            lines = WrapFocusedText(value, Math.Max(1, key.Item2));
            // This bounds the cache across long play sessions with changing guidance.
            if (focusedWrappedLines.Count >= 128)
                focusedWrappedLines.Clear();
            focusedWrappedLines[key] = lines;
        }
        for (var i = 0; i < lines.Length; i++)
            DrawFocusedLine(lines[i], color, destination, categoryIcon, i == 0);
    }

    private static string[] WrapFocusedText(string value, float availableWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in value.Replace("\r", string.Empty).Split('\n'))
        {
            var words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (ImGui.CalcTextSize(candidate).X <= availableWidth)
                {
                    current = candidate;
                    continue;
                }
                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = string.Empty;
                }
                // Only break an individual word when the word itself is too wide.
                // Normal words should not incur per-character font measurements.
                if (ImGui.CalcTextSize(word).X <= availableWidth)
                {
                    current = word;
                    continue;
                }
                foreach (var character in word)
                {
                    var next = current + character;
                    if (current.Length > 0 && ImGui.CalcTextSize(next).X > availableWidth)
                    {
                        lines.Add(current);
                        current = string.Empty;
                    }
                    current += character;
                }
            }
            lines.Add(current);
        }
        return lines.ToArray();
    }

    // Watchlist category labels identify target kinds in a list; the HUD title
    // itself should stay clean whether category icons are enabled or disabled.
    private static string FormatFocusedTitle(PinnedTargetConfig pin, string title)
    {
        if (title.StartsWith("[SET] ", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("[JOB] ", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("[ITEM] ", StringComparison.OrdinalIgnoreCase) ||
            title.StartsWith("[DUTY] ", StringComparison.OrdinalIgnoreCase))
        {
            var end = title.IndexOf(']');
            return title[(end + 1)..].TrimStart();
        }
        return title;
    }

    private uint? GetCachedFocusedGameIcon(PinnedTargetConfig pin)
    {
        var key = PinnedTargetCacheKey(pin);
        if (focusedIconIds.TryGetValue(key, out var icon))
            return icon;
        icon = ResolveFocusedGameIcon(pin);
        focusedIconIds[key] = icon;
        return icon;
    }

    // Use real item icons for collections instead of guessing arbitrary UI icon IDs.
    // Unknown categories remain icon-free rather than showing misleading artwork.
    private uint? ResolveFocusedGameIcon(PinnedTargetConfig pin)
    {
        if (pin.Kind.Equals("Item", StringComparison.OrdinalIgnoreCase) && pin.NumericId != 0)
        {
            try
            {
                var item = DataManager.GetExcelSheet<Item>().GetRow(pin.NumericId);
                return item.RowId != 0 && item.Icon != 0 ? item.Icon : null;
            }
            catch
            {
                return null;
            }
        }

        if (IsJobPin(pin) &&
            pin.NumericId is > 0 and <= 99)
        {
            // Use the real ClassJob emblem as the primary icon for both unlock
            // and progression targets. Quest-type markers are only a fallback.
            return 62000u + pin.NumericId;
        }

        if (pin.Kind.Equals("Set", StringComparison.OrdinalIgnoreCase) &&
            setCatalog.Get(pin.StringId) is { } set)
        {
            // Prefer a reward item (e.g. the expansion's completion mount).
            // Relic sets use a member belonging to the chosen class/job variant.
            var candidateIds = set.ItemIds
                .Where(id => string.IsNullOrWhiteSpace(pin.Variant) ||
                             catalog.GetById(id)?.EquipJobs.Contains(pin.Variant) == true)
                .ToArray();
            if (candidateIds.Length == 0)
                return null;
            var representative = candidateIds.FirstOrDefault(id => set.CompletionRewardItemIds.Contains(id));
            if (representative == 0)
                representative = candidateIds[0];
            try
            {
                var item = DataManager.GetExcelSheet<Item>().GetRow(representative);
                return item.RowId != 0 && item.Icon != 0 ? item.Icon : null;
            }
            catch
            {
                return null;
            }
        }

        // Non-item targets without a reliable game-data icon do not get a
        // fabricated quest icon. In particular, a duty is not a quest.
        return null;
    }

    private void DrawFocusedGameIcon(uint iconId, Vector2 center)
    {
        // The 620xx family contains class/job emblems. If the emblem is
        // unavailable, fall back to the appropriate quest-type icon family.
        // Never apply this fallback to ordinary item or collection icons.
        if (TryDrawFocusedGameIcon(iconId, center))
            return;
        if (iconId is > 62000 and < 62100)
        {
            var classJobId = iconId - 62000u;
            var isBaseClass = classJobId is >= 1 and <= 18 or 29;
            TryDrawFocusedGameIcon((isBaseClass ? 62300u : 62400u) + classJobId, center);
        }
    }

    // Called at plugin initialization and whenever a focused target selection changes.
    // This only loads icons for active focused targets, never the entire catalog.
    private void PrimeFocusedIconRentals()
    {
        var needed = new HashSet<uint>();
        if (config.FocusedTrackingShowIcons)
        {
            foreach (var icon in focusedDisplayIcons.Values)
            {
                if (icon is not { } id) continue;
                needed.Add(id);
                if (id is > 62000 and < 62100)
                {
                    var classJobId = id - 62000u;
                    var isBaseClass = classJobId is >= 1 and <= 18 or 29;
                    needed.Add((isBaseClass ? 62300u : 62400u) + classJobId);
                }
            }
        }
        ReleaseUnusedFocusedIconRentals(needed);
        foreach (var id in needed)
        {
            if (focusedIconRentals.ContainsKey(id)) continue;
            try
            {
                if (TextureProvider.TryGetFromGameIcon(new GameIconLookup { IconId = id }, out var shared) && shared is not null)
                    focusedIconRentals[id] = shared.RentAsync();
            }
            catch
            {
                // Cosmetic resources must never interfere with target guidance.
            }
        }
    }

    private void PrimeFocusedIconsOnStartup()
    {
        focusedIconIds.Clear();
        focusedDisplayIcons.Clear();
        if (config.FocusedTrackingShowIcons)
        {
            foreach (var pin in config.PinnedTargets.Where(pin => pin.FocusedTrackingEnabled).Take(MaxFocusedTargets))
                focusedDisplayIcons[PinnedTargetCacheKey(pin)] = GetCachedFocusedGameIcon(pin);
        }
        PrimeFocusedIconRentals();
    }

    private void ReleaseUnusedFocusedIconRentals(HashSet<uint> keep)
    {
        foreach (var id in focusedIconRentals.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            var leaseTask = focusedIconRentals[id];
            focusedIconRentals.Remove(id);
            if (leaseTask.IsCompletedSuccessfully)
                leaseTask.Result.Dispose();
            else
                _ = leaseTask.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                }, TaskScheduler.Default);
        }
    }

    private bool TryDrawFocusedGameIcon(uint iconId, Vector2 center)
    {
        // All texture acquisition occurs in PrimeFocusedIconRentals, never Draw.
        if (!focusedIconRentals.TryGetValue(iconId, out var rented) || !rented.IsCompletedSuccessfully)
            return false;
        try
        {
            const float size = 18f;
            var half = new Vector2(size / 2f);
            ImGui.GetWindowDrawList().AddImage(rented.Result.Handle, center - half, center + half);
            return true;
        }
        catch { return false; }
    }

    private static uint PackFocusedOpaqueColor(Vector4 color)
    {
        static uint Byte(float component) => (uint)(Math.Clamp(component, 0f, 1f) * 255f + 0.5f);
        // ImDrawList expects ABGR packed pixels. Force alpha to 255 instead of
        // inheriting the host/theme's global text or window opacity.
        return 0xFF000000u | (Byte(color.Z) << 16) | (Byte(color.Y) << 8) | Byte(color.X);
    }

    private void DrawFocusedLine(string line, Vector4 color, PinnedLocationHint? destination, uint? categoryIcon = null, bool firstTitleLine = false)
    {
        var iconSpace = categoryIcon is null ? 0f : 20f;
        var lineAvailable = ImGui.GetContentRegionAvail().X;
        if (!config.FocusedTrackingLeftAligned)
        {
            var offset = lineAvailable - iconSpace - ImGui.CalcTextSize(line).X;
            if (offset > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        }
        // Render directly to the HUD draw list so the text is opaque even when
        // the main ImGui style uses a reduced global alpha. Do not change the
        // window/background alpha: normal mode must remain transparent.
        var position = ImGui.GetCursorScreenPos();
        if (firstTitleLine && categoryIcon is not null)
        {
            var rightEdge = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            DrawFocusedGameIcon(categoryIcon.Value, new Vector2(rightEdge - 9f, position.Y + ImGui.GetTextLineHeight() * 0.5f));
        }
        var draw = ImGui.GetWindowDrawList();
        var outline = 0xF5000000u; // 96%-opaque black (ABGR)
        var foreground = PackFocusedOpaqueColor(color);
        draw.AddText(position + new Vector2(-1f, 0f), outline, line);
        draw.AddText(position + new Vector2( 1f, 0f), outline, line);
        draw.AddText(position + new Vector2(0f, -1f), outline, line);
        draw.AddText(position + new Vector2(0f,  1f), outline, line);
        draw.AddText(position + new Vector2(1f, 1f), 0xCC000000u, line);
        draw.AddText(position, foreground, line);
        // Draw-list text does not advance the layout cursor automatically.
        var textSize = new Vector2(Math.Max(1f, ImGui.CalcTextSize(line).X), ImGui.GetTextLineHeight());
        if (destination is null || string.IsNullOrWhiteSpace(line))
        {
            ImGui.Dummy(textSize);
            return;
        }
        ImGui.InvisibleButton($"##focus-map-{focusedClickRegions.Count}", textSize);
        focusedClickRegions.Add((ImGui.GetItemRectMin(), ImGui.GetItemRectMax()));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show destination on map");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            OpenFocusedMapLocation(destination);
    }
    private void OpenFocusedMapLocation(PinnedLocationHint hint)
    {
        if (hint.IsNpcLocation && !string.IsNullOrWhiteSpace(hint.NpcName))
        {
            var location = hint.Location ?? npcLocations.GetBestLocation(hint.NpcName!);
            if (!string.IsNullOrWhiteSpace(location))
                OpenMapLocation(location, hint.NpcName, hint.NpcId);
            return;
        }
        if (hint.IsLocationOnly && !string.IsNullOrWhiteSpace(hint.Location))
        {
            OpenMapLocation(hint.Location, null, null);
            return;
        }
        var parts = hint.Value.Split(" — ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var npc = parts.Length > 1 ? parts[0] : hint.Value;
        var place = parts.Length > 1 ? string.Join(" — ", parts.Skip(1)) : npcLocations.GetBestLocation(npc);
        if (!string.IsNullOrWhiteSpace(place))
            OpenMapLocation(place, npc, hint.NpcId);
    }
}
