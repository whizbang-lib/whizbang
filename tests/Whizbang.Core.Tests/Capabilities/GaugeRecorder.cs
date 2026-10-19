// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>One observed series of a gauge: its value and its tags.</summary>
internal sealed record GaugeSeries(int Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>
/// Collects one observable gauge once, synchronously. The listener enables only the instrument it is
/// given, by identity, so gauges other tests create on the same meter name never reach it.
/// </summary>
internal static class GaugeRecorder {
  public static IReadOnlyList<GaugeSeries> Collect(ObservableGauge<int> gauge) {
    var series = new List<GaugeSeries>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, gauge)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<int>((_, value, tags, _) => {
      var map = new Dictionary<string, object?>(StringComparer.Ordinal);
      foreach (var tag in tags) {
        map[tag.Key] = tag.Value;
      }
      series.Add(new GaugeSeries(value, map));
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return series;
  }
}
