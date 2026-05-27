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
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
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
  public async Task OutboxDrainWorker_MoreRowsThanMaxPerStream_LoopsUntilEmptyAsync() {
    // Phase H step 6 slice 3: drainer continues fetching for the same stream while there
    // are NEW rows. Simulates 250 pending rows / MaxPerStream=100 → 3 publish iterations
    // (100 + 100 + 50). The fake "consumes" returned rows on each fetch (mimicking what
    // happens once completion-flush lands and complete_outbox_published deletes them).
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 250).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new ConsumingFakeWorkCoordinator();
    coord.RowsByStream[streamId] = msgs.Select(m => _row(m, streamId)).ToList();

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 250 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(250)
      .Because("drainer must keep fetching for the same stream until pending=0; 250 rows / MaxPerStream=100 = 3 iterations");
    await Assert.That(coord.FetchCalls).IsGreaterThanOrEqualTo(3)
      .Because("drainer should have fetched at least 3 times to drain 250 rows at MaxPerStream=100");
  }

  [Test]
  public async Task OutboxDrainWorker_RefetchReturnsSameRows_ExitsWithoutDoublePublishAsync() {
    // Race: completion-flush lags. After publishing batch 1, the next fetch returns the
    // SAME rows (because complete_outbox_published hasn't deleted them yet). Drainer must
    // detect via session-set and exit without re-publishing — the next claim_work tick
    // will re-issue once the rows clear.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgs = Enumerable.Range(0, 50).Select(_ => (Guid)TrackedGuid.NewMedo()).ToArray();

    var coord = new FakeWorkCoordinator();  // does NOT consume rows on fetch
    coord.RowsByStream[streamId] = msgs.Select(m => _row(m, streamId)).ToList();

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 50 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    // Give a chance for any spurious second-pass publishes to happen.
    await Task.Delay(200);
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(50)
      .Because("session-set dedup must skip rows already published this drain session — no re-publish even if fetch returns the same rows");
  }

  /// <summary>Fake coordinator that REMOVES returned rows from the dictionary on each fetch — mimics post-completion DELETE.</summary>
  private sealed class ConsumingFakeWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public int FetchCalls;
    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref FetchCalls);
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (RowsByStream.TryGetValue(sid, out var rows)) {
          var taken = rows.Take(maxPerStream).ToList();
          result.AddRange(taken);
          rows.RemoveRange(0, taken.Count);
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

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
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
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

  // ==========================================================================
  // Pre/Post Outbox lifecycle hooks
  // ==========================================================================

  private sealed class CapturingLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => envelope.Payload;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => envelope.Payload;
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => jsonBytes;
    public object DeserializeFromJsonElement(JsonElement payload, string messageTypeName) => payload;
  }

  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly List<(LifecycleStage Stage, IMessageEnvelope Envelope)> _invocations = [];
    private readonly Lock _lock = new();
    public Func<LifecycleStage, IMessageEnvelope, Task>? OnInvoke { get; set; }

    public List<(LifecycleStage Stage, IMessageEnvelope Envelope)> Invocations {
      get { lock (_lock) { return [.. _invocations]; } }
    }

    public async ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _invocations.Add((stage, envelope));
      }
      if (OnInvoke is not null) {
        await OnInvoke(stage, envelope);
      }
    }
  }

  private sealed class AlwaysHasReceptorsRegistry : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => true;
    public bool HasInboxHandler(string messageType) => true;
    public bool HasAnyConsumer(string messageType) => true;
  }

  [Test]
  public async Task OutboxDrainWorker_WithLifecycleDeps_FiresPreAndPostOutboxInline_AroundPublishAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    // Allow Post-Outbox lifecycle to fire after publish (it runs synchronously after PublishAsync returns).
    for (var i = 0; i < 50 && invoker.Invocations.All(x => x.Stage != LifecycleStage.PostOutboxInline); i++) {
      await Task.Delay(20);
    }
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var stages = invoker.Invocations.Select(x => x.Stage).ToList();
    await Assert.That(stages).Contains(LifecycleStage.PreOutboxInline)
      .Because("PreOutboxInline must fire before publish when a deserializer + receptor registry are wired.");
    await Assert.That(stages).Contains(LifecycleStage.PostOutboxInline)
      .Because("PostOutboxInline must fire after a successful publish.");
    await Assert.That(publish.Published.Count).IsEqualTo(1);
  }

  [Test]
  public async Task OutboxDrainWorker_NoLifecycleDeps_PublishesWithoutLifecycle_NoOpAsync() {
    // When lifecycleMessageDeserializer + receptorRegistry are absent (legacy / minimal
    // hosts), the worker must degrade gracefully: publish + complete still happen,
    // lifecycle invocation simply no-ops.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

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
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("publish must still happen even when lifecycle dependencies are unwired (legacy host).");
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  [Test]
  public async Task OutboxDrainWorker_LifecycleReceptorThrows_PublishAndCompletionStillHappenAsync() {
    // A misbehaving lifecycle receptor must NOT block the publish or completion path —
    // the OutboxDrainWorker wraps lifecycle invocation in try/catch, logs at Warning, and
    // proceeds. Production invariant: an Outbox row that's about to be published or has
    // just been published MUST always reach completion-flush regardless of receptor faults.
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();

    var coord = new FakeWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId)];

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var publish = new FakePublishStrategy { TargetCount = 1 };
    var instance = new FakeServiceInstanceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var invoker = new CapturingReceptorInvoker {
      OnInvoke = (stage, _) => {
        if (stage == LifecycleStage.PreOutboxInline) {
          throw new InvalidOperationException("test-induced receptor failure");
        }
        return Task.CompletedTask;
      }
    };
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IReceptorInvoker>(invoker);
    var sp = services.BuildServiceProvider();

    var worker = new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instance, drainChannel, completion, failure, gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      _jsonOpts,
      NullLogger<OutboxDrainWorker>.Instance,
      publish,
      lifecycleMessageDeserializer: new CapturingLifecycleDeserializer(),
      receptorRegistry: new AlwaysHasReceptorsRegistry(),
      runtimeReceptorRegistry: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    _ = await Task.WhenAny(publish.ReachedCount.Task, Task.Delay(TimeSpan.FromSeconds(15)));
    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    await Assert.That(publish.Published.Count).IsEqualTo(1)
      .Because("Receptor throwing at PreOutboxInline must not stop the transport publish.");
    await Assert.That(completion.AllIds).Contains(msgId)
      .Because("Completion must still be enqueued — the row is safely durable in the outbox table and reaching the transport.");
    await Assert.That(failure.All.Count).IsEqualTo(0)
      .Because("A lifecycle-receptor failure is not a publish failure.");
  }
}
