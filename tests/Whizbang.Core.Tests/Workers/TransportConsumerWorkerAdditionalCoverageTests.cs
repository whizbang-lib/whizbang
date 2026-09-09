using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Additional coverage tests for TransportConsumerWorker targeting untested code paths:
/// - Lifecycle receptor invocation (PreInbox/PostInbox stages) with ILifecycleMessageDeserializer + IReceptorInvoker
/// - IEventTypeProvider-based event detection
/// - Error path in _handleMessageAsync with Activity status
/// - Completion and failure handler callbacks in ordered processor
/// - _onConnectionRecoveredAsync disposing existing subscriptions
/// - Health monitor exception handling
/// - _populateDeliveredAtTimestamp with null envelopeType
/// - _serializeToNewInboxMessage with non-JsonElement payloads
/// - AllowPartialSubscriptions=false with sequential failure detection
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerAdditionalCoverageTests {

  // ========================================
  // Lifecycle Receptor Invocation - deserializer without invoker (skipped)
  // ========================================

  [Test]
  public async Task HandleMessage_WithDeserializerButNoInvoker_SkipsLifecycleInvocationAsync() {
    // Arrange - register deserializer but NOT invoker
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var deserializer = new TrackingLifecycleDeserializer();
    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    // No IReceptorInvoker registered
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - deserializer should NOT be called when invoker is missing
    await Assert.That(deserializer.DeserializeCallCount).IsEqualTo(0)
      .Because("Deserializer should not be called when IReceptorInvoker is not registered");
  }

  // ========================================
  // IEventTypeProvider-based event detection
  // ========================================

  [Test]
  public async Task HandleMessage_WithEventTypeProvider_DetectsEventsCorrectlyAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);
    var eventTypeProvider = new TestEventTypeProvider();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddSingleton<IEventTypeProvider>(eventTypeProvider);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Use a message type matching one in our event type provider
    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act - should not throw since TestCommand is not an event (no StreamId guard)
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1);
    await Assert.That(noOpCoordinator.StoredMessages.Last().IsEvent).IsFalse()
      .Because("TestCommand is not in IEventTypeProvider list so should not be detected as event");
  }

  // ========================================
  // Error path in _handleMessageAsync - Activity status set to Error
  // ========================================

  /// <summary>
  /// The receive span for a failed delivery is marked as an error AND hangs off the producer's trace.
  /// <c>TransportConsumerWorkerDeepCoverageTests.HandleMessage_WithException_SetsActivityErrorTagsAsync</c>
  /// covers the error status from the other side of the same seam; the parent-span linkage asserted
  /// here — the reason the traceparent is in this test's name — is not covered there.
  /// </summary>
  [Test]
  public async Task HandleMessage_WhenExceptionWithTraceParent_SetsActivityErrorStatusAsync() {
    // The inbox span only exists while something is listening, so the listener is not just the
    // observer here — it is what makes the SetStatus path run at all.
    var stopped = new List<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Whizbang.Transport",
      Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        lock (stopped) {
          stopped.Add(activity);
        }
      }
    };
    ActivitySource.AddActivityListener(listener);

    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var services = new ServiceCollection();
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Create envelope WITH a valid traceparent so Activity is created
    const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var envelope = _createJsonEnvelopeWithTraceParent(messageId, traceParent);
    // Unparseable envelope type: the failure has to happen INSIDE the span, which is where the
    // error status is recorded. A throwing IWorkCoordinatorStrategy cannot be used for this —
    // TransportConsumerWorker never resolves that interface, so such a fake is never called.
    const string unusableEnvelopeType = "SomeType.Without.Brackets";

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, unusableEnvelopeType);

    cts.Cancel();

    List<Activity> inboxActivities;
    lock (stopped) {
      inboxActivities = [.. stopped.Where(a =>
        (string?)a.GetTagItem("messaging.message_id") == messageId.ToString())];
    }

    await Assert.That(inboxActivities.Count).IsEqualTo(1)
      .Because("the hop's traceparent is what attaches this consumer span to the producer's trace.");
    await Assert.That(inboxActivities[0].ParentSpanId.ToHexString()).IsEqualTo("b7ad6b7169203331")
      .Because("the span continues the incoming trace instead of starting a new root.");
    await Assert.That(inboxActivities[0].Status).IsEqualTo(ActivityStatusCode.Error)
      .Because("a message that could not be turned into an inbox row is a FAILED receive. The batch "
             + "guard contains the fault, so the span is where the drop stays visible — left Unset, "
             + "the trace shows a clean receive for a message that was thrown away.");
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("nothing may be written for a message whose envelope type cannot be read back.");
  }

  // ========================================
  // Completion/Failure handler callbacks in ordered processor
  // ========================================

  // ========================================
  // _onConnectionRecoveredAsync - disposes existing subscriptions
  // ========================================

  [Test]
  public async Task OnRecovery_DisposesExistingSubscriptions_BeforeResubscribingAsync() {
    // Arrange
    var transport = new AdditionalCoverageRecoveringTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      HealthCheckInterval = TimeSpan.FromMinutes(10)
    };

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilienceOptions,
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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Capture initial subscriptions
    var initialSubscriptions = transport.Subscriptions.ToList();
    await Assert.That(initialSubscriptions.Count).IsEqualTo(2);

    // Act - simulate recovery. SimulateRecoveryAsync awaits the worker's recovery handler to
    // completion, and that handler disposes the old subscriptions and re-subscribes before it
    // returns — so the awaited call is itself the signal and no delay is needed.
    await transport.SimulateRecoveryAsync();

    // Assert - initial subscriptions should be disposed
    foreach (var sub in initialSubscriptions) {
      await Assert.That(sub.IsDisposed).IsTrue()
        .Because("Existing subscriptions should be disposed on recovery before re-subscribing");
    }

    // New subscriptions should be created
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(4)
      .Because("2 initial + 2 recovery subscriptions");

    cts.Cancel();
  }

  // ========================================
  // _populateDeliveredAtTimestamp - non-MessageEnvelope<JsonElement> envelope
  // ========================================

  [Test]
  public async Task HandleMessage_WithNonJsonElementEnvelopeType_SkipsTimestampPopulationAsync() {
    // Arrange - use NonJsonEnvelope which is NOT MessageEnvelope<JsonElement>
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var logger = new AdditionalCoverageCapturingLogger();
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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Use a non-MessageEnvelope<JsonElement> envelope
    var envelope = new NonJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act - per-message error isolation catches the exception; _populateDeliveredAtTimestamp is exercised first
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();

    // Assert - the run reached the SERIALIZER step, which is only possible if the timestamp
    // populator returned early: with a non-MessageEnvelope<JsonElement> envelope it must skip
    // rather than try to parse the envelope type and rewrite a payload it cannot address.
    var contained = logger.Exceptions.OfType<InvalidOperationException>().ToList();
    await Assert.That(contained.Count).IsEqualTo(1);
    await Assert.That(contained[0].Message).Contains("IEnvelopeSerializer is required but not registered")
      .Because("failing here and not earlier is what proves _populateDeliveredAtTimestamp skipped.");
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("an envelope that cannot be serialized must never produce an inbox row.");
  }

  // ========================================
  // Debug-level logging paths in _handleMessageAsync
  // ========================================

  [Test]
  public async Task HandleMessage_WithDebugLogging_ExercisesDebugLogPathsAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: false);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    // Use Debug-level logger to exercise debug logging paths
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act - exercises debug logging for "Processing message", "accepted for processing", "queued completion", "successfully processed"
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // May fail during deserialization but debug paths are exercised
    }

    cts.Cancel();

    // Assert
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(1);
  }

  // ========================================
  // ExecuteAsync with Information-level logging disabled
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithNoLogLevel_SkipsInfoLoggingPathsAsync() {
    // Arrange
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic", "key1"));

    // Use a logger that has Information disabled (Critical only)
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Critical));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    await worker.StartAsync(cts.Token);

    // Await the transport's own subscribe signal rather than sleeping. StartAsync returning only
    // proves ExecuteAsync was queued (.NET 10 dispatches it via Task.Run), so a 200 ms delay left
    // this test failing under load whenever the thread pool was slower than the sleep.
    await transport.FirstSubscribe.WaitAsync(TimeSpan.FromSeconds(10));
    await cts.CancelAsync();

    // Assert - subscriptions should still be created even without logging
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1);
  }

  // ========================================
  // AllowPartialSubscriptions=false sequential failure detection
  // ========================================

  [Test]
  public async Task ExecuteAsync_AllowPartialFalse_WithMultipleDestinations_OneFailsFirst_ThrowsAsync() {
    // Arrange - second destination fails, exercising the sequential iteration + failure check
    var transport = new AdditionalCoverageSelectiveFailTransport(failingTopics: ["fail-topic"]);
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("ok-topic"));
    options.Destinations.Add(new TransportDestination("fail-topic"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      AllowPartialSubscriptions = false,
      InitialRetryAttempts = 1,
      RetryIndefinitely = false,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10)
    };

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilienceOptions,
      scopeFactory: scopeFactory,
      jsonOptions: new JsonSerializerOptions(),
      orderedProcessor: new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger: NullLogger<TransportConsumerWorker>.Instance,
      serviceInstanceProvider: new Whizbang.Core.Observability.ServiceInstanceProvider(),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = new CancellationTokenSource();

    // Act & Assert - AllowPartialSubscriptions=false must fail startup rather than degrade. The
    // worker settles SubscriptionsReady with the startup exception, so awaiting that signal is the
    // deterministic outcome to assert on; the old 500 ms delay merely hoped the failing subscribe,
    // its one retry and the throw all landed inside the window, and the 5 s token that used to cap
    // this test could itself cancel the subscribe pass on a loaded machine.
    Exception? caughtException = null;
    try {
      await worker.StartAsync(cts.Token);
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));
    } catch (InvalidOperationException ex) {
      caughtException = ex;
    } finally {
      try { await worker.StopAsync(CancellationToken.None); } catch { }
    }

    await Assert.That(caughtException).IsNotNull()
      .Because("AllowPartialSubscriptions=false should surface a failed destination as a startup failure");

    // At least one subscribe attempt should have been made
    await Assert.That(transport.SubscribeCallCount).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // _extractMessageTypeFromEnvelopeType - brackets at wrong positions
  // ========================================

  [Test]
  public async Task HandleMessage_WithReversedBracketsInEnvelopeType_SkipsMessageWithoutStoringAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var logger = new AdditionalCoverageCapturingLogger();
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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    var envelope = _createJsonEnvelope(messageId);
    const string invalidType = "Type]]BadOrder[[";

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, invalidType);

    cts.Cancel();

    // Assert - closing brackets before opening ones is not a name the parser can repair.
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("an envelope type that yields no message type cannot produce a processable inbox row.");
    var contained = logger.Exceptions.OfType<InvalidOperationException>().ToList();
    await Assert.That(contained.Count).IsEqualTo(1)
      .Because("the drop must be reported; asserting it also proves the handler actually ran.");
    await Assert.That(contained[0].Message).Contains("Invalid envelope type name format");
  }

  [Test]
  public async Task HandleMessage_WithEmptyMessageTypeInBrackets_SkipsMessageWithoutStoringAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    var logger = new AdditionalCoverageCapturingLogger();
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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    var envelope = _createJsonEnvelope(messageId);
    const string emptyTypeEnvelope = "Type[[ ]]";

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, emptyTypeEnvelope);

    cts.Cancel();

    // Assert - well-formed brackets around nothing parse cleanly and still name no type, so the
    // whitespace guard after the parse is the one that has to reject this.
    await Assert.That(noOpCoordinator.StoredInboxCount).IsEqualTo(0)
      .Because("an envelope type that yields no message type cannot produce a processable inbox row.");
    var contained = logger.Exceptions.OfType<InvalidOperationException>().ToList();
    await Assert.That(contained.Count).IsEqualTo(1)
      .Because("the drop must be reported; asserting it also proves the handler actually ran.");
    await Assert.That(contained[0].Message).Contains("Failed to extract message type");
  }

  // ========================================
  // StopAsync before StartAsync (no linked CTS)
  // ========================================

  [Test]
  public async Task StopAsync_BeforeStartAsync_DoesNotThrowAsync() {
    // Arrange
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var worker = _createWorker(new AdditionalCoverageTransport(), options);

    // Act - stop before start (linkedCts is null)
    await worker.StopAsync(CancellationToken.None);

    // Assert
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(0);
  }

  // ========================================
  // _extractStreamId with non-Guid AggregateId metadata
  // ========================================

  [Test]
  public async Task HandleMessage_WithNonGuidAggregateIdMetadata_FallsBackToMessageIdAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Create envelope with non-Guid AggregateId value
    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", "not-a-guid" } });
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var prop in metadataJson.EnumerateObject()) {
      metadata[prop.Name] = prop.Value.Clone();
    }

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = metadata
        }
      ]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - should fall back to MessageId since AggregateId is not a valid GUID
    await Assert.That(noOpCoordinator.StoredMessages.Last().StreamId).IsEqualTo(messageId.Value)
      .Because("Non-Guid AggregateId should fall back to MessageId");
  }

  // ========================================
  // _extractStreamId with non-string AggregateId value kind
  // ========================================

  [Test]
  public async Task HandleMessage_WithNumericAggregateIdMetadata_FallsBackToMessageIdAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Create envelope with numeric AggregateId (ValueKind != String)
    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", 12345 } });
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var prop in metadataJson.EnumerateObject()) {
      metadata[prop.Name] = prop.Value.Clone();
    }

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = metadata
        }
      ]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - should fall back to MessageId since AggregateId is not a string
    await Assert.That(noOpCoordinator.StoredMessages.Last().StreamId).IsEqualTo(messageId.Value)
      .Because("Numeric AggregateId should fall back to MessageId");
  }

  // ========================================
  // ExecuteAsync - log status with healthy/failed counts
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithMixedResults_LogsHealthyAndFailedCountsAsync() {
    // Arrange - one good, one failing topic
    var transport = new AdditionalCoverageSelectiveFailTransport(failingTopics: ["fail-topic"]);
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("ok-topic"));
    options.Destinations.Add(new TransportDestination("fail-topic"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      AllowPartialSubscriptions = true,
      InitialRetryAttempts = 1,
      RetryIndefinitely = false,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      HealthCheckInterval = TimeSpan.FromMinutes(10)
    };

    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    // Use Info-level logging to exercise the healthy/failed count logging path
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var worker = new TransportConsumerWorker(
      transport: transport,
      options: options,
      resilienceOptions: resilienceOptions,
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
    // AllowPartialSubscriptions=true, so the initial pass runs both destinations to completion and
    // records each status before settling SubscriptionsReady. Awaiting that signal — rather than
    // 300 ms — is what makes the healthy/failed split observable; the failing destination needs a
    // subscribe attempt plus a 10 ms retry delay before it is marked Failed.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    // Assert - one should be healthy, one failed
    var states = worker.SubscriptionStates.Values.ToList();
    var healthyCount = states.Count(s => s.Status == SubscriptionStatus.Healthy);
    var failedCount = states.Count(s => s.Status == SubscriptionStatus.Failed);

    await Assert.That(healthyCount).IsEqualTo(1)
      .Because("One destination should succeed");
    await Assert.That(failedCount).IsEqualTo(1)
      .Because("One destination should fail");

    cts.Cancel();
  }

  // ========================================
  // HandleMessage - message type name extraction for handler name
  // ========================================

  [Test]
  public async Task HandleMessage_WithNestedNamespace_ExtractsCorrectHandlerNameAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new AdditionalCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new AdditionalCoverageWorkCoordinatorStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    var noOpCoordinator = new NoOpWorkCoordinator();
    services.AddScoped<IWorkCoordinator>(_ => noOpCoordinator);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    // Wait for the worker's own subscribe-complete signal: StartAsync only queues ExecuteAsync.
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

    var envelope = _createJsonEnvelope(messageId);
    // Use single-segment assembly name to avoid LastIndexOf('.') picking up assembly dot
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[My.Deep.Namespace.CreateOrderCommand, TestAssembly]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - handler name extraction uses LastIndexOf('.') on full message type string
    // "My.Deep.Namespace.CreateOrderCommand, TestAssembly" -> after last dot = "CreateOrderCommand, TestAssembly"
    // -> Split(',')[0] = "CreateOrderCommand" -> + "Handler" = "CreateOrderCommandHandler"
    await Assert.That(noOpCoordinator.StoredMessages.Last().HandlerName).IsEqualTo("CreateOrderCommandHandler")
      .Because("Handler name should be simple type name + Handler suffix");
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static TransportConsumerWorker _createWorker(
      ITransport transport,
      TransportConsumerOptions options) {
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();
    var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

    return new TransportConsumerWorker(
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
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(MessageId messageId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
        }
      ]
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithTraceParent(
      MessageId messageId, string traceParent) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          TraceParent = traceParent
        }
      ]
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed class AdditionalCoverageTransport : ITransport {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    private readonly List<AdditionalCoverageSubscription> _subscriptions = [];
    private readonly TaskCompletionSource _firstSubscribe =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes the moment the worker issues its first subscribe against this transport.
    /// </summary>
    /// <remarks>
    /// A body-emitted signal to await instead of sleeping: .NET 10 dispatches
    /// <c>ExecuteAsync</c> via <c>Task.Run</c>, so a fixed delay is a bet on the thread pool
    /// rather than proof the subscribe path ran.
    /// </remarks>
    public Task FirstSubscribe => _firstSubscribe.Task;

    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public IReadOnlyList<AdditionalCoverageSubscription> Subscriptions => _subscriptions;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _handler = handler;
      var subscription = new AdditionalCoverageSubscription();
      _subscriptions.Add(subscription);
      _firstSubscribe.TrySetResult();
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _batchHandler = batchHandler;
      var subscription = new AdditionalCoverageSubscription();
      _subscriptions.Add(subscription);
      _firstSubscribe.TrySetResult();
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull =>
      throw new NotSupportedException();

    public async Task SimulateMessageReceivedAsync(IMessageEnvelope envelope, string? envelopeType) {
      if (_batchHandler != null) {
        await _batchHandler([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
      } else if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }
  }

  private sealed class AdditionalCoverageSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class AdditionalCoverageRecoveringTransport : ITransport, ITransportWithRecovery {
    private Func<CancellationToken, Task>? _recoveryHandler;
    private readonly List<AdditionalCoverageSubscription> _subscriptions = [];

    public int SubscribeCallCount { get; private set; }
    public bool HasRecoveryHandler => _recoveryHandler != null;
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public IReadOnlyList<AdditionalCoverageSubscription> Subscriptions => _subscriptions;

    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
      _recoveryHandler = onRecovered;
    }

    public async Task SimulateRecoveryAsync() {
      if (_recoveryHandler != null) {
        await _recoveryHandler(CancellationToken.None);
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      var subscription = new AdditionalCoverageSubscription();
      _subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      var subscription = new AdditionalCoverageSubscription();
      _subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class AdditionalCoverageSelectiveFailTransport(IEnumerable<string> failingTopics) : ITransport {
    private readonly HashSet<string> _failingTopics = [.. failingTopics];

    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        ReadOnlyMemory<byte>? preSerializedBytes = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      if (_failingTopics.Contains(destination.Address)) {
        throw new InvalidOperationException($"Subscription to {destination.Address} failed");
      }
      return Task.FromResult<ISubscription>(new AdditionalCoverageSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      if (_failingTopics.Contains(destination.Address)) {
        throw new InvalidOperationException($"Subscription to {destination.Address} failed");
      }
      return Task.FromResult<ISubscription>(new AdditionalCoverageSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class AdditionalCoverageWorkCoordinatorStrategy(Guid expectedMessageId, bool returnEmptyInboxWork = false) : IWorkCoordinatorStrategy {
    private readonly Guid _expectedMessageId = expectedMessageId;
    private readonly bool _returnEmptyInboxWork = returnEmptyInboxWork;

    public int QueuedInboxCount { get; private set; }
    public int FlushCount { get; private set; }
    public int CompletionCount { get; private set; }
    public int FailureCount { get; private set; }
    public Guid? LastQueuedStreamId { get; private set; }
    public string? LastQueuedHandlerName { get; private set; }
    public bool? LastQueuedIsEvent { get; private set; }

    public void QueueInboxMessage(InboxMessage message) {
      QueuedInboxCount++;
      LastQueuedStreamId = message.StreamId;
      LastQueuedHandlerName = message.HandlerName;
      LastQueuedIsEvent = message.IsEvent;
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
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
          Hops = [
            new MessageHop {
              Type = HopType.Current,
              Timestamp = DateTimeOffset.UtcNow,
              ServiceInstance = ServiceInstanceInfo.Unknown,
            }
          ]
        },
        MessageType = "TestApp.TestMessage, TestApp",
        StreamId = _expectedMessageId
      };

      return Task.FromResult(new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>Logger that keeps the exceptions attached to its entries. The worker's per-message
  /// isolation logs the fault and moves on, and the formatted message never carries the exception,
  /// so this is the only place a contained failure is observable.</summary>
  private sealed class AdditionalCoverageCapturingLogger : ILogger<TransportConsumerWorker> {
    private readonly Lock _lock = new();
    private readonly List<Exception> _exceptions = [];

    public IReadOnlyList<Exception> Exceptions {
      get {
        lock (_lock) {
          return [.. _exceptions];
        }
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      if (exception is not null) {
        lock (_lock) {
          _exceptions.Add(exception);
        }
      }
    }
  }

  private sealed class TrackingReceptorInvoker : IReceptorInvoker {
    public int InvokeCallCount { get; private set; }
    public List<LifecycleStage> InvokedStages { get; } = [];

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      InvokeCallCount++;
      InvokedStages.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class TrackingLifecycleDeserializer : ILifecycleMessageDeserializer {
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

  private sealed class TestEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() {
      return [typeof(TestEvent)];
    }
  }

  private sealed class TestEvent : IEvent { }

  private sealed class NonJsonEnvelope : IMessageEnvelope {
    public int Version => 1;
    public MessageDispatchContext DispatchContext { get; } = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox };
    public MessageId MessageId { get; }
    public object Payload => "not-json-element";
    public List<MessageHop> Hops { get; } = [];

    public NonJsonEnvelope(MessageId messageId) {
      MessageId = messageId;
      Hops.Add(new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      });
    }

    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public void AddHop(MessageHop hop) => Hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => DateTimeOffset.UtcNow;
    public JsonElement? GetMetadata(string key) => null;
    public ScopeContext? GetCurrentScope() => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
  }
}
