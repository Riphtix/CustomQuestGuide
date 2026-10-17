using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CustomQuestGuide;

/// <summary>
/// Lightweight in-process telemetry for CQG's outbound HTTP activity.
/// This is diagnostic only; it never performs network I/O itself.
/// </summary>
public sealed class NetworkRequestTracker
{
    private sealed class MutableStats
    {
        public int Requests;
        public int Successes;
        public int Failures;
        public int CacheHits;
        public int CacheMisses;
        public int Suppressed;
    }

    public readonly record struct ServiceStats(
        string Service,
        int Requests,
        int Successes,
        int Failures,
        int CacheHits,
        int CacheMisses,
        int Suppressed);

    private readonly ConcurrentDictionary<string, MutableStats> stats =
        new(StringComparer.OrdinalIgnoreCase);

    private MutableStats Get(string service) => stats.GetOrAdd(service, _ => new MutableStats());

    public void Request(string service) => System.Threading.Interlocked.Increment(ref Get(service).Requests);
    public void Success(string service) => System.Threading.Interlocked.Increment(ref Get(service).Successes);
    public void Failure(string service) => System.Threading.Interlocked.Increment(ref Get(service).Failures);
    public void CacheHit(string service) => System.Threading.Interlocked.Increment(ref Get(service).CacheHits);
    public void CacheMiss(string service) => System.Threading.Interlocked.Increment(ref Get(service).CacheMisses);
    public void Suppressed(string service) => System.Threading.Interlocked.Increment(ref Get(service).Suppressed);

    public void Reset() => stats.Clear();

    public IReadOnlyList<ServiceStats> Snapshot() =>
        stats
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ServiceStats(
                pair.Key,
                pair.Value.Requests,
                pair.Value.Successes,
                pair.Value.Failures,
                pair.Value.CacheHits,
                pair.Value.CacheMisses,
                pair.Value.Suppressed))
            .ToArray();
}
