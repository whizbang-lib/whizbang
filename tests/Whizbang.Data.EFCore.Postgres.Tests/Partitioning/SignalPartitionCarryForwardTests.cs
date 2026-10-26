// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.Partitioning;

/// <summary>
/// Issue #1758: before the partition manager drops (or, while the table is in use, empties) a <c>wh_signals</c>
/// partition, it carries the signals a live instance has not consumed into the current partition. A signal is
/// unconsumed when its id is past the slowest live instance's tail cursor; an instance is live while it is
/// registered. The id is unchanged, so the tail still receives the carried signals in order.
/// </summary>
/// <remarks>
/// Every moment is read from the database's own clock, the clock <c>created_at</c> and the conversion use; the cycle
/// runs 30 days later, so the legacy partition (everything up to tomorrow) is past a 7-day window.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#signals</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/239_PartitionDropsCarryUnconsumedSignals.sql</code-under-test>
[Category("Integration")]
[Category("Shard5")]
[NotInParallel("SignalRetention")]
public class SignalPartitionCarryForwardTests : EFCoreTestBase {
  private const string LEGACY = "wh_signals_legacy";
  private const string CARRIED = "carried";
  private const string DROPPED = "dropped";
  private const string EMPTIED = "emptied";
  private const string CONSUMED = "consumed";
  private const string PENDING_FIRST = "pending-first";
  private const string PENDING_SECOND = "pending-second";
  private const string WINDOW = "7 days";

  private readonly record struct CarriedProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class NullSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal => ValueTask.CompletedTask;
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  /// <summary>Three signals a day old (all in the legacy partition once converted); returns the consumed one's id.</summary>
  private static async Task<long> _seedAndConvertAsync(NpgsqlConnection connection) {
    var consumed = await connection.ExecuteScalarAsync<long>(
      "INSERT INTO wh_signals (wire_name, created_at) VALUES (@name, NOW() - INTERVAL '1 day') RETURNING id", new { name = CONSUMED });
    foreach (var name in new[] { PENDING_FIRST, PENDING_SECOND }) {
      await connection.ExecuteAsync(
        "INSERT INTO wh_signals (wire_name, created_at) VALUES (@name, NOW() - INTERVAL '1 day')", new { name });
    }
    await Assert.That(await connection.ExecuteScalarAsync<string>(
      "SELECT wh_partition_convert('wh_signals', 'created_at', '1 day')")).IsEqualTo("converted");
    return consumed;
  }

