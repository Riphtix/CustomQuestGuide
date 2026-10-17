using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CustomQuestGuide;

/// <summary>
/// Small reusable scheduler for network-backed resolution work.
///
/// Responsibilities are intentionally narrow:
/// - de-duplicate work by stable key;
/// - bound concurrency;
/// - pace request starts without serializing request completion;
/// - expose queued/running state without leaking worker implementation details;
/// - isolate handler failures so one bad key cannot stop the queue.
///
/// Provider-specific parsing, retry policy, caching, and merge semantics belong
/// to the provider using this scheduler. New enrichers can therefore reuse the
/// same execution primitive without adding another ad-hoc queue to Plugin.cs.
/// </summary>
internal sealed class KeyedWorkScheduler<TKey> : IDisposable where TKey : notnull
{
    private readonly ConcurrentQueue<TKey> queue = new();
    private readonly ConcurrentDictionary<TKey, byte> pending = new();
    private readonly ConcurrentDictionary<TKey, byte> rerunRequested = new();
    private readonly SemaphoreSlim signal = new(0);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly Func<TKey, CancellationToken, Task> handler;
    private readonly TimeSpan minimumStartInterval;
    private readonly CancellationTokenSource cts;
    private readonly List<Task> workers = [];

    private DateTime nextStartUtc = DateTime.MinValue;
    private int disposed;

    public KeyedWorkScheduler(
        int maxConcurrency,
        TimeSpan minimumStartInterval,
        Func<TKey, CancellationToken, Task> handler,
        CancellationToken parentToken = default)
    {
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        this.minimumStartInterval = minimumStartInterval < TimeSpan.Zero
            ? TimeSpan.Zero
            : minimumStartInterval;
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
        cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        for (var i = 0; i < maxConcurrency; i++)
            workers.Add(Task.Run(WorkerAsync));
    }

    public int PendingCount => pending.Count;

    public bool IsPending(TKey key) => pending.ContainsKey(key);

    public bool TryQueue(TKey key, bool rerunIfPending = false)
    {
        if (Volatile.Read(ref disposed) != 0 || cts.IsCancellationRequested)
            return false;

        if (!pending.TryAdd(key, 0))
        {
            // Some fan-out workloads can discover a genuinely new consumer while
            // the key is already running. Remember one follow-up pass rather than
            // dropping that late arrival in the completion race. Repeated requests
            // coalesce to one rerun.
            if (rerunIfPending)
                rerunRequested[key] = 0;
            return false;
        }

        queue.Enqueue(key);
        signal.Release();
        return true;
    }

    private async Task WorkerAsync()
    {
        var token = cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                await signal.WaitAsync(token).ConfigureAwait(false);
                if (!queue.TryDequeue(out var key))
                    continue;

                try
                {
                    await WaitForStartSlotAsync(token).ConfigureAwait(false);
                    await handler(key, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Provider handlers own logging/retry semantics. The scheduler
                    // must remain alive even if a handler unexpectedly escapes an
                    // exception, so the next key can still run.
                }
                finally
                {
                    // Remove pending first. A caller racing after this point will
                    // queue normally; a caller that raced before it records a rerun.
                    // This ordering closes the late-subscriber completion window.
                    pending.TryRemove(key, out _);
                    if (!token.IsCancellationRequested &&
                        rerunRequested.TryRemove(key, out _) &&
                        pending.TryAdd(key, 0))
                    {
                        queue.Enqueue(key);
                        signal.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task WaitForStartSlotAsync(CancellationToken token)
    {
        if (minimumStartInterval <= TimeSpan.Zero)
            return;

        await startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var wait = nextStartUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, token).ConfigureAwait(false);

            nextStartUtc = DateTime.UtcNow.Add(minimumStartInterval);
        }
        finally
        {
            startGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        // Cancellation is sufficient for plugin lifetime. Do not dispose the
        // semaphores out from under workers that may still be unwinding from
        // WaitAsync/Task.Delay; that would turn clean shutdown into unobserved
        // ObjectDisposedException noise.
        cts.Cancel();
    }
}
