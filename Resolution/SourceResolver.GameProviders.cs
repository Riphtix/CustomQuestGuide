using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CustomQuestGuide;

public sealed partial class SourceResolver
{
    // ---------------------------------------------------------------------
    // Game-sheet source providers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Recipe.ItemResult is authoritative for craftable items.
    /// </summary>
    private void IndexCrafting()
    {
        try
        {
            foreach (var recipe in data.GetExcelSheet<Recipe>())
            {
                var itemId = recipe.ItemResult.RowId;
                if (itemId == 0)
                    continue;

                string job = string.Empty;
                byte level = 0;

                try
                {
                    if (recipe.CraftType.IsValid)
                        job = recipe.CraftType.Value.Name.ExtractText().Trim();

                    if (recipe.RecipeLevelTable.IsValid)
                        level = recipe.RecipeLevelTable.Value.ClassJobLevel;
                }
                catch
                {
                    // A name/level is optional metadata. The source itself is
                    // still valid even if a generated reference fails.
                }

                var detail = (job.Length > 0, level > 0) switch
                {
                    (true, true) => $"{job} Lv. {level}",
                    (true, false) => job,
                    (false, true) => $"Lv. {level}",
                    _ => null,
                };

                // Crafting is deterministic and repeatable, so prefer it over
                // one-time quest/achievement rewards and legacy shop relationships
                // when an item has a normal recipe. Explicit repeatable duties can
                // still outrank it when they carry a stronger source priority.
                Add(itemId, new SourceEntry(
                    "Crafting",
                    "Crafted",
                    detail,
                    Priority: 15));

                var ingredients = ExtractRecipeIngredients(recipe);
                if (ingredients.Count > 0)
                {
                    var amountResultRaw = ReflectionUtil.GetNumericProperty(recipe, "AmountResult");
                    var amountResult = amountResultRaw.HasValue &&
                                       amountResultRaw.Value > 0 &&
                                       amountResultRaw.Value <= uint.MaxValue
                        ? (uint)amountResultRaw.Value
                        : 1u;

                    if (!craftingRecipes.TryGetValue(itemId, out var recipes))
                    {
                        recipes = [];
                        craftingRecipes[itemId] = recipes;
                    }

                    recipes.Add(new CraftingRecipeInfo(
                        recipe.RowId,
                        job,
                        level,
                        amountResult,
                        ingredients));
                }
            }

            foreach (var recipes in craftingRecipes.Values)
            {
                recipes.Sort((left, right) =>
                {
                    var levelCompare = left.Level.CompareTo(right.Level);
                    if (levelCompare != 0)
                        return levelCompare;

                    var jobCompare = string.Compare(
                        left.Job,
                        right.Job,
                        StringComparison.OrdinalIgnoreCase);
                    return jobCompare != 0
                        ? jobCompare
                        : left.RecipeId.CompareTo(right.RecipeId);
                });
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index crafting recipes.");
        }
    }

    private IReadOnlyList<CraftingIngredientInfo> ExtractRecipeIngredients(Recipe recipe)
    {
        var ingredientIds = ExtractRecipeRowIds(
            ReflectionUtil.GetPropertyValue(recipe, "Ingredient", "Ingredients"));
        var amounts = ExtractRecipeAmounts(
            ReflectionUtil.GetPropertyValue(recipe, "AmountIngredient", "AmountIngredients"));

        // Older/generated schema shapes may expose numbered members instead of
        // the grouped arrays. Keep this fallback so the feature survives a
        // Lumina schema revision without hard-coding generated types.
        if (ingredientIds.Count == 0)
        {
            for (var i = 0; i < 10; i++)
            {
                var id = ReflectionUtil.GetNestedRowId(
                    recipe,
                    $"Ingredient{i}",
                    $"ItemIngredient{i}");
                if (id == 0)
                    continue;

                ingredientIds.Add(id);
                var rawAmount = ReflectionUtil.GetNumericProperty(
                    recipe,
                    $"AmountIngredient{i}",
                    $"IngredientAmount{i}");
                amounts.Add(rawAmount.HasValue &&
                            rawAmount.Value > 0 &&
                            rawAmount.Value <= uint.MaxValue
                    ? (uint)rawAmount.Value
                    : 0u);
            }
        }

        var result = new List<CraftingIngredientInfo>();
        for (var i = 0; i < ingredientIds.Count; i++)
        {
            var ingredientId = ingredientIds[i];
            if (ingredientId == 0)
                continue;

            var amount = i < amounts.Count ? amounts[i] : 0u;
            if (amount == 0)
                continue;

            var name = itemsById.TryGetValue(ingredientId, out var item)
                ? item.Name.ExtractText().Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                name = $"Item {ingredientId}";

            result.Add(new CraftingIngredientInfo(ingredientId, name, amount));
        }

        return result;
    }

    private static List<uint> ExtractRecipeRowIds(object? value)
    {
        var result = new List<uint>();
        if (value is not IEnumerable values || value is string)
            return result;

        foreach (var entry in values)
            result.Add(ReflectionUtil.ExtractRowId(entry));

        return result;
    }

    private static List<uint> ExtractRecipeAmounts(object? value)
    {
        var result = new List<uint>();
        if (value is not IEnumerable values || value is string)
            return result;

        foreach (var entry in values)
        {
            try
            {
                result.Add(Convert.ToUInt32(entry));
            }
            catch
            {
                result.Add(0);
            }
        }

        return result;
    }

    /// <summary>
    /// GilShopItem is a SUBROW sheet. This is why the generic v0.5 provider
    /// both failed to compile and missed ordinary vendors.
    ///
    /// The shop entry identifies stock; Item.PriceMid is the purchase price.
    /// </summary>
    private void IndexGilShops()
    {
        try
        {
            foreach (var shopRows in data.GetSubrowExcelSheet<GilShopItem>())
            {
                var shopId = shopRows.RowId;
                var npcNames = ResolveShopNpcNames(shopId);
                var supplementalShopName = supplemental.GetShopName(shopId);

                foreach (var entry in shopRows)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                        continue;

                    uint price = 0;
                    if (itemsById.TryGetValue(itemId, out var item))
                        price = item.PriceMid;

                    var detailParts = new List<string>();

                    if (price > 0)
                        detailParts.Add($"{price:N0} gil");

                    if (!string.IsNullOrWhiteSpace(supplementalShopName))
                        detailParts.Add(supplementalShopName!);

                    var detail = detailParts.Count > 0
                        ? string.Join(" | ", detailParts)
                        : null;

                    if (npcNames.Count > 0)
                    {
                        foreach (var npc in npcNames)
                        {
                            Add(itemId, new SourceEntry(
                                "Vendor",
                                $"Vendor: {npc.Name}",
                                detail,
                                Priority: 35,
                                NpcId: npc.Id));
                        }
                    }
                    else
                    {
                        Add(itemId, new SourceEntry(
                            "Vendor",
                            "Gil Vendor",
                            detail,
                            Priority: 40));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index gil shops.");
        }
    }

    /// <summary>
    /// SpecialShop contains tomestone, scrip, mark, Wolf Mark, gemstone,
    /// upgrade-material and other exchange shops.
    ///
    /// This is the provider that resolves examples like Greatsword of the
    /// Behemoth King -> 2,000 Wolf Marks.
    /// </summary>
    private void IndexSpecialShops()
    {
        var resolvedItems = new HashSet<uint>();

        try
        {
            foreach (var shop in data.GetExcelSheet<SpecialShop>())
            {
                string shopName;

                try
                {
                    shopName = shop.Name.ExtractText().Trim();
                }
                catch
                {
                    shopName = string.Empty;
                }

                var supplementalShopName = supplemental.GetShopName(shop.RowId);
                if (string.IsNullOrWhiteSpace(shopName) ||
                    (ContainsCjk(shopName) && !string.IsNullOrWhiteSpace(supplementalShopName) && !ContainsCjk(supplementalShopName!)))
                {
                    shopName = supplementalShopName ?? string.Empty;
                }

                var npcNames = ResolveShopNpcNames(shop.RowId);

                foreach (var trade in shop.Item)
                {
                    try
                    {
                        // Index the RECEIVE side first. Cost parsing is optional
                        // metadata and must never be allowed to prevent an item
                        // from being recognized as a shop reward.
                        var received = new List<(uint ItemId, uint Count)>();

                        try
                        {
                            foreach (var reward in trade.ReceiveItems)
                            {
                                var itemId = reward.Item.RowId;
                                if (itemId == 0)
                                    continue;

                                var count = (uint)Math.Max(1, reward.ReceiveCount);
                                received.Add((itemId, count));
                            }
                        }
                        catch (Exception ex)
                        {
                            log.Debug(ex, $"Could not parse one SpecialShop receive entry in row {shop.RowId}.");
                            continue;
                        }

                        if (received.Count == 0)
                            continue;

                        var costs = new List<string>();
                        var structuredCosts = new List<(uint ItemId, uint Amount)>();

                        try
                        {
                            foreach (var cost in trade.ItemCosts)
                            {
                                var currencyId = cost.ItemCost.RowId;
                                var amount = cost.CurrencyCost;

                                if (amount == 0)
                                    continue;

                                // SpecialShop uses row-id 1 as a sentinel for a number
                                // of non-item currencies. Treating that as Item#1 (Gil)
                                // produces confidently wrong guidance such as "600 gil"
                                // for Poetics gear. Prefer a currency name recoverable
                                // from the shop identity and only create a dependency if
                                // that currency is represented by a real Item row.
                                if (currencyId == 1)
                                {
                                    if (TryInferSpecialShopCurrency(shopName, out var inferredCurrency))
                                    {
                                        costs.Add($"{amount:N0} {inferredCurrency}");
                                        if (itemIdsByName.TryGetValue(inferredCurrency, out var inferredIds) && inferredIds.Count > 0)
                                            structuredCosts.Add((inferredIds[0], amount));
                                    }

                                    // SpecialShop row 1 is a currency sentinel, not
                                    // literal Item#1/Gil. If the currency cannot be
                                    // named safely, omit the price instead of lying.
                                    continue;
                                }

                                if (currencyId == 0)
                                    continue;

                                costs.Add(FormatCost(currencyId, amount));
                                structuredCosts.Add((currencyId, amount));
                            }
                        }
                        catch (Exception ex)
                        {
                            // Some shop rows use non-item bucket currencies.
                            // That should only make the price less specific; the
                            // acquisition source itself is still valid.
                            log.Debug(ex, $"Could not parse SpecialShop costs in row {shop.RowId}.");
                        }

                        var detailParts = new List<string>();

                        if (costs.Count > 0)
                            detailParts.Add(string.Join(" + ", costs));

                        try
                        {
                            if (trade.Quest.IsValid && trade.Quest.RowId != 0)
                            {
                                var questName = RowName<Quest>(trade.Quest.RowId, "Name");
                                if (!string.IsNullOrWhiteSpace(questName))
                                    detailParts.Add($"Requires quest: {questName}");
                            }
                        }
                        catch
                        {
                            // Requirement metadata is optional.
                        }

                        // Old SpecialShop rows can still reference retired glamour
                        // prism catalysts. Those exchanges are no longer actionable in
                        // the live game and must not become a source or prerequisite
                        // tree for otherwise craftable items.
                        if (structuredCosts.Any(cost => IsRetiredLegacyCatalyst(cost.ItemId)))
                            continue;

                        var detail = detailParts.Count > 0
                            ? string.Join(" | ", detailParts)
                            : null;

                        var sourceKind = ClassifySpecialShopKind(shopName, detail);

                        foreach (var reward in received)
                        {
                            if (npcNames.Count > 0)
                            {
                                foreach (var npc in npcNames)
                                {
                                    var displayName = string.IsNullOrWhiteSpace(shopName)
                                        ? $"Vendor: {npc.Name}"
                                        : $"Special / Currency Shop: {shopName} — {npc.Name}";

                                    Add(reward.ItemId, new SourceEntry(
                                        sourceKind,
                                        displayName,
                                        detail,
                                        Priority: 32,
                                        NpcId: npc.Id));
                                }
                            }
                            else
                            {
                                var displayName = string.IsNullOrWhiteSpace(shopName)
                                    ? "Special / Currency Shop"
                                    : $"Special / Currency Shop: {shopName}";

                                Add(reward.ItemId, new SourceEntry(
                                    sourceKind,
                                    displayName,
                                    detail,
                                    Priority: 35));
                            }

                            foreach (var structuredCost in structuredCosts)
                            {
                                AddExchangeDependency(
                                    reward.ItemId,
                                    structuredCost.ItemId,
                                    structuredCost.Amount,
                                    shopName,
                                    npcNames.Select(x => x.Name).ToArray());
                            }

                            resolvedItems.Add(reward.ItemId);
                        }
                    }
                    catch (Exception ex)
                    {
                        // One malformed/odd trade must not abort the remaining
                        // thousands of SpecialShop entries.
                        log.Debug(ex, $"Could not index one SpecialShop trade in row {shop.RowId}.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index special/currency shops.");
        }

        SpecialShopSourceItems = resolvedItems.Count;
        log.Information($"CQG SpecialShop indexed {SpecialShopSourceItems:N0} distinct item rewards.");
    }

    private static bool TryInferSpecialShopCurrency(string shopName, out string currencyName)
    {
        currencyName = string.Empty;
        if (string.IsNullOrWhiteSpace(shopName))
            return false;

        var known = new[]
        {
            "Allagan Tomestone of Poetics",
            "Allagan Tomestone of Mathematics",
            "Allagan Tomestone of Heliometry",
            "Wolf Mark",
            "Trophy Crystal",
            "Manderville Gold Saucer Point",
            "MGP",
            "Allied Seal",
            "Centurio Seal",
            "Sack of Nuts",
            "Bicolor Gemstone",
            "Skybuilders' Scrip",
            "White Crafters' Scrip",
            "Purple Crafters' Scrip",
            "White Gatherers' Scrip",
            "Purple Gatherers' Scrip",
        };

        foreach (var candidate in known)
        {
            if (!shopName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                continue;

            currencyName = candidate.Equals("MGP", StringComparison.OrdinalIgnoreCase)
                ? "MGP"
                : candidate;
            return true;
        }

        // Historical tomestone shop names often use "Allagan Tomestones of X"
        // while the currency item is singular. Recover that form generically.
        var pluralMarker = "Allagan Tomestones of ";
        var pluralIndex = shopName.IndexOf(pluralMarker, StringComparison.OrdinalIgnoreCase);
        if (pluralIndex >= 0)
        {
            var tail = shopName[(pluralIndex + pluralMarker.Length)..];
            var stop = tail.IndexOfAny(['(', '—', '-', '|']);
            if (stop > 0)
                tail = tail[..stop];
            tail = tail.Trim();
            if (!string.IsNullOrWhiteSpace(tail))
            {
                currencyName = $"Allagan Tomestone of {tail}";
                return true;
            }
        }

        return false;
    }

    private static string ClassifySpecialShopKind(string shopName, string? detail)
    {
        var text = $"{shopName} {detail}";

        if (ContainsAny(text, "MGP", "Gold Saucer", "Cactpot", "Verminion", "Chocobo Racing"))
            return "Gold Saucer";
        if (ContainsAny(text, "Khloe", "Wondrous Tails", "Certificate of Commendation"))
            return "Wondrous Tails";
        if (ContainsAny(text, "Faux", "Unreal"))
            return "Faux Hollows";
        if (ContainsAny(text, "Wolf Mark", "Trophy Crystal", "Wolves' Den", "Crystalline Conflict", "PvP"))
            return "PvP";
        if (ContainsAny(text, "Allied Seal", "Centurio Seal", "Sack of Nuts", "Hunt Bill", "Clan Mark"))
            return "Hunts";
        if (ContainsAny(text, "Bicolor Gemstone", "Gemstone Trader", "Shared FATE"))
            return "Shared FATE";
        if (ContainsAny(text, "Skybuilder", "Firmament", "Ishgardian Restoration", "Kupo of Fortune"))
            return "Ishgardian Restoration";
        if (ContainsAny(text, "Cowrie", "Island Sanctuary"))
            return "Island Sanctuary";
        if (ContainsAny(text, "Cosmocredit", "Dronebit", "Cosmic", "Stellar"))
            return "Cosmic Exploration";
        if (ContainsAny(text, "Bozja", "Zadnor", "Eureka", "Occult Crescent", "Phantom"))
            return "Field Operation";
        if (ContainsAny(text, "Scrip Exchange", "Crafter's Scrip", "Gatherer's Scrip", "Crafters' Scrip", "Gatherers' Scrip"))
            return "Scrip Exchange";
        if (ContainsAny(text, "Allied Society", "Tribal", "Beast Tribe"))
            return "Allied Society";
        if (ContainsAny(text, "Achievement Certificate"))
            return "Achievement Certificate";

        return "Vendor";
    }

    private bool IsRetiredLegacyCatalyst(uint itemId)
    {
        var name = ItemName(itemId);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("Glamour Prism (", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Grade 2 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Grade 3 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Grade 4 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Grade 5 Glamour Prism", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grade 2 Carbonized Matter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grade 3 Carbonized Matter", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Grade 4 Carbonized Matter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsCjk(string text) =>
        text.Any(ch =>
            (ch >= '\u3040' && ch <= '\u30ff') ||
            (ch >= '\u3400' && ch <= '\u4dbf') ||
            (ch >= '\u4e00' && ch <= '\u9fff') ||
            (ch >= '\uac00' && ch <= '\ud7af'));

    /// <summary>
    /// Targeted v0.43 source semantics derived from coherent families in the
    /// post-v0.42 specificity backlog.  These rules identify a real named
    /// acquisition system without pretending to know an exact node/NPC/drop
    /// when the retail/supplemental providers do not expose one.
    /// </summary>
    private static SourceEntry? GetTargetedSpecificitySource(TargetEntry target)
    {
        var id = target.ItemId;
        var name = target.Name.Trim();
        var ui = target.GameUiCategory.Trim();

        // Container-backed rewards must resolve to the content that awards the
        // container, not to the internal ItemSupplement conversion edge. This
        // is the first concrete member of that policy and also serves as a
        // regression target for player-facing source ranking.
        if (name.Equals("Modern Aesthetics - A Close Shave", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Deep Dungeon",
                "Eureka Orthos — Bronze-tinged Sacks (floors 1–30)",
                "Obtain Bronze-tinged Sacks from the Accursed Hoard on floors 1–30 and have them appraised for a chance at this hairstyle.",
                Priority: 20,
                SpecificityHint: "Named");
        }

        // Allied Society quest-only gathering/fishing rows.  These contiguous
        // item families are the private quest materials introduced with each
        // gathering society and are explicitly not normal gathering-log items.
        if (id is >= 22616 and <= 22689)
        {
            return new SourceEntry(
                "Allied Society",
                "Namazu society quests",
                "Quest-only gathering/fishing material in the Azim Steppe.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 29512 and <= 29589)
        {
            return new SourceEntry(
                "Allied Society",
                "Qitari society quests",
                "Quest-only gathering/fishing material in the Rak'tika Greatwood.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 38276 and <= 38347)
        {
            return new SourceEntry(
                "Allied Society",
                "Omicron society quests",
                "Quest-only gathering/fishing material in Ultima Thule / Elysion.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (id is >= 48009 and <= 48083)
        {
            return new SourceEntry(
                "Allied Society",
                "Mamool Ja society quests",
                "Quest-only gathering/fishing material in Yak T'el.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        // Housing construction permits are purchased from residential-district
        // housing merchants.  The live GilShop relation knows the price but not
        // the useful vendor context.
        if (ui.Equals("Construction Permit", StringComparison.OrdinalIgnoreCase) &&
            name.Contains(" Permit (", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Housing",
                "Residential District Housing Merchant — construction permit",
                "Purchased for gil after acquiring the corresponding housing plot.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        // Magicked Prism (Heart) (singular) is the Platinum-plan reward tied
        // to The Ties That Bind, distinct from the plural Hearts vendor favor.
        if (name.Equals("Magicked Prism (Heart)", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ceremony",
                "The Ties That Bind — Platinum Ceremony Plan",
                "Ceremony of Eternal Bonding Platinum-plan reward.",
                Priority: 44,
                SpecificityHint: "Named");
        }

        // Ceremony favors all share the same Ceremony Outfitter inventory.
        if (name is "Magicked Prism (Balloon)" or "Magicked Prism (Bubbles)" or
            "Magicked Prism (Hearts)" or "Magicked Prism (Petals)")
        {
            return new SourceEntry(
                "Ceremony",
                "Ceremony Outfitter — East Shroud",
                "Ceremony of Eternal Bonding favor; 19 gil after unlocking the Ceremony Outfitter.",
                Priority: 45,
                SpecificityHint: "Named");
        }

        // Blue Mage Whalaqee totems are unlocked through BLU achievements and
        // claimed from one dedicated NPC.
        if (name.StartsWith("Whalaqee ", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(" Totem", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Blue Mage",
                "Wayward Gaheel Ja — Acquire Whalaqee Totems",
                "Ul'dah - Steps of Thal (X:12.5, Y:12.9); unlock requirement varies by Blue Mage achievement.",
                Priority: 42,
                SpecificityHint: "Named");
        }

        // Replica Cosmic tools are exchange rewards within Cosmic Exploration.
        if (name.EndsWith(" Replica", StringComparison.OrdinalIgnoreCase) &&
            ContainsAny(name, "Cosmic ", "Stellar ", "Hyper", " of Stars"))
        {
            return new SourceEntry(
                "Cosmic Exploration",
                "Cosmic Exploration replica-tool exchange",
                "Replica of a Cosmic Tool progression stage.",
                Priority: 72,
                SpecificityHint: "Named");
        }

        // ARR combat soul crystals are the level-30 job unlock rewards.  Crafter
        // souls belong to the specialist system rather than ordinary job quests.
        if (id is >= 4542 and <= 4548)
        {
            return new SourceEntry(
                "Job Progression",
                "ARR level 30 job quest",
                "Soul crystal awarded when unlocking the corresponding ARR job.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (id is >= 10337 and <= 10344)
        {
            return new SourceEntry(
                "Crafting Specialist",
                "Crafter Specialist system — Soul of the Crafter",
                "Specialist soul crystal for a crafting class.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        // The remaining generic currency rows can usually be assigned to the
        // content system encoded directly in the token/currency name.
        if (name.StartsWith("Allagan Tomestone of ", StringComparison.OrdinalIgnoreCase))
        {
            var detail = name.Equals("Allagan Tomestone of Poetics", StringComparison.OrdinalIgnoreCase)
                ? "Earned repeatedly from eligible duties, roulettes, hunts, and other level-capped content. Use repeatable activities rather than one-time quest rewards when farming a required amount."
                : "Earned from the eligible endgame duties, roulettes, hunts, and other reward systems for its patch/expansion. Availability changes as tomestones rotate into or out of circulation.";

            return new SourceEntry(
                "Tomestones",
                "Allagan Tomestone progression",
                detail,
                Priority: 5,
                SpecificityHint: "Named repeatable currency source");
        }

        if (name.Equals("Silver Chocobo Feather", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "World Population Incentive",
                "Preferred+ World character-creation bonus",
                "Eligible newly created characters on Preferred+ Worlds receive 10 Silver Chocobo Feathers on first login. Exchange them with Calamity Salvagers for leveling gear.",
                Priority: 35,
                SpecificityHint: "Named conditional acquisition source");
        }

        if (ContainsAny(name,
                "Crafters' Scrip", "Gatherers' Scrip",
                "Crafters' Scrip Token", "Gatherers' Scrip Token"))
        {
            return new SourceEntry(
                "Scrip Exchange",
                "Crafter / Gatherer Scrip system",
                "Collectable-delivery and scrip-exchange progression.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        if (name.Equals("Achievement Certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Achievement Certificate",
                "Jonathas — Achievement Certificate rewards",
                "Achievement reward currency/exchange in Old Gridania.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Faux Leaf", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Faux Hollows",
                "Faux Hollows",
                "Reward currency from Unreal-trial Faux Hollows.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Trophy Crystal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "PvP",
                "PvP Series — Trophy Crystals",
                "PvP Series currency spent at the Wolves' Den Pier.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Skybuilders' Scrip", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Ishgardian Restoration",
                "Ishgardian Restoration — Skybuilders' Scrips",
                "Firmament crafting/gathering reward currency.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Khloe's ", StringComparison.OrdinalIgnoreCase) &&
            name.Contains("Certificate of Commendation", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Wondrous Tails",
                "Wondrous Tails — Khloe's Certificate of Commendation",
                "Certificate reward from Wondrous Tails.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("Company Seal Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Grand Company",
                "Grand Company seal/voucher system",
                Priority: 85,
                SpecificityHint: "Named");
        }

        if (name is "Priority Seal Allowance" or "Mission Ceruleum Voucher")
        {
            return new SourceEntry(
                "Grand Company",
                "Adventurer Squadron missions",
                "Grand Company squadron mission item.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Equals("Cryptic Seal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Eureka",
                "The Forbidden Land, Eureka",
                "Eureka reward/exchange token.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Equals("Corvosi Manuscript", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Exploration",
                "The Merchant's Tale",
                "Corvos exploration currency/reward item.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.EndsWith(" Credit", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Exploration Token", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Star Contributor Certificate", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(name, "Lunar", "Phaenna", "Oizys", "Auxesia", "Star Contributor"))
            {
                return new SourceEntry(
                    "Cosmic Exploration",
                    "Cosmic Exploration",
                    "Zone credit/token or Starward Standings reward.",
                    Priority: 84,
                    SpecificityHint: "Named");
            }
        }

        if (name is "Inferno Totem" or "Inferno Seal")
        {
            return new SourceEntry(
                "Trial",
                "The Bowl of Embers (Extreme)",
                "Ifrit Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name is "Vortex Totem" or "Vortex Seal")
        {
            return new SourceEntry(
                "Trial",
                "The Howling Eye (Extreme)",
                "Garuda Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name.Equals("Tremor Seal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Navel (Extreme)",
                "Titan Extreme token/reward item.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        if (name.EndsWith(" Totem", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "Extreme Trial token",
                "Totem awarded by a specific Extreme Trial; exact trial remains in the exact-source backlog.",
                Priority: 91,
                SpecificityHint: "Named");
        }

        if (name is "Aglaia Coin" or "Euphrosyne Coin" or "Manufactured Coin" or
            "Puppet's Coin" or "Breach Coin" or "Ranperre Coin")
        {
            return new SourceEntry(
                "Alliance Raid",
                "Alliance Raid coin reward",
                "Coin awarded by its corresponding Alliance Raid.",
                Priority: 88,
                SpecificityHint: "Named");
        }

        if (ContainsAny(name,
                "Rowena's Token (", "Mowen's Token (",
                "Tomestone"))
        {
            return new SourceEntry(
                "Endgame Gear",
                "Tomestone / raid gear progression",
                "Historical endgame gear exchange or weapon-upgrade token.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        // Seasonal/collaboration families from the remaining legacy-event tail.
        // Old year-specific materials are preserved for historical/existing-copy
        // guidance rather than being mislabeled as currently recurring drops.
        if (name.StartsWith("Legendary ", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(" Medal", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Event",
                "Yo-kai Watch collaboration",
                "Legendary Yo-kai Medal used for collaboration weapon progression.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name.Contains("Archon Egg", StringComparison.OrdinalIgnoreCase) ||
            name is "Odd Egg" or "Lucky Egg" or "Special Brilliant Egg" or "Brittle Motley Egg")
        {
            return new SourceEntry(
                "Legacy Event",
                "Hatching-tide",
                "Historical Hatching-tide event material; existing copies only unless Square Enix reuses the item.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Bombard Ash", StringComparison.OrdinalIgnoreCase) ||
            name is "Blue Spinner" or "Red Lion" or "Green Comet" or "Azeyma's Candle")
        {
            return new SourceEntry(
                "Legacy Event",
                "Firefall Faire / Moonfire Faire",
                "Historical summer-event material or firework.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Faire Voucher", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Moonfire Faire",
                "Historical Moonfire Faire voucher.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Heavensturn ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cut Mochi", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Heavensturn",
                "Historical Heavensturn event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Valentione", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Valentione's Day",
                "Historical Valentione's Day event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Pumpkin Cookie", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Demonic Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "All Saints' Wake",
                "Historical All Saints' Wake event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Equals("Moth-eaten Doll Parts", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Certificate of Collaboration", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Little Ladies' Day",
                "Historical Little Ladies' Day event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.StartsWith("Rising Token", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "The Rising",
                "Historical Rising event token.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name is "Eternal Ice" or "Starlight Box" or "Twinklebox")
        {
            return new SourceEntry(
                "Legacy Event",
                "Starlight Celebration",
                "Historical Starlight Celebration event material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Contains("Otherworldly Parcel", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Legacy Event",
                "Lightning Strikes collaboration",
                "Historical FINAL FANTASY XIII collaboration quest/FATE material.",
                Priority: 90,
                SpecificityHint: "Named");
        }

        if (name.Equals("Deluxe Heavenscracker", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Out-of-game Promotion",
                "Official promotional campaigns",
                "Distributed through official partner campaigns, including Twitch/retail promotions.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        // Current/future currencies and encounter tokens that survived the
        // generic currency pass can be tied to their named content directly.
        if (name.Equals("Guardian Arkveld Certificate", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Windward Wilds (Extreme)",
                "Two Guardian Arkveld Certificates are awarded per clear.",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("Totem of Naught", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Trial",
                "The Unmaking (Extreme)",
                "Extreme-trial token exchanged for Weapons of Naught and related rewards.",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("MGF", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gold Saucer",
                "Blunderville — Manderville Gold Saucer Fame (MGF)",
                "Earned by participating in the Fall Guys collaboration's Blunderville shows.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        if (name.Equals("MGC", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Gold Saucer",
                "Keybound Brawler — Manderville Gold Credits (MGC)",
                "Gold Saucer Keybound Square currency spent at the MGC Trader.",
                Priority: 80,
                SpecificityHint: "Named");
        }

        // Dubious Dirt is not an ordinary mining collectible: it is gathered
        // specifically for the Namazu daily quest Overcommitment.
        if (name.Equals("Dubious Dirt", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Allied Society",
                "Namazu daily quest — Overcommitment",
                "Mine northeast of the Dawn Throne while undertaking Overcommitment.",
                Priority: 78,
                SpecificityHint: "Named");
        }

        // Dawntrail Wachumeqimeqi fishing quest catches are deliberately
        // absent from the ordinary Fish Guide; the quest itself is the real
        // acquisition gate and therefore the most useful source to show.
        if (name.Equals("Cenote Shark", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Fishing Quest",
                "Donuhanu's Devotion — Bitterbark Cenote",
                "Wachumeqimeqi Fisher quest catch in Yak T'el (around X:26.5, Y:39.0).",
                Priority: 76,
                SpecificityHint: "Named");
        }

        if (name.Equals("Fulmisturgeon", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Fishing Quest",
                "An Odd Fish Odyssey — Alexandrian Ruins",
                "Wachumeqimeqi Fisher quest catch in Heritage Found (around X:8.0, Y:34.0).",
                Priority: 76,
                SpecificityHint: "Named");
        }

        // Field-operation coffers are generic in LuminaSupplemental, but several
        // remaining families identify the actual field operation.
        if (name.StartsWith("Occult ", StringComparison.OrdinalIgnoreCase) ||
            name is "Horizontal Neon Wall Light" or "Vertical Neon Wall Light" or
                "Stone Partition" or "Aetherspun Gold" or "Esoteric Gold Piece" or
                "Fortune Carrot")
        {
            return new SourceEntry(
                "Occult Crescent",
                "The Occult Crescent — overworld treasure coffers / field rewards",
                "Available from Occult Crescent field-operation reward pools; exact coffer/encounter remains in the exact-source backlog.",
                Priority: 82,
                SpecificityHint: "Named");
        }

        if (name is "Archaeodemon Horns" or "Stuffed Happy Bunny")
        {
            return new SourceEntry(
                "Field Operation",
                "Eureka Happy Bunny / Occult Crescent treasure coffers",
                "Field-operation treasure reward with multiple current/historical routes.",
                Priority: 84,
                SpecificityHint: "Named");
        }

        if (name.Equals("Garden Mood Lighting", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceEntry(
                "Submarine Voyage",
                "Southern Aerslaent Plunge (K)",
                "The Northern Empty — Free Company Submarine Venture, rank 144.",
                Priority: 58,
                SpecificityHint: "Named");
        }

        return null;
    }

    /// <summary>
    /// Grand Company quartermaster stock lives in GCScripShopItem subrows.
    /// </summary>
    private void IndexGrandCompanyShops()
    {
        try
        {
            var categories = data.GetExcelSheet<GCScripShopCategory>();

            foreach (var shopRows in data.GetSubrowExcelSheet<GCScripShopItem>())
            {
                uint companyId = 0;

                if (categories.TryGetRow(shopRows.RowId, out var category))
                    companyId = category.GrandCompany.RowId;

                foreach (var entry in shopRows)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                        continue;

                    var seals = entry.CostGCSeals;
                    var sealName = companyId switch
                    {
                        1 => ItemName(20) ?? "Storm Seals",
                        2 => ItemName(21) ?? "Serpent Seals",
                        3 => ItemName(22) ?? "Flame Seals",
                        _ => "Grand Company seals",
                    };

                    Add(itemId, new SourceEntry(
                        "Vendor",
                        "Grand Company Quartermaster",
                        seals > 0 ? $"{seals:N0} {sealName}" : null,
                        Priority: 35));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index Grand Company shops.");
        }
    }

    /// <summary>
    /// Quest has explicit guaranteed and optional item-reward collections.
    /// Reading those is much more reliable than recursively reflecting every
    /// property whose name happens to contain "Item".
    /// </summary>
    private void IndexQuestRewards()
    {
        try
        {
            foreach (var quest in data.GetExcelSheet<Quest>())
            {
                var name = quest.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string detail = name;

                try
                {
                    if (quest.ClassJobLevel is [var level, ..] && level > 0)
                        detail = $"{name} (Lv. {level})";
                }
                catch
                {
                    // Quest name alone is enough.
                }

                foreach (var reward in quest.Reward)
                    AddQuestReward(reward.RowId, detail);

                foreach (var reward in quest.OptionalItemReward)
                    AddQuestReward(reward.RowId, detail);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index quest rewards.");
        }
    }

    private void AddQuestReward(uint itemId, string quest)
    {
        if (itemId == 0 || !itemsById.ContainsKey(itemId))
            return;

        Add(itemId, new SourceEntry(
            "Quest",
            quest,
            Priority: 20));
    }

    /// <summary>
    /// Achievement.Item is the achievement's direct item reward.
    /// Fully qualified to avoid the FFXIVClientStructs Achievement type.
    /// </summary>
    private void IndexAchievementRewards()
    {
        try
        {
            foreach (var achievement in data.GetExcelSheet<Lumina.Excel.Sheets.Achievement>())
            {
                var itemId = achievement.Item.RowId;
                if (itemId == 0)
                    continue;

                var name = achievement.Name.ExtractText().Trim();

                Add(itemId, new SourceEntry(
                    "Achievement",
                    string.IsNullOrWhiteSpace(name) ? "Achievement Reward" : name,
                    Priority: 20));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index achievement rewards.");
        }
    }

    /// <summary>
    /// GatheringItem maps gathering-sheet IDs to real Item IDs.
    /// GatheringPointBase then tells us which gathering type can produce them.
    /// </summary>
    private void IndexGathering()
    {
        try
        {
            var gatheringItems = data.GetExcelSheet<GatheringItem>()
                .Where(x => x.RowId != 0 && x.Item.RowId != 0)
                .ToDictionary(x => x.RowId, x => x.Item.RowId);

            // GatheringPointBase owns the item table, while GatheringPoint owns
            // the actual named map locations. Join them once up front so Mining,
            // Botany, Logging and Quarrying entries can be actionable rather
            // than merely naming the gathering method.
            var locationsByBase = new Dictionary<uint, HashSet<string>>();

            foreach (var point in data.GetExcelSheet<GatheringPoint>())
            {
                var baseId = ReflectionUtil.GetNestedRowId(
                    point,
                    "GatheringPointBase",
                    "Base");

                if (baseId == 0)
                    continue;

                var placeNameId = ReflectionUtil.GetNestedRowId(
                    point,
                    "PlaceName");

                var placeName = RowName<PlaceName>(
                    placeNameId,
                    "Name");

                if (string.IsNullOrWhiteSpace(placeName))
                    continue;

                if (!locationsByBase.TryGetValue(baseId, out var names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    locationsByBase[baseId] = names;
                }

                names.Add(placeName);
            }

            foreach (var point in data.GetExcelSheet<GatheringPointBase>())
            {
                string method = "Gathering";

                try
                {
                    if (point.GatheringType.IsValid)
                    {
                        var name = point.GatheringType.Value.Name.ExtractText().Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            method = name;
                    }
                }
                catch
                {
                    // "Gathering" is still truthful if the method name cannot be read.
                }

                locationsByBase.TryGetValue(point.RowId, out var locations);

                foreach (var gatheringItemRef in point.Item)
                {
                    var gatheringItemId = gatheringItemRef.RowId;

                    if (gatheringItemId == 0 ||
                        !gatheringItems.TryGetValue(gatheringItemId, out var itemId))
                        continue;

                    if (locations is { Count: > 0 })
                    {
                        foreach (var location in locations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                        {
                            Add(itemId, new SourceEntry(
                                "Gathering",
                                $"{method}: {location}",
                                Priority: 42));
                        }
                    }
                    else
                    {
                        Add(itemId, new SourceEntry(
                            "Gathering",
                            method,
                            Priority: 45));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index gathering sources.");
        }
    }

    /// <summary>
    /// FishingSpot stores up to ten Item references directly and includes the
    /// named fishing hole. SpearfishingItem links to SpearfishingNotebook,
    /// which provides the named spearfishing location. These are authoritative
    /// live-game relationships and therefore should replace the old broad
    /// "Fishing / Spearfishing" fallback whenever present.
    /// </summary>
    private void IndexFishingSpots()
    {
        try
        {
            foreach (var spot in data.GetExcelSheet<FishingSpot>())
            {
                var placeId =
                    ReflectionUtil.GetNestedRowId(spot, "PlaceName") != 0
                        ? ReflectionUtil.GetNestedRowId(spot, "PlaceName")
                        : ReflectionUtil.GetNestedRowId(spot, "PlaceNameSub", "PlaceNameMain");

                var place = RowName<PlaceName>(placeId, "Name");

                if (string.IsNullOrWhiteSpace(place))
                    continue;

                var level = ReflectionUtil.GetNumericProperty(spot, "GatheringLevel") ?? 0;
                var detail = level > 0 ? $"Fishing hole Lv. {level}" : "Fishing hole";

                var itemRefs = ReflectionUtil.GetPropertyValue(spot, "Item");
                if (itemRefs is not System.Collections.IEnumerable enumerable)
                    continue;

                foreach (var itemRef in enumerable)
                {
                    var itemId = ReflectionUtil.ExtractRowId(itemRef);
                    if (itemId == 0 || !itemsById.ContainsKey(itemId))
                        continue;

                    Add(itemId, new SourceEntry(
                        "Fishing",
                        $"Fishing: {place}",
                        detail,
                        Priority: 42));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named fishing spots.");
        }

        try
        {
            foreach (var spear in data.GetExcelSheet<SpearfishingItem>())
            {
                var itemId = ReflectionUtil.GetNestedRowId(spear, "Item");
                if (itemId == 0 || !itemsById.ContainsKey(itemId))
                    continue;

                var notebookId = ReflectionUtil.GetNestedRowId(
                    spear,
                    "SpearfishingNotebook");

                if (notebookId == 0)
                    continue;

                var notebook = data.GetExcelSheet<SpearfishingNotebook>().GetRow(notebookId);
                var placeId = ReflectionUtil.GetNestedRowId(notebook, "PlaceName");
                var place = RowName<PlaceName>(placeId, "Name");

                if (string.IsNullOrWhiteSpace(place))
                    continue;

                var level = ReflectionUtil.GetNumericProperty(notebook, "GatheringLevel") ?? 0;
                var detail = level > 0 ? $"Spearfishing Lv. {level}" : "Spearfishing";

                Add(itemId, new SourceEntry(
                    "Fishing",
                    $"Spearfishing: {place}",
                    detail,
                    Priority: 42));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named spearfishing locations.");
        }
    }

    /// <summary>
    /// RetainerTaskNormal provides an authoritative item relation while
    /// RetainerTask supplies the venture's job family and required retainer
    /// level.  Index both the linked normal-task row and the RetainerTask row
    /// so LuminaSupplemental RetainerVentureItem records can be named too.
    /// </summary>
    private void IndexRetainerVentures()
    {
        try
        {
            var normalItems = new Dictionary<uint, uint>();

            foreach (var normal in data.GetExcelSheet<RetainerTaskNormal>())
            {
                if (normal.RowId == 0)
                    continue;

                var itemId = ReflectionUtil.GetNestedRowId(normal, "Item");
                if (itemId != 0)
                    normalItems[normal.RowId] = itemId;
            }

            foreach (var task in data.GetExcelSheet<RetainerTask>())
            {
                if (task.RowId == 0)
                    continue;

                var linkedTaskId = ReflectionUtil.GetNestedRowId(task, "Task");
                if (linkedTaskId == 0)
                    continue;

                var isRandom = ReflectionUtil.GetBoolProperty(task, "IsRandom") == true;
                string? label;

                if (isRandom)
                {
                    label = RowName<RetainerTaskRandom>(linkedTaskId, "Name");
                    if (string.IsNullOrWhiteSpace(label))
                        label = "Quick Exploration";
                }
                else
                {
                    label = BuildRetainerVentureLabel(task);
                }

                if (!string.IsNullOrWhiteSpace(label))
                {
                    retainerVentureLabelsById[task.RowId] = label;
                    retainerVentureLabelsById[linkedTaskId] = label;
                }

                if (!isRandom &&
                    normalItems.TryGetValue(linkedTaskId, out var itemId) &&
                    itemsById.ContainsKey(itemId))
                {
                    Add(itemId, new SourceEntry(
                        "Retainer Venture",
                        $"Retainer Venture: {label ?? "Venture"}",
                        "Live retainer venture task",
                        Priority: 45,
                        SpecificityHint: "Named"));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index named retainer ventures.");
        }
    }

    private string BuildRetainerVentureLabel(RetainerTask task)
    {
        var categoryId = ReflectionUtil.GetNestedRowId(task, "ClassJobCategory");
        var categoryName = RowName<ClassJobCategory>(categoryId, "Name") ?? string.Empty;
        var level = ReflectionUtil.GetNumericProperty(task, "RetainerLevel") ?? 0;

        var family =
            categoryName.Contains("Miner", StringComparison.OrdinalIgnoreCase) ? "Mining" :
            categoryName.Contains("Botanist", StringComparison.OrdinalIgnoreCase) ? "Botany" :
            categoryName.Contains("Fisher", StringComparison.OrdinalIgnoreCase) ? "Fishing" :
            "Hunting";

        return level > 0
            ? $"{family} Venture Lv. {level}"
            : $"{family} Venture";
    }

    /// <summary>
    /// Battlecraft / tradecraft / fieldcraft leve rewards.
    ///
    /// Many ARR "Aetherial" equipment rows come through the Leve reward tables
    /// rather than normal dungeon/shop/quest relationships.  The relationship
    /// is:
    ///
    /// Leve -> LeveRewardItem -> LeveRewardItemGroup -> Item
    /// </summary>
    private void IndexLeveRewards()
    {
        try
        {
            var rewardItems = data.GetExcelSheet<LeveRewardItem>();
            var rewardGroups = data.GetExcelSheet<LeveRewardItemGroup>();

            foreach (var leve in data.GetExcelSheet<Leve>())
            {
                if (!leve.LeveRewardItem.IsValid)
                    continue;

                var leveName = leve.Name.ExtractText().Trim();
                if (string.IsNullOrWhiteSpace(leveName))
                    leveName = $"Leve #{leve.RowId}";

                var rewardRowId = leve.LeveRewardItem.RowId;

                if (!rewardItems.TryGetRow(rewardRowId, out var rewardItem))
                    continue;

                foreach (var groupRef in rewardItem.LeveRewardItemGroup)
                {
                    if (!groupRef.IsValid || groupRef.RowId == 0)
                        continue;

                    if (!rewardGroups.TryGetRow(groupRef.RowId, out var group))
                        continue;

                    foreach (var itemRef in group.Item)
                    {
                        var itemId = itemRef.RowId;

                        if (itemId == 0 || !itemsById.ContainsKey(itemId))
                            continue;

                        Add(itemId, new SourceEntry(
                            "Levequest",
                            $"Levequest: {leveName}",
                            leve.ClassJobLevel > 0
                                ? $"Lv. {leve.ClassJobLevel}"
                                : null,
                            Priority: 25));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not index levequest rewards.");
        }
    }
}
