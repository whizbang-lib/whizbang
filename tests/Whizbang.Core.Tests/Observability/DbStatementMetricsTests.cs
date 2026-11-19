// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Each statement a mechanism runs is counted, timed and its rows added, under the mechanism's name.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/DbStatementMetrics.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
[Category("Observability")]
public class DbStatementMetricsTests {
  [Test]
  public async Task ARecordedStatement_IsCountedTimedAndItsRowsAdded_UnderItsMechanismAsync() {
    using var recorder = new DbStatementRecorder();

    DbStatementMetrics.Record(DbMechanism.IntegrityAudit, TimeSpan.FromMilliseconds(12.5), 7);

    await Assert.That(recorder.Measurements).IsEquivalentTo(new List<DbStatementRecorder.Measurement> {
      new(DbStatementMetrics.STATEMENTS, 1, "integrity_audit"),
      new(DbStatementMetrics.DURATION, 12.5, "integrity_audit"),
      new(DbStatementMetrics.ROWS, 7, "integrity_audit"),
    });
  }

  [Test]
  public async Task AnUnknownRowCount_AddsNoRowsAsync() {
    using var recorder = new DbStatementRecorder();

    DbStatementMetrics.Record(DbMechanism.Untagged, TimeSpan.Zero, -1);

    await Assert.That(recorder.Measurements.Single(m => m.Instrument == DbStatementMetrics.ROWS).Value).IsEqualTo(0d);
  }

  [Test]
  public async Task Record_RefusesANullMechanismAsync() {
    await Assert.That(() => DbStatementMetrics.Record(null!, TimeSpan.Zero, 0)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheMeter_IsOneAnOpenTelemetryProviderListensToAsync() {
    await Assert.That(WhizbangMeters.All).Contains(DbStatementMetrics.METER_NAME);
  }
}

/// <summary>
/// Collects the statement measurements recorded on the flow that created it. The meter is process-wide and tests run
/// in parallel; a measurement callback runs on the recording flow, so a flow marker tells this test's statements from
/// every other test's.
/// </summary>
internal sealed class DbStatementRecorder : IDisposable {
  internal readonly record struct Measurement(string Instrument, double Value, string? Mechanism);

  private static readonly AsyncLocal<object?> _flow = new();
  private readonly object _marker = new();
  private readonly MeterListener _listener = new();
  private readonly List<Measurement> _measurements = [];

  public DbStatementRecorder() {
    _flow.Value = _marker;
    _listener.InstrumentPublished = (instrument, listener) => {
      if (instrument.Meter.Name == DbStatementMetrics.METER_NAME) {
        listener.EnableMeasurementEvents(instrument);
      }
    };
    _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => _add(i, v, tags));
    _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => _add(i, v, tags));
    _listener.Start();
  }

  public IReadOnlyList<Measurement> Measurements {
    get {
      lock (_measurements) {
        return [.. _measurements];
      }
    }
  }

  private void _add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    if (!ReferenceEquals(_flow.Value, _marker)) {
      return;
    }
    string? mechanism = null;
    foreach (var tag in tags) {
      if (tag.Key == DbStatementMetrics.MECHANISM_TAG) {
        mechanism = tag.Value as string;
      }
    }
    lock (_measurements) {
      _measurements.Add(new Measurement(instrument.Name, value, mechanism));
    }
  }

  public void Dispose() => _listener.Dispose();
}
