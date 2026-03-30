using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// AOT-compatible helper that uses MeterListener to collect recorded metric values.
/// Uses an isolated IMeterFactory per test to avoid cross-test interference.
/// </summary>
public sealed class MetricAssertionHelper : IDisposable {
  private readonly MeterListener _listener;
  private readonly HashSet<Meter> _trackedMeters = [];
  private readonly List<RecordedMeasurement> _measurements = [];

  public IReadOnlyList<RecordedMeasurement> Measurements => _measurements;

  /// <summary>
  /// Creates a helper that tracks measurements only from the specified Meter instances.
  /// </summary>
  public MetricAssertionHelper(params Meter[] meters) {
    foreach (var m in meters) {
      _trackedMeters.Add(m);
    }

    _listener = new MeterListener {
      InstrumentPublished = (instrument, listener) => {
        if (_trackedMeters.Contains(instrument.Meter)) {
          listener.EnableMeasurementEvents(instrument);
        }
      }
    };

    _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.Start();
  }

  /// <summary>
  /// Creates a helper that tracks measurements by meter name (for simple/isolated tests).
  /// </summary>
  public MetricAssertionHelper(params string[] meterNames) {
    var meterNameSet = new HashSet<string>(meterNames);
    _listener = new MeterListener {
      InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter.Name != null && meterNameSet.Contains(instrument.Meter.Name)) {
          listener.EnableMeasurementEvents(instrument);
        }
      }
    };

    _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => {
      _measurements.Add(new RecordedMeasurement(instrument.Name, value, _extractTags(tags)));
    });

    _listener.Start();
  }

  public List<RecordedMeasurement> GetByName(string instrumentName) {
    var results = new List<RecordedMeasurement>();
    foreach (var m in _measurements) {
      if (m.InstrumentName == instrumentName) {
        results.Add(m);
      }
    }
    return results;
  }

  public void Dispose() {
    _listener.Dispose();
  }

  private static Dictionary<string, string> _extractTags(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    var dict = new Dictionary<string, string>();
    foreach (var tag in tags) {
      dict[tag.Key] = tag.Value?.ToString() ?? "";
    }
    return dict;
  }
}

/// <summary>
/// A single recorded measurement from the MeterListener.
/// </summary>
public sealed record RecordedMeasurement(string InstrumentName, double Value, Dictionary<string, string> Tags);

/// <summary>
/// Test-isolated IMeterFactory that creates unique Meter instances per test.
/// Exposes the created Meter for use with MetricAssertionHelper.
/// </summary>
public sealed class TestMeterFactory : IMeterFactory {
  private readonly List<Meter> _meters = [];

  public IReadOnlyList<Meter> CreatedMeters => _meters;
  public List<string> CreatedMeterNames { get; } = [];

  public Meter Create(MeterOptions options) {
    var meter = new Meter(options);
    _meters.Add(meter);
    CreatedMeterNames.Add(options.Name);
    return meter;
  }

  public void Dispose() {
    foreach (var meter in _meters) {
      meter.Dispose();
    }
  }
}
