using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Error-path coverage for <see cref="OutboxPublishWorker"/>:
/// <list type="bullet">
/// <item><description>Transport-not-ready re-queue (singular + bulk), with and without retry delay</description></item>
/// <item><description>Cancellation while parked in the not-ready delay (singular) and mid bulk publish</description></item>
/// <item><description>Bulk whole-batch catch routing every row to the failure channel (no DLQ wiring)</description></item>
/// <item><description>Bulk "no result returned" fabricated failure per item</description></item>
/// <item><description>Killswitch flip mid-run silently dropping successful publishes</description></item>
/// <item><description>Pre/Post outbox lifecycle stages via coordinator AND via direct receptor invocation</description></item>
/// <item><description>Event-store-only rows (empty destination) skipping outbox lifecycle entirely</description></item>
/// <item><description><see cref="OutboxPublishWorker.ShouldSkipOutboxPublish"/> discard-policy seam</description></item>
/// </list>
/// All waits are signal-based (TaskCompletionSource / SemaphoreSlim / worker events) — no polling.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxPublishWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerErrorPathTests {

  // ============================================================
  // Channel fakes
  // ============================================================

  private sealed class _FakeWorkChannelWriter : IWorkChannelWriter {
    private readonly System.Threading.Channels.Channel<OutboxWork> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<OutboxWork>();
    public System.Threading.Channels.ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    /// <summary>In-flight slots released without completing — how the deferred path settles.</summary>
    public ConcurrentBag<Guid> RemovedInFlight { get; } = [];

    public void RemoveInFlight(Guid messageId) => RemovedInFlight.Add(messageId);
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class _RecordingCompletionChannel : IOutboxCompletionChannel, IDisposable {
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public ConcurrentBag<Guid> Completed { get; } = [];
    public ValueTask EnqueueAsync(Guid id, CancellationToken ct = default) {
      Completed.Add(id);
      _signal.Release();
      return ValueTask.CompletedTask;
    }
    /// <summary>Signal-based wait for N completion enqueues — no polling.</summary>
    /// <remarks>
    /// The publish signal fires INSIDE the transport call; the completion is enqueued after that
    /// call returns. Asserting on the bag straight after awaiting the publish therefore races the
    /// worker and fails intermittently — the assertion has to wait for its own signal.
    /// </remarks>
    public async Task WaitForCountAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _signal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} completion enqueues within {timeout}");
        }
      }
    }
    public void Dispose() => _signal.Dispose();
  }

  private sealed class _RecordingFailureChannel : IFailureChannel, IDisposable {
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public ConcurrentBag<(WorkCategory Category, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken ct = default) {
      All.Add((category, failure));
      _signal.Release();
      return ValueTask.CompletedTask;
    }
    /// <summary>Signal-based wait for N failure enqueues — no polling.</summary>
    public async Task WaitForCountAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _signal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} failure enqueues within {timeout}");
        }
      }
    }
    public void Dispose() => _signal.Dispose();
  }

  private sealed class _RecordingLeaseRenewalChannel : ILeaseRenewalChannel, IDisposable {
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    public ConcurrentBag<(WorkCategory Category, Guid Id)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken ct = default) {
      All.Add((category, id));
      _signal.Release();
      return ValueTask.CompletedTask;
    }
    public async Task WaitForCountAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _signal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} lease-renewal enqueues within {timeout}");
        }
      }
    }
    public void Dispose() => _signal.Dispose();
  }

  // ============================================================
  // Publish strategy fakes
  // ============================================================

  /// <summary>Publishes successfully and records what it was asked to publish.</summary>
  private sealed class _RecordingStrategy : IMessagePublishStrategy {
    public ConcurrentBag<Guid> Published { get; } = [];

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct = default) {
      Published.Add(work.MessageId);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      });
    }
  }

  /// <summary>Reports not-ready for the first N readiness checks, then ready; publishes successfully.</summary>
  private sealed class _FlipReadyStrategy(int notReadyCount) : IMessagePublishStrategy {
    private int _readyChecks;
    public TaskCompletionSource NotReadySeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<OutboxWork> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) {
      var check = Interlocked.Increment(ref _readyChecks);
      if (check <= notReadyCount) {
        NotReadySeen.TrySetResult();
        return Task.FromResult(false);
      }
      return Task.FromResult(true);
    }
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.TrySetResult(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      });
    }
  }

  /// <summary>Never ready — used to park the worker in the not-ready retry delay.</summary>
  private sealed class _NeverReadyStrategy : IMessagePublishStrategy {
    public TaskCompletionSource NotReadySeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) {
      NotReadySeen.TrySetResult();
      return Task.FromResult(false);
    }
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("PublishAsync must not run when the transport is never ready");
  }

  /// <summary>Bulk strategy that reports not-ready for the first N checks, then publishes the batch successfully.</summary>
  private sealed class _FlipReadyBulkStrategy(int notReadyCount) : IMessagePublishStrategy, IDisposable {
    private int _readyChecks;
    public bool SupportsBulkPublish => true;
    public TaskCompletionSource NotReadySeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<Guid> PublishedIds { get; } = [];
    private readonly SemaphoreSlim _publishSignal = new(0, int.MaxValue);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) {
      var check = Interlocked.Increment(ref _readyChecks);
      if (check <= notReadyCount) {
        NotReadySeen.TrySetResult();
        return Task.FromResult(false);
      }
      return Task.FromResult(true);
    }
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("Bulk strategy — PublishBatchAsync is the exercised path");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      var results = new List<MessagePublishResult>(works.Count);
      foreach (var w in works) {
        PublishedIds.Add(w.MessageId);
        results.Add(new MessagePublishResult { MessageId = w.MessageId, Success = true, CompletedStatus = w.Status });
        _publishSignal.Release();
      }
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
    public async Task WaitForPublishedCountAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _publishSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Only saw {i} of {count} bulk publishes within {timeout}");
        }
      }
    }
    public void Dispose() => _publishSignal.Dispose();
  }

  /// <summary>Bulk strategy that returns an EMPTY result list, forcing the fabricated per-item failure.</summary>
  private sealed class _EmptyResultBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("Bulk strategy — PublishBatchAsync is the exercised path");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct)
      => Task.FromResult<IReadOnlyList<MessagePublishResult>>([]);
  }

  /// <summary>Bulk strategy that throws from PublishBatchAsync.</summary>
  private sealed class _ThrowingBulkStrategy(string message) : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("Bulk strategy — PublishBatchAsync is the exercised path");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct)
      => throw new InvalidOperationException(message);
  }

  /// <summary>Bulk strategy that blocks until its cancellation token fires (no Task.Delay — pure signal).</summary>
  private sealed class _BlockUntilCanceledBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public TaskCompletionSource PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct)
      => throw new InvalidOperationException("Bulk strategy — PublishBatchAsync is the exercised path");
    public async Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> works, CancellationToken ct) {
      PublishEntered.TrySetResult();
      var blocked = new TaskCompletionSource<IReadOnlyList<MessagePublishResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
      await using var _ = ct.Register(() => blocked.TrySetCanceled(ct));
      return await blocked.Task;
    }
  }

  /// <summary>Always-ready, always-successful singular strategy.</summary>
  /// <summary>Signals when PublishAsync is entered, then blocks until released — lets tests
  /// flip state at a provable point mid-publish without any timing assumptions.</summary>
  private sealed class _GatedPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      PublishEntered.TrySetResult();
      await ReleasePublish.Task.WaitAsync(ct);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      };
    }
  }

  private sealed class _SucceedingStrategy : IMessagePublishStrategy {
    public TaskCompletionSource<OutboxWork> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken ct) {
      Published.TrySetResult(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      });
    }
  }

  // ============================================================
  // Lifecycle fakes
  // ============================================================

  private sealed class _SpyLifecycleTracking(Guid eventId, List<LifecycleStage> advancedStages, bool throwOnAdvance = false) : ILifecycleTracking {
    public Guid EventId { get; } = eventId;
    public LifecycleStage CurrentStage { get; private set; }
    public bool IsComplete { get; private set; }
    public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
      if (throwOnAdvance) {
        throw new InvalidOperationException("simulated lifecycle advance failure");
      }
      lock (advancedStages) {
        advancedStages.Add(stage);
      }
      CurrentStage = stage;
      if (stage == LifecycleStage.PostLifecycleInline) {
        IsComplete = true;
      }
      return ValueTask.CompletedTask;
    }
    public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
  }

  private sealed class _SpyLifecycleCoordinator : ILifecycleCoordinator {
    public List<LifecycleStage> AdvancedStages { get; } = [];
    public ConcurrentBag<Guid> AbandonedIds { get; } = [];
    public LifecycleStage CapturedEntryStage { get; private set; }
    public MessageSource CapturedSource { get; private set; }
    public int BeginTrackingCount { get; private set; }

    public ILifecycleTracking BeginTracking(
        Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage,
        MessageSource source, Guid? streamId = null, Type? perspectiveType = null) {
      BeginTrackingCount++;
      CapturedEntryStage = entryStage;
      CapturedSource = source;
      return new _SpyLifecycleTracking(eventId, AdvancedStages);
    }

    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(
        Guid eventId, PostLifecycleCompletionSource source,
        IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) => AbandonedIds.Add(eventId);
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  private sealed class _RecordingReceptorInvoker : IReceptorInvoker {
    private readonly object _gate = new();
    private readonly List<LifecycleStage> _stages = [];
    public IReadOnlyList<LifecycleStage> Stages { get { lock (_gate) { return _stages.ToList(); } } }
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_gate) {
        _stages.Add(stage);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class _FakeLifecycleDeserializer : ILifecycleMessageDeserializer {
    private int _callCount;
    public int CallCount => _callCount;
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      Interlocked.Increment(ref _callCount);
      return new object();
    }
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      Interlocked.Increment(ref _callCount);
      return new object();
    }
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      Interlocked.Increment(ref _callCount);
      return new object();
    }
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      Interlocked.Increment(ref _callCount);
      return new object();
    }
  }

  private sealed class _FakeDiscardPolicy(bool shouldDiscard) : IMessageDiscardPolicy {
    public int RecordDiscardCount { get; private set; }
    public MessageDiscardGate? RecordedGate { get; private set; }
    public IReadOnlyDictionary<string, object?>? RecordedTags { get; private set; }
    public MessageDiscardDecision EvaluateReceive(string payloadClrType, string topic, string subscription)
      => new(false, MessageDiscardReason.None);
    public MessageDiscardDecision EvaluateInbox(string payloadClrType)
      => new(false, MessageDiscardReason.None);
    public MessageDiscardDecision EvaluateOutbox(string payloadClrType)
      => new(shouldDiscard, shouldDiscard ? MessageDiscardReason.NoLocalConsumer : MessageDiscardReason.None);
    public void RecordDiscard(
        MessageDiscardGate gate, MessageDiscardDecision decision,
        string payloadClrType, IReadOnlyDictionary<string, object?>? additionalTags = null) {
      RecordDiscardCount++;
      RecordedGate = gate;
      RecordedTags = additionalTags;
    }
  }

  // ============================================================
  // Helpers
  // ============================================================

  private const string VALID_TRACEPARENT = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

  private static OutboxWork _work(string? destination = "test-topic", string? traceParent = null) {
    var msgId = (Guid)TrackedGuid.NewMedo();
    List<MessageHop> hops = traceParent is null
      ? []
      : [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTimeOffset.UtcNow,
            ServiceInstance = ServiceInstanceInfo.Unknown,
            TraceParent = traceParent,
          }
        ];
    return new OutboxWork {
      MessageId = msgId,
      Destination = destination,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = hops,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.NewMedo(),
      PartitionNumber = 1,
      Attempts = 0,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed record _Fixture(
    OutboxPublishWorker Worker,
    _FakeWorkChannelWriter Channel,
    _RecordingCompletionChannel Completion,
    _RecordingFailureChannel Failure,
    _RecordingLeaseRenewalChannel Renewal,
    OutboxPublishWorkerOptions Options,
    SchemaReadyGate Gate);

  private static _Fixture _build(
      IMessagePublishStrategy strategy,
      int transportNotReadyRetryDelayMs = 0,
      IServiceProvider? serviceProvider = null,
      ILifecycleMessageDeserializer? lifecycleDeserializer = null,
      bool markGateReady = true,
      IOccurrencePublishGate? occurrenceGate = null) {
    var channel = new _FakeWorkChannelWriter();
    var completion = new _RecordingCompletionChannel();
    var failure = new _RecordingFailureChannel();
    var renewal = new _RecordingLeaseRenewalChannel();
    var gate = new SchemaReadyGate();
    if (markGateReady) {
      gate.MarkReady();
    }

    var sp = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    var options = new OutboxPublishWorkerOptions {
      Enabled = true,
      MaxOutboxAttempts = null,
      TransportNotReadyRetryDelayMilliseconds = transportNotReadyRetryDelayMs,
    };
    var worker = new OutboxPublishWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      channel, completion, failure, renewal, gate,
      Options.Create(options),
      NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      publishStrategy: strategy,
      lifecycleMessageDeserializer: lifecycleDeserializer,
      occurrenceGate: occurrenceGate);
    return new _Fixture(worker, channel, completion, failure, renewal, options, gate);
  }

  // ============================================================
  // Transport-not-ready: singular
  // ============================================================

  [Test]
  public async Task SingularPublish_TransportNotReadyOnce_RequeuesAndPublishesOnRetryAsync() {
    var strategy = new _FlipReadyStrategy(notReadyCount: 1);
    var fx = _build(strategy, transportNotReadyRetryDelayMs: 0);
    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);

    var published = await strategy.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(published.MessageId).IsEqualTo(work.MessageId)
      .Because("The not-ready path MUST re-buffer the same work item so the retry publishes it, not lose it.");
    await fx.Renewal.WaitForCountAsync(1, TimeSpan.FromSeconds(5));
    var renewal = fx.Renewal.All.Single();
    await Assert.That(renewal.Category).IsEqualTo(WorkCategory.Outbox)
      .Because("Not-ready re-queue must renew the OUTBOX lease so another instance doesn't steal the row mid-wait.");
    await Assert.That(renewal.Id).IsEqualTo(work.MessageId)
      .Because("The lease renewal must reference the re-buffered row.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_TransportNotReadyWithRetryDelay_PublishesAfterDelayAsync() {
    // Non-zero delay exercises the Task.Delay branch in _handleTransportNotReadyAsync (production
    // pacing, not test polling — the test itself waits on the publish signal).
    var strategy = new _FlipReadyStrategy(notReadyCount: 1);
    var fx = _build(strategy, transportNotReadyRetryDelayMs: 1);
    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);

    var published = await strategy.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(published.MessageId).IsEqualTo(work.MessageId)
      .Because("After the configured retry delay elapses, the re-buffered row must be published.");

    // The publish signal fires inside the transport call, before the worker enqueues the
    // completion — wait for the completion's own signal rather than racing it.
    await fx.Completion.WaitForCountAsync(1, TimeSpan.FromSeconds(5));
    await Assert.That(fx.Completion.Completed).Contains(work.MessageId)
      .Because("A successful retry publish must enqueue the outbox completion for the DB flush worker.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_CanceledWhileTransportNotReady_StopsCleanlyAsync() {
    // Large delay parks the worker inside the not-ready Task.Delay; cancellation must
    // propagate out as OperationCanceledException and end the loop without faulting.
    var strategy = new _NeverReadyStrategy();
    var fx = _build(strategy, transportNotReadyRetryDelayMs: 600_000);
    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    await fx.Channel.WriteAsync(_work(), cts.Token);
    await strategy.NotReadySeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await fx.Renewal.WaitForCountAsync(1, TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(fx.Worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("Cancellation during the not-ready delay must terminate ExecuteAsync.");
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("Shutdown cancellation is expected — the worker must swallow the OCE, not crash the host.");
  }

  // ============================================================
  // Transport-not-ready: bulk
  // ============================================================

  [Test]
  public async Task BulkPublish_TransportNotReady_RequeuesWholeBatchAndPublishesOnRetryAsync() {
    var strategy = new _FlipReadyBulkStrategy(notReadyCount: 1);
    var fx = _build(strategy, transportNotReadyRetryDelayMs: 0);

    var work1 = _work();
    var work2 = _work();
    // Queue before start so the first bulk read coalesces both rows into one batch.
    fx.Channel.TryWrite(work1);
    fx.Channel.TryWrite(work2);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    await strategy.WaitForPublishedCountAsync(2, TimeSpan.FromSeconds(5));
    await Assert.That(strategy.PublishedIds).Contains(work1.MessageId)
      .Because("Bulk not-ready must re-buffer EVERY row in the batch — losing any row strands it in wh_outbox.");
    await Assert.That(strategy.PublishedIds).Contains(work2.MessageId)
      .Because("Bulk not-ready must re-buffer EVERY row in the batch — losing any row strands it in wh_outbox.");
    await fx.Renewal.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
    var renewedIds = fx.Renewal.All.Select(r => r.Id).ToList();
    await Assert.That(renewedIds).Contains(work1.MessageId)
      .Because("Each re-buffered row needs its own lease renewal — a shared renewal would let the other row's lease lapse.");
    await Assert.That(renewedIds).Contains(work2.MessageId)
      .Because("Each re-buffered row needs its own lease renewal — a shared renewal would let the other row's lease lapse.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BulkPublish_EmptyResultList_FabricatesFailurePerItemAsync() {
    var strategy = new _EmptyResultBulkStrategy();
    var fx = _build(strategy);

    var work1 = _work();
    var work2 = _work();
    fx.Channel.TryWrite(work1);
    fx.Channel.TryWrite(work2);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    await fx.Failure.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
    var failedIds = fx.Failure.All.Select(f => f.Failure.MessageId).ToList();
    await Assert.That(failedIds).Contains(work1.MessageId)
      .Because("When the transport returns no result for a row, the worker must fabricate a failure for EACH row so none silently vanish.");
    await Assert.That(failedIds).Contains(work2.MessageId)
      .Because("When the transport returns no result for a row, the worker must fabricate a failure for EACH row so none silently vanish.");
    foreach (var (_, failure) in fx.Failure.All) {
      await Assert.That(failure.Error).IsEqualTo("No result returned from batch publish")
        .Because("The fabricated failure must carry the diagnostic sentinel text so operators can distinguish it from real transport errors.");
    }
    await Assert.That(fx.Completion.Completed).IsEmpty()
      .Because("No row got a success result, so nothing may be marked complete.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BulkPublish_PublishBatchThrowsWithoutDlqWiring_RoutesEveryRowToFailureChannelAsync() {
    var strategy = new _ThrowingBulkStrategy("simulated bulk transport meltdown");
    var fx = _build(strategy);

    var work1 = _work();
    var work2 = _work();
    fx.Channel.TryWrite(work1);
    fx.Channel.TryWrite(work2);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    await fx.Failure.WaitForCountAsync(2, TimeSpan.FromSeconds(5));
    var failedIds = fx.Failure.All.Select(f => f.Failure.MessageId).ToList();
    await Assert.That(failedIds).Contains(work1.MessageId)
      .Because("A whole-batch throw affects every in-flight row — each must route to the failure channel so process_outbox_failures bumps attempts.");
    await Assert.That(failedIds).Contains(work2.MessageId)
      .Because("A whole-batch throw affects every in-flight row — each must route to the failure channel so process_outbox_failures bumps attempts.");
    foreach (var (category, failure) in fx.Failure.All) {
      await Assert.That(category).IsEqualTo(WorkCategory.Outbox)
        .Because("Outbox-side failures must be categorized as Outbox for the failure flusher.");
      await Assert.That(failure.Error).IsEqualTo("simulated bulk transport meltdown")
        .Because("The exception message must reach the failure row for diagnosis.");
    }

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task BulkPublish_CanceledMidPublish_StopsCleanlyAsync() {
    var strategy = new _BlockUntilCanceledBulkStrategy();
    var fx = _build(strategy);

    fx.Channel.TryWrite(_work());

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await strategy.PublishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);

    await Assert.That(fx.Worker.ExecuteTask!.IsCompleted).IsTrue()
      .Because("Cancellation while a bulk publish is in flight must terminate the loop.");
    await Assert.That(fx.Worker.ExecuteTask!.IsFaulted).IsFalse()
      .Because("Shutdown cancellation must be swallowed by the bulk-loop OCE rethrow + ExecuteAsync catch, not crash the host.");
    await Assert.That(fx.Failure.All).IsEmpty()
      .Because("Shutdown cancellation is NOT a publish failure — the row stays leased in wh_outbox for the next instance to claim.");
  }

  // ============================================================
  // Killswitch flip mid-run
  // ============================================================

  [Test]
  public async Task RouteResult_KillswitchDisabledMidRun_DropsSuccessfulPublishSilentlyAsync() {
    // The worker is provably INSIDE PublishAsync when the killswitch flips: the gated strategy
    // signals entry and blocks until released, so Enabled=false is guaranteed to be observed
    // by _routeResultAsync (post-publish) and by nothing earlier — no start-order assumptions.
    var strategy = new _GatedPublishStrategy();
    var fx = _build(strategy);
    var publishedEvents = new ConcurrentBag<OutboxMessagePublishedEvent>();
    fx.Worker.OnOutboxMessagePublished += e => publishedEvents.Add(e);
    var idleSignal = new SemaphoreSlim(0, int.MaxValue);
    fx.Worker.OnWorkProcessingIdle += () => idleSignal.Release();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);
    await strategy.PublishEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

    fx.Options.Enabled = false;
    strategy.ReleasePublish.TrySetResult();

    // Idle fires AFTER _routeResultAsync returns for the item — deterministic ordering signal.
    await Assert.That(await idleSignal.WaitAsync(TimeSpan.FromSeconds(5))).IsTrue()
      .Because("The worker must reach idle after routing the (dropped) result.");

    await Assert.That(fx.Completion.Completed).IsEmpty()
      .Because("With the killswitch off, _routeResultAsync must drop the result — no completion enqueue.");
    await Assert.That(publishedEvents).IsEmpty()
      .Because("The killswitch return happens before OnOutboxMessagePublished — no observers may fire.");
    await Assert.That(fx.Failure.All).IsEmpty()
      .Because("Killswitch drop is silent — it must not fabricate failures either.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // Pre/Post outbox lifecycle
  // ============================================================

  [Test]
  public async Task SingularPublish_LifecycleCoordinatorWired_AdvancesAllOutboxAndPostLifecycleStagesAsync() {
    var strategy = new _SucceedingStrategy();
    var deserializer = new _FakeLifecycleDeserializer();
    var invoker = new _RecordingReceptorInvoker();
    var coordinator = new _SpyLifecycleCoordinator();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddScoped<ILifecycleCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();

    var fx = _build(strategy, serviceProvider: sp, lifecycleDeserializer: deserializer);
    var publishedEvent = new TaskCompletionSource<OutboxMessagePublishedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    fx.Worker.OnOutboxMessagePublished += e => publishedEvent.TrySetResult(e);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);

    // Valid traceparent hop exercises the trace-context extraction success branch.
    var work = _work(traceParent: VALID_TRACEPARENT);
    await fx.Channel.WriteAsync(work, cts.Token);

    // OnOutboxMessagePublished fires inside _routeResultAsync, which runs AFTER
    // _invokePostOutboxLifecycleAsync — awaiting it guarantees all stages advanced.
    var evt = await publishedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(evt.MessageId).IsEqualTo(work.MessageId);

    await Assert.That(coordinator.BeginTrackingCount).IsEqualTo(1)
      .Because("Exactly one tracking must be opened per outbox row.");
    await Assert.That(coordinator.CapturedEntryStage).IsEqualTo(LifecycleStage.PreOutboxDetached)
      .Because("Outbox lifecycle tracking must begin at PreOutboxDetached.");
    await Assert.That(coordinator.CapturedSource).IsEqualTo(MessageSource.Outbox)
      .Because("Outbox-side lifecycle must be tagged with the Outbox message source.");
    await Assert.That(coordinator.AdvancedStages).IsEquivalentTo([
      LifecycleStage.PreOutboxDetached,
      LifecycleStage.PreOutboxInline,
      LifecycleStage.PostOutboxDetached,
      LifecycleStage.PostOutboxInline,
      LifecycleStage.PostLifecycleDetached,
      LifecycleStage.PostLifecycleInline,
    ]).Because("The coordinator path must advance through both Pre stages before publish and both Post + both PostLifecycle stages after publish, in order.");
    await Assert.That(coordinator.AbandonedIds).Contains(work.MessageId)
      .Because("After PostLifecycleInline, the tracking must be abandoned so the coordinator doesn't leak state.");
    await Assert.That(deserializer.CallCount).IsEqualTo(1)
      .Because("The lifecycle path deserializes the payload exactly once for the typed envelope.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_NoCoordinator_InvokesLifecycleReceptorsDirectlyAsync() {
    var strategy = new _SucceedingStrategy();
    var deserializer = new _FakeLifecycleDeserializer();
    var invoker = new _RecordingReceptorInvoker();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    // No ILifecycleCoordinator — the direct receptor-invocation fallback runs.
    var sp = services.BuildServiceProvider();

    var fx = _build(strategy, serviceProvider: sp, lifecycleDeserializer: deserializer);
    var publishedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fx.Worker.OnOutboxMessagePublished += _ => publishedEvent.TrySetResult();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(), cts.Token);
    await publishedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // Each of the 4 outbox stages chains an ImmediateDetached invocation in the same closure.
    await Assert.That(invoker.Stages).IsEquivalentTo([
      LifecycleStage.PreOutboxDetached, LifecycleStage.ImmediateDetached,
      LifecycleStage.PreOutboxInline, LifecycleStage.ImmediateDetached,
      LifecycleStage.PostOutboxDetached, LifecycleStage.ImmediateDetached,
      LifecycleStage.PostOutboxInline, LifecycleStage.ImmediateDetached,
    ]).Because("Without a coordinator the worker must invoke receptors directly: each outbox stage followed by its chained ImmediateDetached, Pre stages before publish and Post stages after.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task SingularPublish_EventStoreOnlyRow_SkipsOutboxLifecycleEntirelyAsync() {
    var strategy = new _SucceedingStrategy();
    var deserializer = new _FakeLifecycleDeserializer();
    var invoker = new _RecordingReceptorInvoker();
    var coordinator = new _SpyLifecycleCoordinator();

    var services = new ServiceCollection();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddScoped<ILifecycleCoordinator>(_ => coordinator);
    var sp = services.BuildServiceProvider();

    var fx = _build(strategy, serviceProvider: sp, lifecycleDeserializer: deserializer);
    var publishedEvent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fx.Worker.OnOutboxMessagePublished += _ => publishedEvent.TrySetResult();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    // Null destination = event-store-only row → PreOutbox/PostOutbox lifecycle must not fire.
    await fx.Channel.WriteAsync(_work(destination: null), cts.Token);
    await publishedEvent.Task.WaitAsync(TimeSpan.FromSeconds(5));

    await Assert.That(deserializer.CallCount).IsEqualTo(0)
      .Because("Event-store-only rows skip lifecycle before any deserialization work is spent.");
    await Assert.That(coordinator.BeginTrackingCount).IsEqualTo(0)
      .Because("No lifecycle tracking may be opened for rows with no transport destination.");
    await Assert.That(invoker.Stages).IsEmpty()
      .Because("Neither the coordinator nor the direct path may invoke receptors for event-store-only rows.");

    await cts.CancelAsync();
    await fx.Worker.StopAsync(CancellationToken.None);
  }

  // ============================================================
  // ShouldSkipOutboxPublish discard-policy seam
  // ============================================================

  [Test]
  public async Task ShouldSkipOutboxPublish_NullPolicy_ReturnsFalseAsync() {
    var skip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      discardPolicy: null, messageType: "Test.SomeEvent, Test", messageId: Guid.CreateVersion7());
    await Assert.That(skip).IsFalse()
      .Because("No policy wired → the seam must never suppress a publish.");
  }

  [Test]
  public async Task ShouldSkipOutboxPublish_EmptyMessageType_ReturnsFalseWithoutEvaluatingAsync() {
    var policy = new _FakeDiscardPolicy(shouldDiscard: true);
    var skip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      policy, messageType: "", messageId: Guid.CreateVersion7());
    await Assert.That(skip).IsFalse()
      .Because("An empty message type cannot be evaluated — the safe default is publish.");
    await Assert.That(policy.RecordDiscardCount).IsEqualTo(0)
      .Because("The policy must not record a discard that never happened.");
  }

  [Test]
  public async Task ShouldSkipOutboxPublish_PolicyKeeps_ReturnsFalseWithoutRecordingAsync() {
    var policy = new _FakeDiscardPolicy(shouldDiscard: false);
    var skip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      policy, messageType: "Test.SomeEvent, Test", messageId: Guid.CreateVersion7());
    await Assert.That(skip).IsFalse()
      .Because("A ShouldDiscard=false decision must let the publish proceed.");
    await Assert.That(policy.RecordDiscardCount).IsEqualTo(0)
      .Because("RecordDiscard is only called after acting on a positive decision.");
  }

  [Test]
  public async Task ShouldSkipOutboxPublish_PolicyDiscards_RecordsWithMessageIdTagAndReturnsTrueAsync() {
    var policy = new _FakeDiscardPolicy(shouldDiscard: true);
    var messageId = Guid.CreateVersion7();
    var skip = OutboxPublishWorker.ShouldSkipOutboxPublish(
      policy, messageType: "Test.SomeEvent, Test", messageId: messageId);
    await Assert.That(skip).IsTrue()
      .Because("A positive discard decision must short-circuit the publish.");
    await Assert.That(policy.RecordDiscardCount).IsEqualTo(1)
      .Because("Exactly one discard record per suppressed publish — the telemetry contract.");
    await Assert.That(policy.RecordedGate).IsEqualTo(MessageDiscardGate.Outbox)
      .Because("The discard must be attributed to the Outbox gate for the skipped-counter tags.");
    await Assert.That(policy.RecordedTags!["message_id"]).IsEqualTo((object?)messageId)
      .Because("The message_id tag lets operators trace which row was suppressed.");
  }

  // ============================================================
  // The schedule-occurrence pre-fire gate
  // ============================================================
  //
  // A scheduled job's occurrence runs the consumer's own fire hook before it publishes, so the
  // hook can check authority, skip the run, or push it to a later time. Both non-Proceed
  // decisions have to leave the row in a settled state: a dropped occurrence that stays in the
  // outbox is published on the next pass — running the job the hook just refused — and a deferred
  // one that is completed here loses the reschedule the hook already made.

  private sealed class _DecidingGate(OccurrencePublishDecision decision) : IOccurrencePublishGate {
    public int Evaluations { get; private set; }
    public ValueTask<OccurrencePublishDecision> EvaluateAsync(
        OutboxWork work, CancellationToken cancellationToken = default) {
      Evaluations++;
      return ValueTask.FromResult(decision);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task GateDrops_CompletesTheRowWithoutPublishingAsync(CancellationToken testToken) {
    var strategy = new _RecordingStrategy();
    var gate = new _DecidingGate(OccurrencePublishDecision.Drop);
    var fx = _build(strategy, occurrenceGate: gate);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);
    while (!fx.Completion.Completed.Contains(work.MessageId) && !testToken.IsCancellationRequested) {
      await Task.Delay(20, testToken);
    }
    await cts.CancelAsync();

    await Assert.That(gate.Evaluations).IsGreaterThan(0);
    await Assert.That(strategy.Published).IsEmpty()
      .Because("the hook refused this run — publishing anyway would execute the job it declined");
    await Assert.That(fx.Completion.Completed).Contains(work.MessageId)
      .Because("a dropped occurrence left in the outbox is published on the next pass");
  }

  [Test]
  [Timeout(30000)]
  public async Task GateDefers_LetsGoWithoutCompletingOrPublishingAsync(CancellationToken testToken) {
    // Deferred means the gate already rescheduled the message. Completing it here would delete
    // the row the reschedule points at; publishing it would run the job at the time the hook
    // just moved it away from.
    var strategy = new _RecordingStrategy();
    var gate = new _DecidingGate(OccurrencePublishDecision.Deferred);
    var fx = _build(strategy, occurrenceGate: gate);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);

    // The deferred path settles by releasing the in-flight slot rather than completing.
    while (!fx.Channel.RemovedInFlight.Contains(work.MessageId) && !testToken.IsCancellationRequested) {
      await Task.Delay(20, testToken);
    }
    await cts.CancelAsync();

    await Assert.That(strategy.Published).IsEmpty();
    await Assert.That(fx.Completion.Completed).DoesNotContain(work.MessageId)
      .Because("completing a deferred occurrence deletes the row its reschedule points at");
  }

  [Test]
  [Timeout(30000)]
  public async Task GateProceeds_PublishesNormallyAsync(CancellationToken testToken) {
    // The control: the gate is on the hot path for every outbox row, so a Proceed must be
    // indistinguishable from having no gate at all.
    var strategy = new _RecordingStrategy();
    var gate = new _DecidingGate(OccurrencePublishDecision.Proceed);
    var fx = _build(strategy, occurrenceGate: gate);
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await fx.Worker.StartAsync(cts.Token);

    var work = _work();
    await fx.Channel.WriteAsync(work, cts.Token);
    while (strategy.Published.IsEmpty && !testToken.IsCancellationRequested) {
      await Task.Delay(20, testToken);
    }
    await cts.CancelAsync();

    await Assert.That(strategy.Published).IsNotEmpty();
  }
}
