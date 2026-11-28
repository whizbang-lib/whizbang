// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2622, migration 760: <c>wh_rewind_fan_out_rows_applied_late</c>, which the collective sink calls after a fan-out it
/// applied, rewinds exactly the rows the event targets (its key's stream, its tenant) that had folded an own event
/// committed after it, each from its first own event committed after it.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/PostgresFanOutLateApplyRewinds.cs</tests>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
[Category("Integration")]
[Category("CollectiveEvents")]
[Category("Shard3")]
public class FanOutLateApplyRewindSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Whizbang.Data.EFCore.Postgres.Tests.Perspectives.FanOutReplayOrderPerspective";
  private const string TENANT = "tenant-late-fan-out";

  private static readonly DateTimeOffset _now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

  private sealed class FixedTime : TimeProvider {
    public override DateTimeOffset GetUtcNow() => _now;
  }

  [Test]
  public async Task ARowPastTheFanOut_IsRewoundFromItsFirstEventAfterIt_AndNoOtherRowIsAsync() {
    var (customer, otherCustomer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var (past, before, otherTenant, otherKey) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    foreach (var order in new[] { past, before, otherTenant, otherKey }) {
      await _appendAsync(order, _shipped(order, "Packed"));
    }
    var rename = await _appendAsync(customer, new FanOutReplayCustomerRenamed { CustomerId = customer, Name = "Ltd" });
    var pastAfter = await _appendAsync(past, _shipped(past, "Shipped"));
    await _appendAsync(past, _shipped(past, "Delivered"));
    await _appendAsync(otherTenant, _shipped(otherTenant, "Shipped"));
    await _appendAsync(otherKey, _shipped(otherKey, "Shipped"));
    await _rowAsync(past, customer, TENANT);
    await _rowAsync(before, customer, TENANT);
    await _rowAsync(otherTenant, customer, "tenant-other");
    await _rowAsync(otherKey, otherCustomer, TENANT);

    var rewound = await _rewinds().RewindRowsAppliedLateAsync(FanOutReplayOrderFanOut.Renamed, customer, TENANT, rename, CancellationToken.None);

    await Assert.That(rewound).IsEqualTo(1);
    await Assert.That(await _flaggedAsync()).IsEquivalentTo(new Dictionary<Guid, Guid> { [past] = pastAfter })
      .Because("only the customer's row in the event's tenant that folded a newer event, from its first event after the rename");
    await Assert.That(await _scalarAsync<DateTime>($"SELECT rewind_flagged_at FROM wh_perspective_cursors WHERE stream_id = '{past}'"))
      .IsEqualTo(_now.UtcDateTime).Because("the flag carries the clock the rewinds were given");
  }

  [Test]
  public async Task AnUnstampedFanOut_RewindsNothingAsync() {
    var (customer, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var rename = await _appendAsync(customer, new FanOutReplayCustomerRenamed { CustomerId = customer, Name = "Ltd" });
    await _appendAsync(order, _shipped(order, "Shipped"));
    await _rowAsync(order, customer, TENANT);
    await _executeAsync($"UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = '{rename}'");

    var rewound = await new PostgresFanOutLateApplyRewinds(_database(), time: null)
      .RewindRowsAppliedLateAsync(FanOutReplayOrderFanOut.Renamed, customer, TENANT, rename, CancellationToken.None);

    await Assert.That(rewound).IsEqualTo(0).Because("an event with no commit position cannot be placed against the row's own");
    await Assert.That(await _flaggedAsync()).IsEmpty();
  }

  [Test]
  public async Task TheRewinds_RefuseNoDatabase_NoFanOut_AndNoTenantAsync() {
    await Assert.That(() => new PostgresFanOutLateApplyRewinds(null!, null)).Throws<ArgumentNullException>();
    await Assert.That(async () => await _rewinds().RewindRowsAppliedLateAsync(null!, Guid.Empty, TENANT, Guid.Empty, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await _rewinds().RewindRowsAppliedLateAsync(FanOutReplayOrderFanOut.Renamed, Guid.Empty, null!, Guid.Empty, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  private PostgresArchiveConnection _database() =>
    new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null);

  private PostgresFanOutLateApplyRewinds _rewinds() => new(_database(), new FixedTime());

  private static FanOutReplayOrderShipped _shipped(Guid order, string status) => new() { OrderId = order, Status = status };

  // A committed event: appended in the tenant, then stamped into the store's commit order.
  private async Task<Guid> _appendAsync<TEvent>(Guid stream, TEvent payload) where TEvent : IEvent {
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
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = TENANT, UserId = "user-late-fan-out" }),
        }],
      });
    }
    await _executeAsync("SELECT stamp_pending_commit_sequences()");
    return id;
  }

  // The order's row as its runner last wrote it, at its stream's newest event, with its cursor.
  private async Task _rowAsync(Guid order, Guid customer, string tenant) {
    await _executeAsync($"""
      INSERT INTO {FanOutReplayOrderFanOut.TABLE} (id, data, metadata, scope, created_at, updated_at, version, customer_id)
      SELECT '{order}', jsonb_build_object('Id', '{order}', 'CustomerId', '{customer}'),
             jsonb_build_object('CommitSequence', max(es.commit_sequence)), jsonb_build_object('t', '{tenant}'),
             now(), now(), 1, '{customer}'
      FROM wh_event_store es WHERE es.stream_id = '{order}';
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status)
      SELECT '{order}', '{PERSPECTIVE}', (SELECT event_id FROM wh_event_store WHERE stream_id = '{order}' ORDER BY version DESC LIMIT 1), 1;
      """);
  }

  private async Task<Dictionary<Guid, Guid>> _flaggedAsync() {
    var flagged = new Dictionary<Guid, Guid>();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
      "SELECT stream_id, rewind_trigger_event_id FROM wh_perspective_cursors WHERE (status & 32) <> 0", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      flagged[reader.GetGuid(0)] = reader.GetGuid(1);
    }
    return flagged;
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
