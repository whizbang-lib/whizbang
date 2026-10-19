// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The <c>whizbang.postgres.extension.state</c> gauge on the <c>Whizbang.Postgres</c> meter: one series
/// per extension and database, value 1, tagged with the extension, the database and the state.
/// </summary>
/// <docs>operations/observability/metrics</docs>
public class PostgresExtensionMetricsTests {
  private static readonly string[] _fourSeries = [
    "orders-db/vector/Installed",
    "orders-db/pg_squeeze/NotAllowlisted",
    "audit-db/vector/Available",
    "audit-db/pg_squeeze/Missing",
  ];

  private sealed class FixedCapabilities(IReadOnlyList<PostgresDatabaseExtensions> databases) : IPostgresCapabilities {
    public IReadOnlyList<PostgresDatabaseExtensions> Databases { get; set; } = databases;
    public PostgresExtensionState GetState(string database, string extension) => PostgresExtensionState.Unknown;
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

  private static PostgresExtensionInfo _entry(string name) =>
    PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == name);

  private static WhizbangMetrics _whizbangMetrics(ServiceProvider provider) =>
    new(provider.GetRequiredService<IMeterFactory>());

  [Test]
  public async Task Gauge_ReportsOneSeriesPerExtensionAndDatabaseAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var capabilities = new FixedCapabilities([
      new("orders-db", [
        new(_entry("vector"), PostgresExtensionState.Installed, "0.8.0"),
        new(_entry("pg_squeeze"), PostgresExtensionState.NotAllowlisted, null),
      ], null),
      new("audit-db", [
        new(_entry("vector"), PostgresExtensionState.Available, null),
        new(_entry("pg_squeeze"), PostgresExtensionState.Missing, null),
      ], null),
    ]);
    var metrics = new PostgresExtensionMetrics(_whizbangMetrics(provider), capabilities);

    var series = GaugeRecorder.Collect(metrics.ExtensionState);

    await Assert.That(series.Count).IsEqualTo(4);
    await Assert.That(series.All(s => s.Value == 1)).IsTrue();
    var observed = series.Select(s => $"{s.Tags["database"]}/{s.Tags["extension"]}/{s.Tags["state"]}").ToArray();
    await Assert.That(observed).IsEquivalentTo(_fourSeries);
  }

  [Test]
  public async Task Gauge_ReadsTheLatestAnswerAtEachCollectionAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var capabilities = new FixedCapabilities([
      new("orders-db", [new(_entry("vector"), PostgresExtensionState.Unknown, null)], "not probed yet"),
    ]);
    var metrics = new PostgresExtensionMetrics(_whizbangMetrics(provider), capabilities);
    capabilities.Databases = [new("orders-db", [new(_entry("vector"), PostgresExtensionState.Installed, "0.8.0")], null)];

    var series = GaugeRecorder.Collect(metrics.ExtensionState);

    await Assert.That((string?)series.Single().Tags["state"]).IsEqualTo("Installed");
  }

  [Test]
  public async Task Gauge_IsNamedAndDescribedOnTheWhizbangPostgresMeterAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new PostgresExtensionMetrics(_whizbangMetrics(provider), new FixedCapabilities([]));

    await Assert.That(metrics.ExtensionState.Name).IsEqualTo("whizbang.postgres.extension.state");
    await Assert.That(metrics.ExtensionState.Meter.Name).IsEqualTo("Whizbang.Postgres");
  }

  [Test]
  public async Task TurnkeyMeterList_CarriesTheWhizbangPostgresMeterOnceAsync() {
    await Assert.That(WhizbangMeters.All.Count(n => n == PostgresExtensionMetrics.METER_NAME)).IsEqualTo(1)
      .Because("a consumer wiring AddMeter(WhizbangMeters.All) must export the extension gauge without a hand list");
  }
}
