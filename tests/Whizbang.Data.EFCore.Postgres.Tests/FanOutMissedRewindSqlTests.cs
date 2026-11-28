// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2622, migration 761: <c>wh_rewind_row_fan_outs_missed</c>, which a live write of a row a fan-out targets calls after
/// it commits, rewinds the row only for a foreign event of the model's fan-out types, on a stream one of its keys holds, in
/// its tenant, committed after the row took the key, that the write did not fold and whose sink row is no longer pending.
/// Anything else is only read.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/FanOutMisses.cs</tests>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class FanOutMissedRewindSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.FanOutReplayOrderPerspective";
  private const string TENANT = "tenant-missed-fan-out";

  [Test]
  public async Task AMissedAppliedFanOut_RewindsTheRow_AndAnyOtherFanOutDoesNotAsync() {
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var before = await _appendAsync(customer, _renamed(customer, "before"));
    var placed = await _appendAsync(order, new FanOutReplayOrderShipped { OrderId = order, Status = "Placed" });
    await _cursorAsync(order, placed);
    var folded = await _appendAsync(customer, _renamed(customer, "folded"));
    var pending = await _appendAsync(customer, _renamed(customer, "pending"));
    await _executeAsync("INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id, partition_number, status, attempts, created_at) "
      + $"VALUES (gen_random_uuid(), '{customer}', '__collective__', '{pending}', 0, 1, 0, now())");
    await _appendAsync(customer, _renamed(customer, "other tenant"), tenant: "tenant-other");
    await _appendAsync(customer, new FanOutReplayOrderShipped { OrderId = customer, Status = "not a fan-out type" });

    // Every foreign event after the key is folded, pending, of another tenant or of another type: nothing is missed.
    await Assert.That(await _lookupAsync(order, placed, customer, folded: [folded])).IsEqualTo(0);
    await Assert.That(await _flaggedTriggerAsync(order)).IsNull()
      .Because($"the rename {before} before the key reached no row, the folded one is held, the pending one the sink applies later");

    var missed = await _appendAsync(customer, _renamed(customer, "missed"));
    await Assert.That(await _lookupAsync(order, placed, customer, folded: [folded])).IsEqualTo(1);
    await Assert.That(await _flaggedTriggerAsync(order)).IsEqualTo(placed).Because($"the applied rename {missed} was not folded");
  }

  [Test]
  public async Task AStartWithNoCommitSequence_IsComparedById_AndNoStartFollowsEveryEventAsync() {
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var early = await _appendAsync(customer, _renamed(customer, "early"));
    var placed = await _appendAsync(order, new FanOutReplayOrderShipped { OrderId = order, Status = "Placed" });
    await _cursorAsync(order, placed);

    await Assert.That(await _lookupAsync(order, placed, customer, folded: [], new FanOutKeyTaken(customer, null, placed))).IsEqualTo(0)
      .Because("by id the rename was minted before the event that took the key");
    await Assert.That(await _lookupAsync(order, placed, customer, folded: [], new FanOutKeyTaken(customer, null, null))).IsEqualTo(1)
      .Because($"a key with no start follows every event, {early} included");
  }

  [Test]
  public async Task AnUnstampedForeignEvent_IsComparedById_AgainstTheStartAsync() {
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var placed = await _appendAsync(order, new FanOutReplayOrderShipped { OrderId = order, Status = "Placed" });
    await _cursorAsync(order, placed);
    var late = await _appendAsync(customer, _renamed(customer, "late"));
    await _executeAsync($"UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = '{late}'");

    await Assert.That(await _lookupAsync(order, placed, customer, folded: [])).IsEqualTo(1)
      .Because("with no commit sequence the rename is placed by its id, minted after the event that took the key");
  }

  private static FanOutReplayCustomerRenamed _renamed(Guid customer, string name) => new() { CustomerId = customer, Name = name };

  // The lookup as a live write of the order makes it, for the key the order took at its event placed, unless a key is given.
  private async Task<int> _lookupAsync(Guid order, Guid placed, Guid customer, Guid[] folded, FanOutKeyTaken? key = null) {
    key ??= new FanOutKeyTaken(
      customer, await _scalarAsync<long>($"SELECT commit_sequence FROM wh_event_store WHERE event_id = '{placed}'"), placed);
    return await FanOutMisses.RewindAsync(
      new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null),
      [TypeNameFormatter.Format(typeof(FanOutReplayCustomerRenamed))],
      new FanOutMissCheck(order, PERSPECTIVE, TENANT, placed, [key], folded),
      CancellationToken.None);
  }

  private async Task<Guid> _appendAsync<TEvent>(Guid stream, TEvent payload, string tenant = TENANT) where TEvent : IEvent {
    var id = Guid.CreateVersion7();
    await using (var context = new WorkCoordinationDbContext(DbContextOptions)) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, new MessageEnvelope<TEvent> {
        MessageId = new MessageId(id),
        Payload = payload,
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UnixEpoch,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenant, UserId = "user-missed-fan-out" }),
        }],
      });
    }
    // The scope column, as the store's emit chain writes it; a direct append leaves it to the hops.
    await _executeAsync($"UPDATE wh_event_store SET scope = jsonb_build_object('t', '{tenant}') WHERE event_id = '{id}'");
    await _executeAsync("SELECT stamp_pending_commit_sequences()");
    return id;
  }

  private Task _cursorAsync(Guid order, Guid last) =>
    _executeAsync($"INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES ('{order}', '{PERSPECTIVE}', '{last}', 1)");

  private async Task<Guid?> _flaggedTriggerAsync(Guid order) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      $"SELECT rewind_trigger_event_id FROM wh_perspective_cursors WHERE stream_id = '{order}' AND (status & 32) <> 0", conn);
    return await cmd.ExecuteScalarAsync() as Guid?;
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private async Task _executeAsync(string sql) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();
  }
}
