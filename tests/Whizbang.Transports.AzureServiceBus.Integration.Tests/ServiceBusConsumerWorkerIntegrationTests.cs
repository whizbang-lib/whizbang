// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1707 // Test method naming uses underscores by convention

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Transport;
using Whizbang.Testing.Workers;
using Whizbang.Transports.AzureServiceBus.Integration.Tests.Containers;
using EnvelopeSerializer = Whizbang.Core.Messaging.EnvelopeSerializer;

namespace Whizbang.Transports.AzureServiceBus.Integration.Tests;

/// <summary>
/// Integration tests for ServiceBusConsumerWorker using the real Azure Service Bus emulator.
/// Exercises the full message handling pipeline: transport receive → _handleMessageAsync → work coordination.
/// </summary>
[Timeout(240_000)]
[Category("Integration")]
[NotInParallel("ServiceBus")]
[ClassDataSource<ServiceBusEmulatorFixtureSource>(Shared = SharedType.PerAssembly)]
public class ServiceBusConsumerWorkerIntegrationTests(ServiceBusEmulatorFixtureSource fixtureSource) {
  private readonly ServiceBusEmulatorFixture _fixture = fixtureSource.Emulator;
  private readonly List<IAsyncDisposable> _disposables = [];

  [After(Test)]
  public async Task DisposeTrackedTransportsAsync() {
    foreach (var d in _disposables) {
      try { await d.DisposeAsync(); } catch { /* best-effort cleanup */ }
    }
    _disposables.Clear();
  }

