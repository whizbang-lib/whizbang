using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

  // Body-offload (claim-check): an offloaded message arrives with the internal
  // BodyClaimEnvelopePayload as its wire payload type — no service consumes it, so HasAnyConsumer
  // says "discard". That silently drops every offloaded message before TransportConsumerWorker can
  // rehydrate the original type. Both no-local-consumer gates must exempt claim envelopes.
  // (Regression: production "bulk → Approved" (21,398 IDs) offloaded past ASB's limit, then dropped.)
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
}
