// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The capabilities singleton: one answer per registered database, Unknown until probed, Unknown for a
/// database that cannot be reached (with a warning naming it), and never a throw.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresCapabilitiesTests {
  private static readonly string[] _twoDatabases = ["orders-db", "audit-db"];

  private static PostgresExtensionProbeTarget _unreachable(string database, string message = "connection refused") =>
    new(database, _ => throw new NpgsqlException(message));

  [Test]
  public async Task BeforeTheFirstProbe_EveryDatabaseReadsUnknownAsync() {
    var capabilities = new PostgresCapabilities(
      [_unreachable("orders-db"), _unreachable("audit-db")], NullLogger<PostgresCapabilities>.Instance);

    await Assert.That(capabilities.Databases.Select(d => d.Database)).IsEquivalentTo(_twoDatabases);
    foreach (var database in capabilities.Databases) {
      await Assert.That(database.ProbeFailure).IsEqualTo(PostgresCapabilities.NOT_PROBED);
      await Assert.That(database.Extensions.Count).IsEqualTo(PostgresExtensionCatalog.BuiltIn.Count);
      await Assert.That(database.Extensions.All(e => e.State == PostgresExtensionState.Unknown)).IsTrue();
    }
  }

  [Test]
  public async Task ADatabaseRegisteredTwice_IsProbedOnceAsync() {
    var capabilities = new PostgresCapabilities(
      [_unreachable("orders-db"), _unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance);

    await Assert.That(capabilities.Databases.Count).IsEqualTo(1);
  }

  [Test]
  public async Task GetState_UnknownDatabase_IsUnknownAsync() {
    var capabilities = new PostgresCapabilities([_unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance);

    await Assert.That(capabilities.GetState("other-db", "vector")).IsEqualTo(PostgresExtensionState.Unknown);
  }

  [Test]
  public async Task GetState_ExtensionNotInTheCatalog_IsUnknownAsync() {
    var capabilities = new PostgresCapabilities([_unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance);

    await Assert.That(capabilities.GetState("orders-db", "not_an_extension")).IsEqualTo(PostgresExtensionState.Unknown);
  }

  [Test]
  public async Task GetState_UsesTheCatalogItWasGivenAsync() {
    var own = new PostgresExtensionInfo("my_ext", "something", ["A third-party feature"], true, PostgresExtensionPrerequisites.Allowlist);
    var capabilities = new PostgresCapabilities([_unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance, catalog: [own]);

    await Assert.That(capabilities.Databases[0].Extensions.Single().Extension).IsEqualTo(own);
    await Assert.That(capabilities.GetState("orders-db", "MY_EXT")).IsEqualTo(PostgresExtensionState.Unknown)
      .Because("extension names are compared without regard to case");
  }

  [Test]
  public async Task RefreshAsync_UnreachableDatabase_IsUnknownAndWarnsNamingTheDatabaseAsync() {
    var logger = new FakeLogger<PostgresCapabilities>();
    var capabilities = new PostgresCapabilities([_unreachable("orders-db", "no route to host")], logger);

    await capabilities.RefreshAsync(CancellationToken.None);

    var database = capabilities.Databases.Single();
    await Assert.That(database.ProbeFailure).Contains("no route to host");
    await Assert.That(capabilities.GetState("orders-db", "vector")).IsEqualTo(PostgresExtensionState.Unknown);
    var warning = logger.Collector.GetSnapshot().Single();
    await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warning.Message).Contains("orders-db");
    await Assert.That(warning.Message).Contains("no route to host");
  }

  [Test]
  public async Task WithMetrics_TheGaugeReportsTheRegisteredDatabasesAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var whizbangMetrics = new WhizbangMetrics(provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
    var capabilities = new PostgresCapabilities([_unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance, whizbangMetrics);

    var series = GaugeRecorder.Collect(capabilities.Metrics!.ExtensionState);

    await Assert.That(series.Count).IsEqualTo(PostgresExtensionCatalog.BuiltIn.Count);
    await Assert.That(series.All(s => (string?)s.Tags["state"] == "Unknown")).IsTrue();
  }

  [Test]
  public async Task WithoutMetrics_NoGaugeIsCreatedAsync() {
    var capabilities = new PostgresCapabilities([_unreachable("orders-db")], NullLogger<PostgresCapabilities>.Instance);

    await Assert.That(capabilities.Metrics).IsNull();
  }
}
