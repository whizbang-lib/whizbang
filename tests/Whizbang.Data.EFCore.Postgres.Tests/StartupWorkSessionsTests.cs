// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2596: how both drivers open their Converge session. With no host (a direct call) or no connection of its own there
/// is nothing to claim from, so the work runs as before the cap existed; a cap that cannot work fails the start; the
/// inputs a fingerprint is taken over keep every list of parts distinct; and a storage reconcile that could not read, or
/// left a table behind, is not complete.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupWorkSessions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/PerspectiveStorageParameters.cs</code-under-test>
[Category("Startup")]
[Category("Shard2")]
public class StartupWorkSessionsTests {
  [Test]
  public async Task WithNoHostAndNoConnection_TheWorkRunsDirectlyAsync() {
    await using var session = StartupWorkSessions.Begin(null, "public", services: null, passApplied: false);
    var ran = false;

    var outcome = await session.RunAsync(FrameworkStartupWork.PERSPECTIVE_REGISTRY, "x", _ => {
      ran = true;
      return Task.FromResult(true);
    }, CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(ran).IsTrue();
    await Assert.That(session.HoldsPermit).IsFalse();
  }

  [Test]
  public async Task WithAConnectionButNoIdentity_TheLedgerIsNeverAskedAsync() {
    // A connection that cannot be opened: asking the ledger would throw, so a run proves it was not asked.
    var opened = 0;
    await using var session = StartupWorkSessions.Begin(() => {
      opened++;
      return new NpgsqlConnection("Host=unreachable.invalid");
    }, "public", new ServiceCollection().BuildServiceProvider(), passApplied: false);

    var outcome = await session.RunAsync(FrameworkStartupWork.ANALYZE_NEW_INDEXES, "", _ => Task.FromResult(true), CancellationToken.None);

    await Assert.That(outcome).IsEqualTo(StartupWorkOutcome.Ran);
    await Assert.That(opened).IsEqualTo(0);
  }

  [Test]
  public async Task ACapThatCannotWork_FailsTheStartAsync() {
    var services = new ServiceCollection();
    services.Configure<StartupWorkOptions>(o => o.MaxConcurrentHeavyStarts = 0);

    await Assert.That(() => StartupWorkSessions.Begin(null, "public", services.BuildServiceProvider(), passApplied: false))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task TheInputs_KeepEveryListOfPartsDistinct_AndFollowEachPayloadAsync() {
    await Assert.That(StartupWorkInputs.Of(["ab"])).IsNotEqualTo(StartupWorkInputs.Of(["a", "b"]));
    await Assert.That(StartupWorkInputs.Of(["a", "b"])).IsEqualTo(StartupWorkInputs.Of(["a", "b"]));
    await Assert.That(StartupWorkInputs.Of([new PerspectiveRegistryPayload("svc", "[1]")]))
      .IsNotEqualTo(StartupWorkInputs.Of([new PerspectiveRegistryPayload("svc", "[2]")]));
    await Assert.That(StartupWorkInputs.Of([new PerspectiveRegistryPayload("svc-a", "[1]")]))
      .IsNotEqualTo(StartupWorkInputs.Of([new PerspectiveRegistryPayload("svc-b", "[1]")]));
    await Assert.That(() => StartupWorkInputs.Of((IEnumerable<string>)null!)).Throws<ArgumentNullException>();
    await Assert.That(() => StartupWorkInputs.Of((IEnumerable<PerspectiveRegistryPayload>)null!)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// #2608: the managed-object reconcile's inputs follow every declaration and every setting, whatever order the
  /// declarations were made in, and the EF Core driver gives the same text as the Dapper driver for the same ones.
  /// </summary>
  [Test]
  public async Task TheReconcileInputs_FollowEachDeclarationAndSetting_InAnyOrder_AndAgreeAcrossDriversAsync() {
    static ManagedSchemaObjectSet declared() => new ManagedSchemaObjectSet()
      .Index("wh_per_a", "idx_a", "A [Indexed]").Constraint("wh_per_b", "ck_b", "B [MaxLength]");
    var settings = new ManagedSchemaSettings(ReconcileMode.Apply, [], [], DropAfterFleetConverged: true);
    var baseline = StartupWorkInputs.Of(declared(), settings);

    await Assert.That(StartupWorkInputs.Of(
        new ManagedSchemaObjectSet().Constraint("wh_per_b", "ck_b", "B [MaxLength]").Index("wh_per_a", "idx_a", "A [Indexed]"), settings))
      .IsEqualTo(baseline).Because("the order contributors declared in is not an input");
    string[] changed = [
      StartupWorkInputs.Of(declared().Index("wh_per_a", "idx_a2", "A [Indexed]"), settings),
      StartupWorkInputs.Of(new ManagedSchemaObjectSet().Index("wh_per_a", "idx_a", "A2 [Indexed]").Constraint("wh_per_b", "ck_b", "B [MaxLength]"), settings),
      StartupWorkInputs.Of(declared().Pin("wh_per_a", "idx_a", "kept", "A [KeepSchemaObject]"), settings),
      StartupWorkInputs.Of(declared().Pin("wh_per_a", "idx_a", null, "A [KeepSchemaObject]"), settings),
      StartupWorkInputs.Of(declared(), settings with { Mode = ReconcileMode.ReportOnly }),
      StartupWorkInputs.Of(declared(), settings with { KeepKinds = [ManagedObjectKind.Index] }),
      StartupWorkInputs.Of(declared(), settings with { Pins = ["wh_per_a:*"] }),
      StartupWorkInputs.Of(declared(), settings with { DropAfterFleetConverged = false }),
    ];
    await Assert.That(changed.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(changed.Length);
    await Assert.That(changed).DoesNotContain(baseline);

    var manifest = new ManagedSchemaManifest(typeof(StartupWorkSessionsTests), "public", declared);
    await Assert.That(ManagedSchemaReconcile.Inputs(manifest, settings, services: null)).IsEqualTo(baseline);
    await Assert.That(() => StartupWorkInputs.Of(null!, settings)).Throws<ArgumentNullException>();
    await Assert.That(() => StartupWorkInputs.Of(declared(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => ManagedSchemaReconcile.Inputs(null!, settings, null)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AStorageReconcile_IsCompleteOnlyWhenItReadEverything_AndLeftNoTableBehindAsync() {
    await Assert.That(PerspectiveStorageReport.Empty.Completed).IsTrue();
    await Assert.That(PerspectiveStorageReport.Unread.Completed).IsFalse();
    await Assert.That(PerspectiveStorageReport.Unread.WasUnread).IsTrue();
    await Assert.That(new PerspectiveStorageReport([], [], ["wh_per_orders"]).Completed).IsFalse()
      .Because("a table whose change waited past its lock timeout is left for the next start");
  }
}
