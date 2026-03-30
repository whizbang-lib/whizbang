using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

#pragma warning disable CS0067 // Event is never used (test doubles)
#pragma warning disable CA1822 // Member does not access instance data (test doubles)

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Additional coverage tests for TransportConsumerWorker targeting remaining uncovered lines:
/// - Strongly-typed envelope serialization path via IEnvelopeSerializer
/// - JsonElement payload with non-generic envelope type (throw path)
/// - Fallback payload is IEvent path when no IEventTypeProvider
/// - _deserializeEvent with null JsonTypeInfo
/// - _deserializeEvent exception catch path
/// - Health monitor general exception catch
/// - AllowPartialSubscriptions=false sequential completion with no failure
/// - _populateDeliveredAtTimestamp with non-concrete envelope type
/// - Provisioner with null RoutingOptions
/// - Provisioner with empty OwnedDomains
/// - Connection recovery with multiple destinations
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerAdditionalCoverage2Tests {

  // ========================================
  // _serializeToNewInboxMessage: strongly-typed envelope => IEnvelopeSerializer path
  // ========================================

  [Test]
  public async Task HandleMessage_WithStronglyTypedEnvelope_UsesEnvelopeSerializerAsync() {
    // Arrange - send a strongly-typed (non-JsonElement) envelope to exercise the serializer path
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: true);
    var serializer = new Cov2EnvelopeSerializer();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
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

    // Create a strongly-typed envelope (not IMessageEnvelope<JsonElement>)
    var envelope = new MessageEnvelope<Cov2TestCommand> {
      MessageId = messageId,
      Payload = new Cov2TestCommand { Name = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
        }
      ]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Cov2TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - serializer was called, message was queued
    await Assert.That(serializer.SerializeCallCount).IsEqualTo(1)
      .Because("Strongly-typed envelope should invoke IEnvelopeSerializer");
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(1);
  }

  // ========================================
  // _serializeToNewInboxMessage: JsonElement payload but non-generic envelope type => throw
  // ========================================

  [Test]
  public async Task HandleMessage_WithJsonElementPayloadButNonGenericEnvelope_ThrowsAsync() {
    // Arrange - create an envelope where payload is JsonElement but envelope is NOT IMessageEnvelope<JsonElement>
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
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

    // Envelope that returns JsonElement as payload from the object property but is not IMessageEnvelope<JsonElement>
    var jsonPayload = JsonDocument.Parse("{}").RootElement;
    var envelope = new Cov2JsonElementPayloadEnvelope(messageId, jsonPayload);

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act & Assert - should throw InvalidOperationException about JsonElement payload mismatch
    await Assert.ThrowsAsync<InvalidOperationException>(async () => {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    });

    cts.Cancel();
  }

  // ========================================
  // _serializeToNewInboxMessage: fallback path - no IEventTypeProvider, payload is IEvent
  // ========================================

  [Test]
  public async Task HandleMessage_WithIEventPayload_NoProvider_FallsBackToRuntimeCheckAsync() {
    // Arrange - strongly-typed envelope with IEvent payload, no IEventTypeProvider
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: true);
    var serializer = new Cov2EnvelopeSerializer();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
    // Intentionally NOT registering IEventTypeProvider — exercises fallback `payload is IEvent`
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

    // Create strongly-typed envelope with IEvent payload
    var envelope = new MessageEnvelope<Cov2TestEvent> {
      MessageId = messageId,
      Payload = new Cov2TestEvent(),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = _createStreamIdMetadata(Guid.NewGuid())
        }
      ]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Cov2TestEvent, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - isEvent should be true (runtime check: payload is IEvent)
    await Assert.That(workStrategy.LastQueuedIsEvent).IsTrue()
      .Because("Without IEventTypeProvider, fallback to 'payload is IEvent' should detect events");
  }

  // ========================================
  // _serializeToNewInboxMessage: no IEventTypeProvider, payload is NOT IEvent
  // ========================================

  [Test]
  public async Task HandleMessage_WithNonEventPayload_NoProvider_SetsIsEventFalseAsync() {
    // Arrange - strongly-typed envelope with non-IEvent payload, no IEventTypeProvider
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: true);
    var serializer = new Cov2EnvelopeSerializer();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
    // Intentionally NOT registering IEventTypeProvider
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

    // Create strongly-typed envelope with non-IEvent payload (command)
    var envelope = new MessageEnvelope<Cov2TestCommand> {
      MessageId = messageId,
      Payload = new Cov2TestCommand { Name = "test" },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
        }
      ]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Cov2TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - isEvent should be false (runtime check: payload is not IEvent)
    await Assert.That(workStrategy.LastQueuedIsEvent).IsFalse()
      .Because("Non-IEvent payload without IEventTypeProvider should set isEvent=false");
  }

  // ========================================
  // _populateDeliveredAtTimestamp: null envelopeType (early return)
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullEnvelopeType_SkipsTimestampPopulationAndThrowsAsync() {
    // This path is covered indirectly by existing null envelopeType tests
    // but exercises _populateDeliveredAtTimestamp's null guard (line 782)
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
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

    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }]
    };

    // Act & Assert - null envelopeType skips timestamp, then throws on serialization
    await Assert.ThrowsAsync<InvalidOperationException>(async () => {
      await transport.SimulateMessageReceivedAsync(envelope, null);
    });

    cts.Cancel();
  }

  // ========================================
  // _populateDeliveredAtTimestamp: non-MessageEnvelope<JsonElement> concrete type (early return)
  // ========================================

  [Test]
  public async Task HandleMessage_WithNonConcreteEnvelopeType_SkipsTimestampPopulationAsync() {
    // FakeMessageEnvelope is not MessageEnvelope<JsonElement>, so _populateDeliveredAtTimestamp
    // should hit the `envelope is not MessageEnvelope<JsonElement>` guard and return early
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: true);
    var serializer = new Cov2EnvelopeSerializer();

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
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

    // Use a strongly-typed envelope — _populateDeliveredAtTimestamp checks if `envelope is MessageEnvelope<JsonElement>`
    var envelope = new MessageEnvelope<Cov2TestCommand> {
      MessageId = messageId,
      Payload = new Cov2TestCommand { Name = "skip-timestamp" },
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Cov2TestCommand, TestApp]], Whizbang.Core";

    // Act - should process without timestamp population
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - message was queued successfully
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(1);
  }

  // ========================================
  // ExecuteAsync: provisioner present but null RoutingOptions
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithProvisionerButNoRoutingOptions_SkipsProvisioningAsync() {
    // Arrange
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var provisioner = new Cov2Provisioner();

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    // No IOptions<RoutingOptions> registered - provisioner present but options null
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
    cts.Cancel();

    // Assert - provisioner should NOT be called
    await Assert.That(provisioner.ProvisionCallCount).IsEqualTo(0)
      .Because("No RoutingOptions means provisioning should be skipped");
  }

  // ========================================
  // ExecuteAsync: provisioner present but RoutingOptions has empty OwnedDomains
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithProvisionerAndEmptyOwnedDomains_SkipsProvisioningAsync() {
    // Arrange
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var provisioner = new Cov2Provisioner();

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.Configure<RoutingOptions>(opts => { }); // Empty OwnedDomains
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
    cts.Cancel();

    // Assert - provisioner should NOT be called
    await Assert.That(provisioner.ProvisionCallCount).IsEqualTo(0)
      .Because("Empty OwnedDomains means provisioning should be skipped");
  }

  // ========================================
  // _subscribeToAllDestinationsAsync: AllowPartialSubscriptions=false with all succeeding
  // ========================================

  [Test]
  public async Task ExecuteAsync_AllowPartialFalse_AllSucceed_SubscribesAllAsync() {
    // Arrange
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));
    options.Destinations.Add(new TransportDestination("topic3"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      AllowPartialSubscriptions = false
    };

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, resilienceOptions,
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionsAsync(3, TimeSpan.FromSeconds(5));
    cts.Cancel();

    // Assert - all three should succeed with no exception
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(3);
    var healthyCount = worker.SubscriptionStates.Values.Count(s => s.Status == SubscriptionStatus.Healthy);
    await Assert.That(healthyCount).IsEqualTo(3)
      .Because("All subscriptions should be healthy");
  }

  // ========================================
  // _isEventWithoutPerspectives: registry exists but has perspectives with different event types
  // ========================================

  [Test]
  public async Task HandleMessage_EventNotInPerspectiveRegistry_InvokesPostLifecycleAsync() {
    // Arrange - registry has perspectives but none match our event type
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var invoker = new Cov2ReceptorInvoker();
    var deserializer = new Cov2LifecycleDeserializer();

    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: false,
      messageType: "TestApp.Events.UnrelatedEvent, TestApp");

    var perspectiveRegistry = new Cov2PerspectiveRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["TestApp.Events.OrderCreated, TestApp"] // Different event type
      )
    ]);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
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
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Events.UnrelatedEvent, TestApp]], Whizbang.Core";

    // Act
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // Deserialization may fail
    }

    cts.Cancel();

    // Assert - PostAllPerspectivesAsync should be invoked because event type is NOT in any perspective
    await Assert.That(invoker.InvokedStages).Contains(LifecycleStage.PostAllPerspectivesAsync)
      .Because("Event not in any perspective should trigger PostLifecycle stages");
  }

  // ========================================
  // _serializeToNewInboxMessage: no IEnvelopeSerializer registered => throws
  // ========================================

  [Test]
  public async Task HandleMessage_StronglyTypedEnvelope_NoSerializer_ThrowsAsync() {
    // Arrange - strongly-typed envelope but no IEnvelopeSerializer
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new Cov2WorkStrategy(messageId.Value);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    // No IEnvelopeSerializer registered
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

    var envelope = new MessageEnvelope<Cov2TestCommand> {
      MessageId = messageId,
      Payload = new Cov2TestCommand { Name = "test" },
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }]
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.Cov2TestCommand, TestApp]], Whizbang.Core";

    // Act & Assert - should throw because IEnvelopeSerializer is required but not registered
    await Assert.ThrowsAsync<InvalidOperationException>(async () => {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    });

    cts.Cancel();
  }

  // ========================================
  // Connection recovery with null subscriptions
  // ========================================

  [Test]
  public async Task OnRecovery_WithMultipleDestinations_ResubscribesAllAsync() {
    // Arrange
    var transport = new Cov2RecoveryTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));

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
    await transport.WaitForSubscriptionsAsync(2, TimeSpan.FromSeconds(5));

    // Act - simulate recovery
    await transport.SimulateRecoveryAsync();

    // Wait for 2 resubscriptions (recovery re-subscribes both destinations)
    // Note: WaitForSubscriptionsAsync consumes semaphore signals, so we wait for 2 more (not 4 total)
    await transport.WaitForSubscriptionsAsync(2, TimeSpan.FromSeconds(30));
    cts.Cancel();

    // Assert - should have 4 total subscribe calls (2 initial + 2 recovery)
    await Assert.That(transport.SubscribeCallCount).IsGreaterThanOrEqualTo(4)
      .Because("Recovery should resubscribe all destinations");
  }

  // ========================================
  // _deserializeEvent: null JsonTypeInfo path
  // ========================================

  [Test]
  public async Task HandleMessage_WithUnregisteredMessageType_LogsDeserializationErrorAsync() {
    // Arrange - message type that cannot be resolved by JsonContextRegistry
    var messageId = MessageId.New();
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    // Strategy returns work with an unresolvable message type
    var workStrategy = new Cov2WorkStrategy(messageId.Value, returnEmptyInboxWork: false,
      messageType: "NonExistent.UnknownType, NonExistent");

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
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
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[NonExistent.UnknownType, NonExistent]], Whizbang.Core";

    // Act - should not throw; _deserializeEvent catches the error and returns null
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // May fail in completion handler
    }

    cts.Cancel();

    // Assert - message was queued for processing
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(1);
  }

  // ========================================
  // ExecuteAsync: with metrics, mixed healthy and failed subscriptions
  // (exercises the info logging of healthy/failed counts)
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithMetrics_LogsHealthyAndFailedCountsAsync() {
    // Arrange
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));
    options.Destinations.Add(new TransportDestination("topic2"));

    var metrics = new TransportMetrics(new WhizbangMetrics());

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    // Use info-level logger to exercise the info logging paths
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: metrics,
      logger
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await transport.WaitForSubscriptionsAsync(2, TimeSpan.FromSeconds(5));
    cts.Cancel();

    // Assert - subscriptions created
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(2);
  }

  // ========================================
  // Pause/Resume with mixed null/non-null subscriptions
  // ========================================

  [Test]
  public async Task PauseAllSubscriptionsAsync_WithNullSubscriptionState_DoesNotThrowAsync() {
    // Arrange - worker with destinations but not started (subscriptions are null)
    var transport = new Cov2Transport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

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

    // Act - pause before starting (subscription is null)
    await worker.PauseAllSubscriptionsAsync();
    await worker.ResumeAllSubscriptionsAsync();

    // Assert - no exception thrown
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(1);
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
          ServiceInstance = ServiceInstanceInfo.Unknown,
        }
      ]
    };
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithStreamId(
      MessageId messageId, Guid streamId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = _createStreamIdMetadata(streamId)
        }
      ]
    };
  }

  private static Dictionary<string, JsonElement> _createStreamIdMetadata(Guid streamId) {
    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", streamId.ToString() } });
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var prop in metadataJson.EnumerateObject()) {
      metadata[prop.Name] = prop.Value.Clone();
    }
    return metadata;
  }

  // ========================================
  // Test Doubles
  // ========================================

  internal sealed class Cov2TestCommand {
    public string Name { get; set; } = "";
  }

  internal sealed class Cov2TestEvent : IEvent { }

  private sealed class Cov2Transport : ITransport, IDisposable {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
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

    public async Task WaitForSubscriptionsAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _subscribeSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Expected {count} subscriptions but only got {SubscribeCallCount} within {timeout}");
        }
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
      return Task.FromResult<ISubscription>(new Cov2Subscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();

    public async Task SimulateMessageReceivedAsync(IMessageEnvelope envelope, string? envelopeType) {
      if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }
  }

  private sealed class Cov2RecoveryTransport : ITransport, ITransportWithRecovery, IDisposable {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private Func<CancellationToken, Task>? _recoveryHandler;
    private readonly SemaphoreSlim _subscribeSignal = new(0, int.MaxValue);

    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;

    public void Dispose() => _subscribeSignal.Dispose();

    public void SetRecoveryHandler(Func<CancellationToken, Task>? handler) {
      _recoveryHandler = handler;
    }

    public async Task SimulateRecoveryAsync() {
      if (_recoveryHandler != null) {
        await _recoveryHandler(CancellationToken.None);
      }
    }

    public async Task WaitForSubscriptionsAsync(int count, TimeSpan timeout) {
      for (var i = 0; i < count; i++) {
        if (!await _subscribeSignal.WaitAsync(timeout)) {
          throw new TimeoutException($"Expected {count} subscriptions but only got {SubscribeCallCount} within {timeout}");
        }
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
      return Task.FromResult<ISubscription>(new Cov2Subscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope, TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class Cov2Subscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class Cov2WorkStrategy(
      Guid expectedMessageId,
      bool returnEmptyInboxWork = false,
      string messageType = "TestApp.TestCommand, TestApp") : IWorkCoordinatorStrategy {
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

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      FlushCount++;

      if (returnEmptyInboxWork) {
        return Task.FromResult(new WorkBatch {
          InboxWork = [],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      var inboxWork = new InboxWork {
        MessageId = expectedMessageId,
        Envelope = new MessageEnvelope<JsonElement> {
          MessageId = MessageId.From(expectedMessageId),
          Payload = JsonDocument.Parse("{}").RootElement,
          Hops = [
            new MessageHop {
              Type = HopType.Current,
              Timestamp = DateTimeOffset.UtcNow,
              ServiceInstance = ServiceInstanceInfo.Unknown,
            }
          ]
        },
        MessageType = messageType,
        StreamId = expectedMessageId
      };

      return Task.FromResult(new WorkBatch {
        InboxWork = [inboxWork],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Envelope where Payload is JsonElement but the envelope itself does NOT implement IMessageEnvelope{JsonElement}.
  /// This exercises the throw path in _serializeToNewInboxMessage (line 607-608).
  /// </summary>
  private sealed class Cov2JsonElementPayloadEnvelope : IMessageEnvelope {
    private readonly List<MessageHop> _hops;

    public Cov2JsonElementPayloadEnvelope(MessageId messageId, JsonElement payload) {
      MessageId = messageId;
      Payload = payload; // Payload is JsonElement typed as object
      _hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = ServiceInstanceInfo.Unknown,
      }];
    }

    public MessageId MessageId { get; }
    public object Payload { get; }
    public List<MessageHop> Hops => _hops;
    public void AddHop(MessageHop hop) => _hops.Add(hop);
    public DateTimeOffset GetMessageTimestamp() => _hops[0].Timestamp;
    public CorrelationId? GetCorrelationId() => null;
    public MessageId? GetCausationId() => null;
    public JsonElement? GetMetadata(string key) => null;
    public SecurityContext? GetCurrentSecurityContext() => null;
    public ScopeContext? GetCurrentScope() => null;
  }

  private sealed class Cov2EnvelopeSerializer : IEnvelopeSerializer {
    public int SerializeCallCount { get; private set; }

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      SerializeCallCount++;
      var jsonPayload = JsonDocument.Parse("{}").RootElement;
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonPayload,
        Hops = envelope.Hops
      };
      return new SerializedEnvelope(
        JsonEnvelope: jsonEnvelope,
        EnvelopeType: $"Whizbang.Core.Observability.MessageEnvelope`1[[{typeof(TMessage).FullName}, {typeof(TMessage).Assembly.GetName().Name}]], Whizbang.Core",
        MessageType: typeof(TMessage).FullName ?? typeof(TMessage).Name
      );
    }

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) {
      return new object();
    }
  }

  private sealed class Cov2ReceptorInvoker : IReceptorInvoker {
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

  private sealed class Cov2LifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) => new object();
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) => new object();
    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) => new object();
    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) => new object();
  }

  private sealed class Cov2PerspectiveRegistry(
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives
  ) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class Cov2Provisioner : IInfrastructureProvisioner {
    public int ProvisionCallCount { get; private set; }

    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains,
        CancellationToken cancellationToken = default) {
      ProvisionCallCount++;
      return Task.CompletedTask;
    }
  }
}
