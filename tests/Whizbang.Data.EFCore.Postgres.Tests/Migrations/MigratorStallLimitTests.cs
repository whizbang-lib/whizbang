// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2165: a stuck migrator's role lapses. The migrator's keepalive renews its grant only while the schema work reports
/// progress; once nothing has been reported for longer than the stall limit, it stops renewing, the lease lapses, and
/// another instance takes the role over through the normal vote.
/// </summary>
/// <remarks>
/// Progress is what the schema work already reports: each committed schema step, each phase announcement, each batch
/// or row group a long phase counts. The pipeline routes those to the duty this start holds
/// (<see cref="StartupProgress.ReportTo"/>), so no phase has to know about the keepalive.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigratorDuty.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupProgress.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#a-stuck-migrator-loses-its-role</docs>
[Category("Unit")]
[Category("Shard1")]
public class MigratorStallLimitTests {
  private static readonly TimeSpan _interval = TimeSpan.FromSeconds(4);
  private static readonly TimeSpan _tick = TimeSpan.FromSeconds(2);

  private static SchemaStaging _migrator(IDutyGrant grant) => new(SchemaStage.Migrator, grant, "won");

  [Test]
  public async Task TheStallLimit_IsTheSchemaCommandBudgetPlusTheMigratorsProgressWindow_ByDefaultAsync() {
    var options = new RoleAssignmentOptions();

    var limit = MigratorDuty.StallLimitFor(options, schemaCommandTimeoutSeconds: 600);

    await Assert.That(limit).IsEqualTo(TimeSpan.FromSeconds(600) + options.ProgressWindowFor(StartupDuties.MIGRATOR))
      .Because("one schema statement may run for the whole budget before it fails, and then the role's own window");
  }

  [Test]
  public async Task TheStallLimit_WithNoOptions_UsesTheDefaultOptionsAsync() {
    var limit = MigratorDuty.StallLimitFor(null, schemaCommandTimeoutSeconds: 60);

    await Assert.That(limit).IsEqualTo(TimeSpan.FromSeconds(60) + new RoleAssignmentOptions().ProgressWindowFor(StartupDuties.MIGRATOR));
  }

  [Test]
  public async Task AConfiguredStallLimit_IsUsedAsItIsAsync() {
    var options = new RoleAssignmentOptions { MigratorStallLimit = TimeSpan.FromMinutes(45) };

    await Assert.That(MigratorDuty.StallLimitFor(options, 600)).IsEqualTo(TimeSpan.FromMinutes(45));
  }

  [Test]
  public async Task MigratorStallLimit_DefaultsToZero_AndANegativeOneIsRefusedAsync() {
    await Assert.That(new RoleAssignmentOptions().MigratorStallLimit).IsEqualTo(TimeSpan.Zero)
      .Because("zero derives the limit from the schema command budget");
    await Assert.That(() => new RoleAssignmentOptions { MigratorStallLimit = TimeSpan.FromSeconds(-1) }.Validate())
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  [Timeout(30000)]
  public async Task AHeldGrant_StopsBeingRenewed_WhenTheWorkReportsNothingPastTheLimitAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new MigratorDutyTests.LeasedGrant(_interval);
    await using var duty = new MigratorDuty(time, stallLimit: TimeSpan.FromSeconds(5));
    await duty.HoldAsync(_migrator(grant));

    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(1);
    time.Advance(_tick);
    await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(2);
    time.Advance(_tick);   // six seconds without progress
    time.Advance(TimeSpan.FromMinutes(5));

    await Assert.That(grant.Count).IsEqualTo(2);
    await Assert.That(duty.IsKeptAlive).IsFalse();
  }

  [Test]
  [Timeout(30000)]
  public async Task ProgressTheWorkReports_ReachesTheDutyThisStartHolds_AndKeepsItRenewedAsync(CancellationToken cancellationToken) {
    var time = new FakeTimeProvider();
    var grant = new MigratorDutyTests.LeasedGrant(_interval);
    await using var duty = new MigratorDuty(time, stallLimit: TimeSpan.FromSeconds(5));
    await duty.HoldAsync(_migrator(grant));
    using var sink = StartupProgress.ReportTo(duty.NoteProgress);

    for (var verify = 1; verify <= 6; verify++) {
      time.Advance(_tick);
      await Assert.That(await grant.NextVerifyAsync(cancellationToken)).IsEqualTo(verify);
      // What a committed schema step reports: the pipeline's own signal, not one the step has to know about.
      await SchemaInitializationObservers.StepCommittedAsync(null, "public", "step:" + verify, cancellationToken);
    }

    await Assert.That(duty.IsKeptAlive).IsTrue();
  }

  [Test]
  public async Task ReportTo_RoutesProgressUntilItIsDisposed_ThenToWhatWasThereBeforeAsync() {
    var outer = 0;
    var inner = 0;
    using (StartupProgress.ReportTo(() => outer++)) {
      using (StartupProgress.ReportTo(() => inner++)) {
        StartupProgress.Note();
      }
      StartupProgress.Note();
    }
    StartupProgress.Note();

    await Assert.That(inner).IsEqualTo(1);
    await Assert.That(outer).IsEqualTo(1);
  }

  [Test]
  public async Task ProgressFromEveryReportingPoint_IsNotedAsync() {
    var noted = 0;
    using var sink = StartupProgress.ReportTo(() => noted++);

    await SchemaInitializationObservers.StepCommittedAsync(null, "public", "step:x", CancellationToken.None);
    await SchemaInitializationObservers.PreLockPhasesStartingAsync(null, "public", CancellationToken.None);
    new MigrationBatchProgress("001_X.sql", 100, new FakeTimeProvider(), null).Batch(10);

    await Assert.That(noted).IsEqualTo(3)
      .Because("a committed step, a phase starting and a batch counted are each work done");
  }

  [Test]
  public async Task ReportTo_WithNoSink_IsRefusedAsync() {
    await Assert.That(() => StartupProgress.ReportTo(null!)).Throws<ArgumentNullException>();
  }
}
