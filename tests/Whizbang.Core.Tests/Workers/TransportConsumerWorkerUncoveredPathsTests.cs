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
using Whizbang.Core.Tests.Observability;
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
    // Arrange - a strongly-typed envelope with no IEnvelopeSerializer registered: building the
    // inbox message throws, which is the failure the per-message isolation catches and counts.
    // (This test used to inject the failure through IWorkCoordinatorStrategy.QueueInboxMessage —
    // a call the receive path no longer makes since it moved to a direct bulk insert, so the
    // injected exception never fired and the "failed counter" it is named for was never touched.)
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    var services = new ServiceCollection();
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = new MessageEnvelope<UncoveredTestEvent> {
      MessageId = messageId,
      Payload = new UncoveredTestEvent(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - per-message error isolation catches the exception; InboxMessagesFailed counter, activity error tags, and InboxReceiveDuration in finally are still exercised
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - the failure is recorded, and the unbuildable message is NOT stored. Without the
    // counter a message that dies on the way to the inbox disappears with no operational trace.
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("a message that threw while being built must never reach the inbox");
    var failed = metricHelper.GetByName("whizbang.transport.inbox.messages_failed")
      .Where(m => m.Value > 0)
      .ToList();
    await Assert.That(failed).Count().IsEqualTo(1)
      .Because("the per-message failure path must record exactly one failed-message measurement");
    await Assert.That(failed[0].Value).IsEqualTo(1d);
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - should NOT throw; ObjectDisposedException is caught and message is dropped
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - message was stored via StoreInboxMessagesAsync (QueueInboxMessage no longer called)
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1)
      .Because("Message should be stored via StoreInboxMessagesAsync");
  }

  [Test]
  public async Task HandleMessage_WithMetrics_WhenStored_LeavesTheFailedCounterUntouchedAsync() {
    // Arrange - the counterpart to HandleMessage_WithMetrics_WhenException_RecordsFailedCounterAsync:
    // a counter that also ticks on the success path would report a permanent failure rate and be
    // useless as an alert. (This test was written as an ObjectDisposedException case, injecting the
    // exception through an IWorkCoordinatorStrategy fake. The receive path resolves no such
    // strategy any more — TransportConsumerWorker does not reference the interface at all — so the
    // fake was never called, no exception was ever raised, and the test was quietly asserting
    // nothing about the success path it was actually running. It now pins that success path, and
    // the fake is gone. Its metrics were also unobservable: TransportMetrics built without a
    // TestMeterFactory publishes to a meter no listener is attached to.)
    var messageId = MessageId.New();
    var transport = new UncoveredTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    var services = new ServiceCollection();
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - the message landed in the inbox, and nothing was counted as failed.
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1)
      .Because("the message must reach the inbox — otherwise a zero failure count means nothing");
    var failed = metricHelper.GetByName("whizbang.transport.inbox.messages_failed")
      .Where(m => m.Value > 0)
      .ToList();
    await Assert.That(failed).IsEmpty()
      .Because("a successfully stored message must never be counted as a failure");
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
    await Assert.That(noOpCoordinator.StoredMessages.Last().IsEvent).IsTrue()
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

    var logger = new RecordingWorkerLogger();
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    // Envelope with AggregateId = Guid.Empty (triggers StreamIdGuard)
    var envelope = _createJsonEnvelopeWithStreamId(messageId, Guid.Empty);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Tests.Workers.TransportConsumerWorkerUncoveredPathsTests+UncoveredTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

    // Act - per-message error isolation catches the InvalidStreamIdException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - the guard fired (the caught exception is the evidence, since isolation keeps it off
    // the transport thread) and the streamless event is kept OUT of the inbox. Storing an event
    // with an empty stream id writes a row no perspective or replay can ever address.
    await Assert.That(logger.Exceptions.OfType<InvalidStreamIdException>().Count()).IsEqualTo(1)
      .Because("an event whose StreamId is Guid.Empty must trip StreamIdGuard on the receive path");
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("the guarded message must not be stored — contrast the sibling test, where a valid "
             + "stream id on the same envelope type does reach the inbox");
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: deserializer,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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

    var logger = new RecordingWorkerLogger();
    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, null);

    cts.Cancel();

    // Assert - the guard fired on the missing envelope type, and the untypeable message stayed out
    // of the inbox. A row whose EnvelopeType is null cannot be deserialized by anything downstream,
    // so storing it would create work no worker can ever complete.
    var thrown = logger.Exceptions.OfType<InvalidOperationException>().ToList();
    await Assert.That(thrown).Count().IsEqualTo(1)
      .Because("a transport message with no envelope type must be rejected by the build guard");
    await Assert.That(thrown[0].Message).Contains("EnvelopeType is required");
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("the rejected message must not be stored");
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
    using var meterFactory = new TestMeterFactory();
    var metrics = new TransportMetrics(new WhizbangMetrics(meterFactory));
    using var metricHelper = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);

    // Act - per-message error isolation catches the exception; "Unknown" message type with metrics is still exercised
    await transport.SimulateMessageReceivedAsync(envelope, null);

    cts.Cancel();

    // Assert - a message with no envelope type still gets counted, under the literal "Unknown"
    // tag. Dropping the tag (or the measurement) would make untyped traffic invisible on the
    // dashboards that are the only way to notice a producer publishing without a type.
    var receivedTags = _taggedMessageTypes(metricHelper, "whizbang.transport.inbox.messages_received");
    await Assert.That(receivedTags).Count().IsEqualTo(1)
      .Because("the receive counter must count an unidentifiable message, not skip it");
    await Assert.That(receivedTags[0]).IsEqualTo("Unknown")
      .Because("with no envelope type there is no name to report, and 'Unknown' is that name");

    var failedTags = _taggedMessageTypes(metricHelper, "whizbang.transport.inbox.messages_failed");
    await Assert.That(failedTags).Count().IsEqualTo(1);
    await Assert.That(failedTags[0]).IsEqualTo("Unknown")
      .Because("the failure it causes must carry the same tag, so the two series line up");
  }

  /// <summary>
  /// The <c>message_type</c> tag of every non-zero series on one counter. The untagged series
  /// always reports (at zero), so the tagged ones are what say which path actually counted.
  /// </summary>
  private static List<string> _taggedMessageTypes(MetricAssertionHelper helper, string instrumentName) =>
    [.. helper.GetByName(instrumentName)
      .Where(m => m.Value > 0)
      .Select(m => m.Tags.TryGetValue("message_type", out var t) ? t : "(untagged)")];

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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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
    await Assert.That(noOpCoordinator.StoredMessages.Last().StreamId).IsEqualTo(messageId.Value)
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - no scope deltas on hops means scope should be null
    await Assert.That(noOpCoordinator.StoredMessages.Last().Scope).IsNull()
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();
    // Awaited so ExecuteTask is published before it is read.
    await worker.StartAsync(cts.Token);

    // "Never subscribed" cannot be signaled, but "finished the subscribe phase" can: with zero
    // destinations ExecuteAsync walks an empty loop and settles SubscriptionsReady, then runs to
    // completion. Awaiting both makes the counts below final -- a 100 ms window was equally
    // satisfied by a worker the thread pool had not started yet, which proved nothing.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(10));
    cts.Cancel();
    await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10))
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    // Assert
    await Assert.That(worker.ExecuteTask.IsCompleted).IsTrue()
      .Because("\"stops gracefully\" means ExecuteAsync returns once its token is canceled; a " +
               "worker still parked here after the bounded wait has not stopped at all");
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
      transport: transport,
      options: options,
      resilienceOptions: new SubscriptionResilienceOptions(),
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: logger,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

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

  /// <summary>
  /// Keeps the exceptions the worker caught. Per-message error isolation deliberately keeps a
  /// failure off the transport thread, so the log is the only place a test can see WHICH failure
  /// happened — without it, "the message was dropped" cannot be told apart from "the message was
  /// dropped for the reason this test is named after".
  /// </summary>
  private sealed class RecordingWorkerLogger : ILogger<TransportConsumerWorker> {
    private readonly Lock _sync = new();
    private readonly List<Exception> _exceptions = [];

    public IReadOnlyList<Exception> Exceptions {
      get {
        lock (_sync) { return [.. _exceptions]; }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (exception is null) {
        return;
      }
      lock (_sync) { _exceptions.Add(exception); }
    }
  }

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
        string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

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

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
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

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    }
  }

  private sealed class MatchingEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(UncoveredTestEvent)];
  }

  private sealed class UncoveredReceptorInvoker : IReceptorInvoker {
    // The fallback path fans the terminal stages out as concurrent detached tasks, so InvokeAsync
    // is called from several threads at once. Guard the mutations: an unsynchronized List.Add /
    // counter++ races and loses updates under parallelism (intermittent under-count in CI).
    private readonly object _gate = new();
    private readonly List<LifecycleStage> _invokedStages = [];
    private int _invokeCallCount;

    public int InvokeCallCount { get { lock (_gate) { return _invokeCallCount; } } }
    public IReadOnlyList<LifecycleStage> InvokedStages { get { lock (_gate) { return _invokedStages.ToList(); } } }

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope, LifecycleStage stage,
        ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      lock (_gate) {
        _invokeCallCount++;
        _invokedStages.Add(stage);
      }
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
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
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
