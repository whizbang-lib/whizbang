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

    var outcome = await session.RunAsync(FrameworkStartupWork.MANAGED_OBJECT_RECONCILE, "", _ => Task.FromResult(true), CancellationToken.None);

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

  [Test]
  public async Task AStorageReconcile_IsCompleteOnlyWhenItReadEverything_AndLeftNoTableBehindAsync() {
    await Assert.That(PerspectiveStorageReport.Empty.Completed).IsTrue();
    await Assert.That(PerspectiveStorageReport.Unread.Completed).IsFalse();
    await Assert.That(PerspectiveStorageReport.Unread.WasUnread).IsTrue();
    await Assert.That(new PerspectiveStorageReport([], [], ["wh_per_orders"]).Completed).IsFalse()
      .Because("a table whose change waited past its lock timeout is left for the next start");
  }
}
