using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for IMessageDiscardPolicy — the centralised "should this message be skipped?"
/// decision used by transport-receive, inbox dispatch, and outbox publish gates.
/// Skipping a routine NoLocalConsumer message must log at Debug, not Warning, so it
/// doesn't surface in production logs every time a cross-domain event hits a subscriber.
/// </summary>
public class MessageDiscardPolicyTests {

  private const string CONSUMED_TYPE = "Test.Contracts.ConsumedEvent";
  private const string UNCONSUMED_TYPE = "Test.Contracts.UnconsumedEvent";

  private sealed class TestRegistry : IReceptorRegistryQuery {
    public HashSet<string> Consumed { get; } = [];
    public bool HasReceptors(LifecycleStage stage, string messageType) => Consumed.Contains(messageType);
    public bool HasInboxHandler(string messageType) => Consumed.Contains(messageType);
    public bool HasAnyConsumer(string messageType) => Consumed.Contains(messageType);
  }

  private sealed class RecordingLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }
    private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
  }

  private static (MessageDiscardPolicy Policy, TestRegistry Registry, RecordingLogger<MessageDiscardPolicy> Logger, Meter Meter)
    _newPolicy() {
    var registry = new TestRegistry { Consumed = { CONSUMED_TYPE } };
    var logger = new RecordingLogger<MessageDiscardPolicy>();
    var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests");
    var policy = new MessageDiscardPolicy(registry, logger, meter);
    return (policy, registry, logger, meter);
  }

  [Test]
  public async Task EvaluateReceive_TypeWithNoConsumer_ReturnsShouldDiscard_NoLocalConsumerAsync() {
    var (policy, _, _, _) = _newPolicy();

    var decision = policy.EvaluateReceive(UNCONSUMED_TYPE, topic: "topic.a", subscription: "sub-1");

    await Assert.That(decision.ShouldDiscard).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.NoLocalConsumer);
  }

  [Test]
  public async Task EvaluateReceive_TypeWithLocalConsumer_ReturnsShouldNotDiscardAsync() {
    var (policy, _, _, _) = _newPolicy();

    var decision = policy.EvaluateReceive(CONSUMED_TYPE, topic: "topic.a", subscription: "sub-1");

    await Assert.That(decision.ShouldDiscard).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  private static MessageDiscardPolicy _newPolicyWithAbsorb(Meter meter, params string[] absorb) {
    var routing = new RoutingOptions();
    routing.AbsorbNamespaces(absorb);
    var registry = new TestRegistry(); // UNCONSUMED_TYPE has NO consumer
    var logger = new RecordingLogger<MessageDiscardPolicy>();
    return new MessageDiscardPolicy(registry, logger, meter, Options.Create(routing));
  }

  [Test]
  public async Task EvaluateReceive_UnconsumedType_OnAbsorbedNamespace_IsKeptAsync() {
    // A type with no local consumer whose NAMESPACE is absorbed must NOT be discarded at receive — it has to
    // reach the inbox so the (unconditional) event-store write persists it for a later rebuild.
    using var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests.Absorb");
    var policy = _newPolicyWithAbsorb(meter, "Test.Contracts"); // == namespace of UNCONSUMED_TYPE

    var decision = policy.EvaluateReceive(UNCONSUMED_TYPE, topic: "someservice-test.contracts", subscription: "sub-1");

    await Assert.That(decision.ShouldDiscard).IsFalse();
  }

  [Test]
  public async Task EvaluateReceive_UnconsumedType_OnNonAbsorbedNamespace_StillDiscardsAsync() {
    // Absorb is namespace-scoped: an unconsumed type on a DIFFERENT namespace still drops as before.
    using var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests.Absorb2");
    var policy = _newPolicyWithAbsorb(meter, "Other.Namespace");

    var decision = policy.EvaluateReceive(UNCONSUMED_TYPE, topic: "svc-test.contracts", subscription: "sub-1");

    await Assert.That(decision.ShouldDiscard).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.NoLocalConsumer);
  }

  // Body-offload (claim-check): an offloaded message arrives with the internal
  // BodyClaimEnvelopePayload as its wire payload type — no service consumes it, so HasAnyConsumer
  // says "discard". That silently drops every offloaded message before TransportConsumerWorker can
  // rehydrate the original type. Both no-local-consumer gates must exempt claim envelopes.
  // (Regression: a production "bulk → Approved" batch of many IDs offloaded past ASB's limit, then dropped.)
  private const string CLAIM_ENVELOPE_TYPE =
    "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Offloads.BodyClaimEnvelopePayload, Whizbang.Core]], Whizbang.Core";

  [Test]
  public async Task EvaluateReceive_BodyOffloadClaimEnvelope_NeverDiscardsAsync() {
    var (policy, _, _, _) = _newPolicy();  // registry has NO consumer for the claim type

    var decision = policy.EvaluateReceive(CLAIM_ENVELOPE_TYPE, topic: "inbox", subscription: "jobservice-inbox");

    await Assert.That(decision.ShouldDiscard).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  [Test]
  public async Task EvaluateInbox_BodyOffloadClaimEnvelope_NeverDiscardsAsync() {
    var (policy, _, _, _) = _newPolicy();

    var decision = policy.EvaluateInbox(CLAIM_ENVELOPE_TYPE);

    await Assert.That(decision.ShouldDiscard).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  [Test]
  public async Task EvaluateInbox_TypeNoLongerInRegistry_ReturnsShouldDiscard_RegistryChangedAsync() {
    var (policy, _, _, _) = _newPolicy();

    var decision = policy.EvaluateInbox(UNCONSUMED_TYPE);

    await Assert.That(decision.ShouldDiscard).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.RegistryChanged);
  }

  [Test]
  public async Task EvaluateOutbox_NoCatalogAvailable_ReturnsShouldNotDiscardAsync() {
    var (policy, _, _, _) = _newPolicy();

    var decision = policy.EvaluateOutbox(UNCONSUMED_TYPE);

    // Safe default — without explicit catalog evidence, never drop a publish.
    await Assert.That(decision.ShouldDiscard).IsFalse();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  [Test]
  public async Task RecordDiscard_LogsAtDebugLevel_ForNoLocalConsumerAsync() {
    var (policy, _, logger, _) = _newPolicy();
    var decision = new MessageDiscardDecision(ShouldDiscard: true, Reason: MessageDiscardReason.NoLocalConsumer, Detail: "no consumer");

    policy.RecordDiscard(MessageDiscardGate.Receive, decision, UNCONSUMED_TYPE);

    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Debug);
  }

  [Test]
  public async Task RecordDiscard_LogsAtWarningLevel_ForDomainNotOwnedAsync() {
    var (policy, _, logger, _) = _newPolicy();
    var decision = new MessageDiscardDecision(ShouldDiscard: true, Reason: MessageDiscardReason.DomainNotOwned, Detail: "misconfig");

    policy.RecordDiscard(MessageDiscardGate.Receive, decision, UNCONSUMED_TYPE);

    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  public async Task RecordDiscard_IncrementsCounter_WithExpectedTagsAsync() {
    var (policy, _, _, meter) = _newPolicy();
    long total = 0;
    var tagSnapshots = new List<IReadOnlyDictionary<string, object?>>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter && instrument.Name == "whizbang.message.skipped") {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      total += value;
      var snapshot = new Dictionary<string, object?>(tags.Length);
      foreach (var t in tags) { snapshot[t.Key] = t.Value; }
      tagSnapshots.Add(snapshot);
    });
    listener.Start();

    var decision = new MessageDiscardDecision(ShouldDiscard: true, Reason: MessageDiscardReason.NoLocalConsumer, Detail: null);
    policy.RecordDiscard(MessageDiscardGate.Receive, decision, UNCONSUMED_TYPE,
      additionalTags: new Dictionary<string, object?> { ["topic"] = "topic.a", ["subscription"] = "sub-1" });

    await Assert.That(total).IsEqualTo(1L);
    await Assert.That(tagSnapshots.Count).IsEqualTo(1);
    await Assert.That(tagSnapshots[0]["gate"]).IsEqualTo("receive");
    await Assert.That(tagSnapshots[0]["reason"]).IsEqualTo("NoLocalConsumer");
    await Assert.That(tagSnapshots[0]["payload_type"]).IsEqualTo(UNCONSUMED_TYPE);
    await Assert.That(tagSnapshots[0]["topic"]).IsEqualTo("topic.a");
    await Assert.That(tagSnapshots[0]["subscription"]).IsEqualTo("sub-1");
  }

  // ============================================================
  // Composite exemption — a composite event type (redelivery bundle, coalesced batch, audit
  // composite) never has a receptor or perspective consumer; its consumers are the INNER events,
  // addressable only after the dispatch-seam fan-out. Both no-consumer gates that this policy
  // backs (transport receive + inbox dispatch) must keep composites, or the entire bundle is
  // discarded before fan-out — invisibly (Debug + counter only). The receive gate is handed the
  // ENVELOPE-wrapped wire name; the inbox gate is handed the payload type name — both shapes
  // must resolve through the catalog stamp.
  // ============================================================

  private static MessageDiscardPolicy _newPolicyWithCatalog(Meter meter) {
    var registry = new TestRegistry(); // composite type has NO consumer — faithful to every service
    var logger = new RecordingLogger<MessageDiscardPolicy>();
    var markerResolver = new EventMarkerResolver(new Whizbang.Core.Generated.GeneratedMessageTypeCatalog());
    return new MessageDiscardPolicy(registry, logger, meter, routingOptions: null, markerResolver: markerResolver);
  }

  [Test]
  public async Task EvaluateReceive_CompositeEnvelopeName_NoConsumer_IsKeptAsync() {
    using var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests.CompositeReceive");
    var policy = _newPolicyWithCatalog(meter);

    // DELIBERATELY the pre-move (Whizbang.Core.Messaging) name: in-flight envelopes published
    // by pre-move builds still carry it, and the catalog's formerNames must keep the composite
    // exemption alive for them (see MintedTypeRenameCompatibilityTests for the full matrix).
    var decision = policy.EvaluateReceive(
      "Whizbang.Core.Observability.MessageEnvelope`1[[Whizbang.Core.Messaging.RedeliveryComposite, Whizbang.Core]], Whizbang.Core",
      topic: "inbox",
      subscription: "svc-inbox");

    await Assert.That(decision.ShouldDiscard).IsFalse()
      .Because("a composite has no receptor anywhere by design — discarding it at receive drops the "
             + "whole bundle before its inner events can fan out (the repair-starvation regression)");
  }

  [Test]
  public async Task EvaluateInbox_CompositePayloadName_NoConsumer_IsKeptAsync() {
    using var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests.CompositeInbox");
    var policy = _newPolicyWithCatalog(meter);

    // DELIBERATELY the pre-move name — an inbox row WRITTEN before the namespace move resolves
    // through the catalog's formerNames (see MintedTypeRenameCompatibilityTests).
    var decision = policy.EvaluateInbox("Whizbang.Core.Messaging.RedeliveryComposite, Whizbang.Core");

    await Assert.That(decision.ShouldDiscard).IsFalse()
      .Because("an inbox row holding a composite must reach the dispatch-seam fan-out — the "
             + "RegistryChanged discard would delete the stored bundle after the transport already "
             + "accepted it");
  }

  [Test]
  public async Task EvaluateReceive_PlainUnconsumedType_WithCatalogWired_StillDiscardsAsync() {
    // Guard: wiring the marker resolver must not weaken the gate for ordinary unconsumed types.
    using var meter = new Meter("Whizbang.Tests.MessageDiscardPolicyTests.CompositeNarrow");
    var policy = _newPolicyWithCatalog(meter);

    var decision = policy.EvaluateReceive(UNCONSUMED_TYPE, topic: "inbox", subscription: "svc-inbox");

    await Assert.That(decision.ShouldDiscard).IsTrue();
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.NoLocalConsumer);
  }

  // ---------- a per-message log on a bulk path is a memory leak with extra steps ----------

  /// <summary>
  /// RegistryChanged must not log per message at a level that is on by default.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Observed in production: a service discarding a type it no longer consumes emitted this line
  /// ~735 times per second for a sustained bulk backlog — 22,000 log lines per 30 seconds. The
  /// process was OOM-killed repeatedly, and the log volume was the driver, not the work.
  /// </para>
  /// <para>
  /// The first occurrence per type genuinely matters: it means rows were written when a consumer
  /// existed and that consumer is gone now, which is a real deployment-shape signal an operator
  /// wants. The ten-thousandth occurrence carries no additional information — the counter already
  /// tags every discard by reason and payload type, so the rate is fully observable without the
  /// text.
  /// </para>
  /// </remarks>
  [Test]
  public async Task RegistryChanged_LogsOncePerType_ThenFallsToDebugAsync() {
    var (policy, _, logger, _) = _newPolicy();
    var decision = new MessageDiscardDecision(
      ShouldDiscard: true, MessageDiscardReason.RegistryChanged, Detail: "no consumer registered now");

    for (var i = 0; i < 500; i++) {
      policy.RecordDiscard(MessageDiscardGate.Inbox, decision, UNCONSUMED_TYPE);
    }

    var atOrAboveInfo = logger.Entries.Count(e => e.Level >= LogLevel.Information);
    await Assert.That(atOrAboveInfo).IsEqualTo(1)
      .Because("500 identical discards carry exactly as much information as the first — and at "
             + "production rates the repeats are what exhausts the container's memory");
  }

  [Test]
  public async Task RegistryChanged_StillSurfacesTheFirstOccurrenceOfEachDistinctTypeAsync() {
    var (policy, _, logger, _) = _newPolicy();
    var decision = new MessageDiscardDecision(
      ShouldDiscard: true, MessageDiscardReason.RegistryChanged, Detail: "no consumer registered now");

    policy.RecordDiscard(MessageDiscardGate.Inbox, decision, "Test.Contracts.AlphaEvent");
    policy.RecordDiscard(MessageDiscardGate.Inbox, decision, "Test.Contracts.AlphaEvent");
    policy.RecordDiscard(MessageDiscardGate.Inbox, decision, "Test.Contracts.BetaEvent");

    var surfaced = logger.Entries.Where(e => e.Level >= LogLevel.Information).ToList();
    await Assert.That(surfaced.Count).IsEqualTo(2)
      .Because("suppressing the repeats must not suppress a DIFFERENT type going unconsumed — "
             + "that is a distinct deployment signal, and collapsing them would hide it");
    await Assert.That(surfaced.Any(e => e.Message.Contains("AlphaEvent", StringComparison.Ordinal))).IsTrue();
    await Assert.That(surfaced.Any(e => e.Message.Contains("BetaEvent", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task RegistryChanged_CounterStillCountsEveryDiscardAsync() {
    var meter = new Meter("Whizbang.Tests.DiscardFloodCounter");
    var measured = 0L;
    using var listener = new MeterListener {
      InstrumentPublished = (inst, l) => {
        if (inst.Meter.Name == "Whizbang.Tests.DiscardFloodCounter") { l.EnableMeasurementEvents(inst); }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, v, _, _) => Interlocked.Add(ref measured, v));
    listener.Start();

    var registry = new TestRegistry { Consumed = { CONSUMED_TYPE } };
    var policy = new MessageDiscardPolicy(registry, new RecordingLogger<MessageDiscardPolicy>(), meter);
    var decision = new MessageDiscardDecision(
      ShouldDiscard: true, MessageDiscardReason.RegistryChanged, Detail: "no consumer registered now");

    for (var i = 0; i < 250; i++) {
      policy.RecordDiscard(MessageDiscardGate.Inbox, decision, UNCONSUMED_TYPE);
    }

    await Assert.That(measured).IsEqualTo(250)
      .Because("the log is throttled, the MEASUREMENT never is — otherwise quieting the flood "
             + "would also blind the dashboard that proves it is happening");
  }
}
