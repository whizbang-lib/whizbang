using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Locks the v0.660 Slice 8 invariants around <see cref="InboxMetrics"/> — the
/// instrumentation the inbox dispatch worker uses to record per-message wall
/// time. Slot-3 import surfaced 2,704 <c>DraftJobCompetencyRowAddedEvent</c>
/// rows draining at ~27 rows/sec; without a per-message-type histogram, an
/// operator can't tell whether the bottleneck is one slow event type or the
/// pipeline overall.
/// </summary>
/// <docs>operations/observability/metrics</docs>
public class InboxMetricsTests {

  private static InboxMetrics _newMetrics() =>
    new(new WhizbangMetrics(meterFactory: null));

  /// <summary>
  /// The base contract: <see cref="InboxMetrics.RecordDispatch"/> writes one
  /// observation onto the histogram, tagged with the (shortened) message_type.
  /// Operator dashboards group by this tag to identify slow event types.
  /// </summary>
  [Test]
  public async Task RecordDispatch_RecordsOneObservation_TaggedWithShortMessageTypeAsync() {
    var metrics = _newMetrics();
    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.DispatchDuration)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray()));
    listener.Start();

    metrics.RecordDispatch("Sample.Namespace.MyEvent, Sample.Assembly, Version=0.0.0.0, Culture=neutral", durationMs: 42.0);

    listener.Dispose();
    await Assert.That(observed.Count).IsEqualTo(1);
    var tag = observed[0].FirstOrDefault(kv => kv.Key == "message_type");
    await Assert.That((string?)tag.Value).IsEqualTo("MyEvent")
      .Because("Tag MUST be the short class name (last segment after the final '.' or '+', before any AQN comma) so cardinality stays bounded — full AQNs blow up the metrics backend.");
  }

  /// <summary>
  /// Nested type AQNs use '+' as the inner separator. The shortening must
  /// strip everything up to and including the final '+', not just '.'.
  /// </summary>
  [Test]
  public async Task RecordDispatch_NestedTypeUsesPlusSeparator_ShortensCorrectlyAsync() {
    var metrics = _newMetrics();
    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i, metrics.DispatchDuration)) l.EnableMeasurementEvents(i); };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray()));
    listener.Start();

    metrics.RecordDispatch("JDX.Contracts.Job.DraftJobCompetencyContracts+DraftJobCompetencyRowAddedEvent, JDX.Contracts", durationMs: 100.0);

    listener.Dispose();
    var tag = (string?)observed[0].FirstOrDefault(kv => kv.Key == "message_type").Value;
    await Assert.That(tag).IsEqualTo("DraftJobCompetencyRowAddedEvent")
      .Because("Nested types use '+' between outer and inner — operators care about the actual event class (the inner), not its container.");
  }

  /// <summary>
  /// Plain type name with no namespace + no AQN: passes through unchanged.
  /// </summary>
  [Test]
  public async Task RecordDispatch_PlainTypeName_PassesThroughUnchangedAsync() {
    var metrics = _newMetrics();
    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i, metrics.DispatchDuration)) l.EnableMeasurementEvents(i); };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray()));
    listener.Start();

    metrics.RecordDispatch("CreateOrder", durationMs: 5.0);

    listener.Dispose();
    var tag = (string?)observed[0].FirstOrDefault(kv => kv.Key == "message_type").Value;
    await Assert.That(tag).IsEqualTo("CreateOrder");
  }

  /// <summary>
  /// Empty or null message type: tagged with a stable sentinel so operators can
  /// spot mis-instrumented call sites without losing the observation.
  /// </summary>
  [Test]
  [Arguments("")]
  [Arguments(null)]
  public async Task RecordDispatch_EmptyOrNullMessageType_TaggedWithSentinelAsync(string? messageType) {
    var metrics = _newMetrics();
    var observed = new List<KeyValuePair<string, object?>[]>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i, metrics.DispatchDuration)) l.EnableMeasurementEvents(i); };
    listener.SetMeasurementEventCallback<double>((_, _, tags, _) => observed.Add(tags.ToArray()));
    listener.Start();

    metrics.RecordDispatch(messageType!, durationMs: 1.0);

    listener.Dispose();
    var tag = (string?)observed[0].FirstOrDefault(kv => kv.Key == "message_type").Value;
    await Assert.That(tag).IsEqualTo("<unknown>")
      .Because("Empty/null message_type signals a mis-instrumented call site — surface it explicitly rather than silently dropping the observation.");
  }
}
