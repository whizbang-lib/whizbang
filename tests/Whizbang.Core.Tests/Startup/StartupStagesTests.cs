// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The startup stages (#1636): fixed numbers with gaps, never list positions; seven startup stages logged as
/// <c>Stage X of 7: &lt;name&gt; - &lt;message&gt;</c>, X computed from the list; two shutdown stages; and the lifecycle
/// phase each one keeps on the instance's row.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStage.cs</code-under-test>
[Category("Startup")]
public class StartupStagesTests {
  [Test]
  public async Task Values_AreTheFixedNumbersOnTheRow_WithGapsAsync() {
    int[] values = [.. Enum.GetValues<StartupStage>().Select(s => (int)s)];

    await Assert.That(values).IsEquivalentTo([10, 20, 30, 40, 50, 60, 70, 80, 90])
      .Because("a stored stage means the same thing in every release, and a later stage slots into a gap");
  }

  [Test]
  public async Task Startup_IsTheSevenStartupStagesInOrderAsync() {
    await Assert.That(StartupStages.Startup).IsEquivalentTo([
      StartupStage.Preflight, StartupStage.Elect, StartupStage.Migrate, StartupStage.Converge,
      StartupStage.Coordinate, StartupStage.AcceptingCommands, StartupStage.Running,
   ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  [Arguments(StartupStage.Preflight, "Preflight", 1)]
  [Arguments(StartupStage.Elect, "Elect", 2)]
  [Arguments(StartupStage.Migrate, "Migrate", 3)]
  [Arguments(StartupStage.Converge, "Converge", 4)]
  [Arguments(StartupStage.Coordinate, "Coordinate", 5)]
  [Arguments(StartupStage.AcceptingCommands, "Accept commands", 6)]
  [Arguments(StartupStage.Running, "Running", 7)]
  public async Task AStartupStage_HasItsName_AndItsPositionInTheListAsync(StartupStage stage, string name, int ordinal) {
    await Assert.That(StartupStages.NameOf(stage)).IsEqualTo(name);
    await Assert.That(StartupStages.OrdinalOf(stage)).IsEqualTo(ordinal);
  }

  [Test]
  [Arguments(StartupStage.Draining, "Draining")]
  [Arguments(StartupStage.Stopped, "Stopped")]
  public async Task AShutdownStage_HasItsName_AndNoPositionAsync(StartupStage stage, string name) {
    await Assert.That(StartupStages.NameOf(stage)).IsEqualTo(name);
    await Assert.That(StartupStages.OrdinalOf(stage)).IsNull()
      .Because("startup is counted out of seven; a shutdown stage is not one of them");
  }

  [Test]
  public async Task AnUndefinedValue_HasNoName_AndNoPhaseAsync() {
    var undefined = (StartupStage)15;

    await Assert.That(() => StartupStages.NameOf(undefined)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => StartupStages.LifecyclePhaseOf(undefined)).Throws<ArgumentOutOfRangeException>();
    await Assert.That(StartupStages.OrdinalOf(undefined)).IsNull();
  }

  [Test]
  [Arguments(StartupStage.Preflight, LifecyclePhase.Connecting)]
  [Arguments(StartupStage.Elect, LifecyclePhase.Migrating)]
  [Arguments(StartupStage.Migrate, LifecyclePhase.Migrating)]
  [Arguments(StartupStage.Converge, LifecyclePhase.Migrating)]
  [Arguments(StartupStage.Coordinate, LifecyclePhase.Migrating)]
  [Arguments(StartupStage.AcceptingCommands, LifecyclePhase.AcceptingCommands)]
  [Arguments(StartupStage.Running, LifecyclePhase.Running)]
  [Arguments(StartupStage.Draining, LifecyclePhase.Stopping)]
  [Arguments(StartupStage.Stopped, LifecyclePhase.Stopped)]
  public async Task EachStage_KeepsTheLifecyclePhaseItMapsToAsync(StartupStage stage, LifecyclePhase phase) {
    await Assert.That(StartupStages.LifecyclePhaseOf(stage)).IsEqualTo(phase)
      .Because("lifecycle_phase stays on the row, written from the stage, for the readers and dashboards that read it");
  }

  [Test]
  public async Task Describe_AStartupStage_IsStageXOfSevenAsync() {
    await Assert.That(StartupStages.Describe(StartupStage.Migrate, "migration 42 of 231"))
      .IsEqualTo("Stage 3 of 7: Migrate - migration 42 of 231");
    await Assert.That(StartupStages.Describe(StartupStage.AcceptingCommands, "commands are accepted"))
      .IsEqualTo("Stage 6 of 7: Accept commands - commands are accepted");
  }

  [Test]
  public async Task Describe_AShutdownStage_IsAShutdownStageLineAsync() {
    await Assert.That(StartupStages.Describe(StartupStage.Draining, "host stopping"))
      .IsEqualTo("Shutdown stage: Draining - host stopping");
  }

  [Test]
  public async Task ASnapshot_NamesItsStage_AndReadsAsItsLineAsync() {
    var snapshot = new StartupStageSnapshot(StartupStage.Elect, "voting", Skipped: false, DateTimeOffset.UnixEpoch);

    await Assert.That(snapshot.Name).IsEqualTo("Elect");
    await Assert.That(snapshot.Text).IsEqualTo("Stage 2 of 7: Elect - voting");
  }
}
