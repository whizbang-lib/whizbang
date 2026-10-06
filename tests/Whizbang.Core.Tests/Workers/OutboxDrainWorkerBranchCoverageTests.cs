// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Execution;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Branch coverage for <see cref="OutboxDrainWorker"/>: governor metrics wiring, the byte budget's
/// off states, the dead-letter metrics on a control-plane drop, the attempt gate with no store or no
/// limit, a row with no destination on both publish paths, the published-event hook, a typed envelope
/// with no payload, and lifecycle stages outside the gated outbox set.
/// </summary>
/// <remarks>
/// Worker-driven tests wait on a signal the running body emits (a fetch, a publish, or the idle
/// transition at the end of a batch) before asserting; the rest call the worker's internal publish
/// and lifecycle seams directly.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerBranchCoverageTests {
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);
  private static readonly JsonSerializerOptions _jsonOpts = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  // ---- governor metrics -----------------------------------------------------------------------

  [Test]
  [NotInParallel("Metrics")]
  public async Task Constructor_WithGovernorMetrics_ExportsTheGovernorWidthUnderItsNameAsync() {
    using var factory = new TestMeterFactory();
    var governorMetrics = new GovernorMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    await using var sp = _sp(new BranchWorkCoordinator());

    using var worker = _worker(sp, new BranchDrainChannel(), new BranchCompletionChannel(), new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true }, new BranchPublishStrategy(),
      governor: new FixedGovernor(7), governorMetrics: governorMetrics);

    var widths = helper.GetByName("whizbang.governor.width");
    await Assert.That(widths).Count().IsEqualTo(1)
      .Because("with governor metrics registered the worker wraps its governor so the width is exported");
    await Assert.That(widths[0].Value).IsEqualTo(7d);
    await Assert.That(widths[0].Tags["governor"]).IsEqualTo(OutboxDrainWorker.GOVERNOR_KEY);
  }

  // ---- byte budget ----------------------------------------------------------------------------

  [Test]
  public async Task DrainStreamBatch_ByteBudgetUnset_FetchesUnboundedByBytesAsync() {
    var passed = await _byteBudgetPassedToFetchAsync(null);
    await Assert.That(passed).IsNull();
  }

  [Test]
  [Arguments(0L)]
  [Arguments(-1L)]
  public async Task DrainStreamBatch_NonPositiveByteBudget_IsTreatedAsOffAsync(long configured) {
    var passed = await _byteBudgetPassedToFetchAsync(configured);
    await Assert.That(passed).IsNull()
      .Because("a non-positive budget must disable the byte cap rather than starve every fetch");
  }

  [Test]
  public async Task DrainStreamBatch_PositiveByteBudget_IsPassedThroughAsync() {
    var passed = await _byteBudgetPassedToFetchAsync(65_536);
    await Assert.That(passed).IsEqualTo(65_536L);
  }

  private static async Task<long?> _byteBudgetPassedToFetchAsync(long? configured) {
    var streamId = (Guid)TrackedGuid.New();
    var coord = new BranchWorkCoordinator();
    await using var sp = _sp(coord);
    var drain = new BranchDrainChannel();
    var worker = _worker(sp, drain, new BranchCompletionChannel(), new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true, MaxBytesPerStream = configured }, new BranchPublishStrategy());

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, cts.Token);
    await coord.Fetched.Task.WaitAsync(_wait);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    return coord.MaxBytesSeen.First();
  }

  // ---- attempt gate ---------------------------------------------------------------------------

  [Test]
  [NotInParallel("Metrics")]
  public async Task ControlPlaneDrop_WithDeadLetterMetrics_CountsTheDropAsync() {
    using var factory = new TestMeterFactory();
    var dlqMetrics = new DeadLetterMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters[0]);
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    var coord = new BranchWorkCoordinator();
    coord.RowsByStream[streamId] = [
      _row(msgId, streamId, attempts: 10) with { MessageType = typeof(RequestRedeliveryCommand).AssemblyQualifiedName! },
    ];
    await using var sp = _sp(coord);
    var drain = new BranchDrainChannel();
    var completion = new BranchCompletionChannel();
    var publish = new BranchPublishStrategy();
    var worker = _worker(sp, drain, completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = 3 }, publish, dlqMetrics: dlqMetrics);

    await _drainOnceAsync(worker, drain, streamId);

    await Assert.That(completion.AllIds).Contains(msgId);
    await Assert.That(publish.Published).IsEmpty();
    var dropped = helper.GetByName("whizbang.dead_letters.added")
      .Where(m => m.Tags.TryGetValue("reason", out var reason) && reason == "ControlPlaneDropped")
      .Sum(m => m.Value);
    await Assert.That(dropped).IsEqualTo(1d)
      .Because("a dropped control-plane row is counted as a dead-letter disposal when the metrics are registered");
  }

  [Test]
  public async Task AttemptsOverLimit_NoDeadLetterStoreConfigured_PublishesTheRowAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    var coord = new BranchWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11)];
    await using var sp = _sp(coord);
    var drain = new BranchDrainChannel();
    var completion = new BranchCompletionChannel();
    var publish = new BranchPublishStrategy();
    var worker = _worker(sp, drain, completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = 10 }, publish);

    await _drainOnceAsync(worker, drain, streamId);

    await Assert.That(publish.Published.Select(w => w.MessageId)).IsEquivalentTo([msgId])
      .Because("with no dead-letter store to move it to, an over-limit row is still attempted rather than stranded");
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  [Test]
  public async Task AttemptsHigh_NoAttemptLimit_PublishesTheRowAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    var coord = new BranchWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 500)];
    await using var sp = _sp(coord);
    var drain = new BranchDrainChannel();
    var completion = new BranchCompletionChannel();
    var publish = new BranchPublishStrategy();
    var dlqStore = new CapturingDeadLetterStore();
    var worker = _worker(sp, drain, completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = null }, publish, deadLetterStore: dlqStore);

    await _drainOnceAsync(worker, drain, streamId);

    await Assert.That(publish.Published.Select(w => w.MessageId)).IsEquivalentTo([msgId]);
    await Assert.That(dlqStore.Moved).IsEmpty()
      .Because("with no attempt limit configured no row is ever dead-lettered, however many attempts it has");
  }

  [Test]
  public async Task AttemptsOverLimit_DeadLetterMoveCompletesAsynchronously_RowIsMovedAndNotPublishedAsync() {
    // Every other store fake answers synchronously, so the drain never resumes from inside the move's
    // try block. A real store suspends on the database; the resumed path must still skip the publish,
    // because the row it would publish no longer exists.
    var streamId = (Guid)TrackedGuid.New();
    var msgId = (Guid)TrackedGuid.New();
    var coord = new BranchWorkCoordinator();
    coord.RowsByStream[streamId] = [_row(msgId, streamId, attempts: 11)];
    await using var sp = _sp(coord);
    var drain = new BranchDrainChannel();
    var completion = new BranchCompletionChannel();
    var publish = new BranchPublishStrategy();
    var dlqStore = new YieldingDeadLetterStore();
    var worker = _worker(sp, drain, completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true, MaxOutboxAttempts = 10 }, publish, deadLetterStore: dlqStore);

    await _drainOnceAsync(worker, drain, streamId);

    await Assert.That(dlqStore.Moved).IsEquivalentTo([msgId])
      .Because("an over-limit row is moved to dead letters even when the move completes asynchronously");
    await Assert.That(publish.Published).IsEmpty()
      .Because("after the move resumes the row is skipped; publishing it would deliver a message already dead-lettered");
  }

  // ---- publish seams --------------------------------------------------------------------------

  [Test]
  public async Task PublishOne_RowWithoutDestination_LogsNullPlaceholderAndPublishesAsync() {
    var msgId = (Guid)TrackedGuid.New();
    await using var sp = _sp(new BranchWorkCoordinator());
    var completion = new BranchCompletionChannel();
    var publish = new BranchPublishStrategy();
    var logger = new CapturingLogger();
    var worker = _worker(sp, new BranchDrainChannel(), completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true }, publish, logger: logger);

    var published = await worker.PublishOneAsync(_row(msgId, (Guid)TrackedGuid.New()) with { Destination = null }, CancellationToken.None);

    await Assert.That(published).IsTrue();
    await Assert.That(logger.MessagesFor(45).Single()).Contains("dest=<null>")
      .Because("a row with no destination is logged with an explicit placeholder rather than an empty value");
    await Assert.That(publish.Published.Single().Destination).IsNull();
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  [Test]
  public async Task PublishOne_WithPublishedSubscriber_RaisesTheEventWithTheRowsIdentityAsync() {
    var msgId = (Guid)TrackedGuid.New();
    await using var sp = _sp(new BranchWorkCoordinator());
    var worker = _worker(sp, new BranchDrainChannel(), new BranchCompletionChannel(), new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true }, new BranchPublishStrategy());
    var raised = new List<OutboxMessagePublishedEvent>();
    worker.OnOutboxMessagePublished += e => raised.Add(e);

    var published = await worker.PublishOneAsync(_row(msgId, (Guid)TrackedGuid.New()), CancellationToken.None);

    await Assert.That(published).IsTrue();
    await Assert.That(raised).Count().IsEqualTo(1);
    await Assert.That(raised[0].MessageId).IsEqualTo(msgId);
    await Assert.That(raised[0].Destination).IsEqualTo("branch-test-topic");
  }

  [Test]
  public async Task PublishBulk_RowWithoutDestination_LogsNullPlaceholderAndPublishesAsync() {
    var msgId = (Guid)TrackedGuid.New();
    await using var sp = _sp(new BranchWorkCoordinator());
    var completion = new BranchCompletionChannel();
    var publish = new BranchBulkPublishStrategy();
    var logger = new CapturingLogger();
    var worker = _worker(sp, new BranchDrainChannel(), completion, new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true }, publish, logger: logger);

    await worker.PublishBulkAsync([_row(msgId, (Guid)TrackedGuid.New()) with { Destination = null }], CancellationToken.None);

    await Assert.That(logger.MessagesFor(36).Single()).Contains("destination=<null>");
    await Assert.That(publish.BatchCalls.Single().Single().Destination).IsNull();
    await Assert.That(completion.AllIds).Contains(msgId);
  }

  // ---- lifecycle seam -------------------------------------------------------------------------

  [Test]
  public async Task InvokeOutboxLifecycleStage_TypedEnvelopeWithoutPayload_DoesNotConsultRuntimeRegistryOrInvokeAsync() {
    await using var sp = _sp(new BranchWorkCoordinator());
    var failure = new BranchFailureChannel();
    var worker = _worker(sp, new BranchDrainChannel(), new BranchCompletionChannel(), failure,
      new OutboxDrainWorkerOptions { Enabled = true }, new BranchPublishStrategy(),
      registryQuery: new NeverHasReceptorsQuery(), runtimeRegistry: new AlwaysHasReceptorsRegistry());
    var invoker = new CapturingReceptorInvoker();
    var typed = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = null!,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox }
    };

    await worker.InvokeOutboxLifecycleStageAsync(
      _work(), typed, invoker, LifecycleStage.PreOutboxDetached, LifecycleStage.PreOutboxInline, "PreOutbox", CancellationToken.None);

    await Assert.That(invoker.Stages).IsEmpty()
      .Because("with no payload there is no runtime type to ask the runtime registry about, so a gated stage has no receptors");
    await Assert.That(failure.All).IsEmpty();
  }

  [Test]
  public async Task InvokeOutboxLifecycleStage_StagesOutsideTheGatedSet_FireWithoutAskingTheRegistriesAsync() {
    var detached = new CapturingReceptorInvoker();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(new BranchWorkCoordinator());
    services.AddSingleton<IReceptorInvoker>(detached);
    await using var sp = services.BuildServiceProvider();
    var worker = _worker(sp, new BranchDrainChannel(), new BranchCompletionChannel(), new BranchFailureChannel(),
      new OutboxDrainWorkerOptions { Enabled = true }, new BranchPublishStrategy(),
      registryQuery: new NeverHasReceptorsQuery());
    var inline = new CapturingReceptorInvoker();
    var work = _work();

    await worker.InvokeOutboxLifecycleStageAsync(
      work, work.Envelope, inline, LifecycleStage.ImmediateDetached, LifecycleStage.PostLifecycleInline, "Other", CancellationToken.None);
    await detached.FirstCall.Task.WaitAsync(_wait);

    await Assert.That(inline.Stages).IsEquivalentTo([LifecycleStage.PostLifecycleInline])
      .Because("a stage the registry query does not gate is invoked unconditionally");
    await Assert.That(detached.Stages).IsEquivalentTo([LifecycleStage.ImmediateDetached]);
  }

  // ---- helpers --------------------------------------------------------------------------------

  private static async Task _drainOnceAsync(OutboxDrainWorker worker, BranchDrainChannel drain, Guid streamId) {
    var idled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnWorkProcessingIdle += () => idled.TrySetResult();
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await drain.WriteAsync(streamId, cts.Token);
    // The idle transition fires in the batch's finally block, after the drain and its flush.
    await idled.Task.WaitAsync(_wait);
    await cts.CancelAsync();
    await worker.ExecuteTask!.WaitAsync(_wait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
  }

  private static OutboxBatchRow _row(Guid messageId, Guid streamId, int attempts = 0) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOpts.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "branch-test-topic",
      MessageType = "TestMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = JsonSerializer.Serialize(envelope, typeInfo),
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = attempts,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  private static OutboxWork _work() => new() {
    MessageId = (Guid)TrackedGuid.New(),
    Destination = "branch-test-topic",
    Envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [],
    },
    EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
    MessageType = "TestMessage",
    Attempts = 0,
  };

  private static OutboxDrainWorker _worker(
      IServiceProvider sp,
      BranchDrainChannel drainChannel,
      BranchCompletionChannel completion,
      BranchFailureChannel failure,
      OutboxDrainWorkerOptions options,
      IMessagePublishStrategy publish,
      ILogger<OutboxDrainWorker>? logger = null,
      IReceptorRegistryQuery? registryQuery = null,
      IReceptorRegistry? runtimeRegistry = null,
      IDeadLetterStore? deadLetterStore = null,
      DeadLetterMetrics? dlqMetrics = null,
      IConcurrencyGovernor? governor = null,
      GovernorMetrics? governorMetrics = null) {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new OutboxDrainWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new BranchServiceInstanceProvider(),
      drainChannel: drainChannel,
      completionChannel: completion,
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(options),
      jsonOptions: _jsonOpts,
      logger: logger ?? NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: publish,
      // Fails every lifecycle deserialize, so the publish seams skip lifecycle and security setup.
      lifecycleMessageDeserializer: new ThrowingLifecycleDeserializer(),
      receptorRegistry: registryQuery ?? new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: runtimeRegistry ?? NullReceptorRegistry.Instance,
      deadLetterStore: deadLetterStore ?? NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: governor ?? OutboxDrainWorker.CreateDefaultGovernor(options),
      dlqMetrics: dlqMetrics,
      governorMetrics: governorMetrics);
  }

  private static ServiceProvider _sp(BranchWorkCoordinator coord) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coord);
    return services.BuildServiceProvider();
  }

  // ---- fakes ----------------------------------------------------------------------------------

  private sealed class BranchDrainChannel : IOutboxDrainChannel {
    private readonly System.Threading.Channels.Channel<Guid> _channel =
      System.Threading.Channels.Channel.CreateUnbounded<Guid>();
    public System.Threading.Channels.ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class BranchCompletionChannel : IOutboxCompletionChannel {
    public ConcurrentBag<Guid> AllIds { get; } = [];
    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      AllIds.Add(outboxMessageId);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class BranchFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class BranchPublishStrategy : IMessagePublishStrategy {
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      Published.Enqueue(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  private sealed class BranchBulkPublishStrategy : IMessagePublishStrategy {
    public List<IReadOnlyList<OutboxWork>> BatchCalls { get; } = [];
    public bool SupportsBulkPublish => true;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      throw new InvalidOperationException("PublishAsync must not be called on a bulk-capable strategy");
    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) {
      lock (BatchCalls) { BatchCalls.Add(workItems); }
      IReadOnlyList<MessagePublishResult> results = [.. workItems.Select(w => new MessagePublishResult {
        MessageId = w.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      })];
      return Task.FromResult(results);
    }
  }

  private sealed class BranchServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "branch-test-svc";
    public string HostName => "branch-test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  /// <summary>Serves rows per stream (consuming them), and records the byte budget each fetch was given.</summary>
  private sealed class BranchWorkCoordinator : IWorkCoordinator {
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public ConcurrentQueue<long?> MaxBytesSeen { get; } = new();
    public TaskCompletionSource Fetched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult((Guid)TrackedGuid.New());

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream, long? maxBytes,
        CancellationToken cancellationToken = default) {
      MaxBytesSeen.Enqueue(maxBytes);
      var result = new List<OutboxBatchRow>();
      lock (RowsByStream) {
        foreach (var sid in streamIds) {
          if (RowsByStream.TryGetValue(sid, out var rows)) {
            var taken = rows.Take(maxPerStream).ToList();
            result.AddRange(taken);
            rows.RemoveRange(0, taken.Count);
          }
        }
      }
      Fetched.TrySetResult();
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class ThrowingLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => throw new NotSupportedException();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => throw new NotSupportedException();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => throw new NotSupportedException();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => throw new NotSupportedException();
  }

  private sealed class CapturingReceptorInvoker : IReceptorInvoker {
    private readonly Lock _lock = new();
    private readonly List<LifecycleStage> _stages = [];
    public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<LifecycleStage> Stages {
      get { lock (_lock) { return [.. _stages]; } }
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_lock) {
        _stages.Add(stage);
      }
      FirstCall.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  private sealed class NeverHasReceptorsQuery : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType) => false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType) => false;
  }

  /// <summary>Reports a receptor for every type and stage it is asked about.</summary>
  private sealed class AlwaysHasReceptorsRegistry : IReceptorRegistry {
    private static readonly ReceptorInfo _info = new(
      typeof(object),
      "branch-runtime-receptor",
      (_, _, _, _, _) => ValueTask.FromResult<object?>(null));
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [_info];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  private sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<Guid> Moved { get; } = [];
    public Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      Moved.Add(sourceId);
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  /// <summary>Records each move after yielding, as a store that writes to the database does.</summary>
  private sealed class YieldingDeadLetterStore : IDeadLetterStore {
    public ConcurrentBag<Guid> Moved { get; } = [];
    public async Task<Guid?> MoveAsync(
        Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText,
        Guid instanceId, string generation, CancellationToken ct = default) {
      await Task.Yield();
      Moved.Add(sourceId);
      return deadLetterId;
    }
  }

  private sealed class FixedGovernor(int width) : IConcurrencyGovernor {
    public int CurrentWidth => width;
    public int Floor => width;
    public int Ceiling => width;
    public void Observe(GovernorSignal signal) { }
  }

  /// <summary>Always-enabled logger that keeps each formatted message with its event id.</summary>
  private sealed class CapturingLogger : ILogger<OutboxDrainWorker> {
    private readonly ConcurrentQueue<(int Id, string Message)> _entries = new();

    public List<string> MessagesFor(int eventId) =>
      [.. _entries.Where(e => e.Id == eventId).Select(e => e.Message)];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      _entries.Enqueue((eventId.Id, formatter(state, exception)));
  }
}
