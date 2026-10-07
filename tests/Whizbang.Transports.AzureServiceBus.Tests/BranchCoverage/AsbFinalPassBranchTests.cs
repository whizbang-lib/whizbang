// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests.BranchCoverage;

/// <summary>
/// Final-pass branch coverage for the Azure Service Bus package: a direct Service Bus failure in
/// the connection retry, collaborators that complete asynchronously (the recovery handler, the
/// management plane while a destination filter is applied, and the liveness backlog probe), and an
/// inbox subscription whose metadata does not carry the ownership marker.
/// </summary>
/// <remarks>
/// Every existing test of these paths uses collaborators that complete synchronously, so the
/// awaits inside their <c>try</c> blocks never suspended and the resumption path after a real
/// asynchronous completion was never taken. The fakes here complete only after yielding, or only
/// when the test releases them, which also pins that each caller waits for the collaborator.
/// </remarks>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusConnectionRetry.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ReceiveLivenessWatchdog.cs</code-under-test>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/ServiceBusInfrastructureProvisioner.cs</code-under-test>
[Timeout(10_000)]
public class AsbFinalPassBranchTests {
  private const string CONNECTION_STRING =
    "Endpoint=sb://localhost:1;SharedAccessKeyName=probe;SharedAccessKey=cHJvYmVrZXk=";
  private const string TOPIC = "orders-topic";
  private const string SUBSCRIPTION = "unit-sub";

  // ========================================
  // CONNECTION RETRY
  // ========================================

  /// <summary>
  /// A Service Bus failure that is not wrapped in an aggregate is retried directly by the filter's
  /// first test, without consulting the transient classifier.
  /// </summary>
  [Test]
  public async Task CreateClientWithRetryAsync_DirectServiceBusFailure_RetriesUntilReachableAsync(CancellationToken cancellationToken) {
    var attempts = 0;
    var retry = new AzureServiceBusConnectionRetry(new AzureServiceBusOptions { InitialRetryDelay = TimeSpan.Zero }) {
      VerifyNamespaceReachableAsync = (_, _) => {
        attempts++;
        return attempts == 1
          ? Task.FromException(new ServiceBusException("namespace busy", ServiceBusFailureReason.ServiceBusy))
          : Task.CompletedTask;
      }
    };

    await using var client = await retry.CreateClientWithRetryAsync(CONNECTION_STRING, cancellationToken);

    await Assert.That(client).IsNotNull();
    await Assert.That(attempts).IsEqualTo(2)
      .Because("a Service Bus failure is transient by definition, so the loop tried again");
  }

  // ========================================
  // RECOVERY HANDLER
  // ========================================

