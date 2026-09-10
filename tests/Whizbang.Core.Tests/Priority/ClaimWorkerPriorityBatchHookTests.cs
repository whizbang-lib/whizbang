using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Priority;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// The batch hook runs after each claim, before dispatch order is decided (priority step 1): the claim worker
/// folds the inbox streams the claim returned (most urgent row, oldest arrival, rows in the batch), lets the
/// registered batch hooks adjust each stream's number, and hands the streams to the drain in that order. A hook
/// sets a stream's number, never a row's position, so per-stream order is an invariant no hook can break.
/// </summary>
/// <docs>fundamentals/messaging/message-priority#hooks</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ClaimWorkerPriorityBatchHookTests {

  private sealed class FakeCoordinator(WorkBatch batch) : IWorkCoordinator {
    private readonly Lock _lock = new();
    private readonly Dictionary<int, TaskCompletionSource> _watchers = [];
    private int _calls;
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      lock (_lock) {
        _calls++;
        if (_watchers.TryGetValue(_calls, out var tcs)) { tcs.TrySetResult(); }
      }
      return Task.FromResult(batch);
    }
    public Task WaitForCallsAsync(int n, TimeSpan timeout) {
      TaskCompletionSource tcs;
      lock (_lock) {
        if (_calls >= n) { return Task.CompletedTask; }
        if (!_watchers.TryGetValue(n, out tcs!)) {
          tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
          _watchers[n] = tcs;
        }
      }
      return tcs.Task.WaitAsync(timeout);
    }
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }

  private sealed class RecordingDrain : IInboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public List<Guid> Written { get; } = [];
    public TaskCompletionSource FirstBatchWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      lock (Written) {
        Written.Add(streamId);
        if (Written.Count >= 2) { FirstBatchWritten.TrySetResult(); }
      }
      return _channel.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      lock (Written) { Written.Add(streamId); }
      return _channel.Writer.TryWrite(streamId);
    }
  }

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "claim-svc";
    public string HostName => "claim-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>Puts one stream first whatever the claim said, and records what it was shown.</summary>
  private sealed class _favor(Guid streamId) : IPriorityBatchHook {
    public List<PriorityBatchEntry> Seen { get; } = [];
    public int Order => 100;
    public int Adjust(PriorityBatchEntry stream, IReadOnlyList<PriorityBatchEntry> batch) {
      lock (Seen) { Seen.Add(stream); }
      return stream.StreamId == streamId ? 1 : 999;
    }
  }

  private static (ClaimWorker Worker, RecordingDrain Drain, FakeCoordinator Coordinator) _worker(WorkBatch batch, IPriorityBatchHook? hook) {
    var coordinator = new FakeCoordinator(batch);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var drain = new RecordingDrain();
    var chain = hook is null ? null : new PriorityHookChain([], [], [hook]);
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      inboxDrainChannel: drain,
      priorityHooks: chain);
    return (worker, drain, coordinator);
  }

  private static WorkBatch _batch(Guid first, Guid second) => new() {
    OutboxWork = [],
    InboxWork = [],
    PerspectiveWork = [],
    InboxStreamIds = [first, second],
    InboxStreams = [
      new InboxStreamFold(first, WorkPriority.STANDARD, DateTimeOffset.UtcNow.AddMinutes(-5), 3),
      new InboxStreamFold(second, WorkPriority.BACKGROUND, DateTimeOffset.UtcNow.AddMinutes(-20), 1),
    ],
  };

  [Test]
  public async Task Distribute_RunsTheBatchHooks_AndHandsStreamsToTheDrainInTheAdjustedOrderAsync() {
    var first = (Guid)TrackedGuid.NewMedo();
    var second = (Guid)TrackedGuid.NewMedo();
    var hook = new _favor(second);
    var (worker, drain, coordinator) = _worker(_batch(first, second), hook);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.FirstBatchWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    Guid[] written;
    lock (drain.Written) { written = [.. drain.Written.Take(2)]; }
    await Assert.That(written[0]).IsEqualTo(second)
      .Because("the hook set the second stream's number below the first's; a host policy the defaults never anticipated decides the dispatch order");
    await Assert.That(written[1]).IsEqualTo(first);
    PriorityBatchEntry seenFirst;
    lock (hook.Seen) { seenFirst = hook.Seen.First(e => e.StreamId == first); }
    await Assert.That(seenFirst.FoldedPriority).IsEqualTo(WorkPriority.STANDARD).Because("the hook sees the stream's folded number");
    await Assert.That(seenFirst.PendingRows).IsEqualTo(3).Because("and how many of its rows the batch holds");
    await Assert.That(seenFirst.OldestAge).IsGreaterThan(TimeSpan.FromMinutes(4)).Because("and how long its oldest row has waited");
  }

  [Test]
  public async Task Distribute_WithoutABatchHook_KeepsTheClaimsOrderAsync() {
    var first = (Guid)TrackedGuid.NewMedo();
    var second = (Guid)TrackedGuid.NewMedo();
    var (worker, drain, coordinator) = _worker(_batch(first, second), hook: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.FirstBatchWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    Guid[] written;
    lock (drain.Written) { written = [.. drain.Written.Take(2)]; }
    await Assert.That(written[0]).IsEqualTo(first).Because("the claim's own bucket order stands when no hook adjusts it");
    await Assert.That(written[1]).IsEqualTo(second);
  }
}
