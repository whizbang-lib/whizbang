// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A meter factory for one test, and a reader of the <c>whizbang.probes.ticks</c> series it holds: the idle and work
/// tick totals of one probe (#2346).
/// </summary>
internal sealed class ProbeTickMeters : IMeterFactory {
  private readonly List<Meter> _meters = [];

  public Meter Create(MeterOptions options) {
    var meter = new Meter(options);
    _meters.Add(meter);
    return meter;
  }

  public void Dispose() {
    foreach (var meter in _meters) {
      meter.Dispose();
    }
  }

  /// <summary>The probe's idle and work tick totals.</summary>
  public (long Idle, long Work) Read(string probe) {
    long idle = 0, work = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (_meters.Contains(instrument.Meter) && instrument.Name == "whizbang.probes.ticks") {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      string? p = null, o = null;
      foreach (var tag in tags) {
        if (tag.Key == ProbeCadenceMetrics.PROBE_TAG) { p = tag.Value?.ToString(); }
        if (tag.Key == ProbeCadenceMetrics.OUTCOME_TAG) { o = tag.Value?.ToString(); }
      }
      if (p == probe && o == ProbeCadenceMetrics.OUTCOME_IDLE) { idle = value; }
      if (p == probe && o == ProbeCadenceMetrics.OUTCOME_WORK) { work = value; }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return (idle, work);
  }
}
