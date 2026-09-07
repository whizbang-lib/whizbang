#pragma warning disable CA1707 // Test method names can contain underscores

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Coverage-round-23 resilience tests: the adaptive-acceptor governor surviving a resize
/// failure and a sweep failure, the shared periodic-evaluation loop's start-once idempotency,
/// the namespace-throttle detached pause/resume (success and failed-resume paths, with the
/// EndPause finally always running), plus a handful of unrelated small gaps this round also
/// targets — the slice-2 perspective no-consumer filter with a non-empty-but-non-matching
/// registry, empty-SubscriberName subscription-name fallback, an unclassifiable JsonElement on
/// a published message, and the sender cache's double-checked-lock race.
///
/// No broker is used: the Azure SDK's mocking constructors are exercised via the shared
/// RaisableServiceBusClient / RaisableSessionProcessor doubles. Adaptive-acceptor timing runs
/// on FakeTimeProvider; the namespace-throttle pause genuinely delays in real time (the
/// production code calls the non-TimeProvider Task.Delay overload deliberately), so those two
/// tests take a few real seconds and use a generous class-level Timeout.
/// </summary>
[Timeout(30_000)]
public class AzureServiceBusTransportThrottleAndAdaptiveTests {
  // ========================================
  // ADAPTIVE ACCEPTORS — RESIZE / SWEEP RESILIENCE
  // ========================================

