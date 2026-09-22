using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Reads the current values of a passive counter's series from one specific <see cref="Meter"/>.
/// Passive counters are observable: nothing is emitted until a listener polls, so the reader
/// attaches a listener filtered by meter identity (a name filter would also collect every other
/// live instance of the same metrics class), polls once, and returns what it saw.
/// </summary>
internal static class ProbeMeterReader {

  /// <summary>Every series of <paramref name="instrumentName"/> on <paramref name="meter"/>, with its tags.</summary>
  /// <param name="meter">The meter to read.</param>
  /// <param name="instrumentName">The instrument to read.</param>
  /// <returns>The observed series.</returns>
  public static List<(long Value, Dictionary<string, object?> Tags)> ReadSeries(Meter meter, string instrumentName) {
    var seen = new List<(long, Dictionary<string, object?>)>();
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Meter == meter && instrument.Name == instrumentName) {
          l.EnableMeasurementEvents(instrument);
        }
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
      foreach (var tag in tags) {
        dict[tag.Key] = tag.Value;
      }
      lock (seen) { seen.Add((value, dict)); }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    lock (seen) { return [.. seen]; }
  }

  /// <summary>The probe tick series keyed by (probe, outcome).</summary>
  /// <param name="meter">The meter to read.</param>
  /// <param name="instrumentName">The instrument to read.</param>
  /// <returns>Value per (probe, outcome) pair.</returns>
  public static Dictionary<(string Probe, string Outcome), long> Read(Meter meter, string instrumentName) {
    var result = new Dictionary<(string, string), long>();
    foreach (var (value, tags) in ReadSeries(meter, instrumentName)) {
      var probe = tags.TryGetValue(ProbeCadenceMetrics.PROBE_TAG, out var p) ? p?.ToString() ?? "" : "";
      var outcome = tags.TryGetValue(ProbeCadenceMetrics.OUTCOME_TAG, out var o) ? o?.ToString() ?? "" : "";
      result[(probe, outcome)] = value;
    }
    return result;
  }

  /// <summary>The sum of every series of <paramref name="instrumentName"/>.</summary>
  /// <param name="meter">The meter to read.</param>
  /// <param name="instrumentName">The instrument to read.</param>
  /// <returns>The total across tags.</returns>
  public static long ReadTotal(Meter meter, string instrumentName) {
    long total = 0;
    foreach (var (value, _) in ReadSeries(meter, instrumentName)) {
      total += value;
    }
    return total;
  }
}
