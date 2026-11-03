// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The queue integrity deliveries wait in for the maintainer (#2001), against the real functions of migration 302
/// through <see cref="EFCoreWorkCoordinator{TDbContext}"/>. A delivery is recorded once however many times it is
/// offered, leased in arrival order with its next eligibility pushed out by the lease (so an overlapping drain during
/// a duty failover does not process it twice), skipped while another drain holds it locked, deleted on completion,
/// and held back for its backoff on failure with the attempt counted and the error kept.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/302_IntegrityDeliveriesWaitForTheMaintainer.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
[Category("Integration")]
[Category("Shard5")]
public class IntegrityDeliverySqlTests : EFCoreTestBase {
  private const string TYPE = "integrity-test";
  private static readonly TimeSpan _lease = TimeSpan.FromMinutes(5);

  private readonly List<WorkCoordinationDbContext> _contexts = [];

  private EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator() {
    var ctx = CreateDbContext();
    _contexts.Add(ctx);
    return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());
  }

  [After(Test)]
  public async Task DisposeContextsAsync() {
    foreach (var ctx in _contexts) {
      await ctx.DisposeAsync();
    }
  }

  private async Task<long> _countAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_integrity_deliveries";
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task Enqueue_TheSameDeliveryTwice_RecordsItOnceAsync() {
    var coordinator = _coordinator();
    var id = TrackedGuid.New().Value;

    var first = await coordinator.EnqueueIntegrityDeliveryAsync(id, TYPE, """{"n":1}""");
    var second = await coordinator.EnqueueIntegrityDeliveryAsync(id, TYPE, """{"n":1}""");

    await Assert.That(first).IsEqualTo(IntegrityDeliveryEnqueueResult.Recorded);
    await Assert.That(second).IsEqualTo(IntegrityDeliveryEnqueueResult.AlreadyRecorded)
      .Because("a delivery is keyed by its content, so the inbox, outbox and local copies of one message are one delivery");
    await Assert.That(await _countAsync()).IsEqualTo(1);
  }

  [Test]
  public async Task Lease_ReturnsDueDeliveriesInArrivalOrder_AndPushesThemOutByTheLeaseAsync() {
    var coordinator = _coordinator();
    // Ids in descending order, so arrival order and id order disagree and the test sees which one the lease follows.
    var first = new Guid("ffffffff-0000-0000-0000-000000000001");
    var second = new Guid("aaaaaaaa-0000-0000-0000-000000000002");
    var third = new Guid("11111111-0000-0000-0000-000000000003");
    foreach (var id in new[] { first, second, third }) {
      await coordinator.EnqueueIntegrityDeliveryAsync(id, TYPE, $$"""{"id":"{{id}}"}""");
    }

    var leased = await coordinator.LeaseIntegrityDeliveriesAsync(2, _lease);
    var rest = await coordinator.LeaseIntegrityDeliveriesAsync(10, _lease);

    await Assert.That(leased.Select(d => d.DeliveryId).ToList()).IsEquivalentTo(new List<Guid> { first, second });
    await Assert.That(leased[0].DeliveryId).IsEqualTo(first).Because("the drain works in arrival order");
    await Assert.That(leased[0].DeliveryType).IsEqualTo(TYPE);
    await Assert.That(leased[0].Payload).Contains(first.ToString());
    await Assert.That(leased[0].Attempts).IsEqualTo(0);
    await Assert.That(rest.Select(d => d.DeliveryId).ToList()).IsEquivalentTo(new List<Guid> { third })
      .Because("a leased delivery is not due again until its lease runs out, so a second drain cannot take it");
  }

  [Test]
  public async Task Lease_SkipsADeliveryAnotherDrainHoldsLockedAsync() {
    var coordinator = _coordinator();
    var held = new Guid("00000000-0000-0000-0000-00000000000a");
    var free = new Guid("00000000-0000-0000-0000-00000000000b");
    await coordinator.EnqueueIntegrityDeliveryAsync(held, TYPE, "{}");
    await coordinator.EnqueueIntegrityDeliveryAsync(free, TYPE, "{}");

    await using var other = new NpgsqlConnection(ConnectionString);
    await other.OpenAsync();
    await using var tx = await other.BeginTransactionAsync();
    await using (var lockRow = other.CreateCommand()) {
      lockRow.Transaction = tx;
      lockRow.CommandText = "SELECT 1 FROM wh_integrity_deliveries WHERE delivery_id = @id FOR UPDATE";
      lockRow.Parameters.AddWithValue("id", held);
      await lockRow.ExecuteScalarAsync();
    }

    var leased = await coordinator.LeaseIntegrityDeliveriesAsync(10, _lease);
    await tx.RollbackAsync();

    await Assert.That(leased.Select(d => d.DeliveryId).ToList()).IsEquivalentTo(new List<Guid> { free })
      .Because("a row another drain holds is skipped, never waited on");
  }

  [Test]
  public async Task Complete_DeletesTheDeliveryAsync() {
    var coordinator = _coordinator();
    var id = TrackedGuid.New().Value;
    await coordinator.EnqueueIntegrityDeliveryAsync(id, TYPE, "{}");

    await coordinator.CompleteIntegrityDeliveryAsync(id);

    await Assert.That(await _countAsync()).IsEqualTo(0);
  }

  [Test]
  public async Task Fail_CountsTheAttempt_KeepsTheError_AndHoldsItForTheBackoffAsync() {
    var coordinator = _coordinator();
    var id = TrackedGuid.New().Value;
    await coordinator.EnqueueIntegrityDeliveryAsync(id, TYPE, "{}");
    _ = await coordinator.LeaseIntegrityDeliveriesAsync(1, _lease);

    await coordinator.FailIntegrityDeliveryAsync(id, "boom", TimeSpan.FromHours(1));
    var heldBack = await coordinator.LeaseIntegrityDeliveriesAsync(1, _lease);
    await coordinator.FailIntegrityDeliveryAsync(id, "boom again", TimeSpan.Zero);
    var retried = await coordinator.LeaseIntegrityDeliveriesAsync(1, _lease);

    await Assert.That(heldBack).IsEmpty().Because("a failed delivery waits out its backoff");
    await Assert.That(retried.Count).IsEqualTo(1);
    await Assert.That(retried[0].Attempts).IsEqualTo(2).Because("each failure counts one attempt");
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT last_error FROM wh_integrity_deliveries WHERE delivery_id = @id";
    cmd.Parameters.AddWithValue("id", id);
    await Assert.That((string?)await cmd.ExecuteScalarAsync()).IsEqualTo("boom again");
  }
}
