using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

namespace Whizbang.Transports.AzureServiceBus.Tests;

/// <summary>
/// Topology arc phase 8.5 — Azure Service Bus executes the Core poison verdict natively.
/// <para>
/// A live Standard-namespace probe confirmed the emulator spike: on SESSION-enabled entities a
/// lock loss caused by connection death does NOT increment DeliveryCount (it stays 1), while an
/// explicit abandon and a NON-session lock loss both take it to 2. Command inboxes are
/// session-enabled by default, so the broker's MaxDeliveryCount valve and the transport's own
/// <c>MaxDeliveryAttempts</c> branch — which reads the same counter — are structurally unreachable
/// under a consumer-death storm. These tests lock the replacement: the age verdict travels through
/// the EXISTING <see cref="AsbReceiveDecisionMaker"/> / <see cref="AsbReceiveAction"/> seam and is
/// executed with <c>DeadLetterMessageAsync</c>, so the message lands in the per-namespace DLQ that
/// the existing dead-letter drainer already replays.
/// </para>
/// </summary>
[Timeout(10_000)]
public class AsbPoisonQuarantineTests {

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  private static readonly DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

  private static Dictionary<string, object> _withEnvelopeType(string typeName) =>
    new() { [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = typeName };

  private static PoisonEvaluationContext _context(
      DateTimeOffset? firstEnqueuedAt,
      int? brokerDeliveryCount = 1,
      int? durableObservationCount = null) =>
    new("message-1", firstEnqueuedAt, brokerDeliveryCount, durableObservationCount, _now);

  #region Decision seam

  [Test]
  public async Task Decide_AgedMessage_ReturnsDeadLetterThroughTheExistingSeamAsync() {
    // The lock: an aged message quarantines through AsbReceiveAction.DeadLetter — the seam the
    // transport already maps onto DeadLetterMessageAsync — NOT a parallel code path.
    var decider = new AsbReceiveDecisionMaker();
    var detector = new StubPoisonDetector(
      PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "aged out"));
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Event, X]]");

    var decision = decider.Decide(
      props, "{}", _resolveAlwaysNull, _jsonOptions,
      poisonDetector: detector,
      poisonContext: _context(_now - TimeSpan.FromDays(1)));

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.DeadLetter);
    await Assert.That(decision.Reason).IsEqualTo("PoisonQuarantine");
    await Assert.That(decision.Description).Contains("aged out");
    await Assert.That(decision.PoisonVerdict!.Value.Reason).IsEqualTo(PoisonQuarantineReason.MessageAgeExceeded);
  }

  [Test]
  public async Task Decide_PoisonGateRunsBeforeEnvelopeTypeResolutionAsync() {
    // Age is pure broker metadata. A poison message must quarantine even when it also has no
    // EnvelopeType property — and it must be attributed to POISON, not to the missing-metadata
    // branch, or operators chase the wrong defect.
    var decider = new AsbReceiveDecisionMaker();
    var detector = new StubPoisonDetector(
      PoisonVerdict.Quarantine(PoisonQuarantineReason.MessageAgeExceeded, "aged out"));

    var decision = decider.Decide(
      new Dictionary<string, object>(), "{}", _resolveAlwaysNull, _jsonOptions,
      poisonDetector: detector,
      poisonContext: _context(_now - TimeSpan.FromDays(1)));

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.DeadLetter);
    await Assert.That(decision.Reason).IsEqualTo("PoisonQuarantine");
  }

  [Test]
  public async Task Decide_ProceedVerdict_LeavesEveryOtherBranchUntouchedAsync() {
    var decider = new AsbReceiveDecisionMaker();
    var detector = new StubPoisonDetector(PoisonVerdict.Proceed());
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Event, X]]");

    var decision = decider.Decide(
      props, "{}", _resolveAlwaysNull, _jsonOptions,
      poisonDetector: detector,
      poisonContext: _context(_now));

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.Reason).IsEqualTo("MissingJsonTypeInfo");
    await Assert.That(decision.PoisonVerdict).IsNull();
  }

  [Test]
  public async Task Decide_NoDetector_IsTodaysBehaviorAsync() {
    // Null detector means pre-phase-8.5 behavior: the optional-injected-policy idiom, so no
    // ITransport member and no breaking change for custom transports.
    var decider = new AsbReceiveDecisionMaker();
    var props = _withEnvelopeType("Whizbang.Core.Observability.MessageEnvelope`1[[Unknown.Event, X]]");

    var decision = decider.Decide(props, "{}", _resolveAlwaysNull, _jsonOptions);

    await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
    await Assert.That(decision.PoisonVerdict).IsNull();
  }

  #endregion

  #region Transport execution — session pipeline (the hostage case)

  [Test]
  public async Task ProcessSessionMessage_AgedMessage_DeadLettersToThePerNamespaceDlqAsync() {
    // The arc's motivating failure, end to end on the session pipeline: DeliveryCount is 1 (a
    // session lock loss never moves it) so EVERY count-based valve is inert; the message is
    // quarantined on AGE alone and lands in the entity's DLQ, which the existing dead-letter
    // drainer replays.
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30));
    var (transport, client) = _createTransport(enableSessions: true, poisonDetector: detector);
    var handlerInvoked = false;
    await transport.SubscribeAsync((_, _, _) => { handlerInvoked = true; return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(),
          deliveryCount: 1,
          enqueuedTime: _now - TimeSpan.FromHours(4)),
        receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1)
      .Because("age is the only signal that survives a session lock-loss storm");
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo("PoisonQuarantine");
    await Assert.That(receiver.Completed).IsEmpty();
    await Assert.That(handlerInvoked).IsFalse();
  }

  [Test]
  public async Task ProcessSessionMessage_FreshMessage_ProcessesNormallyAsync() {
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30));
    var (transport, client) = _createTransport(enableSessions: true, poisonDetector: detector);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync((e, _, _) => { handled.Add(e); return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromSeconds(2)),
        receiver));

    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ProcessSessionMessage_SlowButProgressingMessage_ProcessesNormallyAsync() {
    // Inside renewal x attempts the message is progressing, not poison. Quarantining it would be
    // a worse defect than the one this phase closes.
    var detector = _detector(lockRenewalDuration: TimeSpan.FromMinutes(5), maxDeliveryAttempts: 10);
    var (transport, client) = _createTransport(enableSessions: true, poisonDetector: detector);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync((e, _, _) => { handled.Add(e); return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromMinutes(45)),
        receiver));

    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ProcessSessionMessage_DetectorDisabled_ProcessesTheAgedMessageAsync() {
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30), enabled: false);
    var (transport, client) = _createTransport(enableSessions: true, poisonDetector: detector);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync((e, _, _) => { handled.Add(e); return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromDays(30)),
        receiver));

    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ProcessSessionMessage_NoDetectorWired_ProcessesTheAgedMessageAsync() {
    // Zero behavior change when the policy is absent.
    var (transport, client) = _createTransport(enableSessions: true);
    var handled = new List<IMessageEnvelope>();
    await transport.SubscribeAsync((e, _, _) => { handled.Add(e); return Task.CompletedTask; }, _destination());
    var receiver = new RecordingTransportSessionReceiver();

    await client.LastSessionProcessor!.RaiseSessionMessageAsync(
      AsbTransportTestData.SessionArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromDays(30)),
        receiver));

    await Assert.That(receiver.DeadLettered).IsEmpty();
    await Assert.That(handled).Count().IsEqualTo(1);
  }

  #endregion

  #region Transport execution — non-session pipeline

  [Test]
  public async Task ProcessMessage_AgedMessage_DeadLettersAsync() {
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30));
    var (transport, client) = _createTransport(enableSessions: false, poisonDetector: detector);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromHours(4)),
        receiver));

    await Assert.That(receiver.DeadLettered).Count().IsEqualTo(1);
    await Assert.That(receiver.DeadLettered[0].Reason).IsEqualTo("PoisonQuarantine");
    await Assert.That(receiver.Completed).IsEmpty();
  }

  #endregion

  #region Capability honesty

  [Test]
  public async Task ProcessMessage_BrokerSuppliesEnqueuedTime_ReportsTheSurfaceCapableAsync() {
    var capability = new PoisonDetectionCapabilityState();
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30), capabilityState: capability);
    var (transport, client) = _createTransport(enableSessions: false, poisonDetector: detector);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(
        AsbTransportTestData.EnvelopeMessage(
          AsbTransportTestData.CreateEnvelope(), enqueuedTime: _now - TimeSpan.FromSeconds(1)),
        new RecordingTransportReceiver()));

    await Assert.That(capability.HasDegradedSurface).IsFalse()
      .Because("Azure Service Bus always stamps EnqueuedTime — layer 1 is fully live here");
  }

  [Test]
  public async Task ProcessMessage_NoEnqueuedTime_DegradesLoudlyInsteadOfGoingInertAsync() {
    // A broker message with no usable enqueue timestamp must NOT be treated as infinitely old
    // (that would quarantine everything) and must NOT silently stop enforcing (that is exactly
    // how the delivery-count valve failed). It reports the surface degraded, loudly.
    var capability = new PoisonDetectionCapabilityState();
    var detector = _detector(ageThreshold: TimeSpan.FromMinutes(30), capabilityState: capability);
    var (transport, client) = _createTransport(enableSessions: false, poisonDetector: detector);
    await transport.SubscribeAsync((_, _, _) => Task.CompletedTask, _destination());
    var receiver = new RecordingTransportReceiver();

    await client.LastProcessor!.RaiseMessageAsync(
      AsbTransportTestData.MessageArgs(
        AsbTransportTestData.EnvelopeMessage(AsbTransportTestData.CreateEnvelope()),  // enqueuedTime: default
        receiver));

    await Assert.That(receiver.DeadLettered).IsEmpty()
      .Because("an absent timestamp must never be read as an infinitely old message");
    await Assert.That(capability.HasDegradedSurface).IsTrue();
    await Assert.That(capability.DegradedSurfaces[0].Transport).IsEqualTo("azure-service-bus");
    await Assert.That(capability.DegradedSurfaces[0].Entity).Contains("transport-topic");
  }

  #endregion

  #region Threshold derivation from the transport's own options

  [Test]
  public async Task PostConfigure_FillsThePoisonThresholdFromLockAndDeliveryOptionsAsync() {
    // "Derived, not guessed": the age default IS the transport's own lock-renewal window times
    // its own delivery cap. Moving either knob must move the poison threshold with it, or the
    // default drifts into a magic number.
    var poisonOptions = new PoisonMessageOptions();
    var transportOptions = new AzureServiceBusOptions {
      MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(8),
      MaxDeliveryAttempts = 7,
    };

    new AsbPoisonOptionsPostConfigure(Microsoft.Extensions.Options.Options.Create(transportOptions))
      .PostConfigure(null, poisonOptions);

    await Assert.That(poisonOptions.LockRenewalDuration).IsEqualTo(TimeSpan.FromMinutes(8));
    await Assert.That(poisonOptions.MaxDeliveryAttempts).IsEqualTo(7);
    await Assert.That(poisonOptions.EffectiveAgeThreshold).IsEqualTo(TimeSpan.FromMinutes(56));
  }

  [Test]
  public async Task PostConfigure_DoesNotOverrideAnExplicitOperatorValueAsync() {
    // Configuration is the operator's word; the transport only fills blanks.
    var poisonOptions = new PoisonMessageOptions {
      LockRenewalDuration = TimeSpan.FromMinutes(1),
      MaxDeliveryAttempts = 2,
    };
    var transportOptions = new AzureServiceBusOptions {
      MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(8),
      MaxDeliveryAttempts = 7,
    };

    new AsbPoisonOptionsPostConfigure(Microsoft.Extensions.Options.Options.Create(transportOptions))
      .PostConfigure(null, poisonOptions);

    await Assert.That(poisonOptions.LockRenewalDuration).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(poisonOptions.MaxDeliveryAttempts).IsEqualTo(2);
  }

  #endregion

  #region Helpers

  private static JsonTypeInfo? _resolveAlwaysNull(string _, JsonSerializerOptions __) => null;

  private static TransportDestination _destination() => new("transport-topic", "test-subscription");

  private static PoisonMessageDetector _detector(
      TimeSpan? ageThreshold = null,
      TimeSpan? lockRenewalDuration = null,
      int? maxDeliveryAttempts = null,
      bool enabled = true,
      PoisonDetectionCapabilityState? capabilityState = null) =>
    new(
      Microsoft.Extensions.Options.Options.Create(new PoisonMessageOptions {
        Enabled = enabled,
        AgeThreshold = ageThreshold,
        LockRenewalDuration = lockRenewalDuration,
        MaxDeliveryAttempts = maxDeliveryAttempts,
      }),
      NullLogger<PoisonMessageDetector>.Instance,
      new System.Diagnostics.Metrics.Meter("Whizbang.Transports.AzureServiceBus.Tests.Poison"),
      capabilityState);

  private static (AzureServiceBusTransport Transport, RaisableServiceBusClient Client) _createTransport(
      bool enableSessions,
      IPoisonMessageDetector? poisonDetector = null) {
    var client = new RaisableServiceBusClient();
    var options = new AzureServiceBusOptions {
      AutoProvisionInfrastructure = false,
      EnableSessions = enableSessions
    };
    var transport = new AzureServiceBusTransport(
      client,
      AsbTransportTestData.CombinedOptions,
      options,
      NullLogger<AzureServiceBusTransport>.Instance,
      adminClient: null,
      receptorRegistry: null,
      perspectiveRegistry: null,
      rawReceptorRegistry: null,
      typeBinder: null,
      discardPolicy: null,
      absorbedNamespaces: null,
      timeProvider: new FixedTimeProvider(_now),
      poisonDetector: poisonDetector);
    return (transport, client);
  }

  #endregion
}

/// <summary>TimeProvider pinned to a fixed instant so age assertions are deterministic.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider {
  public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Returns one canned verdict, so decision-seam tests exercise the transport's wiring rather than
/// the Core threshold logic (which has its own locks in Whizbang.Core.Tests).
/// </summary>
internal sealed class StubPoisonDetector(PoisonVerdict verdict) : IPoisonMessageDetector {
  public List<(string Transport, string Entity, bool Capable)> CapabilityReports { get; } = [];
  public List<PoisonVerdict> Recorded { get; } = [];

  public PoisonVerdict Evaluate(PoisonEvaluationContext context) => verdict;

  public void RecordQuarantine(
      PoisonQuarantineGate gate,
      PoisonVerdict quarantined,
      PoisonEvaluationContext context,
      IReadOnlyDictionary<string, object?>? additionalTags = null) => Recorded.Add(quarantined);

  public void ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) =>
    CapabilityReports.Add((transport, entity, canSupplyTrustworthyAge));
}
