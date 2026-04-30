using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for <see cref="OutboxDrainWorker"/> — the per-stream-id outbox drainer.
/// Verifies the per-stream drain flow (read stream_id → fetch batch → publish each →
/// enqueue completion) and that messages within a stream publish in fetch order (FIFO).
/// </summary>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerTests {

  // --- fakes ---

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
    public void Complete() => _channel.Writer.Complete();
  }

  private sealed class FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakePublishStrategy : IMessagePublishStrategy {
    public List<OutboxWork> Published { get; } = [];
    public TaskCompletionSource<int> ReachedCount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int TargetCount { get; set; } = 1;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Add(work);
      if (Published.Count >= TargetCount) {
        ReachedCount.TrySetResult(Published.Count);
      }
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>Fake coordinator that returns a fixed batch for a given stream_id.</summary>
  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public TaskCompletionSource<int> FirstFetchCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int FetchCalls;
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var n = Interlocked.Increment(ref FetchCalls);
      FirstFetchCalled.TrySetResult(n);
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    // Required (non-default-implemented) interface members — minimal stubs.
    public Task<WorkBatch> ProcessWorkBatchAsync(ProcessWorkBatchRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  // --- helpers ---

  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup: no JsonTypeInfo for MessageEnvelope<JsonElement>");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = envelopeJson,
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  // --- tests ---

  [Test]
  public async Task OutboxDrainWorker_OnStreamId_FetchesBatch_PublishesEach_EnqueuesCompletionAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();
    var msgB = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId), _row(msgB, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 2 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    // Diagnose: was FetchOutboxBatchAsync even called?
    var fetchCalled = await Task.WhenAny(coord.FirstFetchCalled.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    await Assert.That(coord.FirstFetchCalled.Task.IsCompleted).IsTrue()
      .Because("worker should call FetchOutboxBatchAsync after a stream_id arrives on the drain channel");

    var reached = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.ReachedCount.Task.IsCompleted).IsTrue();
    await Assert.That(publish.Published.Count).IsEqualTo(2);
    // FIFO within stream — fetch order is preserved.
    await Assert.That(publish.Published[0].MessageId).IsEqualTo(msgA);
    await Assert.That(publish.Published[1].MessageId).IsEqualTo(msgB);
    // Both completions enqueued.
    await Assert.That(completion.AllIds).Contains(msgA);
    await Assert.That(completion.AllIds).Contains(msgB);
    await Assert.That(failure.All.Count).IsEqualTo(0);
  }

  [Test]
  public async Task OutboxDrainWorker_RepeatedStreamId_DrainerIsIdempotent_OnceCompletedAsync() {
    // The Part C invariant: claim_work emitting the same stream_id repeatedly must NOT cause
    // re-publish. The drainer fetches eligible rows; once a row is completed (production: deleted)
    // the next fetch returns 0 rows. Models the "rerun-claim doesn't re-issue" guarantee.

    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgA = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgA, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // First drain: publishes msgA.
    await drainChannel.WriteAsync(streamId);
    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));

    // Simulate completion-flush deleting the row by clearing the fake's stream contents.
    coord.RowsByStream[streamId] = [];

    // Second drain on the same stream_id: fetch returns nothing → publish NOT called again.
    await drainChannel.WriteAsync(streamId);
    // Wait briefly to let the worker process the second message via TaskCompletionSource on completion channel signal.
    var secondCompletionSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(async () => {
      // Allow worker to actually iterate. Use a fast loop checking the publish count.
      for (var i = 0; i < 100; i++) {
        await Task.Delay(20);
        if (publish.Published.Count > 1) {
          secondCompletionSeen.TrySetResult(false);
          return;
        }
      }
      secondCompletionSeen.TrySetResult(true);
    });

    var ok = await Task.WhenAny(secondCompletionSeen.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1);
  }
}
