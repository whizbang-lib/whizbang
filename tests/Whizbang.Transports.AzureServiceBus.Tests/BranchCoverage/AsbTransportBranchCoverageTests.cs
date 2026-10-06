// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests.BranchCoverage;

/// <summary>
/// Branch outcomes of <see cref="AzureServiceBusTransport"/> that no other suite takes: the trace
/// tags written when a tracer is listening, the initialization verification paths, the
/// correlation-filter outcomes as a tracer sees them, a destination with no routing key, metadata
/// values AMQP cannot carry natively, a perspective-only receive filter, a recovery with no handler,
/// and a backlog peek over an empty entity.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AzureServiceBusTransport.cs</code-under-test>
[Timeout(10_000)]
public class AsbTransportBranchCoverageTests {
  private const string TOPIC = "coverage-topic";
  private const string SUBSCRIPTION = "coverage-sub";

  // ========================================
  // TRACE TAGS (activity non-null)
  // ========================================

  /// <summary>
  /// With a tracer listening, construction records what the transport is: the transport type,
  /// whether it targets an emulator, whether it can provision, and whether it will. A missing tag
  /// leaves an operator unable to tell from a trace which transport configuration was running.
  /// </summary>
  [Test]
  public async Task Constructor_WithAListeningTracer_TagsTheTransportConfigurationAsync() {
    using var capture = new ActivityCapture();

    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = true },
      NullLogger<AzureServiceBusTransport>.Instance,
      new RecordingProvisioningAdminClient());

    var activity = capture.Single("AzureServiceBusTransport.Initialize");
    await Assert.That(transport.IsInitialized).IsFalse()
      .Because("construction only describes the transport; connecting is InitializeAsync's job");
    await Assert.That(activity.GetTagItem("transport.type")).IsEqualTo("AzureServiceBus");
    await Assert.That(activity.GetTagItem("transport.emulator")).IsEqualTo(false);
    await Assert.That(activity.GetTagItem("transport.admin_client_available")).IsEqualTo(true);
    await Assert.That(activity.GetTagItem("transport.auto_provision")).IsEqualTo(true);
  }

  /// <summary>An emulator endpoint skips the management-plane check and says so on the trace.</summary>
  [Test]
  public async Task InitializeAsync_EmulatorEndpoint_TagsTheEmulatorSkipAsync() {
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient("localhost"),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions(),
      NullLogger<AzureServiceBusTransport>.Instance);
    using var capture = new ActivityCapture();

    await transport.InitializeAsync();

    var activity = capture.Single("AzureServiceBusTransport.Initialize");
    await Assert.That(transport.IsInitialized).IsTrue();
    await Assert.That(activity.GetTagItem("transport.initialized")).IsEqualTo(true);
    await Assert.That(activity.GetTagItem("transport.verification_method")).IsEqualTo("emulator_skip");
  }

  /// <summary>With an administration client the namespace is read, and the trace names that check.</summary>
  [Test]
  public async Task InitializeAsync_WithAdminClient_TagsTheAdminApiVerificationAsync() {
    var adminClient = new RecordingProvisioningAdminClient();
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions(),
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient);
    using var capture = new ActivityCapture();

    await transport.InitializeAsync();

    var activity = capture.Single("AzureServiceBusTransport.Initialize");
    await Assert.That(adminClient.ManagementOpCount).IsEqualTo(1)
      .Because("the namespace read is the connectivity proof on a production endpoint");
    await Assert.That(activity.GetTagItem("transport.verification_method")).IsEqualTo("admin_api");
  }

  /// <summary>Without an administration client only the client's open state is checked; the trace says so.</summary>
  [Test]
  public async Task InitializeAsync_WithoutAdminClient_TagsTheClientOpenCheckAsync() {
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions(),
      NullLogger<AzureServiceBusTransport>.Instance);
    using var capture = new ActivityCapture();

    await transport.InitializeAsync();

    var activity = capture.Single("AzureServiceBusTransport.Initialize");
    await Assert.That(activity.GetTagItem("transport.verification_method")).IsEqualTo("client_open_check");
  }

  /// <summary>
  /// A namespace that cannot be read fails initialization, and the trace carries the error status
  /// with the cause, so the failed span is findable without the log.
  /// </summary>
  [Test]
  public async Task InitializeAsync_NamespaceUnreachable_MarksTheTraceAsErrorAsync() {
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions(),
      NullLogger<AzureServiceBusTransport>.Instance,
      new FailingAdminClient("namespace unreachable"));
    using var capture = new ActivityCapture();

    await Assert.That(() => transport.InitializeAsync()).Throws<InvalidOperationException>();

    var activity = capture.Single("AzureServiceBusTransport.Initialize");
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(activity.StatusDescription).IsEqualTo("namespace unreachable");
    await Assert.That(transport.IsInitialized).IsFalse();
  }

  /// <summary>A missing subscription is reported on the trace and no rule is touched.</summary>
  [Test]
  public async Task ApplyCorrelationFilterAsync_SubscriptionMissing_TagsItAndTouchesNoRuleAsync() {
    var adminClient = new RecordingProvisioningAdminClient();
    var transport = _transportWith(adminClient);
    using var capture = new ActivityCapture();

    await transport.ApplyCorrelationFilterAsync(TOPIC, SUBSCRIPTION, "svc-a", CancellationToken.None);

    var activity = capture.Single("ApplyCorrelationFilter");
    await Assert.That(activity.GetTagItem("servicebus.topic")).IsEqualTo(TOPIC);
    await Assert.That(activity.GetTagItem("servicebus.subscription")).IsEqualTo(SUBSCRIPTION);
    await Assert.That(activity.GetTagItem("servicebus.filter_type")).IsEqualTo("CorrelationRuleFilter");
    await Assert.That(activity.GetTagItem("servicebus.destination")).IsEqualTo("svc-a");
    await Assert.That(activity.GetTagItem("servicebus.subscription_exists")).IsEqualTo(false);
    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  /// <summary>
  /// A filter already targeting the destination is left alone (a delete-create would briefly leave
  /// the subscription unfiltered), and the trace records that nothing changed.
  /// </summary>
  [Test]
  public async Task ApplyCorrelationFilterAsync_FilterAlreadyCorrect_TagsUnchangedAndKeepsTheRuleAsync() {
    var existing = new CorrelationRuleFilter();
    existing.ApplicationProperties["Destination"] = "svc-a";
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingSubscriptions = { (TOPIC, SUBSCRIPTION) },
      ExistingRules = { ServiceBusModelFactory.RuleProperties("DestinationFilter", existing) }
    };
    var transport = _transportWith(adminClient);
    using var capture = new ActivityCapture();

    await transport.ApplyCorrelationFilterAsync(TOPIC, SUBSCRIPTION, "svc-a", CancellationToken.None);

    var activity = capture.Single("ApplyCorrelationFilter");
    await Assert.That(activity.GetTagItem("servicebus.subscription_exists")).IsEqualTo(true);
    await Assert.That(activity.GetTagItem("servicebus.rule_unchanged")).IsEqualTo(true);
    await Assert.That(adminClient.DeletedRules).IsEmpty();
    await Assert.That(adminClient.CreatedRules).IsEmpty();
  }

  /// <summary>Replacing the match-all rule is counted on the trace, and the new rule is recorded as created.</summary>
  [Test]
  public async Task ApplyCorrelationFilterAsync_DefaultRulePresent_TagsTheDeletionAndCreationAsync() {
    var adminClient = new RecordingProvisioningAdminClient {
      ExistingSubscriptions = { (TOPIC, SUBSCRIPTION) },
      ExistingRules = { ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()) }
    };
    var transport = _transportWith(adminClient);
    using var capture = new ActivityCapture();

    await transport.ApplyCorrelationFilterAsync(TOPIC, SUBSCRIPTION, "svc-a", CancellationToken.None);

    var activity = capture.Single("ApplyCorrelationFilter");
    await Assert.That(activity.GetTagItem("servicebus.rules_deleted")).IsEqualTo(1);
    await Assert.That(activity.GetTagItem("servicebus.rule_created")).IsEqualTo(true);
    await Assert.That(adminClient.CreatedRules).Count().IsEqualTo(1);
  }

  /// <summary>A management-plane failure is rethrown, and the trace carries the error status and cause.</summary>
  [Test]
  public async Task ApplyCorrelationFilterAsync_ManagementPlaneFails_MarksTheTraceAsErrorAndRethrowsAsync() {
    var transport = _transportWith(new FailingAdminClient("management plane down"));
    using var capture = new ActivityCapture();

    await Assert.That(() => transport.ApplyCorrelationFilterAsync(TOPIC, SUBSCRIPTION, "svc-a", CancellationToken.None))
      .Throws<InvalidOperationException>();

    var activity = capture.Single("ApplyCorrelationFilter");
    await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    await Assert.That(activity.StatusDescription).IsEqualTo("management plane down");
  }

  // ========================================
  // DESTINATION WITH NO ROUTING KEY
  // ========================================

  /// <summary>
  /// A destination with no routing key subscribes under the configured default subscription name,
  /// and the startup log names that subscription, not an empty one.
  /// </summary>
  [Test]
  public async Task SubscribeAsync_NoRoutingKey_StartsAndLogsTheDefaultSubscriptionAsync() {
    var logger = new RecordingTransportLogger();
    var client = new RaisableServiceBusClient();
    var transport = _receivingTransport(client, logger, enableSessions: false);

    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC));

    await Assert.That(client.CreatedProcessors.ConvertAll(p => (p.Topic, p.Subscription)))
      .IsEquivalentTo([(TOPIC, "fallback-sub")]);
    await Assert.That(logger.Contains(LogLevel.Information, $"Started subscription to {TOPIC}/fallback-sub")).IsTrue();
  }

  /// <summary>
  /// A message dropped on a routing-key-less subscription is attributed to the default subscription
  /// in the drop warning, so the drop can be traced to the entity it came from.
  /// </summary>
  [Test]
  public async Task ProcessMessage_NoRoutingKey_AttributesTheDropToTheDefaultSubscriptionAsync() {
    var logger = new RecordingTransportLogger();
    var client = new RaisableServiceBusClient();
    var transport = _receivingTransport(client, logger, enableSessions: false);
    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC));
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.RawMessage("{}", "Unknown.Contracts.Nothing, Unknown.Contracts"), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("an unresolvable type is acked and dropped, not redelivered");
    await Assert.That(logger.Contains(LogLevel.Warning, "Subscription=fallback-sub")).IsTrue();
  }

  /// <summary>
  /// The session pipeline attributes a dead-lettered message on a routing-key-less subscription to
  /// the default subscription too.
  /// </summary>
  [Test]
  public async Task ProcessSessionMessage_NoRoutingKey_AttributesTheDeadLetterToTheDefaultSubscriptionAsync() {
    var logger = new RecordingTransportLogger();
    var client = new RaisableServiceBusClient();
    var transport = _receivingTransport(client, logger, enableSessions: true);
    await transport.SubscribeAsync(_noopHandler, new TransportDestination(TOPIC));
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(AsbTransportTestData.SessionArgs(
      AsbTransportTestData.RawMessage("{}", envelopeTypeName: null), receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1)
      .Because("a message without an envelope type can never be read and is dead-lettered");
    await Assert.That(logger.Contains(LogLevel.Warning, $"from {TOPIC}/fallback-sub")).IsTrue();
  }

  /// <summary>
  /// The session occupancy clock of a routing-key-less destination is keyed by the default
  /// subscription name: a session accepted through it and completed through a destination that
  /// names the default subscription explicitly is the same session, so its budget is honored.
  /// </summary>
  [Test]
  public async Task RotateSessionIfPastBudget_NoRoutingKey_SharesTheDefaultSubscriptionClockAsync() {
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5) },
      NullLogger<AzureServiceBusTransport>.Instance);
    var t0 = DateTimeOffset.UtcNow;
    var args = new ReleaseCountingSessionArgs();

    transport.OnSessionInitializing(new TransportDestination(TOPIC), ReleaseCountingSessionArgs.SESSION_ID, t0);
    transport.RotateSessionIfPastBudget(
      args, new TransportDestination(TOPIC, "default"), t0 + TimeSpan.FromMinutes(5));

    await Assert.That(args.Releases).IsEqualTo(1)
      .Because("the accept-time stamp recorded without a routing key must be the one the completion reads");
  }

  // ========================================
  // PUBLISH METADATA CONVERSION
  // ========================================

  /// <summary>
  /// A null stream id yields a streamless session key rather than a parsed stream, and a number too
  /// large for a double travels as its JSON text instead of turning into infinity.
  /// </summary>
  [Test]
  public async Task PublishAsync_NullStreamIdAndOverflowingNumber_UsesStreamlessSessionAndRawTextAsync() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false },
      NullLogger<AzureServiceBusTransport>.Instance);
    var envelope = AsbTransportTestData.CreateEnvelope();
    var metadata = new Dictionary<string, JsonElement> {
      ["StreamId"] = AsbTransportTestData.Json("null"),
      ["huge"] = AsbTransportTestData.Json("1e400")
    };

    await transport.PublishAsync(envelope, new TransportDestination(TOPIC, "orders.created", metadata));

    var sent = client.LastSender!.Sent.Single();
    await Assert.That(sent.SessionId).IsEqualTo(AsbSessionKey.For(null, envelope.MessageId.Value));
    await Assert.That(sent.ApplicationProperties["huge"]).IsEqualTo("1e400");
    await Assert.That(sent.ApplicationProperties["StreamId"]).IsNull();
  }

  // ========================================
  // RECEIVE FILTER WITH RECEPTORS ONLY
  // ========================================

  /// <summary>
  /// With a receptor registry and no perspective registry, a payload no receptor handles is dropped
  /// at receive: the missing perspective registry counts as "nothing applies it", not as an error.
  /// </summary>
  [Test]
  public async Task ProcessMessage_ReceptorRegistryOnly_UnhandledPayloadIsAckedAndDroppedAsync() {
    var client = new RaisableServiceBusClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { AutoProvisionInfrastructure = false, EnableSessions = false },
      NullLogger<AzureServiceBusTransport>.Instance,
      receptorRegistry: new StubReceptorRegistry(null));
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      new TransportDestination(TOPIC, SUBSCRIPTION));
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(AsbTransportTestData.MessageArgs(
      AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(handlerInvoked).IsFalse();
    await Assert.That(receiver.Completed).Count().IsEqualTo(1);
  }

  // ========================================
  // LIVENESS RECOVERY AND BACKLOG PEEK
  // ========================================

  /// <summary>
  /// A stalled subscription with no recovery handler set still completes the recovery step (the
  /// silence windows reset, so the next sweep does not probe again) and logs no handler invocation.
  /// </summary>
  [Test]
  public async Task LivenessRecovery_NoRecoveryHandler_ResetsWindowsWithoutInvokingAHandlerAsync() {
    var logger = new RecordingTransportLogger();
    var time = new FakeTimeProvider();
    var adminClient = new RecordingProvisioningAdminClient { ActiveMessageCountResult = 5 };
    var transport = new AzureServiceBusTransport(
      new RaisableServiceBusClient(),
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions { ReceiveLivenessSilenceThreshold = TimeSpan.FromSeconds(30) },
      logger,
      adminClient,
      timeProvider: time);
    var watchdog = transport.LivenessWatchdog!;
    watchdog.Track(TOPIC, SUBSCRIPTION);
    time.Advance(TimeSpan.FromMinutes(1));

    await watchdog.ProbeAsync();
    var opsAfterRecovery = adminClient.ManagementOpCount;
    await watchdog.ProbeAsync();

    await Assert.That(opsAfterRecovery).IsEqualTo(1)
      .Because("the stalled subscription was probed once and judged stalled");
    await Assert.That(adminClient.ManagementOpCount).IsEqualTo(1)
      .Because("the recovery reset every silence window, so the next sweep has nothing to probe");
    await Assert.That(logger.Contains(LogLevel.Information, "invoking recovery handler")).IsFalse();
  }

  /// <summary>
  /// An entity whose head peek finds nothing reports its depth with no age, rather than an age
  /// computed from a missing message.
  /// </summary>
  [Test]
  public async Task PeekBacklogsAsync_EmptyEntityHead_ReportsDepthWithNoAgeAsync() {
    var client = new HeadPeekClient();
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      new AzureServiceBusOptions(),
      NullLogger<AzureServiceBusTransport>.Instance,
      new RecordingProvisioningAdminClient { ActiveMessageCountResult = 3 });
    transport.LivenessWatchdog!.Track(TOPIC, SUBSCRIPTION);

    var samples = await transport.PeekBacklogsAsync(CancellationToken.None);

    await Assert.That(samples).Count().IsEqualTo(1);
    await Assert.That(samples[0].Entity).IsEqualTo($"{TOPIC}/{SUBSCRIPTION}");
    await Assert.That(samples[0].Depth).IsEqualTo(3L);
    await Assert.That(samples[0].OldestAge).IsNull();
    await Assert.That(client.Receiver.PeekCalls).IsEqualTo(1)
      .Because("the age comes from a real head peek when no probe seam is installed");
  }

  // ========================================
  // HELPERS
  // ========================================

  private static Task _noopHandler(IMessageEnvelope envelope, string? envelopeType, CancellationToken cancellationToken) =>
    Task.CompletedTask;

  private static AzureServiceBusTransport _transportWith(IServiceBusAdminClient adminClient) =>
    new(new RaisableServiceBusClient(),
        AsbTransportTestData.CombinedOptions,
        new AzureServiceBusOptions(),
        NullLogger<AzureServiceBusTransport>.Instance,
        adminClient);

  private static AzureServiceBusTransport _receivingTransport(
      RaisableServiceBusClient client, RecordingTransportLogger logger, bool enableSessions) =>
    new(client,
        AsbTransportTestData.CombinedOptions,
        new AzureServiceBusOptions {
          AutoProvisionInfrastructure = false,
          EnableSessions = enableSessions,
          DefaultSubscriptionName = "fallback-sub"
        },
        logger);
}

