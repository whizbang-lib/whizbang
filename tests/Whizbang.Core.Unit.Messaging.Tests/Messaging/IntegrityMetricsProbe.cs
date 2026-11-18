// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>A real <see cref="StreamIntegrityMetrics"/> and a way to read its passive counters' totals.</summary>
internal sealed class IntegrityMetricsProbe : IDisposable {
  private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();
  private readonly MeterListener _listener = new();
  private readonly Dictionary<string, long> _totals = [];

  public IntegrityMetricsProbe() {
    Metrics = new StreamIntegrityMetrics(new WhizbangMetrics(meterFactory: _meters.GetRequiredService<IMeterFactory>()));
    var meter = Metrics.IntegrityDeliveriesQueued.Meter;
    _listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument.Meter, meter)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
      _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value);
    _listener.Start();
  }

  public StreamIntegrityMetrics Metrics { get; }

  /// <summary>The counter's total across every tag series, read now.</summary>
  public long Total(PassiveCounter<long> counter) {
    _totals.Clear();
    _listener.RecordObservableInstruments();
    return _totals.GetValueOrDefault(counter.Name);
  }

  public void Dispose() {
    _listener.Dispose();
    _meters.Dispose();
  }
}
