// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Workers;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Branch-coverage tests for <see cref="OutboxPublishWorker"/>: the options constructor guard (null
/// options and null options value), the no-transport warning on and off, lease registration and
/// completion metering on both the singular and the bulk path, the null publish-error fallback text on
/// dead-letter promotion, an envelope with no hop list, and the lifecycle spans that open only when
/// lifecycle tracing is enabled (coordinator path and direct receptor path).
/// </summary>
/// <remarks>
/// Every wait is on a signal the worker body emits (a log line, a completion enqueue, a publish
/// entry, the idle event, a dead-letter move), never a delay (ai-docs/flaky-tests.md, pattern 7).
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxPublishWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxPublishWorkerBranchCoverageTests {
  private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

  // ============================================================
  // Fakes (mirrors OutboxPublishWorkerErrorPathTests / DlqPromotionTests)
  // ============================================================

  private sealed class FakeWorkChannelWriter : IWorkChannelWriter {
    private readonly Channel<OutboxWork> _channel = Channel.CreateUnbounded<OutboxWork>();
    public ChannelReader<OutboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(OutboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(OutboxWork work) => _channel.Writer.TryWrite(work);
    public void Complete() => _channel.Writer.Complete();
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public void ClearInFlight() { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public event Action? OnNewWorkAvailable;
    public void SignalNewWorkAvailable() => OnNewWorkAvailable?.Invoke();
    public event Action? OnNewPerspectiveWorkAvailable;
    public void SignalNewPerspectiveWorkAvailable() => OnNewPerspectiveWorkAvailable?.Invoke();
  }

  private sealed class FakeOutboxCompletionChannel : IOutboxCompletionChannel {
    public TaskCompletionSource<Guid> FirstId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      AllIds.Add(outboxMessageId);
      FirstId.TrySetResult(outboxMessageId);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<(WorkCategory Cat, MessageFailure Failure)> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add((category, failure));
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLeaseRenewalChannel : ILeaseRenewalChannel {
    public ValueTask EnqueueAsync(WorkCategory category, Guid id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class SucceedingStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      });
  }

  /// <summary>Signals when PublishAsync is entered, then blocks until released.</summary>
  private sealed class GatedPublishStrategy : IMessagePublishStrategy {
    public TaskCompletionSource PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public async Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      PublishEntered.TrySetResult();
      await ReleasePublish.Task.WaitAsync(cancellationToken);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = work.Status,
      };
    }
  }

  /// <summary>Returns a failed result that carries NO error text.</summary>
  private sealed class FailingWithoutErrorTextStrategy : IMessagePublishStrategy {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = false,
        CompletedStatus = work.Status,
        Error = null,
        Reason = MessageFailureReason.Unknown,
      });
  }

  private sealed class SucceedingBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken)
      => throw new InvalidOperationException("Bulk strategy: PublishBatchAsync is the exercised path");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      var results = workItems.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = w.Status,
      }).ToList();
      return Task.FromResult<IReadOnlyList<MessagePublishResult>>(results);
    }
  }

  /// <summary>Bulk strategy that signals when PublishBatchAsync is entered, then blocks until released.</summary>
  private sealed class GatedBulkStrategy : IMessagePublishStrategy {
    public bool SupportsBulkPublish => true;
    public TaskCompletionSource<int> PublishEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleasePublish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken)
      => throw new InvalidOperationException("Bulk strategy: PublishBatchAsync is the exercised path");
    public async Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      PublishEntered.TrySetResult(workItems.Count);
      await ReleasePublish.Task.WaitAsync(cancellationToken);
      return [.. workItems.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = w.Status,
      })];
    }
  }

  private sealed record MoveCall(string SourceTable, Guid SourceId, MessageFailureReason FailureReason, string? ErrorText);

  private sealed class FakeDeadLetterStore : IDeadLetterStore {
    public TaskCompletionSource<MoveCall> FirstMove { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      FirstMove.TrySetResult(new MoveCall(sourceTable, sourceId, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class NullValueOptions : IOptions<OutboxPublishWorkerOptions> {
    public OutboxPublishWorkerOptions Value => null!;
  }

  private sealed class SpyLifecycleTracking(Guid eventId, List<LifecycleStage> advancedStages) : ILifecycleTracking {
    public Guid EventId { get; } = eventId;
    public LifecycleStage CurrentStage { get; private set; }
    public bool IsComplete { get; private set; }
    public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
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

  private sealed class SpyLifecycleCoordinator : ILifecycleCoordinator {
    public List<LifecycleStage> AdvancedStages { get; } = [];
    public ILifecycleTracking BeginTracking(
        Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage,
        MessageSource source, Guid? streamId = null, Type? perspectiveType = null) =>
      new SpyLifecycleTracking(eventId, AdvancedStages);
    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }
    public ValueTask SignalSegmentCompleteAsync(
        Guid eventId, PostLifecycleCompletionSource source,
        IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;
    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  private sealed class RecordingReceptorInvoker : IReceptorInvoker {
    private readonly Lock _gate = new();
    private readonly List<LifecycleStage> _stages = [];
    public List<LifecycleStage> Stages() { lock (_gate) { return [.. _stages]; } }
    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_gate) {
        _stages.Add(stage);
      }
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new();
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static OutboxWork _work(int attempts = 0, string? traceParent = null, bool nullHops = false) {
    var msgId = (Guid)TrackedGuid.New();
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
      Destination = "test-topic",
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(msgId),
        Payload = JsonDocument.Parse("{}").RootElement,
        Hops = nullHops ? null! : hops,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      },
      EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[System.Text.Json.JsonElement, System.Text.Json]], Whizbang.Core",
      MessageType = "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  private sealed record Fixture(
    OutboxPublishWorker Worker,
    FakeWorkChannelWriter Channel,
    FakeOutboxCompletionChannel Completion,
    FakeFailureChannel Failure);

  private static OutboxPublishWorker _construct(
      IOptions<OutboxPublishWorkerOptions> options,
      IMessagePublishStrategy strategy,
      FakeWorkChannelWriter channel,
      FakeOutboxCompletionChannel completion,
      FakeFailureChannel failure,
      IServiceProvider? serviceProvider = null,
      TracingOptions? tracing = null,
      ILogger<OutboxPublishWorker>? logger = null,
      IDeadLetterStore? deadLetterStore = null,
      LeaseRegistry? leaseRegistry = null,
      WorkCompletionMeter? completionMeter = null) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var sp = serviceProvider ?? new ServiceCollection().BuildServiceProvider();
    return new OutboxPublishWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      workChannelWriter: channel,
      outboxCompletionChannel: completion,
      failureChannel: failure,
      leaseRenewalChannel: new FakeLeaseRenewalChannel(),
      schemaReadyGate: gate,
      options: options,
      logger: logger ?? NullLogger<OutboxPublishWorker>.Instance,
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      publishStrategy: strategy,
      lifecycleMessageDeserializer: new FakeLifecycleDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(tracing ?? new TracingOptions()),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: deadLetterStore ?? NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      occurrenceGate: new NoOpOccurrencePublishGate(),
      leaseRegistry: leaseRegistry,
      completionMeter: completionMeter);
  }

  private static Fixture _build(
      IMessagePublishStrategy strategy,
      bool enabled = true,
      int? maxOutboxAttempts = null,
      IServiceProvider? serviceProvider = null,
      TracingOptions? tracing = null,
      ILogger<OutboxPublishWorker>? logger = null,
      IDeadLetterStore? deadLetterStore = null,
      LeaseRegistry? leaseRegistry = null,
      WorkCompletionMeter? completionMeter = null) {
    var channel = new FakeWorkChannelWriter();
    var completion = new FakeOutboxCompletionChannel();
    var failure = new FakeFailureChannel();
    var options = Options.Create(new OutboxPublishWorkerOptions {
      Enabled = enabled,
      MaxOutboxAttempts = maxOutboxAttempts,
      TransportNotReadyRetryDelayMilliseconds = 0,
    });
    var worker = _construct(options, strategy, channel, completion, failure,
      serviceProvider, tracing, logger, deadLetterStore, leaseRegistry, completionMeter);
    return new Fixture(worker, channel, completion, failure);
  }

  private static async Task _stopAsync(OutboxPublishWorker worker, CancellationTokenSource cts) {
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
  }

  private static TaskCompletionSource _idleSignal(OutboxPublishWorker worker) {
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idle.TrySetResult();
    return idle;
  }

  // ============================================================
  // :78 options guard
  // ============================================================

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionNamingOptionsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = _construct(null!, new SucceedingStrategy(), new FakeWorkChannelWriter(), new FakeOutboxCompletionChannel(), new FakeFailureChannel());
      await Task.CompletedTask;
    });
    await Assert.That(ex?.ParamName).IsEqualTo("options")
      .Because("a missing options registration must fail construction naming the parameter, not NRE later in ExecuteAsync");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionNamingOptionsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = _construct(new NullValueOptions(), new SucceedingStrategy(), new FakeWorkChannelWriter(), new FakeOutboxCompletionChannel(), new FakeFailureChannel());
      await Task.CompletedTask;
    });
    await Assert.That(ex?.ParamName).IsEqualTo("options")
      .Because("an options wrapper whose Value is null is as unusable as no options at all and must be refused the same way");
  }

  // ============================================================
  // :130 no-transport warning
  // ============================================================

  [Test]
  public async Task ExecuteAsync_EnabledButNoTransport_WarnsNoTransportAndNeverDrainsAsync() {
    var logger = new EventIdSignalingLogger<OutboxPublishWorker>();
    var fx = _build(NullMessagePublishStrategy.Instance, enabled: true, logger: logger);
    await fx.Channel.WriteAsync(_work(), CancellationToken.None);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    // EventId 3 = LogDisabled, written after the no-transport warning on the same path.
    await logger.WhenLoggedAsync(3, _timeout);

    await Assert.That(logger.LinesWith(5)).Count().IsEqualTo(1)
      .Because("Enabled with no transport registered must warn that the host registered no transport");
    await Assert.That(fx.Channel.Reader.Count).IsEqualTo(1)
      .Because("with no transport the publish loop is skipped, so the queued row stays unread");

    await _stopAsync(fx.Worker, cts);
  }

  [Test]
  public async Task ExecuteAsync_DisabledWithTransport_DoesNotWarnNoTransportAsync() {
    var logger = new EventIdSignalingLogger<OutboxPublishWorker>();
    var fx = _build(new SucceedingStrategy(), enabled: false, logger: logger);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await logger.WhenLoggedAsync(3, _timeout);

    await Assert.That(logger.LinesWith(5)).IsEmpty()
      .Because("a configured transport that is merely disabled must not be reported as a missing transport");

    await _stopAsync(fx.Worker, cts);
  }

  // ============================================================
  // :182 / :283 lease registration
  // ============================================================

  [Test]
  public async Task SingularPublish_LeaseRegistryWired_RegistersLeaseDuringPublishAndRemovesItAfterAsync() {
    var strategy = new GatedPublishStrategy();
    var registry = new LeaseRegistry();
    var fx = _build(strategy, leaseRegistry: registry);
    var idle = _idleSignal(fx.Worker);
    var work = _work();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(work, cts.Token);
    await strategy.PublishEntered.Task.WaitAsync(_timeout);

    var registered = registry.TryGet(WorkCategory.Outbox, work.MessageId, out var handle);
    await Assert.That(registered).IsTrue()
      .Because("the renewal worker can only extend an in-flight row's deadline if its lease is registered while it publishes");
    await Assert.That(handle!.WorkId).IsEqualTo(work.MessageId);

    strategy.ReleasePublish.TrySetResult();
    await idle.Task.WaitAsync(_timeout);

    await Assert.That(registry.Count).IsEqualTo(0)
      .Because("the lease is disposed once the row is done, which removes it from the registry");

    await _stopAsync(fx.Worker, cts);
  }

  [Test]
  public async Task BulkPublish_LeaseRegistryWired_RegistersOneLeaseKeyedByFirstRowAsync() {
    var strategy = new GatedBulkStrategy();
    var registry = new LeaseRegistry();
    var fx = _build(strategy, leaseRegistry: registry);
    var idle = _idleSignal(fx.Worker);
    var first = _work();
    var second = _work();
    // Queued before start so the loop coalesces both into one batch.
    await fx.Channel.WriteAsync(first, CancellationToken.None);
    await fx.Channel.WriteAsync(second, CancellationToken.None);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    var batchSize = await strategy.PublishEntered.Task.WaitAsync(_timeout);

    await Assert.That(batchSize).IsEqualTo(2);
    await Assert.That(registry.TryGet(WorkCategory.Outbox, first.MessageId, out _)).IsTrue()
      .Because("the bulk path registers its single batch lease under the first row");
    await Assert.That(registry.Count).IsEqualTo(1)
      .Because("one lease covers the whole batch");

    strategy.ReleasePublish.TrySetResult();
    await idle.Task.WaitAsync(_timeout);

    await Assert.That(registry.Count).IsEqualTo(0)
      .Because("the batch lease is disposed after the batch, which removes it from the registry");

    await _stopAsync(fx.Worker, cts);
  }

  // ============================================================
  // :242 / :344 completion meter
  // ============================================================

  [Test]
  public async Task SingularPublish_CompletionMeterWired_RecordsOneCompletionPerRowAsync() {
    var meter = new WorkCompletionMeter();
    var fx = _build(new SucceedingStrategy(), completionMeter: meter);
    var idle = _idleSignal(fx.Worker);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(), cts.Token);
    // Idle fires after the per-row finally that records the completion.
    await idle.Task.WaitAsync(_timeout);

    await Assert.That(meter.ReadAndReset()).IsEqualTo(1L)
      .Because("each finished row frees one unit of capacity and must be metered for the claim budget");

    await _stopAsync(fx.Worker, cts);
  }

  [Test]
  public async Task BulkPublish_CompletionMeterWired_RecordsTheBatchSizeAsync() {
    var meter = new WorkCompletionMeter();
    var fx = _build(new SucceedingBulkStrategy(), completionMeter: meter);
    var idle = _idleSignal(fx.Worker);
    await fx.Channel.WriteAsync(_work(), CancellationToken.None);
    await fx.Channel.WriteAsync(_work(), CancellationToken.None);
    await fx.Channel.WriteAsync(_work(), CancellationToken.None);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await idle.Task.WaitAsync(_timeout);

    await Assert.That(fx.Completion.AllIds).Count().IsEqualTo(3);
    await Assert.That(meter.ReadAndReset()).IsEqualTo(3L)
      .Because("a bulk iteration must record its batch size, not one, or drain is under-reported by the batch factor");

    await _stopAsync(fx.Worker, cts);
  }

  // ============================================================
  // :379 null publish error at the attempts cap
  // ============================================================

  [Test]
  public async Task SingularPublish_FailedResultWithoutErrorText_AtCap_PromotesWithFallbackTextAsync() {
    var dlq = new FakeDeadLetterStore();
    var fx = _build(new FailingWithoutErrorTextStrategy(), maxOutboxAttempts: 2, deadLetterStore: dlq);
    var idle = _idleSignal(fx.Worker);
    var work = _work(attempts: 2);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(work, cts.Token);

    var move = await dlq.FirstMove.Task.WaitAsync(_timeout);
    await idle.Task.WaitAsync(_timeout);

    await Assert.That(move.SourceId).IsEqualTo(work.MessageId);
    await Assert.That(move.SourceTable).IsEqualTo(DeadLetterSourceTable.OUTBOX);
    await Assert.That(move.ErrorText).IsEqualTo("publish failed")
      .Because("a transport that reports failure without text must still leave a non-empty error on the dead letter");
    await Assert.That(fx.Failure.All).IsEmpty()
      .Because("a promoted row was deleted from the outbox, so the failure channel must not also be fed");

    await _stopAsync(fx.Worker, cts);
  }

  // ============================================================
  // :456 envelope with no hop list
  // ============================================================

  [Test]
  public async Task SingularPublish_EnvelopeWithNullHops_PublishesWithoutAParentTraceAsync() {
    var fx = _build(new SucceedingStrategy());
    var work = _work(nullHops: true);

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(work, cts.Token);

    var completedId = await fx.Completion.FirstId.Task.WaitAsync(_timeout);
    await Assert.That(completedId).IsEqualTo(work.MessageId)
      .Because("an envelope deserialized without a hop list has no trace parent to extract, which must not fail the publish");
    await Assert.That(fx.Failure.All).IsEmpty();

    await _stopAsync(fx.Worker, cts);
  }

  // ============================================================
  // :487 / :490 / :517-:526 / :555 lifecycle spans
  // ============================================================

  private static TracingOptions _lifecycleTracing() => new() {
    Verbosity = TraceVerbosity.Normal,
    Components = TraceComponents.Lifecycle,
  };

  private static (string TraceParent, ActivityTraceId TraceId) _newTraceParent() {
    var traceId = ActivityTraceId.CreateRandom();
    var spanId = ActivitySpanId.CreateRandom();
    return ($"00-{traceId.ToHexString()}-{spanId.ToHexString()}-01", traceId);
  }

  private static ActivityListener _listen(ConcurrentQueue<Activity> started) {
    // Resolve the name before registering: see PerspectiveWorkerDeepPathChannelTests for why.
    var tracingSourceName = WhizbangActivitySource.Tracing.Name;
    var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == tracingSourceName,
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
      ActivityStarted = started.Enqueue,
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
  }

  private static List<string> _lifecycleSpans(ConcurrentQueue<Activity> started, ActivityTraceId traceId) =>
    [.. started
      .Where(a => a.TraceId == traceId && a.OperationName.StartsWith("Lifecycle ", StringComparison.Ordinal))
      .Select(a => a.OperationName)];

  private static ServiceProvider _lifecycleServices(RecordingReceptorInvoker invoker, SpyLifecycleCoordinator? coordinator) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    if (coordinator is not null) {
      services.AddScoped<ILifecycleCoordinator>(_ => coordinator);
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task SingularPublish_CoordinatorPath_LifecycleTracingOn_OpensASpanPerStageUnderTheHopTraceAsync() {
    var started = new ConcurrentQueue<Activity>();
    using var listener = _listen(started);
    var coordinator = new SpyLifecycleCoordinator();
    await using var sp = _lifecycleServices(new RecordingReceptorInvoker(), coordinator);
    var fx = _build(new SucceedingStrategy(), serviceProvider: sp, tracing: _lifecycleTracing());
    var (traceParent, traceId) = _newTraceParent();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(traceParent: traceParent), cts.Token);
    // Completion is enqueued in route-result, after every post stage has run.
    await fx.Completion.FirstId.Task.WaitAsync(_timeout);

    await Assert.That(_lifecycleSpans(started, traceId)).IsEquivalentTo([
      "Lifecycle PreOutboxDetached",
      "Lifecycle PreOutboxInline",
      "Lifecycle PostOutboxDetached",
      "Lifecycle PostOutboxInline",
      "Lifecycle PostLifecycleDetached",
      "Lifecycle PostLifecycleInline",
    ]).Because("with lifecycle tracing on, each coordinator stage runs inside its own span parented to the row's hop trace");
    await Assert.That(coordinator.AdvancedStages).Count().IsEqualTo(6);

    await _stopAsync(fx.Worker, cts);
  }

  [Test]
  public async Task SingularPublish_CoordinatorPath_LifecycleTracingOff_OpensNoLifecycleSpansAsync() {
    var started = new ConcurrentQueue<Activity>();
    using var listener = _listen(started);
    var coordinator = new SpyLifecycleCoordinator();
    await using var sp = _lifecycleServices(new RecordingReceptorInvoker(), coordinator);
    var fx = _build(new SucceedingStrategy(), serviceProvider: sp);
    var (traceParent, traceId) = _newTraceParent();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(traceParent: traceParent), cts.Token);
    await fx.Completion.FirstId.Task.WaitAsync(_timeout);

    await Assert.That(_lifecycleSpans(started, traceId)).IsEmpty()
      .Because("lifecycle spans are opt-in: with the component off no span may be started even when a listener samples");
    await Assert.That(coordinator.AdvancedStages).Count().IsEqualTo(6)
      .Because("turning spans off must not skip the stages themselves");

    await _stopAsync(fx.Worker, cts);
  }

  [Test]
  public async Task SingularPublish_DirectReceptorPath_LifecycleTracingOn_OpensASpanPerOutboxStageAsync() {
    var started = new ConcurrentQueue<Activity>();
    using var listener = _listen(started);
    var invoker = new RecordingReceptorInvoker();
    await using var sp = _lifecycleServices(invoker, coordinator: null);
    var fx = _build(new SucceedingStrategy(), serviceProvider: sp, tracing: _lifecycleTracing());
    var (traceParent, traceId) = _newTraceParent();

    using var cts = new CancellationTokenSource();
    await fx.Worker.StartAsync(cts.Token);
    await fx.Channel.WriteAsync(_work(traceParent: traceParent), cts.Token);
    await fx.Completion.FirstId.Task.WaitAsync(_timeout);

    await Assert.That(_lifecycleSpans(started, traceId)).IsEquivalentTo([
      "Lifecycle PreOutboxDetached",
      "Lifecycle PreOutboxInline",
      "Lifecycle PostOutboxDetached",
      "Lifecycle PostOutboxInline",
    ]).Because("without a coordinator, each directly invoked outbox stage still runs inside its own span when lifecycle tracing is on");
    await Assert.That(invoker.Stages()).Count().IsEqualTo(8)
      .Because("each of the four stages chains an ImmediateDetached invocation");

    await _stopAsync(fx.Worker, cts);
  }
}