/// <summary>
/// Listens to the framework's activity sources for the duration of one test and keeps only the
/// activities started under this test's own root, so activities from tests running in parallel
/// never satisfy an assertion here.
/// </summary>
file sealed class ActivityCapture : IDisposable {
  private readonly ActivityListener _listener;
  private readonly Activity _root;
  private readonly ConcurrentQueue<Activity> _stopped = new();

  public ActivityCapture() {
    _listener = new ActivityListener {
      ShouldListenTo = static source => source.Name is "Whizbang.Transport" or "Whizbang.Hosting" or "Whizbang.Tracing",
      Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = _stopped.Enqueue
    };
    ActivitySource.AddActivityListener(_listener);
    _root = WhizbangActivitySource.Tracing.StartActivity("branch-coverage-root")
      ?? throw new InvalidOperationException("The listener did not sample the root activity.");
  }

  public Activity Single(string operationName) =>
    _stopped.Single(a => a.OperationName == operationName && a.TraceId == _root.TraceId && a != _root);

  public void Dispose() {
    _root.Dispose();
    _listener.Dispose();
  }
}

/// <summary>An administration client whose every operation fails with the given message.</summary>
file sealed class FailingAdminClient(string message) : IServiceBusAdminClient {
  private Exception _failure() => new InvalidOperationException(message);

  public Task<NamespaceProperties> GetNamespacePropertiesAsync(CancellationToken cancellationToken = default) =>
    Task.FromException<NamespaceProperties>(_failure());

  public Task<bool> TopicExistsAsync(string topicName, CancellationToken cancellationToken = default) =>
    Task.FromException<bool>(_failure());

  public Task CreateTopicAsync(string topicName, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task<bool> SubscriptionExistsAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromException<bool>(_failure());

  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task CreateSubscriptionAsync(string topicName, string subscriptionName, bool requiresSession, int maxDeliveryCount, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task UpdateSubscriptionLockDurationAsync(string topicName, string subscriptionName, TimeSpan lockDuration, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task<SubscriptionProperties> GetSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromException<SubscriptionProperties>(_failure());

  public Task DeleteSubscriptionAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task<long> GetSubscriptionActiveMessageCountAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    Task.FromException<long>(_failure());

  public IAsyncEnumerable<RuleProperties> GetRulesAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default) =>
    throw _failure();

  public Task DeleteRuleAsync(string topicName, string subscriptionName, string ruleName, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());

  public Task CreateRuleAsync(string topicName, string subscriptionName, CreateRuleOptions options, CancellationToken cancellationToken = default) =>
    Task.FromException(_failure());
}

/// <summary>Session message args that count <c>ReleaseSession</c> calls instead of reaching a broker.</summary>
file sealed class ReleaseCountingSessionArgs() : ProcessSessionMessageEventArgs(
    ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: SESSION_ID),
    new FixedSessionReceiver(),
    CancellationToken.None) {
  public const string SESSION_ID = "stream-coverage";

  public int Releases { get; private set; }

  public override void ReleaseSession() => Releases++;

  private sealed class FixedSessionReceiver : ServiceBusSessionReceiver {
    public override string SessionId => SESSION_ID;
  }
}

/// <summary>A client whose receivers find an empty entity head.</summary>
file sealed class HeadPeekClient : ServiceBusClient {
  public EmptyHeadReceiver Receiver { get; } = new();

  public override string FullyQualifiedNamespace => "peek.servicebus.windows.net";

  public override bool IsClosed => false;

  public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName) => Receiver;
}

/// <summary>A receiver whose head peek returns no message.</summary>
file sealed class EmptyHeadReceiver : ServiceBusReceiver {
  public int PeekCalls { get; private set; }

  public override Task<ServiceBusReceivedMessage> PeekMessageAsync(long? fromSequenceNumber = default, CancellationToken cancellationToken = default) {
    PeekCalls++;
    return Task.FromResult<ServiceBusReceivedMessage>(null!);
  }

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Base ServiceBusReceiver.DisposeAsync() calls CloseAsync which fails on mocking-constructor instances; this fake holds nothing")]
  public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
