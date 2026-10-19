// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The startup step: after the migration step, on every instance, never blocking readiness. It probes
/// every database and writes the report, or is skipped when the probe is turned off.
/// </summary>
/// <docs>operations/infrastructure/postgres-extensions</docs>
public class PostgresExtensionsStartupStepTests {
  private sealed class CountingCapabilities(IReadOnlyList<PostgresDatabaseExtensions> afterRefresh) : IPostgresCapabilities {
    public int Refreshes { get; private set; }
    public IReadOnlyList<PostgresDatabaseExtensions> Databases { get; private set; } = [];
    public PostgresExtensionState GetState(string database, string extension) => PostgresExtensionState.Unknown;
    public Task RefreshAsync(CancellationToken cancellationToken) {
      Refreshes++;
      Databases = afterRefresh;
      return Task.CompletedTask;
    }
  }

  private static PostgresExtensionInfo _entry(string name) =>
    PostgresExtensionCatalog.BuiltIn.Single(e => e.Name == name);

  private static PostgresExtensionsStartupStep _step(IPostgresCapabilities capabilities, bool probe, FakeLogger<PostgresExtensionsStartupStep>? logger = null) =>
    new(capabilities, Options.Create(new PostgresExtensionsOptions { ProbeOnStartup = probe }),
      logger ?? new FakeLogger<PostgresExtensionsStartupStep>());

  [Test]
  public async Task Descriptor_RunsAfterMigrateOnEveryInstanceWithoutBlockingReadinessAsync() {
    var descriptor = _step(new CountingCapabilities([]), probe: true).Descriptor;

    await Assert.That(descriptor.Name).IsEqualTo(PostgresExtensionsStartupStep.STEP_NAME);
    await Assert.That(descriptor.DependsOn.Single()).IsEqualTo(FrameworkStartupSteps.MIGRATE);
    await Assert.That(descriptor.RequiredCapability).IsEqualTo(StartupCapabilities.EVERY_INSTANCE);
    await Assert.That(descriptor.Blocking).IsFalse()
      .Because("what the server offers informs; it never holds a service back from ready");
  }

  [Test]
  public async Task ProbeTurnedOff_IsSkippedWithoutProbingAsync() {
    var capabilities = new CountingCapabilities([]);

    var report = await _step(capabilities, probe: false).ExecuteAsync(CancellationToken.None);

    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Skipped);
    await Assert.That(report.Reason).Contains("Whizbang:PostgresExtensions:ProbeOnStartup");
    await Assert.That(capabilities.Refreshes).IsEqualTo(0);
  }

  [Test]
  public async Task ProbeOn_ProbesOnceAndReportsEachDatabaseAsync() {
    var capabilities = new CountingCapabilities([
      new("orders-db", [
        new(_entry("vector"), PostgresExtensionState.Installed, "0.8.0"),
        new(_entry("pg_squeeze"), PostgresExtensionState.Missing, null),
      ], null),
      new("audit-db", [new(_entry("pg_trgm"), PostgresExtensionState.Available, null)], null),
    ]);
    var logger = new FakeLogger<PostgresExtensionsStartupStep>();

    var report = await _step(capabilities, probe: true, logger).ExecuteAsync(CancellationToken.None);

    await Assert.That(capabilities.Refreshes).IsEqualTo(1);
    await Assert.That(report.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(report.Reason).IsEqualTo("orders-db: 1 of 2 usable; audit-db: 1 of 1 usable");
    var information = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Information).ToArray();
    await Assert.That(information.Length).IsEqualTo(2);
    await Assert.That(information[0].Message).Contains("'orders-db'");
    await Assert.That(information[1].Message).Contains("'audit-db'");
  }
}
