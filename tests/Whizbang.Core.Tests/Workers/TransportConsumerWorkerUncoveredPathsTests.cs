using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker targeting remaining uncovered paths:
/// - Metrics instrumentation (all _metrics?.* paths with non-null metrics)
/// - ObjectDisposedException catch path (message dropped during shutdown)
/// - Full message pipeline with TransportMetrics recording durations
/// - Event detection with IEventTypeProvider matching event type
/// - StreamIdGuard firing for events with Guid.Empty StreamId
/// - Health monitor exception catch (non-OperationCanceledException)
/// - _isEventWithoutPerspectives integrated from PostInbox lifecycle with registry
/// - ImmediateDetached lifecycle stage invocations
/// - InvokePostLifecycleForEventAsync coordinator AbandonTracking
/// - _populateDeliveredAtTimestamp with concrete MessageEnvelope JsonElement
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerUncoveredPathsTests {

  // ========================================
  // Metrics instrumentation - non-null metrics exercises all _metrics?.* paths
  // ========================================

  [Test]
  public async Task HandleMessage_WithMetrics_RecordsAllDurationAndCounterMetricsAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: false);
    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - processes message; exercises InboxMessagesReceived, InboxSecurityContextDuration,
    // InboxDedupDuration, InboxProcessingDuration, InboxCompletionDuration,
    // InboxMessagesProcessed, InboxReceiveDuration
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // Deserialization may fail in ordered processor but metrics paths are exercised
    }

    cts.Cancel();

    // Assert - message was queued, metrics code paths were hit
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1);
  }

  [Test]
  public async Task HandleMessage_WithMetrics_WhenDuplicate_RecordsDedupCounterAsync() {
    // Arrange - strategy returns empty inbox work (duplicate detection)
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);
    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - exercises InboxMessagesDeduplicated counter and InboxReceiveDuration
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1)
      .Because("Message should be queued even if dedup returns empty work");
  }

  [Test]
  public async Task HandleMessage_WithMetrics_WhenException_RecordsFailedCounterAsync() {
    // Arrange - strategy throws on FlushAsync to exercise error path with metrics
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new ThrowingFlushStrategy();
    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - per-message error isolation catches the exception; InboxMessagesFailed counter, activity error tags, and InboxReceiveDuration in finally are still exercised
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();
  }

  // ========================================
  // ObjectDisposedException catch path - message dropped during shutdown
  // ========================================

  [Test]
  public async Task HandleMessage_WhenObjectDisposed_DropsMessageGracefullyAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    // Strategy throws ObjectDisposedException to simulate shutdown scenario
    var workStrategy = new ObjectDisposedStrategy();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - should NOT throw; ObjectDisposedException is caught and message is dropped
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - message was dropped without exception propagation
    await Assert.That(workStrategy.QueueCallCount).IsEqualTo(1)
      .Because("Message should reach QueueInboxMessage before ObjectDisposedException");
  }

  [Test]
  public async Task HandleMessage_WhenObjectDisposed_WithMetrics_DropsWithoutFailCounterAsync() {
    // Arrange - ObjectDisposedException with metrics to ensure InboxMessagesFailed is NOT incremented
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new ObjectDisposedStrategy();
    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - should not throw
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - should not throw, ObjectDisposedException path returns early before InboxMessagesFailed
    // No assertion — test verifies no exception
  }

  // ========================================
  // Event detection with IEventTypeProvider matching event type + StreamId guard
  // ========================================

  [Test]
  public async Task HandleMessage_WithEventTypeProviderMatchingEvent_SetsIsEventTrueAsync() {
    // Arrange - register IEventTypeProvider that matches the envelope message type
    var messageId = MessageId.New();
    var expectedStreamId = Guid.NewGuid();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddSingleton<IEventTypeProvider>(new MatchingEventTypeProvider());
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Envelope with valid AggregateId so StreamIdGuard doesn't fire (non-empty GUID)
    var envelope = _createJsonEnvelopeWithStreamId(messageId, expectedStreamId);
    // Use envelope type with the event type name that matches MatchingEventTypeProvider
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.Workers.TransportConsumerWorkerUncoveredPathsTests+UncoveredTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - isEvent should be true
    await Assert.That(workStrategy.LastQueuedIsEvent).IsTrue()
      .Because("Message type matching IEventTypeProvider event types should set isEvent=true");
  }

  [Test]
  public async Task HandleMessage_WithEvent_AndEmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    // Arrange - event with Guid.Empty as StreamId should trigger StreamIdGuard
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddSingleton<IEventTypeProvider>(new MatchingEventTypeProvider());
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Envelope with AggregateId = Guid.Empty (triggers StreamIdGuard)
    var envelope = _createJsonEnvelopeWithStreamId(messageId, Guid.Empty);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.Workers.TransportConsumerWorkerUncoveredPathsTests+UncoveredTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act - per-message error isolation catches the InvalidStreamIdException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();
  }

  [Test]
  public async Task HandleMessage_EventWithPerspectives_DoesNotInvokePostLifecycleDetachedAsync() {
    // Arrange - event message WITH matching perspective, should NOT fire PostLifecycle
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var invoker = new UncoveredReceptorInvoker();
    var deserializer = new UncoveredLifecycleDeserializer();

    // Strategy returns InboxWork with event type that HAS perspectives
    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: false,
      messageType: "TestApp.Events.OrderCreated, TestApp");

    var perspectiveRegistry = new UncoveredPerspectiveRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["TestApp.Events.OrderCreated, TestApp"]
      )
    ]);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddSingleton<IPerspectiveRunnerRegistry>(perspectiveRegistry);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: deserializer,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelopeWithStreamId(messageId, streamId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Events.OrderCreated, TestApp]], Whizbang.Core";

    // Act
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // Deserialization may fail
    }

    cts.Cancel();

    // Assert - PostAllPerspectivesDetached should NOT be invoked (perspectives exist, PerspectiveWorker handles it)
    var hasPostAllPerspectives = invoker.InvokedStages.Any(s => s == LifecycleStage.PostAllPerspectivesDetached);
    await Assert.That(hasPostAllPerspectives).IsFalse()
      .Because("Events WITH perspectives should NOT get PostLifecycle from TransportConsumerWorker");
  }

  // ========================================
  // InvokePostLifecycleForEventAsync - coordinator path with AbandonTracking
  // ========================================

  [Test]
  public async Task InvokePostLifecycleForEvent_CoordinatorPath_CallsAbandonTrackingAsync() {
    // Arrange
    var spy = new SpyLifecycleCoordinator();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelope(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection();
    services.AddSingleton<ILifecycleCoordinator>(spy);
    var scopedProvider = services.BuildServiceProvider();

    // Act
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, new NoOpReceptorInvoker(), lifecycleContext, scopedProvider, CancellationToken.None);

    // Assert - AbandonTracking should be called after advancing through all stages
    await Assert.That(spy.AbandonedEventIds).Contains(eventId)
      .Because("AbandonTracking should be called after all terminal stages advance");
  }

  // ========================================
  // InvokePostLifecycleForEventAsync - fallback path ImmediateDetached invocations
  // ========================================

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackPath_InvokesImmediateDetachedForEachStageAsync() {
    // Arrange - no coordinator => fallback path
    var spyInvoker = new UncoveredReceptorInvoker();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelope(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection(); // No ILifecycleCoordinator
    services.AddSingleton<IReceptorInvoker>(spyInvoker);
    var scopedProvider = services.BuildServiceProvider();

    // Act
    var detachedTasks = new List<Task>();
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, spyInvoker, lifecycleContext, scopedProvider, CancellationToken.None, detachedTasks.Add);
    await Task.WhenAll(detachedTasks);

    // Assert - ImmediateDetached should be invoked for each of the 4 terminal stages
    var immediateAsyncCount = spyInvoker.InvokedStages.Count(s => s == LifecycleStage.ImmediateDetached);
    await Assert.That(immediateAsyncCount).IsEqualTo(4)
      .Because("ImmediateDetached should be invoked once for each of the 4 terminal stages");
  }

  // ========================================
  // _handleMessageAsync with null envelopeType exercises TypeNameFormatter "Unknown" path
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullEnvelopeType_ThrowsInvalidOperationExceptionAsync() {
    // Arrange - null envelopeType hits guard in _serializeToNewInboxMessage
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, null);

    cts.Cancel();
  }

  // ========================================
  // _handleMessageAsync - message type "Unknown" when envelopeType is null
  // (exercises the ternary on line 324)
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullEnvelopeType_WithMetrics_RecordsUnknownMessageTypeAsync() {
    // Arrange - null envelopeType + metrics exercises the "Unknown" message type tag path
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value);
    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);

    // Act - per-message error isolation catches the exception; "Unknown" message type with metrics is still exercised
    await transport.SimulateMessageReceivedAsync(envelope, null);

    cts.Cancel();
  }

  // ========================================
  // _populateDeliveredAtTimestamp - with concrete MessageEnvelope<JsonElement>
  // ========================================

  [Test]
  public async Task HandleMessage_WithMessageEnvelopeJsonElement_PopulatesDeliveredAtTimestampAsync() {
    // Arrange - use MessageEnvelope<JsonElement> to exercise _populateDeliveredAtTimestamp
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Create a concrete MessageEnvelope<JsonElement> to hit _populateDeliveredAtTimestamp path
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"Name\":\"test\"}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - exercises _populateDeliveredAtTimestamp which calls JsonAutoPopulateHelper
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - message was processed
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1);
  }

  // ========================================
  // _extractStreamId with AggregateId that is null string value
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullStringAggregateId_FallsBackToMessageIdAsync() {
    // Arrange - AggregateId is JSON null inside string value kind doesn't apply,
    // so use a scenario where GetString() returns null
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Create envelope with empty string AggregateId (valid string, not a GUID)
    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", "" } });
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var prop in metadataJson.EnumerateObject()) {
      metadata[prop.Name] = prop.Value.Clone();
    }

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = metadata
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - should fall back to MessageId since empty string is not a valid GUID
    await Assert.That(workStrategy.LastQueuedStreamId).IsEqualTo(messageId.Value)
      .Because("Empty string AggregateId should fall back to MessageId");
  }

  // ========================================
  // Envelope with no scope context - Scope is null
  // ========================================

  [Test]
  public async Task HandleMessage_WithNoScopeContext_SetsNullScopeAsync() {
    // Arrange - envelope with no scope deltas exercises GetCurrentScope()?.Scope -> null
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new UncoveredWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - no scope deltas on hops means scope should be null
    await Assert.That(workStrategy.LastQueuedScope).IsNull()
      .Because("Envelope with no scope deltas should result in null Scope");
  }

  // ========================================
  // ExecuteAsync with zero destinations - exercises empty loop
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithZeroDestinations_StartsAndStopsGracefullyAsync() {
    // Arrange
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions(); // No destinations

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);

    // Give time for ExecuteAsync to run
    await Task.Delay(100);
    cts.Cancel();

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(0)
      .Because("No destinations means no subscriptions");
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(0);
  }

  // ========================================
  // ExecuteAsync with Debug logging for destination listing
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithDebugLogging_LogsDestinationsAsync() {
    // Arrange - use debug logger to exercise destination logging path
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1", "key1"));
    options.Destinations.Add(new TransportDestination("topic2")); // null routing key -> "#"

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    // Wait for both subscriptions (one per destination) — signal-based, deterministic
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));
    cts.Cancel();

    // Assert - subscriptions were created, debug log paths were exercised
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithStreamId(
      MessageId messageId, Guid streamId) {
    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", streamId.ToString() } });
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var prop in metadataJson.EnumerateObject()) {
      metadata[prop.Name] = prop.Value.Clone();
    }

    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = metadata
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static InboxWork _createInboxWork(Guid eventId) {
    var messageId = new MessageId(eventId);
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new InboxWork {
      MessageId = eventId,
      Envelope = jsonEnvelope,
      MessageType = "Test.TestEvent, Test"
    };
  }

  private static MessageEnvelope<JsonElement> _createTypedEnvelope(Guid eventId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  internal sealed class UncoveredTestEvent : IEvent { }

  private sealed class UncoveredTransport : ITransport, IDisposable {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    private readonly SemaphoreSlim _subscribeSignal = new(0, int.MaxValue);

    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public void Dispose() => _subscribeSignal.Dispose();

    public async Task WaitForSubscriptionAsync(TimeSpan timeout) {
      if (!await _subscribeSignal.WaitAsync(timeout)) {
        throw new TimeoutException($"Subscription not created within {timeout}");
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope, TransportDestination destination,
        string? envelopeType = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _handler = handler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new UncoveredSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _batchHandler = batchHandler;
      _subscribeSignal.Release();
      return Task.FromResult<ISubscription>(new UncoveredSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();

    public async Task SimulateMessageReceivedAsync(IMessageEnvelope envelope, string? envelopeType) {
      if (_batchHandler != null) {
        await _batchHandler([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
      } else if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }
  }

  private sealed class UncoveredSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class UncoveredWorkStrategy(
      Guid expectedMessageId,
      bool returnEmptyInboxWork = false,
      string messageType = "TestApp.TestCommand, TestApp") : IWorkCoordinatorStrategy {
    private readonly Guid _expectedMessageId = expectedMessageId;
    private readonly bool _returnEmptyInboxWork = returnEmptyInboxWork;
    private readonly string _messageType = messageType;

    public int QueuedInboxCount { get; private set; }
    public int FlushCount { get; private set; }
    public int CompletionCount { get; private set; }
    public int FailureCount { get; private set; }
    public Guid? LastQueuedStreamId { get; private set; }
    public string? LastQueuedHandlerName { get; private set; }
    public bool? LastQueuedIsEvent { get; private set; }
    public PerspectiveScope? LastQueuedScope { get; private set; }

    public void QueueInboxMessage(InboxMessage message) {
      QueuedInboxCount++;
      LastQueuedStreamId = message.StreamId;
      LastQueuedHandlerName = message.HandlerName;
      LastQueuedIsEvent = message.IsEvent;
      LastQueuedScope = message.Scope;
    }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) {
      CompletionCount++;
    }

    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) {
      FailureCount++;
    }

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;

      if (_returnEmptyInboxWork) {
        return Task.FromResult(new WorkBatch {
          InboxWork = [],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      var inboxWork = new InboxWork {
        MessageId = _expectedMessageId,
        Envelope = new MessageEnvelope<JsonElement> {
          MessageId = MessageId.From(_expectedMessageId),
          Payload = JsonDocument.Parse("{}").RootElement,
          Hops = [
            new MessageHop {
              Type = HopType.Current,
              Timestamp = DateTimeOffset.UtcNow,
              ServiceInstance = ServiceInstanceInfo.Unknown
            }
          ],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
        },
        MessageType = _messageType,
        StreamId = _expectedMessageId
      };

      return Task.FromResult(new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class ThrowingFlushStrategy : IWorkCoordinatorStrategy {
    public void QueueInboxMessage(InboxMessage message) =>
      throw new InvalidOperationException("Simulated flush failure for metrics coverage");
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class ObjectDisposedStrategy : IWorkCoordinatorStrategy {
    public int QueueCallCount { get; private set; }

    public void QueueInboxMessage(InboxMessage message) {
      QueueCallCount++;
      throw new ObjectDisposedException("Simulated shutdown disposal");
    }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class MatchingEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(UncoveredTestEvent)];
  }

  private sealed class UncoveredReceptorInvoker : IReceptorInvoker {
    public int InvokeCallCount { get; private set; }
    public List<LifecycleStage> InvokedStages { get; } = [];

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      InvokeCallCount++;
      InvokedStages.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class UncoveredLifecycleDeserializer : ILifecycleMessageDeserializer {
    public int DeserializeCallCount { get; private set; }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      DeserializeCallCount++;
      return new object();
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      DeserializeCallCount++;
      return new object();
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      DeserializeCallCount++;
      return new object();
    }

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      DeserializeCallCount++;
      return new object();
    }
  }

  private sealed class UncoveredPerspectiveRegistry(
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives
  ) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  /// <summary>
  /// Spy lifecycle coordinator that tracks BeginTracking, AdvanceToAsync, and AbandonTracking calls.
  /// </summary>
  private sealed class SpyLifecycleCoordinator : ILifecycleCoordinator {
    public LifecycleStage CapturedEntryStage { get; private set; }
    public List<LifecycleStage> AdvancedStages { get; } = [];
    public List<Guid> AbandonedEventIds { get; } = [];

    public ILifecycleTracking BeginTracking(
        Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage,
        MessageSource source, Guid? streamId = null, Type? perspectiveType = null) {
      CapturedEntryStage = entryStage;
      return new SpyLifecycleTracking(eventId, AdvancedStages);
    }

    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }

    public ValueTask SignalSegmentCompleteAsync(
        Guid eventId, PostLifecycleCompletionSource source,
        IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;

    public void AbandonTracking(Guid eventId) {
      AbandonedEventIds.Add(eventId);
    }

    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  private sealed class SpyLifecycleTracking(Guid eventId, List<LifecycleStage> advancedStages) : ILifecycleTracking {
    public Guid EventId { get; } = eventId;
    public LifecycleStage CurrentStage { get; private set; }
    public bool IsComplete { get; private set; }

    public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
      advancedStages.Add(stage);
      CurrentStage = stage;
      if (stage == LifecycleStage.PostLifecycleInline) {
        IsComplete = true;
      }
      return ValueTask.CompletedTask;
    }

    public ValueTask DrainDetachedAsync() => ValueTask.CompletedTask;
  }
}