  private static Task _registerAsync(NpgsqlConnection connection, Guid instanceId) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata) " +
      "VALUES (@instanceId, 'utest-svc', 'utest-host', 1, NOW(), NOW(), '{}'::jsonb)", new { instanceId });

  private static Task _cursorAsync(NpgsqlConnection connection, Guid instanceId, long last) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id) VALUES (@instanceId, @last)", new { instanceId, last });

  /// <summary>The cycle's moment: 30 days on the database's clock, read once so every statement uses the same instant.</summary>
  private static Task<DateTime> _cycleMomentAsync(NpgsqlConnection connection) =>
    connection.ExecuteScalarAsync<DateTime>("SELECT NOW() + INTERVAL '30 days'");

  private static async Task<List<(string Action, string Name)>> _maintainAsync(NpgsqlConnection connection, DateTime at, string? keep) {
    var results = new List<(string Action, string Name)>();
    await using var command = connection.CreateCommand();
    command.CommandText =
      "SELECT action, partition_name FROM wh_partition_maintain('wh_signals', '1 day', 0, @keep::interval, @at)";
    command.Parameters.Add(new NpgsqlParameter(nameof(keep), NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)keep ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter(nameof(at), NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = at });
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      results.Add((reader.GetString(0), reader.GetString(1)));
    }
    return results;
  }

  private static List<string> _named(List<(string Action, string Name)> results, string action) =>
    [.. results.Where(r => r.Action == action).Select(r => r.Name)];

  private static Task<List<string>> _namesAsync(NpgsqlConnection connection) =>
    connection.QueryAsync<string>("SELECT wire_name FROM wh_signals ORDER BY id");

  [Test]
  public async Task ALiveInstanceBehindTheWindow_KeepsEveryUnconsumedSignalAcrossTheDrop_AndReceivesThemInOrderAsync() {
    await using var connection = await _openAsync();
    var consumed = await _seedAndConvertAsync(connection);
    var instanceId = Guid.CreateVersion7();
    await _registerAsync(connection, instanceId);
    await _cursorAsync(connection, instanceId, consumed);
    var at = await _cycleMomentAsync(connection);

    var results = await _maintainAsync(connection, at, WINDOW);

    await Assert.That(_named(results, CARRIED)).IsEquivalentTo([LEGACY]);
    await Assert.That(_named(results, DROPPED)).IsEquivalentTo([LEGACY]);
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo([PENDING_FIRST, PENDING_SECOND])
      .Because("the signals past the live instance's cursor move to the current partition; the one it consumed goes with the drop");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
        "SELECT count(*) FROM wh_signals WHERE created_at <> @at OR tableoid::regclass::text = @legacy",
        new { at, legacy = LEGACY }))
      .IsEqualTo(0L).Because("a carried signal is stamped with the cycle's moment, and so starts a fresh window");

    var received = new List<string>();
    var map = new Dictionary<string, SignalTypeEntry>(StringComparer.Ordinal);
    foreach (var name in new[] { PENDING_FIRST, PENDING_SECOND }) {
      map[name] = new SignalTypeEntry(typeof(CarriedProbe), name, SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        (_, _) => {
          received.Add(name);
          return ValueTask.CompletedTask;
        });
    }
    var options = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var configuration = new ConfigurationBuilder().Build();
    var tail = new PgDurableSignalTailWorker(
      Options.Create(options), configuration, new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1),
      new NullSink(), NullLogger<PgDurableSignalTailWorker>.Instance);
    tail.UseWireMap(map);
    var plan = NotificationConnectionPlan.Create(null, NotificationConnectionStringResolver.Resolve(options, configuration));

    var delivered = await tail.TickOnceAsync(plan, CancellationToken.None);

    await Assert.That(delivered).IsEqualTo(2);
    await Assert.That(received).IsEquivalentTo([PENDING_FIRST, PENDING_SECOND], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a carried signal keeps its id, and the tail reads past its cursor in id order");
  }

  [Test]
  public async Task ADeadInstancesCursor_CarriesNothing_AndThePartitionIsDroppedAsync() {
    await using var connection = await _openAsync();
    var consumed = await _seedAndConvertAsync(connection);
    // A cursor whose instance has no registration: the reap removed it, so it is dead and holds nothing back.
    await _cursorAsync(connection, Guid.CreateVersion7(), consumed);

    var results = await _maintainAsync(connection, await _cycleMomentAsync(connection), WINDOW);

    await Assert.That(_named(results, CARRIED)).IsEmpty();
    await Assert.That(_named(results, DROPPED)).IsEquivalentTo([LEGACY]);
    await Assert.That(await _namesAsync(connection)).IsEmpty();
  }

  [Test]
  public async Task WithNoLag_NothingIsCarried_AndThePartitionIsDroppedAsync() {
    await using var connection = await _openAsync();
    await _seedAndConvertAsync(connection);
    var instanceId = Guid.CreateVersion7();
    await _registerAsync(connection, instanceId);
    await _cursorAsync(connection, instanceId, await connection.ExecuteScalarAsync<long>("SELECT max(id) FROM wh_signals"));

    var results = await _maintainAsync(connection, await _cycleMomentAsync(connection), WINDOW);

    await Assert.That(_named(results, CARRIED)).IsEmpty();
    await Assert.That(_named(results, DROPPED)).IsEquivalentTo([LEGACY]);
    await Assert.That(await _namesAsync(connection)).IsEmpty();
  }

  [Test]
  public async Task ALiveInstanceBehind_KeepsItsSignals_WhenTheTableIsInUseAndThePartitionIsOnlyEmptiedAsync() {
    await using var setup = await _openAsync();
    var consumed = await _seedAndConvertAsync(setup);
    var instanceId = Guid.CreateVersion7();
    await _registerAsync(setup, instanceId);
    await _cursorAsync(setup, instanceId, consumed);
    // The cycle's own day gets its partition first, so the writer below lands there and never in the legacy partition.
    var at = await _cycleMomentAsync(setup);
    await _maintainAsync(setup, at, keep: null);

    // A writer mid-transaction holds ROW EXCLUSIVE on the parent, so the parent's exclusive lock is not free.
    await using var writer = await _openAsync();
    await using var writing = await writer.BeginTransactionAsync();
    await writer.ExecuteAsync("INSERT INTO wh_signals (wire_name, created_at) VALUES ('in-flight', @at)", new { at });

    var results = await _maintainAsync(setup, at, WINDOW);
    await writing.CommitAsync();

    await Assert.That(_named(results, CARRIED)).IsEquivalentTo([LEGACY]);
    await Assert.That(_named(results, EMPTIED)).IsEquivalentTo([LEGACY]);
    await Assert.That(await _namesAsync(setup)).IsEquivalentTo([PENDING_FIRST, PENDING_SECOND, "in-flight"])
      .Because("emptying the partition keeps the live instance's unconsumed signals as dropping it does");
  }
}
