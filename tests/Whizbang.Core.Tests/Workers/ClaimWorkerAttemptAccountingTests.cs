using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the two claim-side behaviours that stop a backlog spending its own retry budget.
///
/// <para>
/// A claim charges an attempt per row whether or not it is dispatched, so rows a worker claims and
/// never reaches are re-claimed at another attempt each cycle and eventually dead-lettered as
/// <c>MaxAttemptsExceeded</c> having never met a receptor. ClaimWorker therefore (a) narrows its
/// claim when work is coming back re-claimed rather than finished, and (b) hands back anything its
/// handoff loop did not actually deliver.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop</docs>
[NotInParallel(Order = 101)]
public class ClaimWorkerAttemptAccountingTests {

  [Test]
  public async Task ReClaimedWork_NarrowsTheClaimWindowAsync() {
    var coord = new RecordingCoordinator {
      // Every row arrives with attempts > 1 — this instance keeps re-claiming work it never
      // finishes, which is exactly the over-claim signal.
      BatchToReturn = _batchOf(rows: 4, attempts: 5)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 1000,
      MinStreamsPerBatch = 25,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsLessThan(1000)
      .Because("sustained re-claims mean the batch outruns what this instance can dispatch inside "
             + "its lease; every untouched row is silently spending an attempt it never used");
    await Assert.That(coord.LastMaxStreams).IsGreaterThanOrEqualTo(25)
      .Because("the window must never collapse below its floor — a stalled worker is worse than an "
             + "oversized batch");
  }

  [Test]
  public async Task CleanWork_KeepsTheClaimWindowAtTheConfiguredCeilingAsync() {
    var coord = new RecordingCoordinator {
      // attempts == 1 is a FIRST claim, not a re-claim: nothing to correct.
      BatchToReturn = _batchOf(rows: 4, attempts: 1)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 500,
      MinStreamsPerBatch = 25,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsEqualTo(500)
      .Because("a healthy worker must keep the operator's configured batch size — narrowing on "
             + "first-claim traffic would throttle a service that is keeping up perfectly well");
  }

  [Test]
  public async Task AdaptiveWindowDisabled_PinsTheClaimAtTheCeilingAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 4, attempts: 9)
    };
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
      MaxStreamsPerBatch = 700,
      MinStreamsPerBatch = 25,
      AdaptiveClaimWindow = false,
    });

    await coord.WaitForCallsAsync(4, TimeSpan.FromSeconds(5));

    await Assert.That(coord.LastMaxStreams).IsEqualTo(700)
      .Because("the opt-out must be a real opt-out — heavy churn notwithstanding, an operator who "
             + "pins the batch gets the batch they pinned");
  }

  /// <summary>
  /// The handoff loop charges every claimed row an attempt up front. If it cannot deliver them all,
  /// the undelivered remainder must be handed back rather than left holding a lease and a spent
  /// attempt it never used.
  /// </summary>
  [Test]
  public async Task UndeliveredHandoff_ReleasesTheRowsItCouldNotDispatchAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 3, attempts: 1)
    };
    // A channel that refuses the very first write stands in for shutdown, a full channel, or a
    // faulting writer — every case where the loop stops with rows still in hand.
    var refusing = new RefusingInboxChannel();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxChannel: refusing);

    await coord.WaitForReleaseAsync(TimeSpan.FromSeconds(5));

    await Assert.That(coord.ReleasedIds.Count).IsGreaterThan(0)
      .Because("rows the loop never delivered have paid an attempt for a dispatch that did not "
             + "happen; without the hand-back they pay again every cycle until they dead-letter");
    await Assert.That(coord.ReleasedInstanceId).IsNotEqualTo(Guid.Empty)
      .Because("the release must name the claiming instance — an unscoped release could unlock a "
             + "row another worker is actively dispatching");
  }

  /// <summary>
  /// A release that fails must not take the worker down with it. This path runs during shutdown,
  /// where throwing would turn a clean stop into a crash — and the orchestrator reads a non-zero
  /// exit as a failed pod, restarts it, and the next shutdown does the same thing. The cost of
  /// swallowing is one attempt per row; the cost of throwing is a crash loop.
  /// </summary>
  [Test]
  public async Task ReleaseFailure_IsSwallowedSoShutdownStaysCleanAsync() {
    var coord = new RecordingCoordinator {
      BatchToReturn = _batchOf(rows: 2, attempts: 1),
      ThrowOnRelease = true,
    };
    var refusing = new RefusingInboxChannel();
    using var harness = _startWorker(coord, new ClaimWorkerOptions {
      PollingIntervalMilliseconds = 20,
      PollingMaxIntervalMilliseconds = 60,
    }, inboxChannel: refusing);

    // The worker must keep claiming after a release blew up — a faulting hand-back is recoverable,
    // a dead poller is not.
    await coord.WaitForCallsAsync(3, TimeSpan.FromSeconds(5));

    await Assert.That(coord.ReleaseAttempts).IsGreaterThan(0)
      .Because("the release must actually have been attempted — a test that passes because nothing "
             + "was tried would prove nothing");
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(3)
      .Because("a failing release must not stop the claim loop; the rows keep their attempt and are "
             + "re-claimed later, which is strictly better than a worker that stops polling");
  }

  // ==================== helpers ====================

  private static WorkBatch _batchOf(int rows, int attempts) {
    var inbox = new List<InboxWork>(rows);
    for (var i = 0; i < rows; i++) {
      inbox.Add(new InboxWork {
        MessageId = TrackedGuid.NewMedo().Value,
        MessageType = "TestEvent",
        Envelope = null!,
        Attempts = attempts,
      });
    }
    return new WorkBatch { OutboxWork = [], InboxWork = inbox, PerspectiveWork = [] };
  }

  private static WorkerHarness _startWorker(
      RecordingCoordinator coord, ClaimWorkerOptions options, IInboxChannelWriter? inboxChannel = null) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstance(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(options),
      NullLogger<ClaimWorker>.Instance,
      inboxChannel: inboxChannel);
    var cts = new CancellationTokenSource();
    worker.StartAsync(cts.Token).GetAwaiter().GetResult();
    return new WorkerHarness(worker, cts);
  }

  private sealed class WorkerHarness(ClaimWorker worker, CancellationTokenSource cts) : IDisposable {
    public void Dispose() {
      cts.Cancel();
      try { worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
      cts.Dispose();
    }
  }

  private sealed class StubInstance : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo();
    public string ServiceName => "test";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>
  /// Refuses every write — standing in for shutdown, a full channel, or a faulting writer: any case
  /// where the handoff loop stops with claimed rows still in hand.
  /// </summary>
  private sealed class RefusingInboxChannel : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public event Action? OnNewInboxWorkAvailable { add { } remove { } }
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) =>
      throw new InvalidOperationException("channel refuses writes");
    public bool TryWrite(InboxWork work) => false;
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.TryComplete();
    public void SignalNewInboxWorkAvailable() { }
  }

  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount { get; private set; }
    public int LastMaxStreams { get; private set; }
    public List<Guid> ReleasedIds { get; } = [];
    public Guid ReleasedInstanceId { get; private set; }
    public int ReleaseAttempts { get; private set; }
    public bool ThrowOnRelease { get; set; }
    public WorkBatch BatchToReturn { get; set; } = new() { OutboxWork = [], InboxWork = [], PerspectiveWork = [] };

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (_lock) {
        CallCount++;
        LastMaxStreams = req.MaxStreams;
        if (_watchers.TryGetValue(CallCount, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(BatchToReturn);
    }

    public Task<int> ReleaseUnprocessedInboxAsync(
        Guid instanceId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) {
      lock (_lock) {
        ReleaseAttempts++;
        if (ThrowOnRelease) {
          _released.TrySetResult();
          throw new InvalidOperationException("release failed");
        }
        ReleasedInstanceId = instanceId;
        ReleasedIds.AddRange(messageIds);
      }
      _released.TrySetResult();
      return Task.FromResult(messageIds.Count);
    }

    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (CallCount >= n) { return Task.CompletedTask; }
        if (!_watchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _watchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }

    public Task WaitForReleaseAsync(TimeSpan timeout) => _released.Task.WaitAsync(timeout);

    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
