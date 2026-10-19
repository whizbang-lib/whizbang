// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <c>Whizbang.Postgres</c> meter: <c>whizbang.postgres.extension.state</c>, an observable gauge with
/// one series per catalog extension and database, value 1, tagged <c>extension</c>, <c>database</c> and
/// <c>state</c>.
/// </summary>
/// <remarks>
/// Value 1 on a state tag is the shape that lets a dashboard count, filter or alert by state ("any
/// extension NotAllowlisted on any database") without decoding a number. Each collection reads the
/// latest answer from <see cref="IPostgresCapabilities"/>, so a refresh shows on the next scrape.
/// </remarks>
/// <docs>operations/observability/metrics#postgres</docs>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresExtensionMetricsTests.cs</tests>
public sealed class PostgresExtensionMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Postgres";
#pragma warning restore CA1707

  /// <summary>Creates the gauge over the capabilities it reports.</summary>
  /// <param name="whizbangMetrics">The shared meter factory owner.</param>
  /// <param name="capabilities">The answers the gauge reports.</param>
  public PostgresExtensionMetrics(WhizbangMetrics whizbangMetrics, IPostgresCapabilities capabilities) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    ArgumentNullException.ThrowIfNull(capabilities);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    ExtensionState = meter.CreateObservableGauge(
      "whizbang.postgres.extension.state",
      observeValues: () => capabilities.Databases.SelectMany(database => database.Extensions.Select(status =>
        new Measurement<int>(1,
          new KeyValuePair<string, object?>("extension", status.Extension.Name),
          new KeyValuePair<string, object?>("database", database.Database),
          new KeyValuePair<string, object?>("state", status.State.ToString())))),
      description: "One series per Postgres extension and database, value 1, tagged with the extension's state");
  }

  /// <summary>The <c>whizbang.postgres.extension.state</c> gauge.</summary>
  public ObservableGauge<int> ExtensionState { get; }
}
