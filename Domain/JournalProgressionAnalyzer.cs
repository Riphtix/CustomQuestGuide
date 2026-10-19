using System;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Pure, local journal-graph exploration. Prerequisite alternatives are routes,
/// not evidence that any one route is mandatory for a particular character.
/// Neither this result nor its preview paths may drive completion or pin handoffs.
/// </summary>
internal static class JournalProgressionAnalyzer
{
    internal sealed record Node(uint Id, ushort Level, IReadOnlyList<uint> Previous);
    internal sealed record Result(IReadOnlyList<uint> ProposedPath,
        IReadOnlyList<uint> OffPath, string Status, string Detail,
        IReadOnlyList<uint>? CommonPath = null, IReadOnlyList<uint>? AlternativeQuestIds = null,
        int RouteCount = 0, IReadOnlyList<IReadOnlyList<uint>>? Routes = null);

    // Keep diagnostics bounded even if later expansions introduce complex graphs.
    private const int MaxRoutes = 4096;

    internal static Result Analyze(IReadOnlyCollection<Node> nodes)
    {
        if (nodes.Count == 0)
            return new(Array.Empty<uint>(), Array.Empty<uint>(), "NoCandidates", "No journal-family quest records.");
        if (nodes.GroupBy(n => n.Id).Any(g => g.Count() != 1))
            return new(Array.Empty<uint>(), Array.Empty<uint>(), "DuplicateQuestIds", "Duplicate quest IDs in journal family.");

        var byId = nodes.ToDictionary(n => n.Id);
        var successors = byId.Keys.ToDictionary(id => id, _ => new HashSet<uint>());
        foreach (var node in nodes)
            foreach (var prior in node.Previous.Where(byId.ContainsKey))
                successors[prior].Add(node.Id);

        var terminals = nodes.Where(n => successors[n.Id].Count == 0).ToArray();
        if (terminals.Length == 0)
            return Blocked("CycleOrNoTerminal", "No terminal candidate; possible cycle.", byId.Keys);
        var maxLevel = terminals.Max(n => n.Level);
        var endpoints = terminals.Where(n => n.Level == maxLevel).ToArray();
        if (endpoints.Length != 1)
            return Blocked("AmbiguousEndpoint", $"{endpoints.Length} terminal quests share the highest level ({maxLevel}).", byId.Keys);

        var routes = new List<uint[]>();
        var reverse = new List<uint>();
        var visiting = new HashSet<uint>();
        var cycle = false;
        var exceeded = false;

        void Visit(uint id)
        {
            if (exceeded || cycle) return;
            if (!visiting.Add(id)) { cycle = true; return; }
            reverse.Add(id);
            var parents = byId[id].Previous.Where(byId.ContainsKey).Distinct().OrderBy(p => p).ToArray();
            if (parents.Length == 0)
            {
                if (routes.Count >= MaxRoutes) exceeded = true;
                else routes.Add(reverse.AsEnumerable().Reverse().ToArray());
            }
            else
                foreach (var parent in parents) Visit(parent);
            reverse.RemoveAt(reverse.Count - 1);
            visiting.Remove(id);
        }

        Visit(endpoints[0].Id);
        if (cycle) return Blocked("Cycle", "Cycle encountered in journal-family prerequisite graph.", byId.Keys);
        if (exceeded) return Blocked("RouteLimit", $"More than {MaxRoutes} prerequisite routes; cannot safely summarize.", byId.Keys);
        if (routes.Count == 0) return Blocked("NoRoutes", "No acyclic route to terminal quest.", byId.Keys);

        // Stable representative for UI inspection, never a character-applicable choice.
        var representative = routes.OrderByDescending(r => r.Length)
            .ThenBy(r => string.Join("/", r.Select(id => id.ToString("D10"))), StringComparer.Ordinal)
            .First();
        var common = routes.Select(r => r.ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; });
        var all = routes.SelectMany(r => r).ToHashSet();
        var offPath = byId.Keys.Where(id => !all.Contains(id)).OrderBy(id => id).ToArray();
        var alternatives = all.Where(id => !common.Contains(id)).OrderBy(id => id).ToArray();
        var commonOrdered = representative.Where(common.Contains).ToArray();
        var status = routes.Count == 1 ? "ProposedOnly" : "AlternativeRoutes";
        var detail = routes.Count == 1
            ? $"One structural route ({representative.Length} quests); {offPath.Length} off-route quests. Membership and eligibility unverified."
            : $"{routes.Count} structural prerequisite routes; {commonOrdered.Length} shared quests, {alternatives.Length} route-specific quests, {offPath.Length} off-route quests. AND/OR joins, starting-class eligibility and mandatory membership remain unverified.";
        return new(representative, offPath, status, detail, commonOrdered, alternatives, routes.Count,
            routes.Select(r => (IReadOnlyList<uint>)r).ToArray());
    }

    /// <summary>
    /// Diagnostic evidence only. Completed quests can rule out some structural
    /// alternatives, but cannot prove a route is mandatory or character-applicable.
    /// In particular a new class with no completed quests must stay unresolved.
    /// </summary>
    internal static (string Status, IReadOnlyList<uint> Route, int CompatibleCount, string Detail)
        CompareCompletedRoutes(Result result, IReadOnlySet<uint> completed)
    {
        if (result.Routes is not { Count: > 0 } routes)
            return ("NoStructuralRoute", Array.Empty<uint>(), 0, "No candidate routes to compare.");
        var alternativeIds = (result.AlternativeQuestIds ?? Array.Empty<uint>()).ToHashSet();
        var completedAlternatives = completed.Where(alternativeIds.Contains).ToArray();
        if (completedAlternatives.Length == 0)
            return ("NoVariantEvidence", Array.Empty<uint>(), routes.Count,
                "No completed route-specific quests; starting-class eligibility still requires verification.");
        var compatible = routes.Where(route => completedAlternatives.All(route.Contains)).ToArray();
        if (compatible.Length == 1)
            return ("SingleObservedRoute", compatible[0], 1,
                "One route contains all completed route-specific quests. This is evidence only, not proof of mandatory membership.");
        if (compatible.Length == 0)
            return ("ConflictingCompletionEvidence", Array.Empty<uint>(), 0,
                "Completed route-specific quests cannot be explained by any single structural route.");
        return ("MultipleObservedRoutes", Array.Empty<uint>(), compatible.Length,
            $"{compatible.Length} routes remain compatible with completed route-specific quests.");
    }

    private static Result Blocked(string status, string detail, IEnumerable<uint> ids) =>
        new(Array.Empty<uint>(), ids.OrderBy(id => id).ToArray(), status, detail);
}
