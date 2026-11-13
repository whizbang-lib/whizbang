// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The housekeeping forced-pass ceiling is measured from a first deferral the service's store holds (#2288), so a
/// restart does not start the hour over.
/// </summary>
/// <remarks>
/// The moment is read with the service backlog, in the same statement, and written only on transitions: the first
/// deferral of a run (the first writer wins, so two instances deferring at once keep the earlier moment), and the clear
/// once a pass of that activity has ended.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorFleetSql.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/399_TheForcedPassCeilingSurvivesARestart.sql</code-under-test>
/// <docs>operations/workers/housekeeping-arbitration#the-ceiling-survives-a-restart</docs>
[Category("Shard1")]
public class HousekeepingDeferralSqlTests : EFCoreTestBase {

  private static readonly DateTimeOffset _first = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the workers call through the interface, so the test reaches the driver's members as they do.")]
  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task NothingDeferred_IsReadAsAnEmptyFigure_NotAMissingOneAsync() {
    await using var ctx = CreateDbContext();

    var backlog = await _coordinator(ctx).CountServiceBacklogAsync();

    await Assert.That(backlog!.HousekeepingDeferrals).IsNotNull()
      .Because("null means the backend cannot hold the figure and the ceiling stays in memory; this one can");
    await Assert.That(backlog.HousekeepingDeferrals).IsEmpty();
  }

  [Test]
  public async Task TheFirstDeferral_IsReadWithTheBacklog_AndTheFirstWriterWinsAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.RecordHousekeepingDeferralAsync("maintenance", _first);
    await coordinator.RecordHousekeepingDeferralAsync("maintenance", _first + TimeSpan.FromMinutes(10));
    await coordinator.RecordHousekeepingDeferralAsync("integrity:audit", _first + TimeSpan.FromMinutes(5));
    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.HousekeepingDeferrals!["maintenance"]).IsEqualTo(_first)
      .Because("the run of deferrals began at the first write; a later instance's write must not move it later");
    await Assert.That(backlog.HousekeepingDeferrals!["integrity:audit"]).IsEqualTo(_first + TimeSpan.FromMinutes(5));
    await Assert.That(backlog.HousekeepingDeferrals!.Count).IsEqualTo(2);
  }

  [Test]
  public async Task AClear_RemovesOnlyItsOwnBudgetAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    await coordinator.RecordHousekeepingDeferralAsync("maintenance", _first);
    await coordinator.RecordHousekeepingDeferralAsync("dead-letter-recovery", _first);

    await coordinator.ClearHousekeepingDeferralAsync("maintenance");
    await coordinator.ClearHousekeepingDeferralAsync("never-recorded");
    var backlog = await coordinator.CountServiceBacklogAsync();

    await Assert.That(backlog!.HousekeepingDeferrals!.ContainsKey("maintenance")).IsFalse()
      .Because("a completed sweep re-arms its own ceiling for every instance of the service");
    await Assert.That(backlog.HousekeepingDeferrals!["dead-letter-recovery"]).IsEqualTo(_first)
      .Because("each activity's ceiling is its own: a sweep's pass does not re-arm recovery's");
  }
}
