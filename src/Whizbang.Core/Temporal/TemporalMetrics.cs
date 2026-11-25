// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Temporal;

/// <summary>
/// The temporal engine's overdue gauges (#2575): how many Active schedules are overdue beyond
/// <see cref="TemporalOptions.OverdueWarningMilliseconds"/>, and how long ago the oldest of them was due. Refreshed by
/// <see cref="ScheduleWorker"/> on every drain pass. A schedule that no instance fires shows here instead of nowhere.
/// Meter name: <c>Whizbang.Temporal</c>.
/// </summary>
/// <docs>fundamentals/temporal/temporal-engine#a-schedule-that-does-not-fire-is-reported</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Temporal/ScheduleWorkerOverdueTests.cs</tests>
public sealed class TemporalMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Temporal";

  /// <summary>The overdue-count gauge's instrument name.</summary>
  public const string OVERDUE_COUNT = "whizbang.temporal.schedules.overdue";

  /// <summary>The oldest-overdue-age gauge's instrument name.</summary>
  public const string OVERDUE_OLDEST_AGE_SECONDS = "whizbang.temporal.schedules.overdue.oldest_age_seconds";
#pragma warning restore CA1707

  // The last reading, or null while it is unknown (no reading yet, or the last one failed).
  private ScheduleOverdueReading? _reading;
  private readonly Lock _gate = new();

  /// <summary>Creates the gauges on the shared Whizbang meter.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public TemporalMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    meter.CreateObservableGauge(
      OVERDUE_COUNT,
      observeValues: () => _observe(static r => r.Count),
      unit: "{schedule}",
      description: "Active schedules overdue beyond the warning bound that no instance has fired; zero when none is");
    meter.CreateObservableGauge(
      OVERDUE_OLDEST_AGE_SECONDS,
      observeValues: () => _observe(static r => r.OldestAge.TotalSeconds),
      unit: "s",
      description: "How long ago the oldest overdue schedule was due; zero when none is overdue");
  }

  private IEnumerable<Measurement<double>> _observe(Func<ScheduleOverdueReading, double> select) {
    ScheduleOverdueReading? reading;
    lock (_gate) {
      reading = _reading;
    }
    return reading is { } r ? [new Measurement<double>(select(r))] : [];
  }

  /// <summary>Records one reading, or null when it is unknown (the gauges then report nothing).</summary>
  /// <param name="reading">The reading.</param>
  public void Record(ScheduleOverdueReading? reading) {
    lock (_gate) {
      _reading = reading;
    }
  }
}
