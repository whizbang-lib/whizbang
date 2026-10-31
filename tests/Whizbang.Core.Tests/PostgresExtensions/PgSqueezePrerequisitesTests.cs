// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// What is checked before each <c>squeeze.squeeze_table</c> call, so a table that cannot be squeezed is reported with
/// the reason instead of failing inside pg_squeeze's background worker: the extension and the right to call it, a
/// logical WAL level below Postgres 19, a free replication slot, an identity index, a role allowed to create a
/// replication slot, and a connection whose role owns the table. One test per prerequisite; the integration tests
/// prove the query reads these facts from a real server.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#online-rewrite-prerequisites</docs>
public class PgSqueezePrerequisitesTests {
  private static readonly PgSqueezePrerequisiteFacts _met = new(
    Role: "whizbang_init",
    TableExists: true,
    SqueezeInstalled: true,
    CanExecuteSqueeze: true,
    WalLevel: "logical",
    ServerVersionNum: 170_004,
    SlotsInUse: 2,
    MaxReplicationSlots: 10,
    HasIdentityIndex: true,
    CanReplicate: true,
    OwnsTable: true);

  [Test]
  public async Task EveryPrerequisiteMet_IsNothingUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met, "wh_outbox")).IsNull();
  }

  [Test]
  public async Task AMissingTable_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { TableExists = false }, "wh_outbox"))
      .IsEqualTo("wh_outbox does not exist");
  }

  [Test]
  public async Task PgSqueezeNotInstalled_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { SqueezeInstalled = false, CanExecuteSqueeze = false }, "wh_outbox"))
      .IsEqualTo("pg_squeeze is not installed in this database (no squeeze.squeeze_table function)");
  }

  [Test]
  public async Task NoRightToCallIt_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { CanExecuteSqueeze = false }, "wh_outbox"))
      .IsEqualTo("role 'whizbang_init' may not call squeeze.squeeze_table; grant it USAGE on schema squeeze and EXECUTE on the function");
  }

  [Test]
  [Arguments("replica")]
  [Arguments("minimal")]
  public async Task ANonLogicalWalLevel_BelowPostgres19_IsUnmetAsync(string walLevel) {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { WalLevel = walLevel }, "wh_outbox"))
      .IsEqualTo($"wal_level is {walLevel}, and pg_squeeze needs logical below Postgres 19; set wal_level = logical, then restart the server");
  }

  [Test]
  public async Task AReplicaWalLevel_OnPostgres19_IsMetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { WalLevel = "replica", ServerVersionNum = 190_000 }, "wh_outbox"))
      .IsNull();
  }

  [Test]
  [Arguments(10, 10)]
  [Arguments(11, 10)]
  public async Task NoFreeReplicationSlot_IsUnmetAsync(int inUse, int max) {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { SlotsInUse = inUse, MaxReplicationSlots = max }, "wh_outbox"))
      .IsEqualTo($"all {max} replication slots are in use (max_replication_slots = {max}); pg_squeeze needs one free slot for the rewrite");
  }

  [Test]
  public async Task OneFreeSlot_IsEnoughAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { SlotsInUse = 9, MaxReplicationSlots = 10 }, "wh_outbox")).IsNull();
  }

  [Test]
  public async Task NoIdentityIndex_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { HasIdentityIndex = false }, "wh_outbox"))
      .IsEqualTo("wh_outbox has no primary key or replica identity index, which pg_squeeze needs to apply the changes made during the rewrite");
  }

  [Test]
  public async Task ARoleThatCannotCreateAReplicationSlot_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { CanReplicate = false }, "wh_outbox"))
      .IsEqualTo("role 'whizbang_init' has neither the REPLICATION attribute nor superuser, and pg_squeeze creates a replication "
        + "slot as the calling role; grant it REPLICATION (ALTER ROLE whizbang_init WITH REPLICATION)");
  }

  [Test]
  public async Task ARoleThatDoesNotOwnTheTable_IsUnmetAsync() {
    await Assert.That(PgSqueezePrerequisites.Unmet(_met with { OwnsTable = false }, "wh_outbox"))
      .IsEqualTo("role 'whizbang_init' does not own wh_outbox; configure the schema-initialization (-init) connection so the "
        + "table owner runs the rewrite");
  }

  [Test]
  public async Task TheFirstUnmetPrerequisite_IsTheOneReportedAsync() {
    var nothingMet = new PgSqueezePrerequisiteFacts("app", TableExists: true, SqueezeInstalled: false, CanExecuteSqueeze: false,
      WalLevel: "replica", ServerVersionNum: 170_004, SlotsInUse: 0, MaxReplicationSlots: 0, HasIdentityIndex: false,
      CanReplicate: false, OwnsTable: false);

    await Assert.That(PgSqueezePrerequisites.Unmet(nothingMet, "wh_outbox")).StartsWith("pg_squeeze is not installed")
      .Because("the extension is the operator's first fix; the others only matter once it is there");
  }
}
