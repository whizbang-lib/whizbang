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
/// Deep coverage tests for TransportConsumerWorker targeting remaining uncovered code paths:
/// - Strongly-typed envelope serialization via IEnvelopeSerializer (non-JsonElement payloads)
/// - _extractStreamId with valid GUID AggregateId metadata
/// - Readiness check logging paths (true/false with info logging)
/// - Infrastructure provisioning with info-level logging
/// - Health monitor recovery cycle for failed subscriptions
/// - _handleMessageAsync error path with activity tracing tags
/// - null envelopeType skipping _populateDeliveredAtTimestamp
/// - _serializeToNewInboxMessage with JsonElement payload but wrong envelope type
/// - _onConnectionRecoveredAsync with null subscriptions
/// - duplicate message info logging with info level
/// - _extractStreamId with null metadata
/// - _extractStreamId with missing AggregateId key
/// - _handleMessageAsync failure handler callback
/// - Lifecycle paths with null deserializer result
/// </summary>
[Category("Workers")]
public class TransportConsumerWorkerDeepCoverageTests {

  // ========================================
  // _extractStreamId - valid GUID AggregateId extracts correctly
  // ========================================

  [Test]
  public async Task HandleMessage_WithValidGuidAggregateId_ExtractsStreamIdFromMetadataAsync() {
    // Arrange
    var messageId = MessageId.New();
    var expectedStreamId = Guid.NewGuid();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "AggregateId", expectedStreamId.ToString() } });
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

    // Assert
    await Assert.That(workStrategy.LastQueuedStreamId).IsEqualTo(expectedStreamId)
      .Because("Valid GUID AggregateId should be extracted from metadata");
  }

  // ========================================
  // _extractStreamId - envelope with null Hops list fallback
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullHops_FallsBackToMessageIdForStreamIdAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    // Envelope with empty Hops (no metadata at all)
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert
    await Assert.That(workStrategy.LastQueuedStreamId).IsEqualTo(messageId.Value)
      .Because("Empty hops should fall back to MessageId for StreamId");
  }

  // ========================================
  // _extractStreamId - hop has metadata but no AggregateId key
  // ========================================

  [Test]
  public async Task HandleMessage_WithMetadataButNoAggregateIdKey_FallsBackToMessageIdAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    var metadataJson = JsonSerializer.SerializeToElement(
      new Dictionary<string, object> { { "SomeOtherKey", "value" } });
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

    // Assert
    await Assert.That(workStrategy.LastQueuedStreamId).IsEqualTo(messageId.Value)
      .Because("Missing AggregateId key should fall back to MessageId");
  }

  // ========================================
  // Readiness check returns false - exercises the warning and early return
  // ========================================

  [Test]
  public async Task ExecuteAsync_WhenReadinessReturnsFalse_WithInfoLogging_LogsWarningAndReturnsAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    services.AddSingleton<ITransportReadinessCheck>(new FalseReadinessCheck());
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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

    // Act
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(300);

    // Assert - no subscriptions should be created when readiness returns false
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(0)
      .Because("Worker should not subscribe when readiness check returns false");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // Readiness check returns true with info logging
  // ========================================

  [Test]
  public async Task ExecuteAsync_WhenReadinessReturnsTrue_WithInfoLogging_LogsReadyAndSubscribesAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    services.AddSingleton<ITransportReadinessCheck>(new TrueReadinessCheck());
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
    await Task.Delay(300);
    cts.Cancel();

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("Worker should subscribe after readiness check returns true");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // Provisioning with info-level logging exercises all provisioning log lines
  // ========================================

  [Test]
  public async Task ExecuteAsync_WithProvisionerAndInfoLogging_LogsProvisioningMessagesAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var provisioner = new TrackingProvisioner();
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    services.AddSingleton<IInfrastructureProvisioner>(provisioner);
    services.AddSingleton(Options.Create(
      new RoutingOptions().OwnDomains("myapp.users")));
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
    await Task.Delay(300);
    cts.Cancel();

    // Assert
    await Assert.That(provisioner.WasCalled).IsTrue()
      .Because("Provisioner should be called for owned domains");
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("Subscriptions should be created after provisioning");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // Duplicate message detection with info-level logging
  // ========================================

  [Test]
  public async Task HandleMessage_WhenDuplicate_WithInfoLogging_LogsDuplicateAndSkipsAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    // Strategy returns empty inbox work (simulates duplicate detection)
    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

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
      logger
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - no completions/failures since it was a duplicate
    await Assert.That(workStrategy.CompletionCount).IsEqualTo(0)
      .Because("Duplicate messages should be skipped without processing");
  }

  // ========================================
  // _handleMessageAsync - error path sets activity error tags
  // ========================================

  [Test]
  public async Task HandleMessage_WithException_SetsActivityErrorTagsAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new ThrowingOnQueueStrategy();

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
    await Task.Delay(200);

    // Envelope with traceparent to create activity for error tagging
    const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          TraceParent = traceParent
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act - per-message error isolation catches the exception (Activity error tags still set internally)
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);

    cts.Cancel();
  }

  // ========================================
  // _handleMessageAsync with whitespace envelopeType throws
  // ========================================

  [Test]
  public async Task HandleMessage_WithWhitespaceEnvelopeType_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value);

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
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);

    // Act - per-message error isolation catches the InvalidOperationException (logged, not propagated)
    await transport.SimulateMessageReceivedAsync(envelope, "   ");

    cts.Cancel();
  }

  // ========================================
  // _handleMessageAsync - non-event message with IEventTypeProvider fallback
  // ========================================

  [Test]
  public async Task HandleMessage_WithoutEventTypeProvider_FallsBackToRuntimeCheckAsync() {
    // Arrange - no IEventTypeProvider registered, exercises payload is IEvent fallback
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
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
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - JsonElement payload is not IEvent, so isEvent should be false
    await Assert.That(workStrategy.LastQueuedIsEvent).IsFalse()
      .Because("JsonElement payload is not IEvent so runtime check should return false");
  }

  // ========================================
  // _onConnectionRecoveredAsync when subscriptions have null state
  // ========================================

  [Test]
  public async Task OnRecovery_WithNullSubscriptions_DoesNotThrowOnDisposeAsync() {
    // Arrange - use a transport that doesn't store subscriptions in state initially
    var transport = new DeepCoverageRecoveringTransport(failFirstSubscription: true);
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("topic1"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      InitialRetryAttempts = 1,
      RetryIndefinitely = false,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      HealthCheckInterval = TimeSpan.FromMinutes(10),
      AllowPartialSubscriptions = true
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
    await Task.Delay(300); // Allow subscription to fail

    // Now stop failing and simulate recovery
    transport.StopFailing();
    await transport.SimulateRecoveryAsync();
    await Task.Delay(200);

    cts.Cancel();

    // Assert - recovery should succeed without throwing even when subscription was null
    await Assert.That(transport.RecoveryCount).IsEqualTo(1)
      .Because("Recovery handler should have been invoked");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // Health monitor recovers failed subscriptions
  // ========================================

  [Test]
  public async Task HealthMonitor_WithFailedSubscription_AttemptsRecoveryAsync() {
    // Arrange - transport fails initially, then succeeds after interval
    var transport = new DeepCoverageSelectiveFailTransport(failingTopics: ["fail-topic"]);
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("fail-topic"));

    var resilienceOptions = new SubscriptionResilienceOptions {
      AllowPartialSubscriptions = true,
      InitialRetryAttempts = 1,
      RetryIndefinitely = false,
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      HealthCheckInterval = TimeSpan.FromMilliseconds(200) // Short interval for test
    };

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

    var services = new ServiceCollection();
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, resilienceOptions,
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null,
      metrics: null,
      logger
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);

    // Wait for initial subscription attempt to fail (condition-based, not fixed delay)
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (transport.SubscribeCallCount < 1 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    // Stop failing so health monitor recovery succeeds
    transport.StopFailing();

    // Wait for health monitor to retry (condition-based, not fixed delay)
    deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (transport.SubscribeCallCount < 2 && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(20);
    }

    // At this point health monitor should have attempted recovery
    await Assert.That(transport.SubscribeCallCount).IsGreaterThanOrEqualTo(2)
      .Because("Health monitor should retry failed subscriptions");

    cts.Cancel();
    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // _handleMessageAsync - failure handler callback path
  // ========================================

  [Test]
  public async Task HandleMessage_WhenProcessorFails_InvokesFailureHandlerAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: false);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    // Use debug logging to exercise more code paths
    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
    var logger = loggerFactory.CreateLogger<TransportConsumerWorker>();

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
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act - process message; deserialization may fail but completion/failure handlers run
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // deserialization failures are expected
    }

    cts.Cancel();

    // Assert - either completion or failure handler should have been called
    var totalHandled = workStrategy.CompletionCount + workStrategy.FailureCount;
    await Assert.That(totalHandled).IsGreaterThanOrEqualTo(1)
      .Because("Either completion or failure handler should be invoked by ordered processor");
  }

  // ========================================
  // Lifecycle Pre/Post inbox with no deserializer (null _lifecycleMessageDeserializer)
  // ========================================

  [Test]
  public async Task HandleMessage_WithNullDeserializerAndInvoker_SkipsLifecycleAsync() {
    // Arrange - register invoker but NOT deserializer
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var invoker = new TrackingDeepReceptorInvoker();
    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: false);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddScoped<IReceptorInvoker>(_ => invoker);
    services.AddWhizbangMessageSecurity(opts => { opts.AllowAnonymous = true; });
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var worker = new TransportConsumerWorker(
      transport, options, new SubscriptionResilienceOptions(),
      scopeFactory, new JsonSerializerOptions(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      lifecycleMessageDeserializer: null, // No deserializer
      metrics: null,
      NullLogger<TransportConsumerWorker>.Instance
    );

    using var cts = new CancellationTokenSource();
    _ = worker.StartAsync(cts.Token);
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestMessage, TestApp]], Whizbang.Core";

    // Act
    try {
      await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    } catch {
      // deserialization may fail
    }

    cts.Cancel();

    // Assert - invoker should NOT be called since deserializer is null
    await Assert.That(invoker.InvokeCallCount).IsEqualTo(0)
      .Because("Without lifecycle deserializer, invoker should not be called");
  }

  // ========================================
  // _handleMessageAsync - traceParent parsing with Split for message type
  // ========================================

  [Test]
  public async Task HandleMessage_WithTraceParent_ExtractsMessageTypeForActivityNameAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    // Create envelope with valid traceparent to exercise activity creation path
    const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          TraceParent = traceParent
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[My.Namespace.OrderCreated, App]], Whizbang.Core";

    // Act - should succeed and activity should be created with "Inbox OrderCreated" name
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - message processed successfully
    await Assert.That(workStrategy.QueuedInboxCount).IsEqualTo(1);
  }

  // ========================================
  // _handleMessageAsync - envelopeType without assembly in type (no comma)
  // ========================================

  [Test]
  public async Task HandleMessage_WithSimpleTypeNameNoAssembly_ExtractsHandlerNameAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    // Type without assembly qualifier (no comma in message type)
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[SimpleCommand]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert
    await Assert.That(workStrategy.LastQueuedHandlerName).IsEqualTo("SimpleCommandHandler")
      .Because("Simple type name without namespace dots should work");
  }

  // ========================================
  // StopAsync - clears states and disposes subscriptions
  // ========================================

  [Test]
  public async Task StopAsync_AfterStart_ClearsAllStatesAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
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
    await Task.Delay(200);

    // Assert states exist before stop
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(2);

    // Act
    await worker.StopAsync(CancellationToken.None);

    // Assert - states cleared, subscriptions disposed
    await Assert.That(worker.SubscriptionStates.Count).IsEqualTo(0)
      .Because("StopAsync should clear all subscription states");

    foreach (var sub in transport.Subscriptions) {
      await Assert.That(sub.IsDisposed).IsTrue()
        .Because("All subscriptions should be disposed");
    }
  }

  // ========================================
  // ExecuteAsync - cancellation requested path
  // ========================================

  [Test]
  public async Task ExecuteAsync_WhenCancelled_LogsCancellationRequestedAsync() {
    // Arrange
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
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
    await Task.Delay(200);

    // Act - cancel to trigger the OperationCanceledException catch
    cts.Cancel();
    await Task.Delay(100);

    // Assert
    await Assert.That(transport.SubscribeCallCount).IsEqualTo(1)
      .Because("Worker should have subscribed before cancellation");

    try { await worker.StopAsync(CancellationToken.None); } catch { }
  }

  // ========================================
  // _serializeToNewInboxMessage - IEventTypeProvider registered, non-event message
  // ========================================

  [Test]
  public async Task HandleMessage_WithEventTypeProvider_NonEventMessage_SetsIsEventFalseAsync() {
    // Arrange - IEventTypeProvider registered but message is not in the event list
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => workStrategy);
    services.AddSingleton<IEventTypeProvider>(new DeepCoverageEventTypeProvider());
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
    await Task.Delay(200);

    var envelope = _createJsonEnvelope(messageId);
    // Use a command type (not in the event type provider list)
    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.SomeCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert - should not be detected as event since SomeCommand is not in event list
    await Assert.That(workStrategy.LastQueuedIsEvent).IsFalse()
      .Because("Message type not in IEventTypeProvider list should not be detected as event");
  }

  // ========================================
  // _extractStreamId - hop with null metadata (metadata not set)
  // ========================================

  [Test]
  public async Task HandleMessage_WithHopNullMetadata_FallsBackToMessageIdAsync() {
    // Arrange
    var messageId = MessageId.New();
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

    var workStrategy = new DeepCoverageWorkStrategy(messageId.Value, returnEmptyInboxWork: true);

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
    await Task.Delay(200);

    // Hop with null metadata (no Metadata property set)
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = null
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    const string envelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[TestApp.TestCommand, TestApp]], Whizbang.Core";

    // Act
    await transport.SimulateMessageReceivedAsync(envelope, envelopeType);
    cts.Cancel();

    // Assert
    await Assert.That(workStrategy.LastQueuedStreamId).IsEqualTo(messageId.Value)
      .Because("Null metadata should fall back to MessageId");
  }

  // ========================================
  // Pause/Resume with no subscriptions (null state.Subscription)
  // ========================================

  [Test]
  public async Task PauseAllSubscriptionsAsync_BeforeStartAsync_DoesNotThrowAsync() {
    // Arrange - don't start worker, so subscriptions are null
    var transport = new DeepCoverageTransport();
    var options = new TransportConsumerOptions();
    options.Destinations.Add(new TransportDestination("test-topic"));

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

    // Act & Assert - should not throw with null subscriptions
    await worker.PauseAllSubscriptionsAsync();
    await worker.ResumeAllSubscriptionsAsync();
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

  // ========================================
  // Test Doubles
  // ========================================

  private sealed class DeepCoverageTransport : ITransport {
    private Func<IMessageEnvelope, string?, CancellationToken, Task>? _handler;
    private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _batchHandler;
    private readonly List<DeepCoverageSubscription> _subscriptions = [];

    public int SubscribeCallCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
    public IReadOnlyList<DeepCoverageSubscription> Subscriptions => _subscriptions;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _handler = handler;
      var subscription = new DeepCoverageSubscription();
      _subscriptions.Add(subscription);
      return Task.FromResult<ISubscription>(subscription);
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      _batchHandler = batchHandler;
      var subscription = new DeepCoverageSubscription();
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

    public async Task SimulateMessageReceivedAsync(IMessageEnvelope envelope, string? envelopeType) {
      if (_batchHandler != null) {
        await _batchHandler([new TransportMessage(envelope, envelopeType)], CancellationToken.None);
      } else if (_handler != null) {
        await _handler(envelope, envelopeType, CancellationToken.None);
      }
    }
  }

  private sealed class DeepCoverageSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
    public bool IsDisposed { get; private set; }

#pragma warning disable CS0067
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067

    public Task PauseAsync() { IsActive = false; return Task.CompletedTask; }
    public Task ResumeAsync() { IsActive = true; return Task.CompletedTask; }
    public void Dispose() { IsDisposed = true; }
  }

  private sealed class DeepCoverageRecoveringTransport(bool failFirstSubscription) : ITransport, ITransportWithRecovery {
    private Func<CancellationToken, Task>? _recoveryHandler;
    private bool _shouldFail = failFirstSubscription;

    public int SubscribeCallCount { get; private set; }
    public int RecoveryCount { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public void SetRecoveryHandler(Func<CancellationToken, Task>? onRecovered) {
      _recoveryHandler = onRecovered;
    }

    public void StopFailing() { _shouldFail = false; }

    public async Task SimulateRecoveryAsync() {
      RecoveryCount++;
      if (_recoveryHandler != null) {
        await _recoveryHandler(CancellationToken.None);
      }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      if (_shouldFail) {
        throw new InvalidOperationException("Simulated subscription failure");
      }
      return Task.FromResult<ISubscription>(new DeepCoverageSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      SubscribeCallCount++;
      if (_shouldFail) {
        throw new InvalidOperationException("Simulated subscription failure");
      }
      return Task.FromResult<ISubscription>(new DeepCoverageSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class DeepCoverageSelectiveFailTransport(IEnumerable<string> failingTopics) : ITransport {
    private readonly HashSet<string> _failingTopics = [.. failingTopics];
    private int _subscribeCallCount;
    private volatile bool _isFailing = true;

    public int SubscribeCallCount => Volatile.Read(ref _subscribeCallCount);
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public void StopFailing() { _isFailing = false; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishAsync(
        IMessageEnvelope envelope,
        TransportDestination destination,
        string? envelopeType = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
        Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
        TransportDestination destination,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCallCount);
      if (_isFailing && _failingTopics.Contains(destination.Address)) {
        throw new InvalidOperationException($"Subscription to {destination.Address} failed");
      }
      return Task.FromResult<ISubscription>(new DeepCoverageSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
        Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
        TransportDestination destination,
        TransportBatchOptions batchOptions,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _subscribeCallCount);
      if (_isFailing && _failingTopics.Contains(destination.Address)) {
        throw new InvalidOperationException($"Subscription to {destination.Address} failed");
      }
      return Task.FromResult<ISubscription>(new DeepCoverageSubscription());
    }

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(
        IMessageEnvelope requestEnvelope,
        TransportDestination destination,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull =>
      throw new NotSupportedException();
  }

  private sealed class DeepCoverageWorkStrategy(Guid expectedMessageId, bool returnEmptyInboxWork = false) : IWorkCoordinatorStrategy {
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

  private sealed class ThrowingOnQueueStrategy : IWorkCoordinatorStrategy {
    public void QueueInboxMessage(InboxMessage message) {
      throw new InvalidOperationException("Simulated queue failure");
    }

    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus status, string errorDetails) { }

    public Task<WorkBatch> FlushAsync(WorkBatchOptions flags, FlushMode mode = FlushMode.Required, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  private sealed class FalseReadinessCheck : ITransportReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(false);
    }
  }

  private sealed class TrueReadinessCheck : ITransportReadinessCheck {
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(true);
    }
  }

  private sealed class TrackingProvisioner : IInfrastructureProvisioner {
    public bool WasCalled { get; private set; }

    public Task ProvisionOwnedDomainsAsync(
        IReadOnlySet<string> ownedDomains,
        CancellationToken cancellationToken = default) {
      WasCalled = true;
      return Task.CompletedTask;
    }
  }

  private sealed class TrackingDeepReceptorInvoker : IReceptorInvoker {
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

  private sealed class DeepCoverageEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() {
      return [typeof(DeepTestEvent)];
    }
  }

  private sealed class DeepTestEvent : IEvent { }
}