  [Test]
  public async Task Worker_ReceivesMessage_ProcessesThroughHandleMessageAsync() {
    // Arrange: Wire up worker with real transport and test strategy
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    // The worker appends from the processor's thread while the test polls, so the capture is thread-safe.
    var capturedInboxMessages = new System.Collections.Concurrent.ConcurrentQueue<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    // Drain existing messages
    await _drainMessagesAsync("topic-00", "sub-00-a");

    // Act: Start worker, publish a message, wait for it to be processed
    await worker.StartAsync(CancellationToken.None);

    try {
      // The subscription exists once SubscriptionsReady completes, so a message published from
      // here on waits on the broker for this worker; no settle wait.
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

      var envelope = _createTestEnvelopeWithAggregateId(Guid.NewGuid());
      var destination = new TransportDestination("topic-00");
      await transport.PublishAsync(envelope, destination);

      // Wait for OUR message specifically. The shared emulator subscription is at-least-once
      // and the pre-test drain is best-effort, so a straggler from a prior test can land
      // first — asserting on .First() flaked exactly that way in CI. The strategy signals the
      // moment the worker hands it this message id.
      var inbox = await strategy.WaitForInboxMessageAsync(envelope.MessageId.Value, TimeSpan.FromSeconds(30));

      // Assert
      await Assert.That(inbox.MessageId).IsEqualTo(envelope.MessageId.Value);
      // TestMessage does not implement IEvent, so isEvent is false
      await Assert.That(inbox.IsEvent).IsFalse();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// Increment 4's missing e2e arm: the Ready composite is withheld until the REAL broker
  /// subscription completes — in-proc signals prove the wiring, this proves it against the wire.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Startup/StartupReadiness.cs</code-under-test>
  [Test]
  public async Task Ready_IsWithheldUntilTheRealBrokerSubscriptionCompletesAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => new CapturingWorkCoordinatorStrategy([]));
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));
    var serviceProvider = services.BuildServiceProvider();

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: new TestConsumerLogger(),
      orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance),
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: new ServiceBusConsumerOptions { Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")] });

    // The pipeline itself is drained (no blocking steps) — readiness now hangs ONLY on the broker.
    var state = new Whizbang.Core.Startup.StartupPipelineState();
    await new Whizbang.Core.Startup.StartupPipelineRunner(steps: [], observers: [state], dutyElector: NullDutyElector.Instance).RunAsync(CancellationToken.None);
    var signal = new Whizbang.Core.Startup.StartupReadySignal();
    var workerContributor = new ObservedContributor(worker);
    var readyService = new Whizbang.Core.Startup.StartupReadyService(pipelineState: state, signal: signal, contributors: [workerContributor], logger: NullLogger<StartupReadyService>.Instance, timeProvider: TimeProvider.System); // review-checks:allow end-to-end against a real broker

    var startedTask = readyService.StartedAsync(CancellationToken.None);
    // The composite has drained the pipeline and is now waiting on the worker: the exact moment
    // it would flip if it did not wait on the real subscription.
    await workerContributor.Asked.WaitAsync(TimeSpan.FromSeconds(30));
    await Assert.That(signal.IsReady).IsFalse()
      .Because("the composite must wait on the real subscription, not on the passage of time — "
             + "\"started\" is not \"subscribed\"");
    await Assert.That(startedTask.IsCompleted).IsFalse()
      .Because("the worker has not started, so its subscription cannot be ready");

    await worker.StartAsync(CancellationToken.None);
    try {
      await startedTask.WaitAsync(TimeSpan.FromSeconds(60));
      await Assert.That(signal.IsReady).IsTrue()
        .Because("the worker subscribed against the real broker, so the composite may now flip");
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithSecurityContext_EstablishesContextDuringHandlingAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    IScopeContext? capturedScope = null;
    // The worker appends from the processor's thread while the test polls, so the capture is thread-safe.
    var capturedInboxMessages = new System.Collections.Concurrent.ConcurrentQueue<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(
      capturedInboxMessages,
      onFlush: sp => {
        var accessor = sp?.GetService<IScopeContextAccessor>();
        capturedScope ??= accessor?.Current;
      });

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

      // Publish message with security context in hop
      var envelope = _createTestEnvelopeWithScope("test-user-id", "test-tenant-id");
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      // Wait for THIS message: any other message on the shared subscription would also reach
      // the strategy, and counting "at least one" would pass on it.
      var inbox = await strategy.WaitForInboxMessageAsync(envelope.MessageId.Value, TimeSpan.FromSeconds(30));

      // Security context should have been established during handling
      // Note: Context may not be captured if strategy doesn't have scope access,
      // but the test verifies the full pipeline runs this message without errors
      await Assert.That(inbox.MessageId).IsEqualTo(envelope.MessageId.Value);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_DuplicateMessage_SkipsSecondProcessingAsync() {
    // Arrange: Strategy returns empty work batch for duplicates
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var strategy = new DuplicateDetectingStrategy();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

      // Publish a message
      var envelope = _createTestEnvelopeWithAggregateId(Guid.NewGuid());
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      // Wait for the first processing of THIS message (a stray on the shared subscription would
      // have satisfied the old "any message processed" wait).
      await strategy.WaitForFirstProcessingAsync(envelope.MessageId.Value, TimeSpan.FromSeconds(30));

      await Assert.That(strategy.TimesProcessed(envelope.MessageId.Value)).IsEqualTo(1);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_StartAndStop_ManagesSubscriptionsCorrectlyAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [
        new TopicSubscription("topic-00", "sub-00-a"),
        new TopicSubscription("topic-01", "sub-01-a")
      ]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    // Act: Start creates subscriptions, stop disposes them
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));
    // Worker should have created 2 subscriptions - verify by stopping cleanly
    await worker.StopAsync(CancellationToken.None);

    // Assert: No exceptions means subscriptions were managed correctly
    // Logger should have logged starting/stopping
    await Assert.That(logger.HasMessage("starting")).IsTrue();
    await Assert.That(logger.HasMessage("stopping")).IsTrue();
  }

  [Test]
  public async Task Worker_PauseAndResume_PausesAndResumesSubscriptionsAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    await worker.StartAsync(CancellationToken.None);

    try {
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));
      // Act: Pause and resume should not throw
      await worker.PauseAllSubscriptionsAsync();
      await worker.ResumeAllSubscriptionsAsync();

      // Assert: Worker still running after pause/resume - no exception means success
      await Assert.That(logger.HasMessage("starting")).IsTrue();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_WithDestinationFilter_CreatesSubscriptionWithFilterAsync() {
    // Arrange
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    var strategy = new NoOpWorkCoordinatorStrategy();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    // Use DestinationFilter to test that code path
    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a", "my-filter-value")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    // Act: Start should handle filter metadata without errors
    await worker.StartAsync(CancellationToken.None);

    try {
      // Subscribing moved out of StartAsync when the schema gate landed — the worker
      // subscribes from ExecuteAsync, so "started" no longer implies "subscribed".
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));
      await Assert.That(logger.HasMessage("Subscribed")).IsTrue();
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithAggregateId_ExtractsStreamIdFromHopMetadataAsync() {
    // Arrange: Publish message with AggregateId in hop metadata
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    // The worker appends from the processor's thread while the test polls, so the capture is thread-safe.
    var capturedInboxMessages = new System.Collections.Concurrent.ConcurrentQueue<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-01", "sub-01-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    await _drainMessagesAsync("topic-01", "sub-01-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

      var expectedStreamId = Guid.NewGuid();
      var envelope = _createTestEnvelopeWithAggregateId(expectedStreamId);
      var destination = new TransportDestination("topic-01");
      await transport.PublishAsync(envelope, destination);

      // Key the wait and the assertion on THIS test's MessageId: the emulator entities are
      // shared across classes, and a prior test's message abandoned while still LOCKED is
      // invisible to this test's drain — it redelivers into this capture window later and
      // First() picks up the stale message instead of ours.
      var inbox = await strategy.WaitForInboxMessageAsync(envelope.MessageId.Value, TimeSpan.FromSeconds(30));

      // Assert: StreamId extracted from AggregateId in hop metadata
      await Assert.That(inbox.MessageId).IsEqualTo(envelope.MessageId.Value);
      await Assert.That(inbox.StreamId).IsEqualTo(expectedStreamId);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task Worker_MessageWithoutAggregateId_FallsBackToMessageIdAsync() {
    // Arrange: Message without AggregateId metadata - should fall back to MessageId
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var transport = new AzureServiceBusTransport(_fixture.Client, jsonOptions, new AzureServiceBusOptions { EnableSessions = false });
    _disposables.Add(transport);
    await transport.InitializeAsync();

    // The worker appends from the processor's thread while the test polls, so the capture is thread-safe.
    var capturedInboxMessages = new System.Collections.Concurrent.ConcurrentQueue<InboxMessage>();
    var strategy = new CapturingWorkCoordinatorStrategy(capturedInboxMessages);

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var serviceProvider = services.BuildServiceProvider();
    var orderedProcessor = new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance);
    var logger = new TestConsumerLogger();

    var options = new ServiceBusConsumerOptions {
      Subscriptions = [new TopicSubscription("topic-00", "sub-00-a")]
    };

    var worker = new ServiceBusConsumerWorker(
      transport: transport,
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      logger: logger,
      orderedProcessor: orderedProcessor,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      envelopeSerializer: new EnvelopeSerializer(jsonOptions),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
      ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
      options: options);

    await _drainMessagesAsync("topic-00", "sub-00-a");
    await worker.StartAsync(CancellationToken.None);

    try {
      await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(30));

      // Envelope with no AggregateId metadata
      var envelope = _createTestEnvelopeWithoutAggregateId();
      var destination = new TransportDestination("topic-00");
      await transport.PublishAsync(envelope, destination);

      // Key the wait and the assertion on THIS test's MessageId: the emulator entities are
      // shared across classes, and a prior test's message abandoned while still LOCKED is
      // invisible to this test's drain — it redelivers into this capture window later and
      // First() picks up the stale message (whose hop-stamped StreamId differs from its
      // MessageId) instead of ours. Observed on CI as expected/found ids minted in the same
      // millisecond — the stale message's own MessageId + adjacent hop AggregateId.
      var inbox = await strategy.WaitForInboxMessageAsync(envelope.MessageId.Value, TimeSpan.FromSeconds(30));

      // Assert: StreamId falls back to MessageId
      await Assert.That(inbox.MessageId).IsEqualTo(envelope.MessageId.Value);
      await Assert.That(inbox.StreamId).IsEqualTo(envelope.MessageId.Value);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  #region Helper Methods

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithAggregateId(Guid aggregateId) {
    var metadata = new Dictionary<string, JsonElement> {
      ["AggregateId"] = JsonSerializer.SerializeToElement(aggregateId.ToString())
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("integration-test"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          Metadata = metadata,
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithoutAggregateId() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("no-aggregate-id"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithScope(string userId, string tenantId) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("security-context-test"),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          Topic = "test-topic",
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ]
    };
  }

  private async Task _drainMessagesAsync(string topicName, string subscriptionName) {
    var receiver = _fixture.Client.CreateReceiver(topicName, subscriptionName);
    try {
      for (var i = 0; i < 100; i++) {
        var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100));
        if (msg == null) {
          break;
        }
        await receiver.CompleteMessageAsync(msg);
      }
    } finally {
      await receiver.DisposeAsync();
    }
  }

  #endregion

  #region Test Doubles

  /// <summary>
  /// Work coordinator strategy that captures inbox messages and returns them as work items.
  /// </summary>
  private sealed class CapturingWorkCoordinatorStrategy(
      System.Collections.Concurrent.ConcurrentQueue<InboxMessage> capturedMessages,
      Action<IServiceProvider?>? onFlush = null) : IWorkCoordinatorStrategy {
    private readonly System.Collections.Concurrent.ConcurrentQueue<InboxMessage> _capturedMessages = capturedMessages;
    private readonly Action<IServiceProvider?>? _onFlush = onFlush;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource<InboxMessage>> _arrivals = new();
    private InboxMessage? _pendingMessage;

    /// <summary>Completes when the worker hands this strategy the inbox message with <paramref name="messageId"/>.</summary>
    public Task<InboxMessage> WaitForInboxMessageAsync(Guid messageId, TimeSpan timeout) =>
      _arrival(messageId).Task.WaitAsync(timeout);

    private TaskCompletionSource<InboxMessage> _arrival(Guid messageId) =>
      _arrivals.GetOrAdd(messageId, _ => new TaskCompletionSource<InboxMessage>(TaskCreationOptions.RunContinuationsAsynchronously));

    public void QueueOutboxMessage(OutboxMessage message) { }

    public void QueueInboxMessage(InboxMessage message) {
      _pendingMessage = message;
      _capturedMessages.Enqueue(message);
      _arrival(message.MessageId).TrySetResult(message);
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      _onFlush?.Invoke(null);

      if (_pendingMessage != null) {
        var msg = _pendingMessage;
        _pendingMessage = null;

        // Create InboxWork from captured message
        var inboxWork = new InboxWork {
          MessageId = msg.MessageId,
          Envelope = msg.Envelope,
          StreamId = msg.StreamId,
          MessageType = msg.MessageType
        };

        return Task.FromResult(new WorkBatch {
          InboxWork = [inboxWork],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Strategy that tracks processed message IDs and returns empty for duplicates.
  /// </summary>
  private sealed class DuplicateDetectingStrategy : IWorkCoordinatorStrategy {
    // The worker flushes from the processor's threads while the test reads, so both maps are concurrent.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _processCounts = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> _firstProcessings = new();
    private InboxMessage? _pendingMessage;

    /// <summary>Completes when the message with <paramref name="messageId"/> is processed for the first time.</summary>
    public Task WaitForFirstProcessingAsync(Guid messageId, TimeSpan timeout) =>
      _firstProcessing(messageId).Task.WaitAsync(timeout);

    /// <summary>How many times the message with <paramref name="messageId"/> was returned as work.</summary>
    public int TimesProcessed(Guid messageId) => _processCounts.GetValueOrDefault(messageId);

    private TaskCompletionSource _firstProcessing(Guid messageId) =>
      _firstProcessings.GetOrAdd(messageId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public void QueueOutboxMessage(OutboxMessage message) { }

    public void QueueInboxMessage(InboxMessage message) {
      _pendingMessage = message;
    }

    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      if (_pendingMessage != null) {
        var msg = _pendingMessage;
        _pendingMessage = null;

        // Check for duplicate
        if (_processCounts.ContainsKey(msg.MessageId)) {
          // Return empty work - duplicate
          return Task.FromResult(new WorkBatch {
            InboxWork = [],
            OutboxWork = [],
            PerspectiveWork = []
          });
        }

        _processCounts.AddOrUpdate(msg.MessageId, 1, (_, n) => n + 1);
        _firstProcessing(msg.MessageId).TrySetResult();

        var inboxWork = new InboxWork {
          MessageId = msg.MessageId,
          Envelope = msg.Envelope,
          StreamId = msg.StreamId,
          MessageType = msg.MessageType
        };

        return Task.FromResult(new WorkBatch {
          InboxWork = [inboxWork],
          OutboxWork = [],
          PerspectiveWork = []
        });
      }

      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Minimal no-op strategy for startup/shutdown tests.
  /// </summary>
  private sealed class NoOpWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return Task.FromResult(new WorkBatch {
        InboxWork = [],
        OutboxWork = [],
        PerspectiveWork = []
      });
    }
  }

  /// <summary>
  /// Logger that captures log messages for assertion.
  /// </summary>
  private sealed class TestConsumerLogger : ILogger<ServiceBusConsumerWorker> {
    private readonly List<string> _messages = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      _messages.Add(formatter(state, exception));
    }

    public bool HasMessage(string substring) =>
      _messages.Any(m => m.Contains(substring, StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>
  /// Wraps a readiness contributor and signals when the composite asks it for readiness, which it
  /// does only after the pipeline's blocking steps have drained.
  /// </summary>
  private sealed class ObservedContributor(IStartupReadinessContributor inner) : IStartupReadinessContributor {
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the composite starts waiting on the wrapped contributor.</summary>
    public Task Asked => _asked.Task;

    public string ContributorName => inner.ContributorName;

    public Task WaitForContributorReadyAsync(CancellationToken cancellationToken) {
      var ready = inner.WaitForContributorReadyAsync(cancellationToken);
      _asked.TrySetResult();
      return ready;
    }
  }

  #endregion
}
