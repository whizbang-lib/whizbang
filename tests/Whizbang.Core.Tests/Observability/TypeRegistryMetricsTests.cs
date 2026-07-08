using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// Tests for <see cref="TypeRegistryMetrics"/> — the counters the pinned-type rename reconcile emits at startup so
/// acknowledged renames and un-acknowledged drift are queryable + alertable across the fleet.
/// </summary>
/// <docs>fundamentals/identity/pinned-type-ledger</docs>
public class TypeRegistryMetricsTests {

  private static TypeRegistryMetrics _newMetrics() => new(new WhizbangMetrics(meterFactory: null));

  private static (List<long> values, List<KeyValuePair<string, object?>[]> tags) _capture(
      TypeRegistryMetrics metrics, Counter<long> instrument, System.Action act) {
    var values = new List<long>();
    var tags = new List<KeyValuePair<string, object?>[]>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (inst, l) => {
      if (ReferenceEquals(inst, instrument)) {
        l.EnableMeasurementEvents(inst);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, measurement, t, _) => {
      values.Add(measurement);
      tags.Add(t.ToArray());
    });
    listener.Start();
    act();
    listener.Dispose();
    return (values, tags);
  }

  [Test]
  public async Task Record_AcknowledgedRenames_IncrementsRenamedCounterTaggedWithServiceAsync() {
    var metrics = _newMetrics();
    var (values, tags) = _capture(metrics, metrics.Renamed, () => metrics.Record(renamed: 3, driftDetected: 0, service: "Job.Service"));

    await Assert.That(values).Count().IsEqualTo(1);
    await Assert.That(values[0]).IsEqualTo(3L);
    var svc = tags[0].FirstOrDefault(kv => kv.Key == "service");
    await Assert.That((string?)svc.Value).IsEqualTo("Job.Service");
  }

  [Test]
  public async Task Record_UnacknowledgedDrift_IncrementsDriftCounterAsync() {
    var metrics = _newMetrics();
    var (values, _) = _capture(metrics, metrics.DriftDetected, () => metrics.Record(renamed: 0, driftDetected: 2, service: "Bff.Service"));

    await Assert.That(values).Count().IsEqualTo(1);
    await Assert.That(values[0]).IsEqualTo(2L);
  }

  [Test]
  public async Task Record_ZeroCounts_EmitsNothingAsync() {
    var metrics = _newMetrics();
    var (renamed, _) = _capture(metrics, metrics.Renamed, () => metrics.Record(renamed: 0, driftDetected: 0, service: "Svc"));
    var (drift, _) = _capture(metrics, metrics.DriftDetected, () => metrics.Record(renamed: 0, driftDetected: 0, service: "Svc"));

    await Assert.That(renamed).IsEmpty();
    await Assert.That(drift).IsEmpty();
  }

  [Test]
  public async Task Record_EmptyServiceName_TaggedUnknownAsync() {
    var metrics = _newMetrics();
    var (_, tags) = _capture(metrics, metrics.Renamed, () => metrics.Record(renamed: 1, driftDetected: 0, service: ""));

    var svc = tags[0].FirstOrDefault(kv => kv.Key == "service");
    await Assert.That((string?)svc.Value).IsEqualTo("<unknown>");
  }
}