  /// <summary>
  /// A connection-level processor error waits for a recovery handler that completes later, so the
  /// error callback returns only once recovery has finished.
  /// </summary>
  [Test]
  public async Task ProcessorError_RecoveryHandlerCompletesAsynchronously_ErrorHandlingWaitsForItAsync() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, new TransportDestination("err-topic", "err-sub"));
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handlerFinished = false;
    transport.SetRecoveryHandler(async _ => {
      await gate.Task;
      handlerFinished = true;
    });

    var errorHandling = client.LastProcessor!.RaiseErrorAsync(new ProcessErrorEventArgs(
      new ServiceBusException("connection error", ServiceBusFailureReason.ServiceCommunicationProblem),
      ServiceBusErrorSource.Receive, "unit-test.servicebus.windows.net", "err-topic", CancellationToken.None));

    await Assert.That(errorHandling.IsCompleted).IsFalse()
      .Because("the error callback awaits recovery, so it cannot finish while the handler runs");
    gate.SetResult();
    await errorHandling;
    await Assert.That(handlerFinished).IsTrue();
  }

  // ========================================
  // DESTINATION FILTER OVER AN ASYNCHRONOUS MANAGEMENT PLANE
  // ========================================

  /// <summary>
  /// A destination filter is applied through a management plane whose every call completes
  /// asynchronously: subscribe waits for the rule to be written before it returns.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterOverAsyncAdminPlane_WritesTheRuleAsync() {
    var inner = _existingSubscriptionAdmin();
    var transport = _transportWith(new YieldingAdminClient(inner), NullLogger<AzureServiceBusTransport>.Instance);

    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _filteredDestination());

    var (_, _, ruleOptions) = inner.CreatedRules.Single(r => r.Options.Name == "DestinationFilter");
    var filter = (CorrelationRuleFilter)ruleOptions.Filter;
    await Assert.That(filter.ApplicationProperties["Destination"]).IsEqualTo("my-destination");
  }

  /// <summary>
  /// When the asynchronous management plane rejects the rule, subscribe logs the failure and
  /// proceeds without the filter instead of failing the subscription.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_DestinationFilterRejectedAsynchronously_ProceedsWithoutTheFilterAsync() {
    var inner = _existingSubscriptionAdmin();
    var logger = new RecordingTransportLogger();
    var client = new RaisableServiceBusClient();
    var transport = _transportWith(new YieldingAdminClient(inner) { FailRuleCreation = true }, logger, client);

    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _filteredDestination());

    await Assert.That(logger.Contains(LogLevel.Warning, "failed to apply, proceeding without filter")).IsTrue();
    await Assert.That(client.CreatedProcessors).Count().IsEqualTo(1)
      .Because("a filter failure must not cost the subscription its receiver");
  }

  // ========================================
  // LIVENESS SWEEP
  // ========================================

  /// <summary>
  /// A silent subscription whose backlog probe answers asynchronously with an empty backlog is
  /// healthy idle: the sweep re-baselines it, so the next sweep does not probe it again.
  /// </summary>
  [Test]
  public async Task ProbeAsync_AsyncEmptyBacklog_RebaselinesTheSilentSubscriptionAsync() {
    var time = new FakeTimeProvider();
    var probes = 0;
    var recoveries = 0;
    await using var watchdog = new ReceiveLivenessWatchdog(
      new AzureServiceBusOptions { ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30) },
      async (_, _, _) => {
        await Task.Yield();
        probes++;
        return 0L;
      },
      _ => {
        recoveries++;
        return Task.CompletedTask;
      },
      time,
      NullLogger.Instance);
    watchdog.Track("a-topic", "quiet");
    time.Advance(TimeSpan.FromMinutes(1));

    await watchdog.ProbeAsync();
    await watchdog.ProbeAsync();

    await Assert.That(probes).IsEqualTo(1)
      .Because("an empty backlog explains the silence, so the window restarts and the second sweep skips it");
    await Assert.That(recoveries).IsEqualTo(0);
  }

  // ========================================
  // OWNERSHIP MARKER
  // ========================================

  /// <summary>
  /// Metadata that carries other keys but no ownership marker is a shared entity: a foreign
  /// subscription on it is normal topology, not drift.
  /// </summary>
  [Test]
  public async Task ProvisionManifest_MetadataWithoutOwnershipMarker_IsNotDriftCheckedAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingTopics = { "inbox.shared" },
      ExistingSubscriptions = { ("inbox.shared", "other-service") }
    };
    var driftState = new TopologyDriftState();
    var provisioner = new ServiceBusInfrastructureProvisioner(
      adminClient, NullLogger<ServiceBusInfrastructureProvisioner>.Instance, driftState: driftState);
    var manifest = new TopologyManifest("coverage-service", [], [
      new InboxSubscription("inbox.shared", Metadata: new Dictionary<string, object> { ["unrelated"] = "value" })
    ]);

    await provisioner.ProvisionManifestAsync(manifest);

    await Assert.That(driftState.HasDrift).IsFalse();
  }

  // ========================================
  // HELPERS
  // ========================================

  private static RecordingProvisioningAdminClient _existingSubscriptionAdmin() => new() {
    ExistingTopics = { TOPIC },
    ExistingSubscriptions = { (TOPIC, SUBSCRIPTION) }
  };

  private static AzureServiceBusTransport _transportWith(
      IServiceBusAdminClient adminClient, ILogger<AzureServiceBusTransport> logger, RaisableServiceBusClient? client = null) =>
    new(
      client ?? new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = true, EnableSessions = false },
      logger,
      adminClient);

  private static TransportDestination _filteredDestination() {
    using var doc = JsonDocument.Parse("\"my-destination\"");
    var metadata = new Dictionary<string, JsonElement> { ["DestinationFilter"] = doc.RootElement.Clone() };
    return new TransportDestination(TOPIC, SUBSCRIPTION, metadata);
  }

  /// <summary>
  /// A management plane that yields before every call and then delegates, so each await on it
  /// completes asynchronously. Rule creation can be made to fail after the yield.
  /// </summary>
  private sealed class YieldingAdminClient(RecordingProvisioningAdminClient inner) : IServiceBusAdminClient {
    public bool FailRuleCreation { get; init; }

    public async Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) {
      await Task.Yield();
      return await inner.GetNamespacePropertiesAsync(cancellationToken);
    }

    public async Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      return await inner.TopicExistsAsync(topicName, cancellationToken);
    }

    public async Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.CreateTopicAsync(topicName, cancellationToken);
    }

    public async Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      return await inner.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken);
    }

    public async Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.CreateSubscriptionAsync(topicName, subscriptionName, maxDeliveryCount, lockDuration, cancellationToken);
    }

    public async Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.CreateSubscriptionAsync(topicName, subscriptionName, requiresSession, maxDeliveryCount, lockDuration, cancellationToken);
    }

    public async Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.UpdateSubscriptionLockDurationAsync(topicName, subscriptionName, lockDuration, cancellationToken);
    }

    public async Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      return await inner.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken);
    }

    public async Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.DeleteSubscriptionAsync(topicName, subscriptionName, cancellationToken);
    }

    public async Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      return await inner.GetSubscriptionActiveMessageCountAsync(topicName, subscriptionName, cancellationToken);
    }

    public IAsyncEnumerable<SubscriptionProperties> GetSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
      inner.GetSubscriptionsAsync(topicName, cancellationToken);

    public IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
      inner.GetRulesAsync(topicName, subscriptionName, cancellationToken);

    public async Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) {
      await Task.Yield();
      await inner.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken);
    }

    public async Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) {
      await Task.Yield();
      if (FailRuleCreation) {
        throw new InvalidOperationException("rule rejected by the management plane");
      }
      await inner.CreateRuleAsync(topicName, subscriptionName, options, cancellationToken);
    }
  }
}
