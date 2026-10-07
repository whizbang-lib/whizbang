// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests.BranchCoverage;

/// <summary>
/// Branch outcomes of the Azure Service Bus package's supporting components that no other suite
/// takes: the connection retry's exception filter, the dead-letter drainers' construction
/// guards, configuration with no configuration source, the readiness check and backlog peek
/// guards, the provisioner's ownership marker and missing drift state, the liveness sweep's
/// fresh-subscription skip, the receive decision for a payload-less envelope, and three DI
/// registrations resolved in shapes the transport registration never produces.
/// </summary>
[Timeout(10_000)]
public class AsbComponentBranchCoverageTests {
  private const string CONNECTION_STRING =
    "Endpoint=sb://localhost:1;SharedAccessKeyName=probe;SharedAccessKey=cHJvYmVrZXk=";
  private const string UNIT_CONNECTION_STRING =
    "Endpoint=sb://unit-test.example/;SharedAccessKeyName=unit;SharedAccessKey=dW5pdC10ZXN0LWtleQ==";
  private const string EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";
  private const string SECOND_EMULATOR_CONNECTION_STRING =
    "Endpoint=sb://127.0.0.1;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true";

  // ========================================
  // CONNECTION RETRY
  // ========================================

  /// <summary>
  /// An exception that is neither a Service Bus nor a transient Azure failure is a configuration or
  /// programming fault: it surfaces on the first attempt instead of being retried forever.
  /// </summary>
  [Test]
  public async Task CreateClientWithRetryAsync_NonTransientFailure_PropagatesWithoutRetryAsync() {
    var attempts = 0;
    var retry = new AzureServiceBusConnectionRetry(new AzureServiceBusOptions { InitialRetryDelay = TimeSpan.Zero }) {
      VerifyNamespaceReachableAsync = (_, _) => {
        attempts++;
        return Task.FromException(new InvalidOperationException("misconfigured namespace"));
      }
    };

    await Assert.ThrowsAsync<InvalidOperationException>(async () => await retry.CreateClientWithRetryAsync(CONNECTION_STRING));
    await Assert.That(attempts).IsEqualTo(1)
      .Because("only Service Bus and transient Azure failures are worth another attempt");
  }

  /// <summary>
  /// A Service Bus failure wrapped in an aggregate is transient: the loop retries, and the next
  /// reachable attempt returns a client.
  /// </summary>
  [Test]
  public async Task CreateClientWithRetryAsync_AggregateOfServiceBusFailure_RetriesUntilReachableAsync() {
    var attempts = 0;
    var retry = new AzureServiceBusConnectionRetry(new AzureServiceBusOptions { InitialRetryDelay = TimeSpan.Zero }) {
      VerifyNamespaceReachableAsync = (_, _) => {
        attempts++;
        return attempts == 1
          ? Task.FromException(new AggregateException(
            new ServiceBusException("namespace busy", ServiceBusFailureReason.ServiceBusy)))
          : Task.CompletedTask;
      }
    };

    await using var client = await retry.CreateClientWithRetryAsync(CONNECTION_STRING);

    await Assert.That(client).IsNotNull();
    await Assert.That(attempts).IsEqualTo(2)
      .Because("the aggregated Service Bus failure is transient, so the loop tried again");
  }

  // ========================================
  // DEAD-LETTER DRAINERS
  // ========================================

  /// <summary>
  /// A drainer built without a logger still drains: an unconvertible dead-letter is abandoned and
  /// reported through the no-op logger rather than failing on a null one.
  /// </summary>
  [Test]
  public async Task DeadLetterDrainer_NullLogger_StillAbandonsAnUnconvertibleMessageAsync() {
    var client = new DeadLetterQueueClient();
    client.Receiver.Batches.Enqueue([
      ServiceBusModelFactory.ServiceBusReceivedMessage(
        body: BinaryData.FromString("dlq-body"), messageId: "not-a-guid")
    ]);
    await using var drainer = new AzureServiceBusDeadLetterDrainer(
      client, "orders", "billing", (_, _) => Task.FromResult(true), logger: null!);

    var drained = await drainer.DrainDeadLetterQueueAsync(maxCount: 10);

    await Assert.That(drained).IsEqualTo(0);
    await Assert.That(client.Receiver.Abandoned).IsEquivalentTo(["not-a-guid"]);
  }

