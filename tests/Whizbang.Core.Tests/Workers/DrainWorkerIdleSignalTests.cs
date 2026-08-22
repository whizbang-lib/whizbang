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

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Verifies <see cref="OutboxDrainWorker.IsIdle"/> + <see cref="OutboxDrainWorker.OnWorkProcessingIdle"/>
/// and the sibling <see cref="InboxDrainWorker"/> contract that integration-test fixtures
/// rely on to wait for the drain pipeline to quiesce before truncating tables.
///
/// Regression context: before this contract existed, the ECommerce RabbitMQ fixture's
/// <c>_waitForWorkersReadyAsync</c> only polled <c>OutboxPublishWorker</c> (which Phase H
/// step 4b defaults to disabled and reports IsIdle=true instantly) and
/// <c>PerspectiveWorker</c>. The fixture truncated the database while the actual drain
/// workers were still mid-flight, producing the 3-minute timeouts on
/// <c>DistributeStages_MultipleCommands_AllStagesFireForEachAsync</c> and friends.
/// </summary>
/// <docs>operations/workers/publisher-worker</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class DrainWorkerIdleSignalTests {

  [Test]
  public async Task OutboxDrainWorker_StartsIdle_AndStaysIdleWhenChannelEmptyAsync() {
    var worker = _buildOutboxDrainWorker(out _, out _);
    await Assert.That(worker.IsIdle).IsTrue();
  }

  [Test]
  public async Task OutboxDrainWorker_FiresStartedThenIdle_AroundEachBatchAsync() {
    var coord = new _StubCoordinator();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    coord.OutboxRowsByStream[streamId] = [_outboxRow(msgId, streamId)];

    var publish = new _StubPublisher();
    var worker = _buildOutboxDrainWorker(out var drainChannel, out var completion,
      coord: coord, publish: publish);

    var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingStarted += () => startedTcs.TrySetResult(true);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    await drainChannel.WriteAsync(streamId);

    await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    await Assert.That(publish.Published.Count).IsEqualTo(1);
    await Assert.That(completion.AllIds.Count).IsEqualTo(1);
    await Assert.That(worker.IsIdle).IsTrue();

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch { /* shutdown best-effort */ }
  }

  [Test]
  public async Task OutboxDrainWorker_RepeatedSameStateCalls_FireEventOnceAsync() {
    // Multiple back-to-back batches should fire each transition exactly once per change
    // — idempotent _setIdleState protects fixture handlers that only want to know
    // "is the worker between batches" not "how many transitions happened".
    var coord = new _StubCoordinator();
    var s1 = (Guid)TrackedGuid.NewMedo();
    var s2 = (Guid)TrackedGuid.NewMedo();
    coord.OutboxRowsByStream[s1] = [_outboxRow((Guid)TrackedGuid.NewMedo(), s1)];
    coord.OutboxRowsByStream[s2] = [_outboxRow((Guid)TrackedGuid.NewMedo(), s2)];

    var publish = new _StubPublisher();
    var worker = _buildOutboxDrainWorker(out var drainChannel, out var completion,
      coord: coord, publish: publish);

    var startedCount = 0;
    var idleCount = 0;
    worker.OnWorkProcessingStarted += () => Interlocked.Increment(ref startedCount);
    worker.OnWorkProcessingIdle += () => Interlocked.Increment(ref idleCount);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Process two streams sequentially — fixture-style wait reaches idle between batches.
    var firstIdleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnFirstIdle() { firstIdleTcs.TrySetResult(true); worker.OnWorkProcessingIdle -= OnFirstIdle; }
    worker.OnWorkProcessingIdle += OnFirstIdle;
    await drainChannel.WriteAsync(s1);
    await firstIdleTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    var secondIdleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnSecondIdle() { secondIdleTcs.TrySetResult(true); worker.OnWorkProcessingIdle -= OnSecondIdle; }
    worker.OnWorkProcessingIdle += OnSecondIdle;
    await drainChannel.WriteAsync(s2);
    await secondIdleTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    // Two batches → two started + two idle transitions.
    await Assert.That(startedCount).IsEqualTo(2);
    await Assert.That(idleCount).IsEqualTo(2);
    await Assert.That(publish.Published.Count).IsEqualTo(2);
    await Assert.That(completion.AllIds.Count).IsEqualTo(2);

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  [Test]
  public async Task InboxDrainWorker_StartsIdle_AndFiresStartedThenIdle_AroundEachBatchAsync() {
    var coord = new _StubCoordinator();
    var streamId = (Guid)TrackedGuid.NewMedo();
    var msgId = (Guid)TrackedGuid.NewMedo();
    coord.InboxRowsByStream[streamId] = [_inboxRow(msgId, streamId)];

    var worker = _buildInboxDrainWorker(out var drainChannel, out var inboxWriter, coord: coord);
    await Assert.That(worker.IsIdle).IsTrue();

    var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var idleTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingStarted += () => startedTcs.TrySetResult(true);
    worker.OnWorkProcessingIdle += () => idleTcs.TrySetResult(true);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drainChannel.WriteAsync(streamId);

    await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    await Assert.That(inboxWriter.WrittenIds.Count).IsEqualTo(1);
    await Assert.That(worker.IsIdle).IsTrue();

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  [Test]
  public async Task OutboxDrainWorker_FixtureStyleRaceAwareIdleSubscribe_ResolvesAsync() {
    // Reproduces the fixture cleanup pattern: subscribe to OnWorkProcessingIdle
    // while the worker MIGHT already be idle. The re-check after subscribe closes
    // the race. Asserting it here so a future refactor that drops the re-check
    // immediately breaks this test.
    var coord = new _StubCoordinator();
    var worker = _buildOutboxDrainWorker(out _, out _, coord: coord);
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);

    // Worker started; with no work it stays idle (await foreach blocks).
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    if (worker.IsIdle) { tcs.TrySetResult(true); } else {
      void Handler() { tcs.TrySetResult(true); worker.OnWorkProcessingIdle -= Handler; }
      worker.OnWorkProcessingIdle += Handler;
      if (worker.IsIdle) { tcs.TrySetResult(true); }
    }

    await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // --- builders + stubs ---

  private static readonly JsonSerializerOptions _jsonOpts =
    Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxDrainWorker _buildOutboxDrainWorker(
      out _FakeOutboxDrainChannel drainChannel,
      out _FakeOutboxCompletionChannel completion,
      _StubCoordinator? coord = null,
      _StubPublisher? publish = null) {
    drainChannel = new _FakeOutboxDrainChannel();
    completion = new _FakeOutboxCompletionChannel();
    coord ??= new _StubCoordinator();
    publish ??= new _StubPublisher();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();

    return new OutboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeInstance(),
      drainChannel,
      completion,
      new _FakeFailureChannel(),
      gate,
      Options.Create(new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<OutboxDrainWorker>.Instance,
      publish);
  }

  private static InboxDrainWorker _buildInboxDrainWorker(
      out _FakeInboxDrainChannel drainChannel,
      out _CapturingInboxWriter inboxWriter,
      _StubCoordinator? coord = null) {
    drainChannel = new _FakeInboxDrainChannel();
    inboxWriter = new _CapturingInboxWriter();
    coord ??= new _StubCoordinator();

    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    var sp = services.BuildServiceProvider();

    var gate = new SchemaReadyGate();
    gate.MarkReady();

    return new InboxDrainWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      new _FakeInstance(),
      drainChannel,
      inboxWriter,
      gate,
      Options.Create(new InboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 }),
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      NullLogger<InboxDrainWorker>.Instance);
  }

  private static OutboxBatchRow _outboxRow(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup");
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

  private static InboxBatchRow _inboxRow(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>))
      ?? throw new InvalidOperationException("Test setup");
    var envelopeJson = JsonSerializer.Serialize(envelope, typeInfo);
    return new InboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      HandlerName = "TestHandler",
      MessageType = "TestMessage",
      EventData = envelopeJson,
      Metadata = "{}",
      Status = 1,
      Attempts = 0,
      PartitionNumber = 0,
    };
  }

  private sealed class _FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class _FakeInboxDrainChannel : IInboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken ct = default) => _channel.Writer.WriteAsync(streamId, ct);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class _FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      AllIds.Add(id);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _FakeFailureChannel : IFailureChannel {
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default)
      => ValueTask.CompletedTask;
  }

  private sealed class _StubPublisher : IMessagePublishStrategy {
    public List<OutboxWork> Published { get; } = [];
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.Add(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  private sealed class _FakeInstance : IServiceInstanceProvider {
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

  private sealed class _CapturingInboxWriter : IInboxChannelWriter {
    // Not exercised by this fake: it tracks no in-flight work, so there is nothing to gate on.
    public int InFlightCount => 0;
    public int PruneInFlightOlderThan(TimeSpan age) => 0;
    public ConcurrentBag<Guid> WrittenIds { get; } = [];
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) {
      WrittenIds.Add(work.MessageId);
      return _channel.Writer.WriteAsync(work, ct);
    }
    public bool TryWrite(InboxWork work) {
      if (_channel.Writer.TryWrite(work)) { WrittenIds.Add(work.MessageId); return true; }
      return false;
    }
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class _StubCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> OutboxRowsByStream { get; } = [];
    public Dictionary<Guid, List<InboxBatchRow>> InboxRowsByStream { get; } = [];

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken ct = default) {
      var result = new List<OutboxBatchRow>();
      foreach (var sid in streamIds) {
        if (OutboxRowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
          rows.Clear();  // simulate the row being processed (next fetch returns empty → drain returns)
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<IReadOnlyList<InboxBatchRow>> FetchInboxBatchAsync(
      IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken ct = default) {
      var result = new List<InboxBatchRow>();
      foreach (var sid in streamIds) {
        if (InboxRowsByStream.TryGetValue(sid, out var rows)) {
          result.AddRange(rows.Take(maxPerStream));
          rows.Clear();
        }
      }
      return Task.FromResult<IReadOnlyList<InboxBatchRow>>(result);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion c, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure f, CancellationToken ct = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount = 2, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string name, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }
}
