// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>One pair of the ephemeral blocker report: events of an ephemeral type kept Sourced by a durable type.</summary>
/// <param name="EphemeralType">The ephemeral event type whose history is held back, as stored.</param>
/// <param name="BlockingType">The durable event type in the same streams, as stored.</param>
/// <param name="Events">The ephemeral type's events it keeps Sourced in the type's current history walk.</param>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
public readonly record struct EphemeralBlockedCount(string EphemeralType, string BlockingType, long Events);

/// <summary>
/// The ephemeral blocker report as a gauge: <c>whizbang.ephemeral.blocked_events</c>, tagged <c>ephemeral_type</c> and
/// <c>blocking_type</c>. Meter name: <c>Whizbang.Ephemeral</c>.
/// </summary>
/// <remarks>
/// <para>
/// The ephemeral history reclassify step publishes the stored report after every cycle in which the report is on. A pair
/// that left the report (its blocking type was released, marked ephemeral or its streams were cleared) reads zero rather
/// than disappearing with its last count, so a dashboard shows the report shrink to nothing.
/// </para>
/// <para>
/// The instance also remembers whether this process has logged the startup summary of the stored report, because the
/// step itself is created per maintenance cycle.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#what-holds-ephemeral-history-back</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/EphemeralBlockerMetricsTests.cs</tests>
public sealed class EphemeralBlockerMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Ephemeral";
#pragma warning restore CA1707

  private readonly Lock _lock = new();
  private Measurement<long>[] _current = [];
  private readonly Dictionary<(string Ephemeral, string Blocking), long> _pairs = [];
  private int _summaryClaimed;

  /// <summary>Creates the meter and its gauge.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  public EphemeralBlockerMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    _ = meter.CreateObservableGauge(
      "whizbang.ephemeral.blocked_events",
      () => Volatile.Read(ref _current),
      description: "Events of an ephemeral type kept Sourced because their streams hold a durable event type, "
        + "tagged by ephemeral_type and blocking_type; zero once a pair is released or cleared");
  }

  /// <summary>Replaces the gauge's reading with the stored report; a pair published before and absent now reads zero.</summary>
  /// <param name="counts">Every pair of the stored report.</param>
  public void Publish(IEnumerable<EphemeralBlockedCount> counts) {
    ArgumentNullException.ThrowIfNull(counts);
    lock (_lock) {
      foreach (var key in _pairs.Keys.ToList()) {
        _pairs[key] = 0;
      }
      foreach (var count in counts) {
        _pairs[(count.EphemeralType, count.BlockingType)] = count.Events;
      }
      var measurements = _pairs.Select(pair => new Measurement<long>(pair.Value,
        new KeyValuePair<string, object?>("ephemeral_type", pair.Key.Ephemeral),
        new KeyValuePair<string, object?>("blocking_type", pair.Key.Blocking))).ToArray();
      Volatile.Write(ref _current, measurements);
    }
  }

  /// <summary>Whether the caller is the first in this process to ask: it then logs the startup summary.</summary>
  /// <returns><see langword="true"/> exactly once per instance.</returns>
  public bool ClaimStartupSummary() => Interlocked.Exchange(ref _summaryClaimed, 1) == 0;
}
