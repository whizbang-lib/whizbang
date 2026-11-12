// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The maintenance meter reports how much cleanup has left to do, not only what it removed (#2289).
/// </summary>
/// <remarks>
/// <c>whizbang.maintenance.rows_affected</c> says what each task removed. Without the rows still eligible, a dashboard
/// cannot tell "nothing left to clean" from "cleanup is falling behind", which is exactly the question while the sweep
/// is not being admitted. The readings are gauges, replaced whole by each reading, so a task that is no longer measured
/// stops reporting rather than repeating a stale count.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/MaintenanceMetrics.cs</code-under-test>
/// <docs>operations/observability/metrics#pending-reclamation</docs>
[Category("Observability")]
public class MaintenanceMetricsPendingTests {

  private static PendingReclamation _reading(
      Dictionary<string, PendingRows> tasks, Dictionary<string, long>? walks = null) =>
    new(tasks, walks ?? new Dictionary<string, long>(StringComparer.Ordinal));

  [Test]
  public async Task PendingRows_AreReportedPerTask_TaggedWithWhetherTheCountWasCappedAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(factory));
    using var listener = new Readings(factory);

    metrics.UpdatePendingReclamation(_reading(new Dictionary<string, PendingRows>(StringComparer.Ordinal) {
      ["purge_old_deduplication"] = new(1_000, Capped: true),
      ["purge_instance_evictions"] = new(3, Capped: false),
    }));
    var readings = listener.Read();

    await Assert.That(readings).Contains(("whizbang.maintenance.pending_rows", 1_000L, "task", "purge_old_deduplication", true))
      .Because("a count that stopped at the cap is reported as at least that many, and says so");
    await Assert.That(readings).Contains(("whizbang.maintenance.pending_rows", 3L, "task", "purge_instance_evictions", false));
    await Assert.That(readings.Count).IsEqualTo(2);
  }

  [Test]
  public async Task HistoryWalkRemaining_IsReportedPerEventTypeAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(factory));
    using var listener = new Readings(factory);

    metrics.UpdatePendingReclamation(_reading(
      new Dictionary<string, PendingRows>(StringComparer.Ordinal),
      new Dictionary<string, long>(StringComparer.Ordinal) { ["Shop.CartViewed"] = 42 }));
    var readings = listener.Read();

    await Assert.That(readings).IsEquivalentTo([("whizbang.ephemeral.history_walk_remaining", 42L, "event_type", "Shop.CartViewed", (bool?)null)]);
  }

  [Test]
  public async Task AReading_ReplacesThePreviousOne_SoAnUnmeasuredTaskStopsReportingAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new MaintenanceMetrics(new WhizbangMetrics(factory));
    using var listener = new Readings(factory);

    metrics.UpdatePendingReclamation(_reading(
      new Dictionary<string, PendingRows>(StringComparer.Ordinal) { ["purge_idle_routes"] = new(9, Capped: false) },
      new Dictionary<string, long>(StringComparer.Ordinal) { ["Shop.CartViewed"] = 42 }));
    metrics.UpdatePendingReclamation(PendingReclamation.None);
    var readings = listener.Read();

    await Assert.That(readings).IsEmpty()
      .Because("an instance that did not take the window reports nothing, so a sum across instances counts the store once");
  }

  /// <summary>Collects the maintenance meter's observable readings on demand.</summary>
  private sealed class Readings : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, long Value, string TagKey, string TagValue, bool? Capped)> _readings = [];

    public Readings(TestMeterFactory factory) {
      _listener.InstrumentPublished = (instrument, listener) => {
        if (factory.CreatedMeters.Contains(instrument.Meter) && instrument.Meter.Name == MaintenanceMetrics.METER_NAME) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
        string? key = null;
        string? name = null;
        bool? capped = null;
        foreach (var tag in tags) {
          if (tag.Key is "task" or "event_type") {
            key = tag.Key;
            name = tag.Value?.ToString();
          } else if (tag.Key == "capped") {
            capped = (bool?)tag.Value;
          }
        }
        _readings.Add((instrument.Name, value, key!, name!, capped));
      });
      _listener.Start();
    }

    public List<(string Name, long Value, string TagKey, string TagValue, bool? Capped)> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    public void Dispose() => _listener.Dispose();
  }
}
