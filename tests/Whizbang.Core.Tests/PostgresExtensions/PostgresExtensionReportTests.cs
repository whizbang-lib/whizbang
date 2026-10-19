// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The startup report: one Information block per database listing exactly the usable extensions and
/// what each enables, and the unusable ones at Debug with what to do about each.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresExtensionReportTests {
  private static PostgresExtensionInfo _entry(string name) =>
    PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == name);

  private static PostgresDatabaseExtensions _mixed() => new("orders-db", [
    new(_entry("vector"), PostgresExtensionState.Installed, "0.8.0"),
    new(_entry("pg_trgm"), PostgresExtensionState.Available, null),
    new(_entry("pg_squeeze"), PostgresExtensionState.NotAllowlisted, null),
    new(_entry("pg_stat_statements"), PostgresExtensionState.NeedsPreload, "1.11"),
    new(_entry("pg_repack"), PostgresExtensionState.Missing, null),
  ], ProbeFailure: null);

  [Test]
  public async Task FormatUsable_ListsExactlyTheUsableExtensionsWithTheirPurposeAsync() {
    var text = PostgresExtensionReport.FormatUsable(_mixed());
    var lines = text.Split('\n');

    await Assert.That(lines.Length).IsEqualTo(2);
    await Assert.That(lines[0]).IsEqualTo($"  vector 0.8.0 (Installed): {_entry("vector").Enables}");
    await Assert.That(lines[1]).IsEqualTo($"  pg_trgm (Available): {_entry("pg_trgm").Enables}");
  }

  [Test]
  public async Task FormatUsable_NothingUsable_SaysNoneAsync() {
    var database = new PostgresDatabaseExtensions("orders-db",
      [new(_entry("pg_squeeze"), PostgresExtensionState.Missing, null)], null);

    await Assert.That(PostgresExtensionReport.FormatUsable(database)).IsEqualTo("  (none)");
  }

  [Test]
  public async Task FormatUnusable_ListsEachUnusableExtensionWithItsStateAndRemedyAsync() {
    var lines = PostgresExtensionReport.FormatUnusable(_mixed()).Split('\n');

    await Assert.That(lines.Length).IsEqualTo(3);
    await Assert.That(lines[0]).IsEqualTo(
      $"  pg_squeeze (NotAllowlisted): {_entry("pg_squeeze").Enables}. Fix: "
      + PostgresExtensionCatalog.Remedy(_entry("pg_squeeze"), PostgresExtensionState.NotAllowlisted));
    await Assert.That(lines[1]).StartsWith("  pg_stat_statements (NeedsPreload): ");
    await Assert.That(lines[2]).StartsWith("  pg_repack (Missing): ");
  }

  [Test]
  public async Task Log_WritesOneInformationBlockAndOneDebugBlockAsync() {
    var logger = new FakeLogger();

    PostgresExtensionReport.Log(logger, _mixed());

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count).IsEqualTo(2);
    var information = records.Single(r => r.Level == LogLevel.Information);
    await Assert.That(information.Message).Contains("'orders-db'");
    await Assert.That(information.Message).Contains("2 usable");
    await Assert.That(information.Message).Contains(PostgresExtensionReport.FormatUsable(_mixed()));
    await Assert.That(information.Message).DoesNotContain("pg_squeeze");
    var debug = records.Single(r => r.Level == LogLevel.Debug);
    await Assert.That(debug.Message).Contains(PostgresExtensionReport.FormatUnusable(_mixed()));
  }

  [Test]
  public async Task Log_EverythingUsable_WritesNoDebugBlockAsync() {
    var logger = new FakeLogger();
    var database = new PostgresDatabaseExtensions("orders-db",
      [new(_entry("vector"), PostgresExtensionState.Available, null)], null);

    PostgresExtensionReport.Log(logger, database);

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count).IsEqualTo(1);
    await Assert.That(records[0].Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task Log_InformationOnAndDebugOff_WritesOnlyTheUsableBlockAsync() {
    var logger = new FakeLogger();
    logger.ControlLevel(LogLevel.Debug, enabled: false);

    PostgresExtensionReport.Log(logger, _mixed());

    var records = logger.Collector.GetSnapshot();
    await Assert.That(records.Count).IsEqualTo(1);
    await Assert.That(records[0].Level).IsEqualTo(LogLevel.Information);
  }

  [Test]
  public async Task Log_BothLevelsOff_WritesNothingAsync() {
    var logger = new FakeLogger();
    logger.ControlLevel(LogLevel.Information, enabled: false);
    logger.ControlLevel(LogLevel.Debug, enabled: false);

    PostgresExtensionReport.Log(logger, _mixed());

    await Assert.That(logger.Collector.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Log_ADatabaseThatCouldNotBeProbed_WritesNothingAsync() {
    var logger = new FakeLogger();
    var database = PostgresDatabaseExtensions.Unknown("orders-db", PostgresExtensionCatalog.BuiltIn, "unreachable");

    PostgresExtensionReport.Log(logger, database);

    await Assert.That(logger.Collector.Count).IsEqualTo(0)
      .Because("the probe's own warning already named the database and the reason");
  }
}
