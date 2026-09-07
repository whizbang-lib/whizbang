using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Coverage round 23 — targets the empty-payload-type early exits on both no-consumer gates, the
/// unclassified branches of the reason-to-log-level and gate-to-tag mappings, and the tracked-key
/// cap that bounds <see cref="MessageDiscardPolicy"/>'s "log the first sighting" throttle.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/MessageDiscardPolicy.cs</code-under-test>
public class MessageDiscardPolicyCoverageTests {

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
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }
    private sealed class NullDisposable : IDisposable { public static readonly NullDisposable Instance = new(); public void Dispose() { } }
  }

  private static MessageDiscardPolicy _newPolicy(out RecordingLogger<MessageDiscardPolicy> logger) {
    logger = new RecordingLogger<MessageDiscardPolicy>();
    var meter = new Meter($"Whizbang.Tests.MessageDiscardPolicyCoverageTests.{Guid.NewGuid()}");
    return new MessageDiscardPolicy(new TestRegistry(), logger, meter);
  }

  // ---------------------------------------------------------------------------------------------
  // Empty payload type — both no-consumer gates must fail OPEN (keep the message) when the type
  // couldn't be determined at all, rather than falling through to a registry lookup on "".
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// If this early return is lost, an envelope whose payload type could not be determined falls
  /// through to the consumer-registry check, which reports no consumer for the empty string and
  /// discards it — silently dropping a message nobody can even classify, instead of surfacing the
  /// missing-type-info bug that produced an empty type in the first place.
  /// </summary>
  [Test]
  public async Task EvaluateReceive_EmptyPayloadType_ReturnsShouldNotDiscardAsync() {
    var policy = _newPolicy(out _);

    var decision = policy.EvaluateReceive(string.Empty, topic: "topic.a", subscription: "sub-1");

    await Assert.That(decision.ShouldDiscard).IsFalse()
      .Because("a message with no discoverable payload type must be kept, not discarded on a blank lookup");
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  /// <summary>
  /// Same fail-open guarantee on the inbox dispatch gate — a row that got this far with an empty
  /// payload type must not be marked <c>Skipped</c> just because the registry lookup on "" fails.
  /// </summary>
  [Test]
  public async Task EvaluateInbox_EmptyPayloadType_ReturnsShouldNotDiscardAsync() {
    var policy = _newPolicy(out _);

    var decision = policy.EvaluateInbox(string.Empty);

    await Assert.That(decision.ShouldDiscard).IsFalse()
      .Because("an inbox row with no discoverable payload type must be kept, not skipped on a blank lookup");
    await Assert.That(decision.Reason).IsEqualTo(MessageDiscardReason.None);
  }

  // ---------------------------------------------------------------------------------------------
  // Reason -> log level mapping: the NoKnownConsumer arm and the unclassified default arm.
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// If NoKnownConsumer stops mapping to Debug, an outbox-side "no consumer anywhere in the
  /// system" signal — expected to be routine, exactly like NoLocalConsumer — starts logging loudly
  /// by default and reproduces the same log-volume-driven OOM this throttle exists to prevent.
  /// </summary>
  [Test]
  public async Task RecordDiscard_NoKnownConsumerReason_LogsAtDebugLevelAsync() {
    var policy = _newPolicy(out var logger);
    var decision = new MessageDiscardDecision(ShouldDiscard: true, MessageDiscardReason.NoKnownConsumer, Detail: "no subscriber anywhere");

    policy.RecordDiscard(MessageDiscardGate.Outbox, decision, "Test.Contracts.UnknownAnywhereEvent");

    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Debug)
      .Because("NoKnownConsumer is routine (like NoLocalConsumer) and must not surface at Information by default");
  }

  /// <summary>
  /// If a future <see cref="MessageDiscardReason"/> value is ever added without a matching arm
  /// here, it must default to Information (loud) rather than silently landing at Debug — an
  /// unclassified discard reason hiding at Debug is a brand-new failure mode nobody would notice.
  /// </summary>
  [Test]
  public async Task RecordDiscard_UnclassifiedReason_DefaultsToInformationLevelAsync() {
    var policy = _newPolicy(out var logger);
    // Reason=None paired with ShouldDiscard=true is not produced by the two Evaluate* methods
    // (None only ever accompanies ShouldDiscard=false there), but the switch has no explicit arm
    // for it either — exercising the same "unclassified reason" default arm a genuinely new future
    // value would fall into.
    var decision = new MessageDiscardDecision(ShouldDiscard: true, MessageDiscardReason.None, Detail: "unclassified");

    policy.RecordDiscard(MessageDiscardGate.Inbox, decision, "Test.Contracts.UnclassifiedReasonEvent");

    await Assert.That(logger.Entries.Count).IsEqualTo(1);
    await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Information)
      .Because("an unclassified reason must default LOUD, not quiet — the safe failure mode for a mapping gap");
  }

  // ---------------------------------------------------------------------------------------------
  // Gate -> OTel tag mapping: the Outbox arm and the unmapped-gate-value fallback.
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// If the outbox arm of the gate-tag mapping regresses, an outbox-side discard mistags (or, if
  /// the switch ever grows a throw, loses entirely) its OTel counter entry — the per-gate dashboard
  /// breakdown (receive vs inbox vs outbox) silently stops accounting for outbox drops.
  /// </summary>
  [Test]
  public async Task RecordDiscard_OutboxGate_TagsGateAsOutboxAsync() {
    using var meter = new Meter($"Whizbang.Tests.MessageDiscardPolicyCoverageTests.Outbox.{Guid.NewGuid()}");
    var policy = new MessageDiscardPolicy(new TestRegistry(), new RecordingLogger<MessageDiscardPolicy>(), meter);
    var tagSnapshots = new List<IReadOnlyDictionary<string, object?>>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter && instrument.Name == "whizbang.message.skipped") { l.EnableMeasurementEvents(instrument); }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, _, tags, _) => {
      var snapshot = new Dictionary<string, object?>(tags.Length);
      foreach (var t in tags) { snapshot[t.Key] = t.Value; }
      tagSnapshots.Add(snapshot);
    });
    listener.Start();
    var decision = new MessageDiscardDecision(ShouldDiscard: true, MessageDiscardReason.NoLocalConsumer, Detail: null);

    policy.RecordDiscard(MessageDiscardGate.Outbox, decision, "Test.Contracts.SomeOutboxType");

    await Assert.That(tagSnapshots.Count).IsEqualTo(1);
    await Assert.That(tagSnapshots[0]["gate"]).IsEqualTo("outbox")
      .Because("the outbox gate must tag its discard metric \"outbox\" so per-gate dashboards can isolate outbox drops");
  }

  /// <summary>
  /// If an unmapped gate value ever throws instead of falling back to its numeric
  /// <c>ToString()</c>, the ENTIRE metric write for that call is lost — the raw numeric fallback,
  /// while not pretty, at least keeps the discard counter emitting instead of going dark.
  /// </summary>
  [Test]
  public async Task RecordDiscard_UnknownGateValue_FallsBackToNumericTagAsync() {
    using var meter = new Meter($"Whizbang.Tests.MessageDiscardPolicyCoverageTests.UnknownGate.{Guid.NewGuid()}");
    var policy = new MessageDiscardPolicy(new TestRegistry(), new RecordingLogger<MessageDiscardPolicy>(), meter);
    var tagSnapshots = new List<IReadOnlyDictionary<string, object?>>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter && instrument.Name == "whizbang.message.skipped") { l.EnableMeasurementEvents(instrument); }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, _, tags, _) => {
      var snapshot = new Dictionary<string, object?>(tags.Length);
      foreach (var t in tags) { snapshot[t.Key] = t.Value; }
      tagSnapshots.Add(snapshot);
    });
    listener.Start();
    var decision = new MessageDiscardDecision(ShouldDiscard: true, MessageDiscardReason.NoLocalConsumer, Detail: null);

    policy.RecordDiscard((MessageDiscardGate)999, decision, "Test.Contracts.SomeType");

    await Assert.That(tagSnapshots.Count).IsEqualTo(1);
    await Assert.That(tagSnapshots[0]["gate"]).IsEqualTo("999")
      .Because("an unmapped gate value must fall back to its numeric ToString() rather than throwing " +
               "and dropping the whole discard metric write");
  }

  // ---------------------------------------------------------------------------------------------
  // Tracked-key cap: past MAX_TRACKED_DISCARD_KEYS (1024, per the private const in
  // MessageDiscardPolicy.cs), even a genuinely new type must be treated as a repeat.
  // ---------------------------------------------------------------------------------------------

  /// <summary>
  /// If this cap check is lost, a malformed or attacker-influenced stream of distinct type strings
  /// grows the seen-discards dictionary without bound for the life of the process — the exact
  /// unbounded-memory failure this cap exists to stop.
  /// </summary>
  [Test]
  public async Task RecordDiscard_PastTrackedKeyCap_TreatsNewTypesAsRepeatsAsync() {
    var policy = _newPolicy(out var logger);
    var decision = new MessageDiscardDecision(ShouldDiscard: true, MessageDiscardReason.RegistryChanged, Detail: "no consumer registered now");

    for (var i = 0; i < 1024; i++) {
      policy.RecordDiscard(MessageDiscardGate.Inbox, decision, $"Test.Contracts.CapFillType{i}");
    }
    var informationCountBeforeCap = logger.Entries.Count(e => e.Level >= LogLevel.Information);

    policy.RecordDiscard(MessageDiscardGate.Inbox, decision, "Test.Contracts.NeverSeenBeforeType");

    var informationCountAfterCap = logger.Entries.Count(e => e.Level >= LogLevel.Information);
    await Assert.That(informationCountAfterCap).IsEqualTo(informationCountBeforeCap)
      .Because("past the cap, even a genuinely NEW type must be treated as a repeat (logged at Debug) — " +
               "the alternative is the unbounded dictionary growth this cap exists to stop");
  }
}