  /// <summary>
  /// The fleet drainer resolves its client lazily, so a missing client factory surfaces on the
  /// first drain pass that needs a drainer, naming the missing dependency.
  /// </summary>
  [Test]
  public async Task FleetDrainer_NullClientFactory_FailsOnFirstDrainNamingItAsync() {
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      clientFactory: null!,
      activeSubscriptions: () => [("orders", "billing")],
      importAsync: (_, _) => Task.FromResult(true),
      loggerFactory: NullLoggerFactory.Instance);

    await Assert.That(() => fleet.DrainDeadLetterQueueAsync(10))
      .Throws<ArgumentNullException>().WithParameterName("clientFactory");
  }

  /// <summary>A missing import seam surfaces on the first drain pass, naming it.</summary>
  [Test]
  public async Task FleetDrainer_NullImport_FailsOnFirstDrainNamingItAsync() {
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      clientFactory: () => new RaisableServiceBusClient(),
      activeSubscriptions: () => [("orders", "billing")],
      importAsync: null!,
      loggerFactory: NullLoggerFactory.Instance);

    await Assert.That(() => fleet.DrainDeadLetterQueueAsync(10))
      .Throws<ArgumentNullException>().WithParameterName("importAsync");
  }

  /// <summary>A missing logger factory surfaces on the first drain pass, naming it.</summary>
  [Test]
  public async Task FleetDrainer_NullLoggerFactory_FailsOnFirstDrainNamingItAsync() {
    var fleet = new AzureServiceBusFleetDeadLetterDrainer(
      clientFactory: () => new RaisableServiceBusClient(),
      activeSubscriptions: () => [("orders", "billing")],
      importAsync: (_, _) => Task.FromResult(true),
      loggerFactory: null!);

    await Assert.That(() => fleet.DrainDeadLetterQueueAsync(10))
      .Throws<ArgumentNullException>().WithParameterName("loggerFactory");
  }

  // ========================================
  // CONFIGURATION, READINESS, BACKLOG PEEK GUARDS
  // ========================================

  /// <summary>
  /// With no configuration source at all, post-configuration leaves the code-configured options
  /// exactly as they were.
  /// </summary>
  [Test]
  public async Task OptionsPostConfigure_NoConfiguration_LeavesOptionsUntouchedAsync() {
    var options = new AzureServiceBusOptions { DefaultSubscriptionName = "from-code", MaxConcurrentCalls = 7 };

    new AzureServiceBusOptionsPostConfigure(configuration: null).PostConfigure(null, options);

    await Assert.That(options.DefaultSubscriptionName).IsEqualTo("from-code");
    await Assert.That(options.MaxConcurrentCalls).IsEqualTo(7);
  }

  [Test]
  public async Task ReadinessCheck_NullTransport_ThrowsNamingItAsync() {
    await Assert.That(() => new ServiceBusReadinessCheck(
        null!, new TestServiceBusClient(isHealthy: true), NullLogger<ServiceBusReadinessCheck>.Instance))
      .Throws<ArgumentNullException>().WithParameterName("transport");
  }

  [Test]
  public async Task ReadinessCheck_NullClient_ThrowsNamingItAsync() {
    await Assert.That(() => new ServiceBusReadinessCheck(
        new TestTransport(isInitialized: true), null!, NullLogger<ServiceBusReadinessCheck>.Instance))
      .Throws<ArgumentNullException>().WithParameterName("client");
  }

  [Test]
  public async Task ReadinessCheck_NullLogger_ThrowsNamingItAsync() {
    await Assert.That(() => new ServiceBusReadinessCheck(
        new TestTransport(isInitialized: true), new TestServiceBusClient(isHealthy: true), null!))
      .Throws<ArgumentNullException>().WithParameterName("logger");
  }

  [Test]
  public async Task BacklogPeek_NullTransport_ThrowsNamingItAsync() {
    await Assert.That(() => new AsbBacklogPeek(null!))
      .Throws<ArgumentNullException>().WithParameterName("transport");
  }

  // ========================================
  // INFRASTRUCTURE PROVISIONER
  // ========================================

  /// <summary>
  /// Only a subscription whose ownership marker is literally true is drift-checked. A marker set to
  /// false, and a subscription with no metadata, are shared entities: a foreign subscription on
  /// them is the normal topology, not drift.
  /// </summary>
  [Test]
  public async Task ProvisionManifest_OwnershipMarkerNotTrue_IsNotDriftCheckedAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox.marker-false", "inbox.no-metadata", "inbox.unmarked", "inbox.marker-text", "inbox.owned" },
      ExistingSubscriptions = {
        ("inbox.marker-false", "other-service-a"),
        ("inbox.no-metadata", "other-service-b"),
        ("inbox.unmarked", "other-service-d"),
        ("inbox.marker-text", "other-service-e"),
        ("inbox.owned", "other-service-c")
      }
    };
    var driftState = new TopologyDriftState();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient, NullLogger<ServiceBusInfrastructureProvisioner>.Instance, driftState: driftState);
    var manifest = new TopologyManifest("coverage-service", [], [
      new InboxSubscription("inbox.marker-false", Metadata: new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = false
      }),
      new InboxSubscription("inbox.no-metadata"),
      // Metadata for something else entirely carries no ownership marker at all.
      new InboxSubscription("inbox.unmarked", Metadata: new Dictionary<string, object> {
        ["coverage-unrelated-key"] = true
      }),
      // Only the boolean true is the marker; a value that merely reads like it is not.
      new InboxSubscription("inbox.marker-text", Metadata: new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = "true"
      }),
      new InboxSubscription("inbox.owned", Metadata: new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = true
      })
    ]);

    await provisioner.ProvisionManifestAsync(manifest);

    var finding = driftState.Findings.Single();
    await Assert.That(finding.Entity).IsEqualTo("inbox.owned")
      .Because("the true marker is checked (proving the check runs) and the other four are not");
  }

  /// <summary>
  /// With no drift state wired, a foreign subscription on an owned inbox is still reported in the
  /// error log, and provisioning carries on to create this service's own subscription.
  /// </summary>
  [Test]
  public async Task ProvisionManifest_NoDriftState_LogsTheDriftAndStillProvisionsAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox.owned" },
      ExistingSubscriptions = { ("inbox.owned", "other-service") }
    };
    var logger = new RecordingLogger<ServiceBusInfrastructureProvisioner>();
    var provisioner = new ServiceBusInfrastructureProvisioner(adminClient, logger, driftState: null);
    var manifest = new TopologyManifest("coverage-service", [], [
      new InboxSubscription("inbox.owned", Metadata: new Dictionary<string, object> {
        [NamespaceInboxStrategy.OwnedCommandInboxMetadataKey] = true
      })
    ]);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(logger.Contains(LogLevel.Error, "Topology ownership drift")).IsTrue();
    var ownName = ServiceBusSubscriptionNameHelper.GenerateSubscriptionName("coverage-service", "inbox.owned");
    await Assert.That(adminClient.CreatedSubscriptions.Select(s => s.Subscription)).Contains(ownName);
  }

  // ========================================
  // LIVENESS SWEEP
  // ========================================

  /// <summary>
  /// A subscription that received recently is skipped without touching the management plane; only
  /// the one that has been silent past the threshold is probed.
  /// </summary>
  [Test]
  public async Task ProbeAsync_RecentlyActiveSubscription_IsNotProbedAsync() {
    var time = new FakeTimeProvider();
    var probed = new List<string>();
    await using var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions { ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30) },
      (topic, subscription, _) => { probed.Add($"{topic}/{subscription}"); return Task.FromResult(0L); },
      _ => Task.CompletedTask,
      time,
      NullLogger.Instance);
    watchdog.Track("a-topic", "silent");
    time.Advance(TimeSpan.FromMinutes(1));
    watchdog.Track("b-topic", "fresh");

    await watchdog.ProbeAsync();

    await Assert.That(probed).IsEquivalentTo(["a-topic/silent"]);
  }

  // ========================================
  // RECEIVE DECISION
  // ========================================

  /// <summary>
  /// An envelope whose payload is null has no type to look a consumer up by, so the consumer filter
  /// is not consulted and the envelope proceeds to processing, where the handler sees it as it is.
  /// </summary>
  [Test]
  public async Task Decide_EnvelopeWithNullPayload_SkipsTheConsumerFilterAndProcessesAsync() {
    var options = AsbTransportTestData.CombinedOptions;
    var typeInfo = options.GetTypeInfo(typeof(MessageEnvelope<TestMessage>));
    var node = JsonNode.Parse(JsonSerializer.Serialize(AsbTransportTestData.CreateEnvelope(), typeInfo))!.AsObject();
    node["p"] = null;
    var properties = new Dictionary<string, object> {
      [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = typeof(MessageEnvelope<TestMessage>).AssemblyQualifiedName!
    };

    var decision = new AsbReceiveDecisionMaker().Decide(
      properties,
      node.ToJsonString(),
      (_, _) => typeInfo,
      options,
      isHandledLocally: _ => throw new InvalidOperationException("a payload-less envelope has no type to look up"));

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
    await Assert.That(decision.Envelope).IsNotNull();
    await Assert.That(decision.Envelope!.Payload).IsNull();
  }

  // ========================================
  // DI REGISTRATIONS
  // ========================================

  /// <summary>
  /// When the registered transport is not the Service Bus transport (a decorator or a replacement),
  /// the fleet drainer finds no Service Bus subscriptions to drain and does nothing, rather than
  /// failing a cast on every drain pass.
  /// </summary>
  [Test]
  public async Task FleetDrainerRegistration_NonServiceBusTransport_DrainsNothingAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(new ServiceBusClient(UNIT_CONNECTION_STRING));
    services.AddAzureServiceBusTransport(UNIT_CONNECTION_STRING);
    services.AddSingleton<ITransport>(new TestTransport(isInitialized: true));
    await using var provider = services.BuildServiceProvider();
    var fleet = provider.GetServices<ITransportDeadLetterDrainer>()
      .OfType<AzureServiceBusFleetDeadLetterDrainer>()
      .Single();

    var drained = await fleet.DrainDeadLetterQueueAsync(10);

    await Assert.That(drained).IsEqualTo(0);
  }

  /// <summary>
  /// A namespace-routed host with namespace bindings but no receptor registry handles nothing, so
  /// it subscribes on the default namespace only and opens no receiver in a class namespace.
  /// </summary>
  [Test]
  public async Task SubscribeBatchAsync_NoReceptorRegistry_SubscribesOnDefaultOnlyAsync() {
    var tagOptions = new Whizbang.Core.Tags.TagOptions();
    tagOptions.RouteNamespace("bulk-import", "bulk");
    var resolver = new Whizbang.Core.Tags.TransportNamespaceResolver(
      tagOptions, () => [_tagRegistration(typeof(BulkImportRequested), "bulk-import")]);
    var defaultClient = new RaisableServiceBusClient("default.servicebus.windows.net");
    var factory = new RecordingNamespaceClientFactory();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ServiceBusClient>(defaultClient);
    services.AddSingleton<IServiceBusNamespaceClientFactory>(factory);
    services.AddSingleton(resolver);
    services.AddAzureServiceBusTransport(
      new Dictionary<string, string> {
        [TransportNamespaces.DefaultKey] = EMULATOR_CONNECTION_STRING,
        ["bulk"] = SECOND_EMULATOR_CONNECTION_STRING
      },
      o => {
        o.AutoProvisionInfrastructure = false;
        o.EnableSessions = false;
      });
    // The transport registration defaults a registry query; remove it so the host genuinely has
    // no receptor registry, which is the shape this test is named for.
    services.RemoveAll<Whizbang.Core.Messaging.IReceptorRegistryQuery>();
    await using var provider = services.BuildServiceProvider();
    var router = (NamespaceRoutingTransport)provider.GetRequiredService<ITransport>();
    await router.InitializeAsync();

    using var subscription = await router.SubscribeBatchAsync(
      (_, _) => Task.CompletedTask, new TransportDestination("orders", "svc-orders"), new TransportBatchOptions());

    await Assert.That(defaultClient.CreatedProcessors.Count).IsEqualTo(1);
    await Assert.That(factory.Clients["bulk"].CreatedProcessors).IsEmpty()
      .Because("with no receptor registry nothing is handled, so no class namespace is consumed from");
  }

  /// <summary>
  /// The provisioner registered on its own (no transport registration, so no options container)
  /// still resolves, and provisions with the default transport settings.
  /// </summary>
  [Test]
  public async Task ProvisionerRegistration_WithoutOptionsContainer_ProvisionsWithDefaultSettingsAsync() {
    var adminClient = new RecordingProvisioningAdminClient();
    var services = new ServiceCollection();
    services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    services.AddAzureServiceBusProvisioner(CONNECTION_STRING);
    services.AddSingleton<IServiceBusAdminClient>(adminClient);
    await using var provider = services.BuildServiceProvider();
    var provisioner = provider.GetRequiredService<IInfrastructureProvisioner>();

    await provisioner.ProvisionManifestAsync(
      new TopologyManifest("coverage-service", [], [new InboxSubscription("inbox.coverage")]));

    var defaults = new AzureServiceBusOptions();
    var (_, _, requiresSession, maxDeliveryCount) = adminClient.CreatedSubscriptions.Single();
    await Assert.That(maxDeliveryCount).IsEqualTo(defaults.MaxDeliveryAttempts);
    await Assert.That(requiresSession).IsEqualTo(defaults.EnableSessions);
  }

  private static Whizbang.Core.Tags.MessageTagRegistration _tagRegistration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(Whizbang.Core.Attributes.SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new Whizbang.Core.Attributes.SignalTagAttribute { Tag = tag }
  };

  private sealed record BulkImportRequested(string BatchId);
}

