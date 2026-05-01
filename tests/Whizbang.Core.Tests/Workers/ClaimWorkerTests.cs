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

[NotInParallel(Order = 100)]
public class ClaimWorkerTests {

  private sealed class StubInstanceProvider : IServiceInstanceProvider {
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

  private sealed class FakeCoordinator : IWorkCoordinator {
    public TaskCompletionSource FirstCallSignal { get; } = new();
    public int CallCount { get; private set; }
    public WorkBatch BatchToReturn { get; set; } = new() {
      OutboxWork = [],
      InboxWork = [],
      PerspectiveWork = []
    };

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) {
      CallCount++;
      FirstCallSignal.TrySetResult();
      return Task.FromResult(BatchToReturn);
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("not used");
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

  [Test]
  public async Task ExecuteAsync_PollsAtLeastOnceAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task RequestImmediatePoll_BypassesWaitAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 10_000,            // very long
        PollingMaxIntervalMilliseconds = 60_000           // very long
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var startCount = coord.CallCount;

    // Immediate poll should fire another tick within hundreds of ms despite the 10s base.
    var beforeWake = coord.CallCount;
    worker.RequestImmediatePoll();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (coord.CallCount == beforeWake && sw.Elapsed < TimeSpan.FromSeconds(2)) {
      await Task.Delay(20);
    }

    await Assert.That(coord.CallCount).IsGreaterThan(startCount);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_DisabledOptions_NoClaimFiresAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();  // gate ready but Enabled=false should still suppress the claim loop
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions {
        Enabled = false,
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200
      }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    var raced = await Task.WhenAny(coord.FirstCallSignal.Task, Task.Delay(500, CancellationToken.None));
    await Assert.That(coord.CallCount).IsEqualTo(0);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_BlocksOnSchemaGate_UntilMarkedReadyAsync() {
    var coord = new FakeCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();  // not marked ready
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // No claim while gate is closed.
    var racedBefore = await Task.WhenAny(coord.FirstCallSignal.Task, Task.Delay(300, CancellationToken.None));
    await Assert.That(coord.CallCount).IsEqualTo(0);

    // Open gate — claim fires.
    gate.MarkReady();
    await coord.FirstCallSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await Assert.That(coord.CallCount).IsGreaterThanOrEqualTo(1);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  // --- Part C step 4: stream_id drain channels ---

  private sealed class CapturingDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _ch = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public List<Guid> Written { get; } = [];
    public TaskCompletionSource SecondWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public System.Threading.Channels.ChannelReader<Guid> Reader => _ch.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) {
      Written.Add(streamId);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult();
      }
      return _ch.Writer.WriteAsync(streamId, ct);
    }
    public bool TryWrite(Guid streamId) {
      Written.Add(streamId);
      if (Written.Count >= 2) {
        SecondWritten.TrySetResult();
      }
      return _ch.Writer.TryWrite(streamId);
    }
  }

  [Test]
  public async Task Distribute_OutboxWork_AlsoEmitsDistinctStreamIds_ToOutboxDrainChannelAsync() {
    var streamA = (Guid)TrackedGuid.NewMedo();
    var streamB = (Guid)TrackedGuid.NewMedo();
    var coord = new FakeCoordinator {
      BatchToReturn = new WorkBatch {
        OutboxWork = [
          _stubOutboxWork(streamA),
          _stubOutboxWork(streamA),  // dupe stream — should not emit twice
          _stubOutboxWork(streamB),
        ],
        InboxWork = [],
        PerspectiveWork = []
      }
    };

    var drain = new CapturingDrainChannel();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new ClaimWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new StubInstanceProvider(),
      new NoOpWorkNotificationListener(),
      gate,
      Options.Create(new ClaimWorkerOptions { PollingIntervalMilliseconds = 50, PollingMaxIntervalMilliseconds = 200 }),
      NullLogger<ClaimWorker>.Instance,
      outboxDrainChannel: drain);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await drain.SecondWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    // Two distinct stream_ids written even though OutboxWork had 3 items (one dupe).
    await Assert.That(drain.Written.Count).IsEqualTo(2);
    await Assert.That(drain.Written).Contains(streamA);
    await Assert.That(drain.Written).Contains(streamB);
  }

  private static OutboxWork _stubOutboxWork(Guid streamId) => new() {
    MessageId = (Guid)TrackedGuid.NewMedo(),
    Destination = "topic",
    Envelope = new Whizbang.Core.Observability.MessageEnvelope<System.Text.Json.JsonElement> {
      MessageId = Whizbang.Core.ValueObjects.MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = System.Text.Json.JsonDocument.Parse("{}").RootElement,
      DispatchContext = new Whizbang.Core.Observability.MessageDispatchContext {
        Mode = Whizbang.Core.Dispatch.DispatchModes.Local,
        Source = Whizbang.Core.Messaging.MessageSource.Local,
      },
      Hops = [],
    },
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[System.Text.Json.JsonElement]",
    MessageType = "TestMessage",
    StreamId = streamId,
    Attempts = 0,
    Status = MessageProcessingStatus.Stored,
  };
}