  private static readonly TimeSpan _window = TimeSpan.FromSeconds(30);

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, FakeTimeProvider Time, RecordingTransportLogger Logger)
      _createAdaptiveTransport() {
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      AcceptorFloor = 4,
      MaxConcurrentSessions = 200,
      AcceptorEvaluationInterval = _window,
    };
    var client = new RaisableServiceBusClient();
    var time = new FakeTimeProvider();
    var logger = new RecordingTransportLogger();
    var transport = new AzureServiceBusTransport(
      client, AsbTransportTestData.CombinedOptions, options, logger, timeProvider: time);
    return (transport, client, time, logger);
  }

  private static Task<ISubscription> _subscribeBatchAsync(AzureServiceBusTransport transport, string topic, string routingKey) =>
    transport.SubscribeBatchAsync(
      (batch, ct) => Task.CompletedTask,
      new TransportDestination(topic) { RoutingKey = routingKey },
      new TransportBatchOptions());

  private static ProcessSessionEventArgs _sessionEventArgs() =>
    new(new RecordingTransportSessionReceiver(), CancellationToken.None);

  private static async Task _raiseSessionInitializingAsync(RaisableSessionProcessor processor, int count) {
    for (var i = 0; i < count; i++) {
      await processor.RaiseSessionInitializingAsync(_sessionEventArgs());
    }
  }

  /// <summary>
  /// A resize failure on one governed subscription must not stop the SHARED periodic sweep
  /// from reaching every OTHER governed subscription in the same pass. Without the catch
  /// around UpdateConcurrency, one processor that happened to close mid-recovery would throw
  /// out of the sweep's foreach and silently freeze every OTHER governed pool on the transport
  /// at its current size too — not just the one that actually failed.
  /// </summary>
  [Test]
  public async Task AdaptiveAcceptors_OneRegistrationFailsToResize_SiblingRegistrationStillGrowsAsync() {
    var (transport, client, time, logger) = _createAdaptiveTransport();
    await transport.InitializeAsync();

    await _subscribeBatchAsync(transport, "inbox-a", "sub-a");
    var failing = client.LastSessionProcessor!;
    await _subscribeBatchAsync(transport, "inbox-b", "sub-b");
    var healthy = client.LastSessionProcessor!;

    // Stamp 100%-of-floor pressure on both pools at the same moment.
    await _raiseSessionInitializingAsync(failing, 4);
    await _raiseSessionInitializingAsync(healthy, 4);

    // Everything must be armed BEFORE the clock moves. Advancing the fake clock fires the
    // periodic tick synchronously, and it is the only thing that ever evaluates the elapsed
    // window — arm the failure or attach the listener afterwards and the one sweep this test
    // gets has already come and gone.
    //
    // The failing registration's processor is made to look stale: the SDK's real
    // UpdateConcurrency resolves through InnerProcessor before doing anything else, so this is
    // the seam a disposed or closed processor actually fails at. IsClosed is deliberately left
    // working, because the sweep checks it for every registration before resizing any of them.
    failing.ThrowFromInnerProcessor = new InvalidOperationException("processor closed");

    var healthyGrown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("inbox-b/sub-b", StringComparison.Ordinal)
          && message.Contains("concurrency ->", StringComparison.Ordinal)) {
        healthyGrown.TrySetResult();
      }
    };

    time.Advance(_window);

    // One sweep evaluates BOTH registrations; wait on the healthy pool's own growth log rather
    // than on the sweep call returning.
    await healthyGrown.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(logger.Contains(LogLevel.Warning, "Adaptive acceptors: failed to apply concurrency")).IsTrue()
      .Because("the failing registration's resize failure must still be logged, not swallowed silently");
    await Assert.That(logger.Contains(LogLevel.Warning, "inbox-a/sub-a")).IsTrue()
      .Because("a diagnostic that doesn't name the topic/subscription is useless during an incident");
    await Assert.That(healthy.MaxConcurrentSessions).IsEqualTo(8)
      .Because("registration B's resize must still succeed in the SAME sweep even though registration A's resize call raised — one stale processor must not silently freeze every other governed pool");
  }

  /// <summary>
  /// The shared periodic-evaluation loop is documented as idempotent — started once, on the
  /// first governed subscription. If a later governed subscription's start call ever failed to
  /// recognize the loop was already running (or, worse, spun up its own second loop), a pool
  /// whose occupancy never generates a session event would depend on whichever loop actually
  /// exists; this test proves the SECOND subscription's pool is still swept even though its own
  /// start call is the one that no-ops.
  /// </summary>
  [Test]
  public async Task AdaptiveAcceptors_SecondGovernedSubscription_SharesTheOnePeriodicLoopAsync() {
    var (transport, client, time, logger) = _createAdaptiveTransport();
    await transport.InitializeAsync();

    // First governed subscription starts the shared loop.
    await _subscribeBatchAsync(transport, "inbox-a", "sub-a");
    // Second governed subscription: _startAcceptorEvaluationLoop finds a loop already running
    // and returns without starting a duplicate.
    await _subscribeBatchAsync(transport, "inbox-b", "sub-b");
    var second = client.LastSessionProcessor!;

    var grown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("inbox-b/sub-b", StringComparison.Ordinal)
          && message.Contains("concurrency ->", StringComparison.Ordinal)) {
        grown.TrySetResult();
      }
    };

    // Saturate the SECOND subscription's pool and let only the periodic tick observe the
    // elapsed window — no further session churn on it at all.
    await _raiseSessionInitializingAsync(second, 4);
    time.Advance(_window);

    await grown.Task;
    await Assert.That(second.MaxConcurrentSessions).IsEqualTo(8)
      .Because("the second governed subscription must be swept by the SAME shared loop the first one started — a skipped or duplicated loop would leave it unattended");
  }

  /// <summary>
  /// If a sweep failure ever escaped the periodic loop's own try/catch, the background Task
  /// would fault and nothing awaits it except Dispose — the periodic tick would never run
  /// again, and every adaptive pool on the transport (not just the one whose closed-check
  /// failed) would freeze at its last size forever, with no further diagnostic once the fault
  /// happened.
  /// </summary>
  [Test]
  public async Task AdaptiveAcceptors_PeriodicSweepThrows_LoopSurvivesAndKeepsSweepingAsync() {
    var (transport, client, time, logger) = _createAdaptiveTransport();
    await transport.InitializeAsync();
    await _subscribeBatchAsync(transport, "inbox", "adaptive-sub");
    var processor = client.LastSessionProcessor!;

    // EvaluateAcceptorGovernors's own closed-processor check (RemoveAll) throws BEFORE any
    // registration is evaluated — a failure of the sweep itself, not of one registration's
    // resize (that path is covered separately above, via ThrowFromInnerProcessor / UpdateConcurrency).
    processor.IsClosedException = new InvalidOperationException("closed-check failed");

    var sweepFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("Adaptive acceptor evaluation sweep failed", StringComparison.Ordinal)) {
        sweepFailed.TrySetResult();
      }
    };

    time.Advance(_window);
    await sweepFailed.Task;

    // Clear the failure and prove the SAME loop still sweeps on a LATER tick — using only the
    // periodic tick again (no session event on the final evaluation) so growth can only be
    // explained by the loop still being alive.
    processor.IsClosedException = null;
    var grownAfterRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("Adaptive acceptors", StringComparison.Ordinal)
          && message.Contains("concurrency ->", StringComparison.Ordinal)) {
        grownAfterRecovery.TrySetResult();
      }
    };
    await _raiseSessionInitializingAsync(processor, 4);
    time.Advance(_window);

    await grownAfterRecovery.Task;
    await Assert.That(processor.MaxConcurrentSessions).IsEqualTo(8)
      .Because("one failed sweep must not kill the loop — every governed pool depends on the SAME loop to ever re-evaluate again");
  }

  // ========================================
  // NAMESPACE THROTTLE (ServiceBusy) — DETACHED PAUSE/RESUME
  // ========================================

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, RecordingTransportLogger Logger)
      _createSessionTransport() {
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = true,
      // Irrelevant to the throttle governor and would otherwise start real background timers
      // (real TimeProvider.System) that outlive the test.
      EnableAdaptiveAcceptors = false,
      EnableReceiveLivenessWatchdog = false,
    };
    var client = new RaisableServiceBusClient();
    var logger = new RecordingTransportLogger();
    var transport = new AzureServiceBusTransport(
      client, AsbTransportTestData.CombinedOptions, options, logger);
    return (transport, client, logger);
  }

  private static TransportDestination _throttleDestination() => new("inbox", "throttle-sub");

  private static ProcessErrorEventArgs _serviceBusyErrorArgs() =>
    new(new ServiceBusException("throttled", ServiceBusFailureReason.ServiceBusy),
      ServiceBusErrorSource.Receive, "unit-test.servicebus.windows.net", "inbox", CancellationToken.None);

  /// <summary>
  /// The detached resume completes on the thread pool after the error handler already
  /// returned — the handler returning proves nothing about whether the pause actually ran or
  /// whether the processor came back. If the resume silently never ran (or ran but the
  /// "Resumed" diagnostic never fired), an operator watching logs during a live throttle
  /// incident would have no way to tell the consumer ever stopped accepting sessions, let alone
  /// that it is accepting them again.
  /// </summary>
  [Test]
  public async Task NamespaceThrottle_ServiceBusy_PausesThenResumesTheSessionProcessorAsync() {
    var (transport, client, logger) = _createSessionTransport();
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _throttleDestination());
    var processor = client.LastSessionProcessor!;

    var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    processor.Started += () => {
      // Call #1 was the initial subscribe start; call #2 is the detached pause's resume.
      if (processor.StartProcessingAsyncCallCount >= 2) {
        resumed.TrySetResult();
      }
    };

    await processor.RaiseErrorAsync(_serviceBusyErrorArgs());

    // The pause runs detached (stop -> real delay -> start) — wait for the SECOND
    // StartProcessingAsync, never a fixed sleep.
    await resumed.Task;

    await Assert.That(processor.StopProcessingAsyncCallCount).IsEqualTo(1)
      .Because("shedding accept pressure is the whole point of the pause");
    await Assert.That(logger.Contains(LogLevel.Warning, "Namespace throttled (ServiceBusy)")).IsTrue()
      .Because("the pause must be visible to an operator, not silent");
    await Assert.That(logger.Contains(LogLevel.Warning, "inbox/throttle-sub")).IsTrue()
      .Because("a throttle warning that doesn't name the topic/subscription is useless during an incident");
    await Assert.That(logger.Contains(LogLevel.Information, "Resumed Service Bus processor for inbox/throttle-sub after throttle pause")).IsTrue()
      .Because("the resume confirmation is the only operator-visible proof the detached pause actually completed and the processor is accepting sessions again");
  }

  /// <summary>
  /// The finally is the only thing standing between a failed resume and a transport that has
  /// silently and permanently stopped throttling itself: if EndPause never ran, TryBeginPause
  /// would return false forever, and no FUTURE namespace throttle would ever get a pause again
  /// — the accept loop would just keep retrying into an already-throttled namespace forever.
  /// </summary>
  [Test]
  public async Task NamespaceThrottle_ResumeFails_StillEndsPauseSoALaterThrottleIsHandledAsync() {
    var (transport, client, logger) = _createSessionTransport();
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _throttleDestination());
    var processor = client.LastSessionProcessor!;
    processor.StartProcessingException = new InvalidOperationException("resume failed");

    var pauseFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("Throttle pause/resume failed", StringComparison.Ordinal)) {
        pauseFailed.TrySetResult();
      }
    };

    await processor.RaiseErrorAsync(_serviceBusyErrorArgs());
    await pauseFailed.Task;

    await Assert.That(logger.Contains(LogLevel.Error, "Throttle pause/resume failed for inbox/throttle-sub")).IsTrue()
      .Because("the failure diagnostic must name the topic/subscription — an operator needs to know WHICH consumer is stuck");

    // The finally must have run despite the failed resume: a SECOND throttle must still be
    // able to claim the single-flight pause instead of finding it permanently held.
    processor.StartProcessingException = null;
    var countBeforeSecondThrottle = processor.StartProcessingAsyncCallCount;
    var secondPauseWarned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    logger.MessageLogged += (_, message) => {
      if (message.Contains("Namespace throttled (ServiceBusy)", StringComparison.Ordinal)) {
        secondPauseWarned.TrySetResult();
      }
    };
    var resumedSecondTime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    processor.Started += () => {
      if (processor.StartProcessingAsyncCallCount > countBeforeSecondThrottle) {
        resumedSecondTime.TrySetResult();
      }
    };

    await processor.RaiseErrorAsync(_serviceBusyErrorArgs());
    await secondPauseWarned.Task;
    await resumedSecondTime.Task;

    await Assert.That(processor.StopProcessingAsyncCallCount).IsEqualTo(2)
      .Because("EndPause must have released the single-flight lock after the failed resume — otherwise TryBeginPause would keep returning false and this second throttle would never even attempt a stop");
  }

  // ========================================
  // RECEIVE-DECISION / SUBSCRIPTION-NAME / PUBLISH-METADATA / SENDER-CACHE GAPS
  // ========================================

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createDecisionTransport(
      IReceptorRegistry? receptorRegistry = null,
      IPerspectiveRunnerRegistry? perspectiveRegistry = null) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = false,
      EnableReceiveLivenessWatchdog = false,
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: null,
      receptorRegistry: receptorRegistry,
      perspectiveRegistry: perspectiveRegistry);
    return (transport, client);
  }

  private static TransportDestination _decisionDestination() => new("inbox", "decision-sub");

  /// <summary>
  /// Earlier coverage of the no-local-consumer drop only ever used an EMPTY perspective
  /// registry, which (for a foreach over an array) never actually executes the loop body or
  /// its exit — a service that legitimately owns perspectives for OTHER event types takes a
  /// different path through this same predicate. If that path ever mistakenly reported a match
  /// (or threw), an unrelated payload could either be delivered to a handler that never
  /// registered for it, or silently stop being ack+dropped at all.
  /// </summary>
  [Test]
  public async Task ProcessMessage_PerspectiveRegistryTracksOtherTypesOnly_NoLocalConsumerDropsAsync() {
    var (transport, client) = _createDecisionTransport(
      receptorRegistry: new StubReceptorRegistry(null),
      perspectiveRegistry: new StubPerspectiveRegistry(typeof(UnregisteredBatchMessage)));
    var handlerInvoked = false;
    await transport.SubscribeAsync(
      (_, _, _) => { handlerInvoked = true; return Task.CompletedTask; },
      _decisionDestination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()), receiver));

    await Assert.That(receiver.Completed).Count().IsEqualTo(1)
      .Because("no-local-consumer drops still ack the broker so the message exits the topic");
    await Assert.That(handlerInvoked).IsFalse()
      .Because("a perspective registry that tracks OTHER event types must not make an unrelated payload look locally handled");
  }

  /// <summary>
  /// If an empty-but-present SubscriberName ever won over the whitespace guard and was used
  /// verbatim, the subscription would be created under a blank/malformed name — Azure Service
  /// Bus rejects that outright — instead of correctly falling back to a perfectly valid routing
  /// key.
  /// </summary>
  [Test]
  public async Task Subscribe_EmptySubscriberNameMetadata_FallsBackToRoutingKeyAsync() {
    var (transport, client) = _createDecisionTransport();
    var metadata = new Dictionary<string, JsonElement> {
      ["SubscriberName"] = AsbTransportTestData.Json("\"\"")
    };
    var destination = new TransportDestination("orders-topic", "orders-fallback-key", metadata);

    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, destination);

    await Assert.That(client.CreatedProcessors).Count().IsEqualTo(1);
    await Assert.That(client.CreatedProcessors[0].Subscription).IsEqualTo("orders-fallback-key")
      .Because("an empty SubscriberName must not win over a perfectly valid routing key");
  }

  /// <summary>
  /// AMQP application properties only support a fixed set of primitive types. An unclassifiable
  /// JsonElement (ValueKind.Undefined — e.g. a metadata value built without going through JSON
  /// parsing) must still degrade to something sendable; if it instead propagated as-is or threw
  /// during conversion, the SDK would reject the WHOLE message at send time over one stray
  /// metadata entry.
  /// </summary>
  [Test]
  public async Task PublishAsync_MetadataWithUndefinedJsonElement_SendsEmptyStringPropertyAsync() {
    var (transport, client) = _createDecisionTransport();
    var metadata = new Dictionary<string, JsonElement> { ["odd-key"] = default };
    var destination = new TransportDestination("bulk-topic", "orders.created", metadata);

    await transport.PublishAsync(AsbTransportTestData.CreateEnvelope(), destination);

    var message = client.LastSender!.Sent[0];
    await Assert.That(message.ApplicationProperties["odd-key"]).IsEqualTo(string.Empty)
      .Because("a value the switch cannot classify must still serialize as SOMETHING sendable — throwing here would fail the whole publish over one stray metadata entry");
  }

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client, RecordingProvisioningAdminClient AdminClient)
      _createProvisioningTransport() {
    var client = new RaisableServiceBusClient();
    var adminClient = new RecordingProvisioningAdminClient { ExistingTopics = { "race-topic" } };
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = true,
      EnableSessions = false,
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: adminClient);
    return (transport, client, adminClient);
  }

  /// <summary>
  /// If the second TryGetValue after acquiring the sender lock were ever removed, two publishes
  /// racing on a brand-new topic would each create their OWN sender — a leaked broker-side link
  /// per racing publisher instead of the single link this cache exists to guarantee.
  /// </summary>
  [Test]
  public async Task GetOrCreateSender_ConcurrentFirstUse_SecondCallerReusesTheWinnersSenderAsync() {
    var (transport, client, adminClient) = _createProvisioningTransport();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    adminClient.TopicExistsGate = () => gate.Task;
    var destination = new TransportDestination("race-topic", "race-key");

    // Caller A: reaches the admin topic-existence check while HOLDING the sender lock, then
    // blocks on the gate. Its execution up to that await runs synchronously on this thread, so
    // it is guaranteed to still hold the lock by the time caller B is started below.
    var taskA = transport.PublishAsync(AsbTransportTestData.CreateEnvelope("from-a"), destination);
    // Caller B: its own lock-free first check and its WaitAsync() call on the same lock A holds
    // also run synchronously up to the point B suspends — guaranteed to have happened by the
    // time this statement returns, before the gate is ever released.
    var taskB = transport.PublishAsync(AsbTransportTestData.CreateEnvelope("from-b"), destination);

    gate.SetResult();
    await Task.WhenAll(taskA, taskB);

    await Assert.That(client.CreatedSenderTopics).Count().IsEqualTo(1)
      .Because("both publishers targeted the same never-before-seen topic — exactly one sender/link may be created no matter how they interleave");
    await Assert.That(client.LastSender!.Sent).Count().IsEqualTo(2)
      .Because("the caller that lost the race must still send through the WINNER's sender, not one it would have created itself without the second check");
  }
}