/// <summary>A client that hands every receiver request the same scripted dead-letter receiver.</summary>
sealed file class DeadLetterQueueClient : ServiceBusClient {
  public ScriptedDeadLetterReceiver Receiver { get; } = new();

  public override ServiceBusReceiver CreateReceiver(
    string topicName, string subscriptionName, ServiceBusReceiverOptions options) => Receiver;
}

/// <summary>Serves queued batches, then empty pages; records what was abandoned.</summary>
sealed file class ScriptedDeadLetterReceiver : ServiceBusReceiver {
  public Queue<IReadOnlyList<ServiceBusReceivedMessage>> Batches { get; } = new();
  public List<string> Abandoned { get; } = [];

  public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
    int maxMessages, TimeSpan? maxWaitTime = default, CancellationToken cancellationToken = default) =>
    Task.FromResult(Batches.Count == 0 ? (IReadOnlyList<ServiceBusReceivedMessage>)[] : Batches.Dequeue());

  public override Task AbandonMessageAsync(
    ServiceBusReceivedMessage message,
    IDictionary<string, object>? propertiesToModify = null,
    CancellationToken cancellationToken = default) {
    Abandoned.Add(message.MessageId);
    return Task.CompletedTask;
  }

  [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusReceiver.DisposeAsync() calls CloseAsync which fails on mocking-constructor instances; this fake holds nothing")]
  public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hands each non-default namespace its own recording client, keyed by namespace.</summary>
sealed file class RecordingNamespaceClientFactory : IServiceBusNamespaceClientFactory {
  public Dictionary<string, RaisableServiceBusClient> Clients { get; } = new(StringComparer.Ordinal);

  public ServiceBusClient CreateClient(string namespaceKey, string connectionString, AzureServiceBusOptions options) {
    var client = new RaisableServiceBusClient($"{namespaceKey}.servicebus.windows.net");
    Clients[namespaceKey] = client;
    return client;
  }

  public IServiceBusAdminClient? CreateAdminClient(
    string namespaceKey, string connectionString, AzureServiceBusOptions options) => null;
}

/// <summary>A logger with every level enabled that records level and rendered message.</summary>
sealed file class RecordingLogger<T> : ILogger<T> {
  private readonly List<(LogLevel Level, string Message)> _entries = [];

  public bool Contains(LogLevel level, string fragment) =>
    _entries.Exists(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
    _entries.Add((logLevel, formatter(state, exception)));
}
