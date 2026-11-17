// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Workers;

/// <summary>
/// Says when every work-table write this instance has queued has landed (#1899): the completions, failures, lease
/// renewals and handler commits the drain workers hand to the flush workers and that are written later.
/// </summary>
/// <remarks>
/// <para>
/// The drain workers report idle as soon as they have queued their writes, so a caller that waits for them and then
/// touches the work tables (a test fixture cleaning between tests, an operator before a manual purge) also waits on
/// this, which completes only once those writes are in the database or have been given up.
/// </para>
/// <para>
/// It covers queued writes, not the whole pipeline: a write that lands can make new work that is claimed and drained
/// again, so a caller that needs the pipeline quiet waits for the drain workers' idle signals and for this, together.
/// </para>
/// </remarks>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/QueuedWritesLandedSignalTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/QueuedWritesLandedRegistrationTests.cs</tests>
public interface IQueuedWritesLandedSignal {
  /// <summary>The flushers watched: every worker hosted with its start point that reports queued writes.</summary>
  IReadOnlyList<IQueuedWriteFlusher> Flushers { get; }

  /// <summary>True when every flusher was idle at the same moment, with nothing offered to any of them since.</summary>
  bool AllLanded { get; }

  /// <summary>The writes pending across every flusher, summed from one reading of each.</summary>
  long PendingWrites { get; }

  /// <summary>Completes once every flusher has been idle at the same moment.</summary>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>A task that completes when every queued write has landed or been given up.</returns>
  Task WaitForAllLandedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IQueuedWritesLandedSignal"/>: every flusher idle at one moment, shown by reading each flusher
/// twice. The worker pipeline builds it over every worker hosted with its start point that implements
/// <see cref="IQueuedWriteFlusher"/>.
/// </summary>
/// <remarks>
/// Reading the flushers one after another is not enough: a flush can queue a write to another flusher (a handler commit
/// that fails queues a failure), so each can be read idle at a different moment and never all at once. Each flusher is
/// read twice, all of them once and then all of them again; when every first reading is idle and every second reading
/// equals the first, no flusher was offered anything between its two readings, so each stayed idle across them, and the
/// moment between the last first reading and the first second reading is one when all were idle. The wait waits on each
/// flusher's own idle signal and then reads; when the reading does not settle, it waits again. It never polls.
/// </remarks>
/// <docs>fundamentals/work-coordinator/batched-flushers#when-writes-have-landed</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/QueuedWritesLandedSignalTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/QueuedWritesLandedRegistrationTests.cs:TheWorkerPipeline_RegistersTheSignal_OverEveryHostedFlushWorkerAsync</tests>
public sealed class QueuedWritesLandedSignal : IQueuedWritesLandedSignal {
  private readonly Lazy<IReadOnlyList<IQueuedWriteFlusher>> _flushers;

  /// <summary>Creates the signal over <paramref name="flushers"/>, which is read once, on first use.</summary>
  /// <param name="flushers">The flushers to watch; one listed twice is watched once.</param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="flushers"/> is null.</exception>
  public QueuedWritesLandedSignal(IEnumerable<IQueuedWriteFlusher> flushers) {
    ArgumentNullException.ThrowIfNull(flushers);
    _flushers = new Lazy<IReadOnlyList<IQueuedWriteFlusher>>(
      () => [.. flushers.Distinct<IQueuedWriteFlusher>(ReferenceEqualityComparer.Instance)]);
  }

  /// <inheritdoc />
  public IReadOnlyList<IQueuedWriteFlusher> Flushers => _flushers.Value;

  /// <inheritdoc />
  public bool AllLanded => _allIdleAtOnce(Flushers);

  /// <inheritdoc />
  public long PendingWrites => Flushers.Sum(f => f.Activity.Pending);

  /// <inheritdoc />
  public async Task WaitForAllLandedAsync(CancellationToken cancellationToken = default) {
    var flushers = Flushers;
    while (true) {
      foreach (var flusher in flushers) {
        await flusher.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
      }
      if (_allIdleAtOnce(flushers)) {
        return;
      }
    }
  }

  private static bool _allIdleAtOnce(IReadOnlyList<IQueuedWriteFlusher> flushers) {
    var first = new QueuedWriteActivity[flushers.Count];
    for (var i = 0; i < flushers.Count; i++) {
      first[i] = flushers[i].Activity;
      if (first[i].Pending != 0) {
        return false;
      }
    }
    for (var i = 0; i < flushers.Count; i++) {
      if (flushers[i].Activity != first[i]) {
        return false;
      }
    }
    return true;
  }

  /// <summary>
  /// The flushers registered with the container, read lazily, so they are resolved when the signal is first used and not
  /// while it is built.
  /// </summary>
  /// <param name="services">The container.</param>
  /// <returns>The registered flushers.</returns>
  internal static IEnumerable<IQueuedWriteFlusher> RegisteredIn(IServiceProvider services) {
    foreach (var flusher in services.GetServices<IQueuedWriteFlusher>()) {
      yield return flusher;
    }
  }

  /// <summary>
  /// Registers a hosted worker as a flusher the signal watches when it reports queued writes, and does nothing
  /// otherwise. Called by every path that hosts a worker with its start point, so a flush worker is watched by being
  /// hosted, with no list to keep. <paramref name="factory"/> is the one the worker is hosted with, which resolves the
  /// singleton the host runs.
  /// </summary>
  /// <typeparam name="TWorker">The hosted worker.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="factory">Resolves the worker.</param>
  internal static void RegisterIfFlusher<TWorker>(IServiceCollection services, Func<IServiceProvider, TWorker> factory)
      where TWorker : class {
    if (!typeof(IQueuedWriteFlusher).IsAssignableFrom(typeof(TWorker))) {
      return;
    }
    // A factory typed by the worker keeps the worker as the descriptor's implementation type, so registering the same
    // worker twice adds it once and two different workers are both kept.
    Func<IServiceProvider, object> typed = factory;
    services.TryAddEnumerable(new ServiceDescriptor(typeof(IQueuedWriteFlusher), typed, ServiceLifetime.Singleton));
  }
}
