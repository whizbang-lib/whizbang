using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage tests for ServiceBusConsumerWorker targeting uncovered error paths:
/// - StartAsync exception handling (lines 105-106)
/// - ExecuteAsync fatal error handling (lines 125-126)
/// </summary>
[Category("Workers")]
public class ServiceBusConsumerWorkerCoverageTests {

  [Test]
  public async Task StartAsync_WhenSubscribeFails_LogsAndRethrowsAsync() {
    // Arrange - Transport that throws on subscribe (exercises lines 105-106)
    var failingTransport = new FailingTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(new TestWorkCoordinatorStrategy(
      () => new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] }
    ));
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();
    var orderedProcessor = new OrderedStreamProcessor();

    var workerOptions = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("test-topic", "test-sub")
      ]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: failingTransport,
      scopeFactory: scopeFactory,
      jsonOptions: jsonOptions,
      logger: logger,
      orderedProcessor: orderedProcessor,
      options: workerOptions,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act & Assert — subscribing happens in the background now (behind the schema gate), so the
    // failure surfaces through SubscriptionsReady rather than StartAsync. A waiter must fault,
    // never hang, when the subscriptions will not arrive.
    await worker.StartAsync(CancellationToken.None);
    await Assert.That(async () => await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5)))
      .Throws<InvalidOperationException>();
    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task ExecuteAsync_WhenFatalErrorOccurs_LogsAndRethrowsAsync() {
    // Arrange - Create a worker with no subscriptions (so StartAsync succeeds quickly)
    // Then cancel immediately to trigger the OperationCanceledException path (line 122-123)
    var transport = new TestTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(new TestWorkCoordinatorStrategy(
      () => new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] }
    ));
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();
    var logger = new TestLogger<ServiceBusConsumerWorker>();
    var orderedProcessor = new OrderedStreamProcessor();

    var workerOptions = new ServiceBusConsumerOptions {
      Subscriptions = [] // No subscriptions
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      jsonOptions: jsonOptions,
      logger: logger,
      orderedProcessor: orderedProcessor,
      options: workerOptions,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    // Act - Start then stop (triggers OperationCanceledException in ExecuteAsync)
    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    // Cancel to stop ExecuteAsync — StopAsync will also cancel and await the task
    await cts.CancelAsync();

    // Stop gracefully — StopAsync awaits ExecuteAsync completion, no delay needed
    await worker.StopAsync(CancellationToken.None);

    // Assert - Worker stopped without throwing (OperationCanceledException was caught)
    var stopped = true;
    await Assert.That(stopped).IsTrue();
  }

  // NOTE: a test for the non-cancellation catch in ExecuteAsync's idle wait
  // (ServiceBusConsumerWorker.cs:226-227) was attempted here and removed. It relied on
  // Task.Delay(Timeout.Infinite, token) throwing ObjectDisposedException for a token whose source
  // was already disposed; it does not -- the delay simply never completes, so the test hung to its
  // timeout instead of failing. Reaching that catch needs the idle wait to fault with something
  // other than OperationCanceledException, and no seam in the current API produces one.
  // See residue entry BB.

  [Test]
  public async Task HandleMessage_WhenCompletionQueueingThrows_QueuesFailureAndStillCompletesOtherStreamAsync() {
    // A transient DB error (deadlock, connection blip) while queuing ONE stream's completion
    // must not take down the whole delivered batch: OrderedStreamProcessor routes the failure
    // to failureHandler for that stream only, and every OTHER stream in the same batch still
    // has to reach QueueInboxCompletion. If this regressed, one flaky stream could silently
    // starve every sibling stream sharing the inbound message.
    var messageId = MessageId.New();
    var failingStreamId = Guid.NewGuid();
    var healthyStreamId = Guid.NewGuid();
    const string messageTypeName = "Whizbang.Core.Tests.Workers.CoverageWorkerTestEvent, Whizbang.Core.Tests";

    var batch = new WorkBatch {
      InboxWork = [
        new InboxWork {
          MessageId = messageId.Value,
          Envelope = _buildJsonEnvelope(messageId, failingStreamId),
          MessageType = messageTypeName,
          StreamId = failingStreamId,
          Status = MessageProcessingStatus.None,
          Attempts = 0
        },
        new InboxWork {
          MessageId = messageId.Value,
          Envelope = _buildJsonEnvelope(messageId, healthyStreamId),
          MessageType = messageTypeName,
          StreamId = healthyStreamId,
          Status = MessageProcessingStatus.None,
          Attempts = 0
        }
      ],
      OutboxWork = [],
      PerspectiveWork = []
    };

    var strategy = new CompletionThrowingWorkCoordinatorStrategy(batch);
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var transport = new CapturingBatchTransport();
    var worker = _buildWorker(
      transport,
      new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] },
      services);

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _buildJsonEnvelope(messageId, failingStreamId);
    var envelopeType = $"MessageEnvelope`1[[{messageTypeName}]], Whizbang.Core";

    await transport.CapturedBatchHandler!([new TransportMessage(envelope, envelopeType)], CancellationToken.None);

    await Assert.That(strategy.Failures.Count).IsEqualTo(1)
      .Because("the throwing stream's completion failure must be routed to QueueInboxFailure, not lost");
    await Assert.That(strategy.Completions.Count).IsEqualTo(1)
      .Because("a failure on one stream must not stop the batch's other stream from completing normally");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task FireDetachedStage_WhenReceptorInvokerMissingInFreshScope_SkipsFiringButStillCompletesAsync() {
    // Every detached lifecycle stage (PreInboxDetached, PostInboxDetached, and the
    // PostLifecycleDetached fallback used when no ILifecycleCoordinator is registered) resolves
    // IReceptorInvoker from a BRAND NEW DI scope — not the scope that gated entry into the
    // lifecycle methods. If that resolution ever comes back null (a scoped-registration
    // ordering bug, a container swap mid-flight), those detached stages must bail out cleanly
    // instead of NullReferenceException-ing, and the core inbox pipeline (dedup + completion
    // tracking) must still finish for the message.
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();
    const string messageTypeName = "Whizbang.Core.Tests.Workers.CoverageWorkerTestEvent, Whizbang.Core.Tests";

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _buildJsonEnvelope(messageId, streamId),
      MessageType = messageTypeName,
      StreamId = streamId,
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };
    var strategy = new RecordingWorkCoordinatorStrategy(
      new WorkBatch { InboxWork = [inboxWork], OutboxWork = [], PerspectiveWork = [] });

    var registry = new SpyReceptorRegistry();
    var resolutionCount = 0;
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(o => { o.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => {
      var call = Interlocked.Increment(ref resolutionCount);
      // Only the very first resolution — the scope _handleMessageAsync itself created — gets a
      // real invoker. Every later resolution happens inside a freshly created detached-stage
      // scope, which is exactly the gap this test targets.
      return call == 1 ? new ReceptorInvoker(registry, sp) : null!;
    });

    var transport = new CapturingBatchTransport();
    var worker = _buildWorker(
      transport,
      new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] },
      services,
      lifecycleMessageDeserializer: new SimpleLifecycleMessageDeserializer());

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _buildJsonEnvelope(messageId, streamId);
    var envelopeType = $"MessageEnvelope`1[[{messageTypeName}]], Whizbang.Core";

    await transport.CapturedBatchHandler!([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
    await worker.DrainDetachedAsync();

    await Assert.That(resolutionCount).IsGreaterThanOrEqualTo(2)
      .Because("PreInboxDetached/PostInboxDetached/PostLifecycleDetached each resolve IReceptorInvoker from their own fresh scope, independent of the scope that gated entry");
    await Assert.That(strategy.Completions.Count).IsEqualTo(1)
      .Because("a missing invoker in the detached scopes must not stop the core inbox completion from being queued");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task FireDetachedStageStatic_WhenReceptorThrows_LogsViaFreshScopeLoggerAndInlineStageStillFiresAsync() {
    // The PostLifecycleDetached fallback (used when no ILifecycleCoordinator is registered)
    // runs on a detached Task.Run with its OWN try/catch: a receptor exception there must be
    // logged via a logger resolved from a brand-new scope (the original message scope is
    // already disposed by the time this runs) — never swallowed invisibly — and it must not
    // take down the synchronous PostLifecycleInline firing that happens alongside it on the
    // main flow.
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();
    const string messageTypeName = "Whizbang.Core.Tests.Workers.CoverageWorkerTestEvent, Whizbang.Core.Tests";

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _buildJsonEnvelope(messageId, streamId),
      MessageType = messageTypeName,
      StreamId = streamId,
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };
    var strategy = new RecordingWorkCoordinatorStrategy(
      new WorkBatch { InboxWork = [inboxWork], OutboxWork = [], PerspectiveWork = [] });

    var registry = new SpyReceptorRegistry();
    var inlineFired = new System.Collections.Concurrent.ConcurrentBag<LifecycleStage>();
    registry.AddReceptor(LifecycleStage.PostLifecycleDetached, typeof(CoverageWorkerTestEvent), new ReceptorInfo(
      MessageType: typeof(CoverageWorkerTestEvent),
      ReceptorId: "coverage-postlifecycle-detached-throws",
      InvokeAsync: (_, _, _, _, _) => throw new InvalidOperationException("simulated detached receptor failure")
    ));
    registry.AddReceptor(LifecycleStage.PostLifecycleInline, typeof(CoverageWorkerTestEvent), new ReceptorInfo(
      MessageType: typeof(CoverageWorkerTestEvent),
      ReceptorId: "coverage-postlifecycle-inline",
      InvokeAsync: (_, _, _, _, _) => {
        inlineFired.Add(LifecycleStage.PostLifecycleInline);
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var recordingLogger = new RecordingLogger<ServiceBusConsumerWorker>();
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(o => { o.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddSingleton<ILogger<ServiceBusConsumerWorker>>(recordingLogger);

    var transport = new CapturingBatchTransport();
    var worker = _buildWorker(
      transport,
      new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] },
      services,
      lifecycleMessageDeserializer: new SimpleLifecycleMessageDeserializer());

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _buildJsonEnvelope(messageId, streamId);
    var envelopeType = $"MessageEnvelope`1[[{messageTypeName}]], Whizbang.Core";

    await transport.CapturedBatchHandler!([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
    await worker.DrainDetachedAsync();

    await Assert.That(recordingLogger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException))
      .IsTrue()
      .Because("a detached-stage receptor failure must be logged via the freshly resolved logger, not swallowed invisibly");
    await Assert.That(inlineFired).Contains(LifecycleStage.PostLifecycleInline)
      .Because("the synchronous PostLifecycleInline firing must not be derailed by the sibling detached stage's failure");

    await worker.StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task HandleMessage_WhenEnvelopeTypeHasEmptyBracketedTypeName_ThrowsInvalidOperationExceptionAsync() {
    // A malformed producer (or a transport bug) that serializes an envelope-type string with an
    // empty bracketed section — "MessageEnvelope`1[[]], ..." — must fail loudly right here.
    // Silently accepting it would create an inbox row whose MessageType is empty, which no
    // downstream consumer or dispatch-by-type lookup can ever match — the message would sit
    // unprocessed forever with no diagnostic pointing at the real cause.
    var messageId = MessageId.New();
    var strategy = new TestWorkCoordinatorStrategy(
      () => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);

    var transport = new CapturingBatchTransport();
    var worker = _buildWorker(
      transport,
      new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("t", "s")] },
      services);

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _buildJsonEnvelope(messageId, Guid.NewGuid());
    const string malformedEnvelopeType = "MessageEnvelope`1[[]], Whizbang.Core";

    await Assert.That(async () =>
      await transport.CapturedBatchHandler!([new TransportMessage(envelope, malformedEnvelopeType)], CancellationToken.None)
    ).Throws<InvalidOperationException>()
     .Because("an empty bracketed type name must fail parsing loudly instead of silently producing an unmatchable inbox row");

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceBusConsumerWorker _buildWorker(
      ITransport transport,
      ServiceBusConsumerOptions options,
      ServiceCollection services,
      ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null) {
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    return new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: scopeFactory,
      jsonOptions: Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      logger: new TestLogger<ServiceBusConsumerWorker>(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      options: options,
      lifecycleMessageDeserializer: lifecycleMessageDeserializer,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());
  }

  private static MessageEnvelope<JsonElement> _buildJsonEnvelope(MessageId messageId, Guid streamId) {
    var payload = JsonDocument.Parse("{\"Data\":\"coverage-test\"}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "coverage-test-service",
            HostName = "test-host",
            ProcessId = 4242
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonDocument.Parse($"\"{streamId}\"").RootElement
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #region Test Doubles

  /// <summary>
  /// Transport that fails on subscribe to trigger StartAsync error path.
  /// </summary>
  private sealed class FailingTransport : ITransport {
    public bool IsInitialized => false;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated subscription failure");
    }

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
      throw new InvalidOperationException("Simulated subscription failure");
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope envelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull =>
      throw new NotImplementedException();

    public void Dispose() { }
  }

  /// <summary>
  /// Transport that captures the batch handler so tests can deliver messages directly.
  /// </summary>
  private sealed class CapturingBatchTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? CapturedBatchHandler { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) => Task.FromResult<ISubscription>(new _NopSubscription());

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
      CapturedBatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new _NopSubscription());
    }

    public Task PublishAsync(
      IMessageEnvelope envelope,
      TransportDestination destination,
      string? envelopeType = null,
      ReadOnlyMemory<byte>? preSerializedBytes = null,
      CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
      IMessageEnvelope envelope,
      TransportDestination destination,
      CancellationToken cancellationToken = default)
      where TRequest : notnull
      where TResponse : notnull =>
      throw new NotImplementedException();

    public void Dispose() { }

    private sealed class _NopSubscription : ISubscription {
      public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067 // Required by the interface; nothing raises it in this double
      public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
      public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
      public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
      public void Dispose() => IsActive = false;
    }
  }

  /// <summary>
  /// Work coordinator strategy whose FIRST QueueInboxCompletion call throws (simulating a
  /// transient DB failure while queuing one stream's completion); subsequent calls succeed and
  /// are recorded, alongside any failures routed through QueueInboxFailure.
  /// </summary>
  private sealed class CompletionThrowingWorkCoordinatorStrategy(WorkBatch batch) : IWorkCoordinatorStrategy {
    private int _completionCalls;
    public List<(Guid MessageId, MessageProcessingStatus Status)> Completions { get; } = [];
    public List<(Guid MessageId, MessageProcessingStatus Status, string Error)> Failures { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) {
      _completionCalls++;
      if (_completionCalls == 1) {
        throw new InvalidOperationException("Simulated completion-queueing failure");
      }
      Completions.Add((messageId, status));
    }

    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) {
      Failures.Add((messageId, partialStatus, error));
    }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.FromResult(batch);
  }

  /// <summary>
  /// Work coordinator strategy that returns a fixed batch and records every QueueInboxCompletion
  /// call — used to prove the core inbox pipeline still finishes when detached lifecycle stages
  /// hit a gap.
  /// </summary>
  private sealed class RecordingWorkCoordinatorStrategy(WorkBatch batch) : IWorkCoordinatorStrategy {
    public List<(Guid MessageId, MessageProcessingStatus Status)> Completions { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) => Completions.Add((messageId, status));
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => FlushAndGetBatchAsync(flags, ct);
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.FromResult(batch);
  }

  /// <summary>
  /// Minimal IReceptorRegistry double — tests add receptors per (messageType, stage) key.
  /// </summary>
  private sealed class SpyReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, Type messageType, ReceptorInfo receptor) {
      var key = (messageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>
  /// Lifecycle message deserializer that always returns a fresh CoverageWorkerTestEvent.
  /// </summary>
  private sealed class SimpleLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) =>
      new CoverageWorkerTestEvent { Data = "deserialized" };

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) =>
      new CoverageWorkerTestEvent { Data = "deserialized" };

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) =>
      new CoverageWorkerTestEvent { Data = "deserialized" };

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) =>
      new CoverageWorkerTestEvent { Data = "deserialized" };
  }

  /// <summary>
  /// ILogger double that records every log call so tests can assert observability invariants
  /// (e.g. "the failure was logged before it propagated") instead of only "it did not throw".
  /// </summary>
  private sealed class RecordingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
      LogLevel logLevel,
      Microsoft.Extensions.Logging.EventId eventId,
      TState state,
      Exception? exception,
      Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, exception));
    }
  }

  /// <summary>
  /// Bypasses the BackgroundService host machinery (StartAsync/StopAsync) so a test can drive
  /// ExecuteAsync directly with a deliberately poisoned CancellationToken, exercising the idle
  /// wait's non-cancellation catch without racing a real host shutdown.
  /// </summary>
  private sealed class ExecuteAsyncProbeWorker(
      ITransport transport,
      IServiceScopeFactory scopeFactory,
      JsonSerializerOptions jsonOptions,
      ILogger<ServiceBusConsumerWorker> logger,
      OrderedStreamProcessor orderedProcessor,
      ServiceBusConsumerOptions options)
    : ServiceBusConsumerWorker(
        transport: transport,
        scopeFactory: scopeFactory,
        jsonOptions: jsonOptions,
        logger: logger,
        orderedProcessor: orderedProcessor,
        schemaReadyGate: null!,
        options: options) {
    public Task InvokeExecuteAsync(CancellationToken stoppingToken) => ExecuteAsync(stoppingToken);
  }

  #endregion
}

/// <summary>
/// Test event used by ServiceBusConsumerWorkerCoverageTests' lifecycle/receptor coverage tests.
/// </summary>
public record CoverageWorkerTestEvent : IEvent {
  [StreamId]
  public string Data { get; init; } = string.Empty;
}
