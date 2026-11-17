// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// What each framework mechanism costs the database: statements run, the time they held the connection, and the
/// rows they returned or changed, each tagged by <c>mechanism</c>. Meter name: <c>Whizbang.Database</c>.
/// </summary>
/// <remarks>
/// <para>
/// Static, because the statement hook sits where a command is built and executed, which is below anything injected.
/// The meter is a plain one under its name, which an OpenTelemetry provider listening by name (see
/// <see cref="WhizbangMeters.All"/>) still sees.
/// </para>
/// <para>
/// The <c>mechanism</c> tag is a <see cref="DbMechanism"/>, a closed set, so the three instruments hold one series
/// per mechanism at most. <see cref="DbMechanism.Untagged"/> counts the framework statements no scope claimed.
/// </para>
/// </remarks>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/DbStatementMetricsTests.cs</tests>
public static class DbStatementMetrics {
#pragma warning disable CA1707
  /// <summary>OpenTelemetry meter name.</summary>
  public const string METER_NAME = "Whizbang.Database";

  /// <summary>The counter of statements run. Tagged by <c>mechanism</c>.</summary>
  public const string STATEMENTS = "whizbang.db.statements";

  /// <summary>The histogram of statement durations in milliseconds. Tagged by <c>mechanism</c>.</summary>
  public const string DURATION = "whizbang.db.duration";

  /// <summary>The counter of rows returned or changed. Tagged by <c>mechanism</c>.</summary>
  public const string ROWS = "whizbang.db.rows";

  /// <summary>The tag naming the mechanism.</summary>
  public const string MECHANISM_TAG = "mechanism";
#pragma warning restore CA1707

  private static readonly Meter _meter = new(METER_NAME);
  private static readonly Counter<long> _statements = _meter.CreateCounter<long>(
    STATEMENTS, description: "Database statements a framework mechanism ran; tagged by mechanism");
  private static readonly Histogram<double> _duration = _meter.CreateHistogram<double>(
    DURATION, unit: "ms", description: "How long a framework mechanism's statement held its connection; tagged by mechanism");
  private static readonly Counter<long> _rows = _meter.CreateCounter<long>(
    ROWS, description: "Rows a framework mechanism's statements returned or changed; tagged by mechanism");

  /// <summary>Records one statement.</summary>
  /// <param name="mechanism">The mechanism that ran it; <see cref="DbMechanism.Untagged"/> when none was open.</param>
  /// <param name="duration">How long it held the connection.</param>
  /// <param name="rows">Rows it returned or changed; a negative count (unknown) records none.</param>
  public static void Record(DbMechanism mechanism, TimeSpan duration, long rows) {
    ArgumentNullException.ThrowIfNull(mechanism);
    var tag = new KeyValuePair<string, object?>(MECHANISM_TAG, mechanism.Name);
    _statements.Add(1, tag);
    _duration.Record(duration.TotalMilliseconds, tag);
    _rows.Add(Math.Max(0, rows), tag);
  }
}
