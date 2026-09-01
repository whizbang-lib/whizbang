using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The batching worker that writes perspective completions back to the store.
/// </summary>
/// <remarks>
/// Every processed perspective event produces a completion, so writing them one at a time would
/// put a round trip on the hot path per event. This worker coalesces them into batches instead —
/// which means a completion that is dropped or mis-sorted here is a perspective the store believes
/// is still behind, and the work gets done again on the next pass.
///
/// <para>
/// The batch carries two different things through one channel: cursor completions and bare event
/// work ids. They go to different parameters of the same call, so splitting them wrongly is
/// silent — the call succeeds and completes the wrong set.
/// </para>
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class PerspectiveCompletionFlushWorkerTests {

  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    public List<(IReadOnlyList<PerspectiveCursorCompletion> Cursors, IReadOnlyList<Guid> WorkIds, bool Debug)> Completions { get; } = [];
    public List<IReadOnlyList<Guid>> Cleanups { get; } = [];
    public bool CleanupThrows { get; init; }

    public TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task CompletePerspectiveAsync(
        IReadOnlyList<PerspectiveCursorCompletion> cursors, IReadOnlyList<Guid> eventWorkIds,
        bool debugMode, CancellationToken cancellationToken = default) {
      lock (_lock) {
        Completions.Add(([.. cursors], [.. eventWorkIds], debugMode));
      }
      Flushed.TrySetResult();
      return Task.CompletedTask;
    }

    public Task<int> CleanupCompletedStreamsAsync(
        IReadOnlyList<Guid> streamIds, CancellationToken cancellationToken = default) {
      lock (_lock) {
        Cleanups.Add([.. streamIds]);
      }
      return CleanupThrows
        ? Task.FromException<int>(new InvalidOperationException("cleanup_completed_streams unavailable"))
        : Task.FromResult(streamIds.Count);
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] m, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private static PerspectiveCompletionFlushWorker _worker(
      RecordingCoordinator coordinator, bool enabled = true, bool debugMode = false) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    return new PerspectiveCompletionFlushWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new PerspectiveCompletionFlushWorkerOptions {
        Enabled = enabled,
        Flusher = new BatchFlusherOptions {
          MaxBatchSize = 100,
          CoalesceWindowMs = 5,
          ImmediateFlushThreshold = 1,
          ChannelCapacity = 1000,
        },
      }),
      Options.Create(new WorkCoordinatorOptions { DebugMode = debugMode }),
      NullLogger<PerspectiveCompletionFlushWorker>.Instance);
  }

  private static PerspectiveCursorCompletion _cursor(Guid? streamId = null) => new() {
    StreamId = streamId ?? (Guid)TrackedGuid.NewMedo(),
    PerspectiveName = "TestPerspective",
    LastEventId = (Guid)TrackedGuid.NewMedo(),
    Status = PerspectiveProcessingStatus.Completed,
  };

  [Test]
  [Timeout(30000)]
  public async Task EnqueuedWorkIds_ReachTheCoordinatorAsync(CancellationToken testToken) {
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var workId = (Guid)TrackedGuid.NewMedo();
    await worker.EnqueueEventWorkIdAsync(workId, testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    var all = coordinator.Completions.SelectMany(c => c.WorkIds).ToList();
    await Assert.That(all).Contains(workId);
  }

  [Test]
  [Timeout(30000)]
  public async Task EnqueuedCursors_ReachTheCoordinatorAsync(CancellationToken testToken) {
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var cursor = _cursor();
    await worker.EnqueueCursorAsync(cursor, testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    var all = coordinator.Completions.SelectMany(c => c.Cursors).ToList();
    await Assert.That(all.Any(c => c.StreamId == cursor.StreamId)).IsTrue();
  }

  [Test]
  [Timeout(30000)]
  public async Task AMixedBatch_IsSplitIntoCursorsAndWorkIdsAsync(CancellationToken testToken) {
    // Both kinds travel through one channel and go to different parameters of the same call.
    // Putting a cursor in the work-id list is silent: the call succeeds and completes the wrong
    // set, so the perspective is marked done while the store still thinks it is behind.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var workId = (Guid)TrackedGuid.NewMedo();
    var cursor = _cursor();
    await worker.EnqueueEventWorkIdAsync(workId, testToken);
    await worker.EnqueueCursorAsync(cursor, testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    var cursors = coordinator.Completions.SelectMany(c => c.Cursors).ToList();
    var workIds = coordinator.Completions.SelectMany(c => c.WorkIds).ToList();
    await Assert.That(workIds).Contains(workId);
    await Assert.That(cursors.Any(c => c.StreamId == cursor.StreamId)).IsTrue();
    await Assert.That(workIds).DoesNotContain(cursor.StreamId)
      .Because("a cursor must not be sent as a bare work id — the call would succeed on the "
             + "wrong set");
  }

  [Test]
  [Timeout(30000)]
  public async Task CursorsTriggerStreamEvictionAsync(CancellationToken testToken) {
    // Once completions land, a stream with no pending work can leave wh_active_streams so the
    // next event for it rebinds. Skipping this leaves streams pinned to an instance forever.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var cursor = _cursor();
    await worker.EnqueueCursorAsync(cursor, testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Cleanups.SelectMany(c => c)).Contains(cursor.StreamId);
  }

  [Test]
  [Timeout(30000)]
  public async Task EvictionAsksOncePerDistinctStreamAsync(CancellationToken testToken) {
    // A batch usually holds several completions for the same stream. Passing the id repeatedly
    // makes the store do the same no-op delete once per event rather than once per stream.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var streamId = (Guid)TrackedGuid.NewMedo();
    await worker.EnqueueCursorAsync(_cursor(streamId), testToken);
    await worker.EnqueueCursorAsync(_cursor(streamId), testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    foreach (var call in coordinator.Cleanups) {
      await Assert.That(call.Count).IsEqualTo(call.Distinct().Count())
        .Because("the same stream id twice in one call is a repeated no-op delete");
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task WorkIdsAloneDoNotTriggerEvictionAsync(CancellationToken testToken) {
    // Eviction is keyed on the stream ids the cursors carry; a bare work id has none, so there
    // is nothing to evict and the call must not be made with an empty set.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    await worker.EnqueueEventWorkIdAsync((Guid)TrackedGuid.NewMedo(), testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Cleanups).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task AFailedEvictionDoesNotLoseTheCompletionAsync(CancellationToken testToken) {
    // Eviction is opportunistic and the maintenance worker is its backstop. Letting its failure
    // propagate would fail the batch and re-run perspective work that already succeeded.
    var coordinator = new RecordingCoordinator { CleanupThrows = true };
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var cursor = _cursor();
    await worker.EnqueueCursorAsync(cursor, testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Completions.SelectMany(c => c.Cursors)
      .Any(c => c.StreamId == cursor.StreamId)).IsTrue()
      .Because("the completion was already written — an opportunistic eviction failure must not "
             + "undo it and make the work run again");
  }

  [Test]
  [Timeout(30000)]
  public async Task DebugModeIsPassedThroughAsync(CancellationToken testToken) {
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator, debugMode: true);
    await worker.StartAsync(testToken);

    await worker.EnqueueEventWorkIdAsync((Guid)TrackedGuid.NewMedo(), testToken);
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Completions.All(c => c.Debug)).IsTrue();
  }

  [Test]
  [Timeout(30000)]
  public async Task WhenDisabled_NothingIsWrittenAsync(CancellationToken testToken) {
    // Disabled means the completions are handled elsewhere, so writing them here would double
    // up. The worker still runs as a hosted service and still accepts enqueues.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator, enabled: false);
    await worker.StartAsync(testToken);

    await worker.EnqueueEventWorkIdAsync((Guid)TrackedGuid.NewMedo(), testToken);
    await worker.EnqueueCursorAsync(_cursor(), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Completions).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task AFullBatchIsWrittenInOneCallAsync(CancellationToken testToken) {
    // Batching is the reason this worker exists: every processed perspective event produces a
    // completion, so one round trip each would put a database call on the hot path per event.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    var ids = Enumerable.Range(0, 20).Select(_ => (Guid)TrackedGuid.NewMedo()).ToList();
    foreach (var id in ids) {
      await worker.EnqueueEventWorkIdAsync(id, testToken);
    }
    await coordinator.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), testToken);
    await worker.StopAsync(CancellationToken.None);

    var written = coordinator.Completions.SelectMany(c => c.WorkIds).ToList();
    await Assert.That(written).IsNotEmpty();
    await Assert.That(coordinator.Completions.Count).IsLessThan(ids.Count)
      .Because("twenty completions must not cost twenty round trips");
  }

  [Test]
  [Timeout(30000)]
  public async Task ShutdownCancelsRatherThanDrainingAsync(CancellationToken testToken) {
    // Worth pinning because it is the surprising half of the contract and the flusher says so
    // in its own log line: dispose completes the writer and then cancels, so whatever is still
    // in the channel is dropped. That is survivable only because a lost completion re-runs
    // perspective work that is safe to repeat — and it is the reason the flush must stay
    // idempotent. If this ever needs to change, it changes here first.
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    await worker.EnqueueEventWorkIdAsync((Guid)TrackedGuid.NewMedo(), testToken);
    await worker.StopAsync(CancellationToken.None);

    // Whether that one item landed is a genuine race and either outcome is correct. What must
    // hold is that shutdown completed at all rather than hanging on an undrained channel, and
    // that a second stop is still safe.
    await worker.StopAsync(CancellationToken.None);
    await Assert.That(coordinator.Completions.Count).IsLessThanOrEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task StopIsSafeWithNothingEnqueuedAsync(CancellationToken testToken) {
    var coordinator = new RecordingCoordinator();
    var worker = _worker(coordinator);
    await worker.StartAsync(testToken);

    await worker.StopAsync(CancellationToken.None);

    await Assert.That(coordinator.Completions).IsEmpty();
  }

  [Test]
  public async Task Constructor_RejectsItsRequiredCollaboratorsAsync() {
    var services = new ServiceCollection().BuildServiceProvider();
    var gate = new SchemaReadyGate();
    var options = Options.Create(new PerspectiveCompletionFlushWorkerOptions());
    var coordOptions = Options.Create(new WorkCoordinatorOptions());
    var logger = NullLogger<PerspectiveCompletionFlushWorker>.Instance;
    var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

    await Assert.That(() => new PerspectiveCompletionFlushWorker(null!, gate, options, coordOptions, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PerspectiveCompletionFlushWorker(scopeFactory, null!, options, coordOptions, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PerspectiveCompletionFlushWorker(scopeFactory, gate, null!, coordOptions, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PerspectiveCompletionFlushWorker(scopeFactory, gate, options, null!, logger))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new PerspectiveCompletionFlushWorker(scopeFactory, gate, options, coordOptions, null!))
      .Throws<ArgumentNullException>();
  }
}
