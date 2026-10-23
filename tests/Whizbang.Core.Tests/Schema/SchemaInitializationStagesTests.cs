// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Tests.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The stages schema initialization reports (#1636): Elect around the migrator election (skipped when none was held),
/// Migrate for the pass (skipped when the schema needs none), Converge after it, and Coordinate before the
/// schema-ready gate opens. The messages are framework-authored text only: the election's own detail, which can carry
/// a server's words, stays in the election's log and off the health surface the stage feeds.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaInitializationStages.cs</code-under-test>
[Category("Startup")]
public class SchemaInitializationStagesTests {
  [Test]
  public async Task Electing_EntersElectAsync() {
    var (stages, services) = _build();
    using var cts = new CancellationTokenSource();

    await SchemaInitializationStages.ElectingAsync(services, cts.Token);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Elect: joining the registry and voting for the migrator duty"]);
    await Assert.That(stages.Tokens[0]).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task Registered_WritesTheHeldStageAsync() {
    var (stages, services) = _build();

    await SchemaInitializationStages.RegisteredAsync(services, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Registered"]);
  }

  [Test]
  [Arguments(SchemaStage.Migrator, "Enter Elect: this instance migrates the schema")]
  [Arguments(SchemaStage.Waiter, "Enter Elect: another instance migrates the schema")]
  [Arguments(SchemaStage.Unstaged, "Enter Elect: no migrator was elected, so this instance migrates under the schema lock")]
  public async Task Elected_AfterVoting_ReportsTheOutcome_WithoutTheElectionsDetailAsync(SchemaStage stage, string expected) {
    var (stages, services) = _build();
    await SchemaInitializationStages.ElectingAsync(services, CancellationToken.None);

    await SchemaInitializationStages.ElectedAsync(
      services, new SchemaStaging(stage, null, "server said: permission denied for table wh_secret"), CancellationToken.None);

    await Assert.That(stages.Calls[^1]).IsEqualTo(expected);
  }

  [Test]
  public async Task Elected_WhenNoElectionWasHeld_SkipsElectAsync() {
    var (stages, services) = _build();
    stages.Current = new StartupStageSnapshot(StartupStage.Preflight, "host starting", false, DateTimeOffset.UnixEpoch);

    await SchemaInitializationStages.ElectedAsync(
      services, new SchemaStaging(SchemaStage.Unstaged, null, "staged startup was not attempted"), CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo([
      "Skip Elect: no election could be held, so this instance migrates under the schema lock",
   ]);
  }

  [Test]
  public async Task Elected_BeforeAnyStage_SkipsElectAsync() {
    var (stages, services) = _build();

    await SchemaInitializationStages.ElectedAsync(
      services, new SchemaStaging(SchemaStage.Unstaged, null, "staged startup was not attempted"), CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo([
      "Skip Elect: no election could be held, so this instance migrates under the schema lock",
   ]);
  }

  [Test]
  public async Task Migrating_EntersMigrateAsync() {
    var (stages, services) = _build();

    await SchemaInitializationStages.MigratingAsync(services, "12 migration steps to apply", CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Migrate: 12 migration steps to apply"]);
  }

  [Test]
  [Arguments(1, "Enter Migrate: 1 migration step to apply")]
  [Arguments(231, "Enter Migrate: 231 migration steps to apply")]
  public async Task Applying_EntersMigrate_WithTheStepsThePassOwesAsync(int steps, string expected) {
    var (stages, services) = _build();

    await SchemaInitializationStages.ApplyingAsync(services, steps, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo([expected]);
  }

  [Test]
  public async Task MigrationNotNeeded_BeforeMigrate_SkipsItAsync() {
    var (stages, services) = _build();
    stages.Current = new StartupStageSnapshot(StartupStage.Elect, "this instance migrates the schema", false, DateTimeOffset.UnixEpoch);

    await SchemaInitializationStages.MigrationNotNeededAsync(services, "the schema is already current", CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Skip Migrate: the schema is already current"]);
  }

  [Test]
  public async Task MigrationNotNeeded_InMigrate_IsProgressWithinItAsync() {
    var (stages, services) = _build();
    await SchemaInitializationStages.MigratingAsync(services, "waiting for the instance migrating the schema", CancellationToken.None);

    await SchemaInitializationStages.MigrationNotNeededAsync(services, "the schema is already current", CancellationToken.None);

    await Assert.That(stages.Calls[^1]).IsEqualTo("Enter Migrate: the schema is already current")
      .Because("a waiter whose migrator finished was in Migrate; it did not skip it");
  }

  [Test]
  public async Task Converging_EntersConvergeAsync() {
    var (stages, services) = _build();

    await SchemaInitializationStages.ConvergingAsync(services, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Converge: analyzing new indexes and reconciling managed objects"]);
  }

  [Test]
  public async Task Coordinating_EntersCoordinateAsync() {
    var (stages, services) = _build();

    await SchemaInitializationStages.CoordinatingAsync(services, CancellationToken.None);

    await Assert.That(stages.Calls).IsEquivalentTo(["Enter Coordinate: recomputing partitions before the schema-ready gate opens"]);
  }

  [Test]
  public async Task WithNoProvider_OrNoReporter_NothingIsReportedAsync() {
    await using var empty = new ServiceCollection().BuildServiceProvider();
    var staging = new SchemaStaging(SchemaStage.Migrator, null, "elected");

    foreach (var services in new IServiceProvider?[] { null, empty }) {
      await Assert.That(async () => {
        await SchemaInitializationStages.ElectingAsync(services, CancellationToken.None);
        await SchemaInitializationStages.RegisteredAsync(services, CancellationToken.None);
        await SchemaInitializationStages.ElectedAsync(services, staging, CancellationToken.None);
        await SchemaInitializationStages.MigratingAsync(services, "applying", CancellationToken.None);
        await SchemaInitializationStages.ApplyingAsync(services, 3, CancellationToken.None);
        await SchemaInitializationStages.MigrationNotNeededAsync(services, "current", CancellationToken.None);
        await SchemaInitializationStages.ConvergingAsync(services, CancellationToken.None);
        await SchemaInitializationStages.CoordinatingAsync(services, CancellationToken.None);
      }).ThrowsNothing();
    }
  }

  private static (RecordingStageReporter Stages, IServiceProvider Services) _build() {
    var stages = new RecordingStageReporter();
    var services = new ServiceCollection().AddSingleton<IStartupStageReporter>(stages).BuildServiceProvider();
    return (stages, services);
  }
}
