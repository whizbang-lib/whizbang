// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Workers;

namespace ECommerce.Integration.TestUtilities.Fixtures;

/// <summary>
/// The one implementation behind every ECommerce integration fixture's
/// <c>WaitForPerspectiveProcessingAsync</c>: waits until the given perspective workers have
/// applied a number of events that belong to the given streams.
/// </summary>
/// <remarks>
/// <para>
/// The stream filter is REQUIRED and there is deliberately no unfiltered variant. A shared
/// fixture's perspective workers keep firing <see cref="PerspectiveWorker.OnPerspectiveEventProcessed"/>
/// for a previous test's in-flight events after that test ends (the per-test cleanup can even
/// deadlock against them). An unfiltered counter lets those stale fires satisfy the wait before
/// the current test's events are applied, so the test then reads a perspective row that does not
/// exist yet. Counting only the current test's streams makes the wait mean what the caller wants.
/// </para>
/// <para>
/// The handler fires once per (perspective, stream) batch with <c>EventCount</c> set to the number
/// of events applied in that batch. This waiter sums <c>EventCount</c>, so callers state a precise
/// event-application total regardless of how the worker happened to batch them. Example: a
/// CreateProductCommand with InitialStock &gt; 0 applies 3 events on the inventory host
/// (ProductCreated to two perspectives, InventoryRestocked to one), all on the ProductId stream;
/// <c>expectedCompletions: 3</c> waits for all 3 whether they arrive in 2 batches or 3.
/// </para>
/// </remarks>
public static class PerspectiveProcessingWaiter {
  /// <summary>
  /// Waits until <paramref name="workers"/> have applied at least
  /// <paramref name="expectedCompletions"/> events whose stream id is in <paramref name="streamIds"/>.
  /// </summary>
  /// <param name="workers">Perspective workers to observe; null entries are skipped.</param>
  /// <param name="expectedCompletions">Total event applications, across all workers and streams.</param>
  /// <param name="streamIds">Streams whose events count; must not be empty.</param>
  /// <param name="timeoutMilliseconds">Caller-side wait timeout, scaled by <see cref="Whizbang.Testing.TestTimeouts"/>.</param>
  public static async Task WaitAsync(
      IEnumerable<PerspectiveWorker?> workers,
      int expectedCompletions,
      IReadOnlyCollection<Guid> streamIds,
      int timeoutMilliseconds) {
    ArgumentNullException.ThrowIfNull(workers);
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      throw new ArgumentException("At least one stream id is required: an unfiltered wait counts other tests' events.", nameof(streamIds));
    }

    var streams = new HashSet<Guid>(streamIds);
    var eventCount = 0;
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    void Handler(PerspectiveEventProcessedEvent e) {
      if (!streams.Contains(e.StreamId)) {
        return;
      }
      var current = Interlocked.Add(ref eventCount, e.EventCount);
      if (current >= expectedCompletions) {
        tcs.TrySetResult(true);
      }
    }

    var wired = new List<PerspectiveWorker>();
    foreach (var worker in workers) {
      if (worker is null) {
        continue;
      }
      worker.OnPerspectiveEventProcessed += Handler;
      wired.Add(worker);
    }

    try {
      var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(timeoutMilliseconds);
      await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout)).ConfigureAwait(false);
    } finally {
      foreach (var worker in wired) {
        worker.OnPerspectiveEventProcessed -= Handler;
      }
    }
  }

  /// <summary>
  /// Waits until, on every stream in <paramref name="streamIds"/>, every perspective in <paramref name="expectations"/>
  /// has applied at least its <see cref="PerspectiveApplyExpectation.EventsPerStream"/> distinct events (#2237).
  /// </summary>
  /// <remarks>
  /// Distinct event ids per (perspective, stream), not a sum of batch sizes: a batch can name an event an earlier batch
  /// already reported (a rewind replays its stream), and a summed wait met by those reports returned while another
  /// stream's apply was still pending. A pair that never meets its expectation keeps this wait open, so a lost apply
  /// surfaces as a timeout naming the pair, not as a read that comes up short.
  /// </remarks>
  /// <param name="sources">The perspective workers to observe; null entries (a host with none) are skipped.</param>
  /// <param name="streamIds">The streams that must be applied; must not be empty.</param>
  /// <param name="expectations">What each perspective must apply per stream; must not be empty.</param>
  /// <param name="cancellationToken">Ends the wait; it then throws <see cref="TimeoutException"/> naming what is missing.</param>
  /// <exception cref="TimeoutException">The wait ended before every pair met its expectation.</exception>
  public static async Task WaitForAppliedAsync(
      IEnumerable<IPerspectiveEventProcessedSource?> sources,
      IReadOnlyCollection<Guid> streamIds,
      IReadOnlyList<PerspectiveApplyExpectation> expectations,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(streamIds);
    ArgumentNullException.ThrowIfNull(expectations);
    if (streamIds.Count == 0) {
      throw new ArgumentException("At least one stream id is required: an unfiltered wait counts other tests' events.", nameof(streamIds));
    }
    if (expectations.Count == 0) {
      throw new ArgumentException("At least one perspective expectation is required.", nameof(expectations));
    }

    var required = expectations.ToDictionary(e => _perspectiveName(e.Perspective), e => e.EventsPerStream, StringComparer.Ordinal);
    var applied = new Dictionary<(string Perspective, Guid Stream), HashSet<Guid>>();
    foreach (var perspective in required.Keys) {
      foreach (var stream in streamIds) {
        applied[(perspective, stream)] = [];
      }
    }
    var unmet = applied.Count;
    var gate = new Lock();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    void Handler(PerspectiveEventProcessedEvent e) {
      lock (gate) {
        if (!applied.TryGetValue((e.PerspectiveName, e.StreamId), out var ids)) {
          return;
        }
        var need = required[e.PerspectiveName];
        var wasMet = ids.Count >= need;
        ids.UnionWith(e.EventIds);
        if (!wasMet && ids.Count >= need && --unmet == 0) {
          done.TrySetResult();
        }
      }
    }

    var wired = new List<IPerspectiveEventProcessedSource>();
    foreach (var source in sources) {
      if (source is null) {
        continue;
      }
      source.OnPerspectiveEventProcessed += Handler;
      wired.Add(source);
    }

    try {
      await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) {
      string missing;
      lock (gate) {
        missing = string.Join("; ", applied
          .Where(pair => pair.Value.Count < required[pair.Key.Perspective])
          .Select(pair => $"stream {pair.Key.Stream}: {pair.Key.Perspective} {pair.Value.Count} of {required[pair.Key.Perspective]}"));
      }
      throw new TimeoutException($"Perspective applies not seen before the wait ended: {missing}");
    } finally {
      foreach (var source in wired) {
        source.OnPerspectiveEventProcessed -= Handler;
      }
    }
  }

  private static string _perspectiveName(Type perspective) =>
    TypeNameFormatter.TryFormatClrTypeName(perspective, out var name)
      ? name
      : throw new ArgumentException($"{perspective} has no CLR name a perspective worker could report.", nameof(perspective));
}
