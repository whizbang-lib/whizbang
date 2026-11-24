// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// The held-age gauge (#2542): how long the oldest outbox event this instance holds for its commit-order stamp has
/// waited. Refreshed by <see cref="SequenceHoldWorker"/>. Meter name: <c>Whizbang.SequenceIntegrity</c>.
/// </summary>
/// <docs>fundamentals/messaging/sequence-integrity#watching-held-messages</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Observability/SequenceHoldWorkerTests.cs</tests>
public sealed class SequenceHoldMetrics {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.SequenceIntegrity";

  /// <summary>The gauge's instrument name.</summary>
  public const string OLDEST_AGE_SECONDS = "whizbang.outbox.sequence_hold.oldest_age_seconds";
#pragma warning restore CA1707

  // Seconds, or NaN while the age is unknown (no reading yet, or the last one failed).
  private double _oldestAgeSeconds = double.NaN;

  /// <summary>Creates the gauge on the shared Whizbang meter.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public SequenceHoldMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    meter.CreateObservableGauge(
      OLDEST_AGE_SECONDS,
      observeValues: _observe,
      unit: "s",
      description: "Age of the oldest outbox event this instance holds for its commit-order stamp; zero when it holds none");
  }

  private IEnumerable<Measurement<double>> _observe() {
    var seconds = Volatile.Read(ref _oldestAgeSeconds);
    return double.IsNaN(seconds) ? [] : [new Measurement<double>(seconds)];
  }

  /// <summary>Records one reading: the oldest held event's age, zero when none is held, or null when it is unknown.</summary>
  /// <param name="oldestAge">The reading.</param>
  public void Record(TimeSpan? oldestAge) =>
    Volatile.Write(ref _oldestAgeSeconds, oldestAge?.TotalSeconds ?? double.NaN);
}
