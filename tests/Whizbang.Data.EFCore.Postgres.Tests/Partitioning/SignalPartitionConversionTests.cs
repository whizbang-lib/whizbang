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
/// Issue #1646: <c>wh_signals</c> converts to daily partitions by <c>created_at</c>, its key becomes
/// <c>(id, created_at)</c>, a durable signal is still published and delivered, and once the manager keeps the table
/// the signal sweep leaves its partitions to the partition drop and clears only the default partition.
/// </summary>
/// <remarks>
/// The conversion runs on the database's own clock (its <c>p_now</c> default), the clock <c>created_at</c> is stamped
/// with; later moments are written relative to it in SQL.
/// </remarks>
/// <docs>operations/infrastructure/partitioned-ledgers#signals</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/231_PartitionSignalsAndLifecycleCompletions.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
[NotInParallel("SignalRetention")]
public class SignalPartitionConversionTests : EFCoreTestBase {
  private const string CONVERT = "SELECT wh_partition_convert('wh_signals', 'created_at', '1 day')";
  private const string CONVERTED = "converted";
  private const string RELKIND = "SELECT relkind::text FROM pg_class WHERE oid = 'wh_signals'::regclass";

  private readonly record struct DurableProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await connection.ExecuteAsync("SET statement_timeout = '30s'");
    return connection;
  }

  private static Task<long> _signalAsync(NpgsqlConnection connection, string name, string age) =>
    connection.ExecuteScalarAsync<long>(
      "INSERT INTO wh_signals (wire_name, created_at) VALUES (@name, NOW() - @age::interval) RETURNING id", new { name, age });

  private static Task _cursorPastEverythingAsync(NpgsqlConnection connection) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id) VALUES (@id, (SELECT max(id) FROM wh_signals))",
      new { id = Guid.CreateVersion7() });

  private static Task _enablePartitionMaintenanceAsync(NpgsqlConnection connection, bool enabled = true) =>
    connection.ExecuteAsync(
      "INSERT INTO wh_settings (setting_key, setting_value, value_type) VALUES ('partition_maintenance_enabled', @value, 'boolean') " +
      "ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value", new { value = enabled ? "true" : "false" });

  private static Task<List<string>> _namesAsync(NpgsqlConnection connection) =>
    connection.QueryAsync<string>("SELECT wire_name FROM wh_signals ORDER BY id");

  private PgDurableSignalRetentionWorker _sweeper(TimeSpan window) =>
    new(Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString, SignalRetention = window }),
      new ConfigurationBuilder().Build(), NullLogger<PgDurableSignalRetentionWorker>.Instance);

  [Test]
  public async Task Conversion_KeepsEverySignal_AndTheKeyGainsCreatedAtAsync() {
    await using var connection = await _openAsync();
    await _signalAsync(connection, "week-old", "7 days");
    await _signalAsync(connection, "today", "0 days");

    var outcome = await connection.ExecuteScalarAsync<string>(CONVERT);

    await Assert.That(outcome).IsEqualTo(CONVERTED);
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("p");
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo(["week-old", "today"]);
    await Assert.That(await connection.ExecuteScalarAsync<string>(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'wh_signals'::regclass AND contype = 'p'"))
      .IsEqualTo("PRIMARY KEY (id, created_at)")
      .Because("every unique key of a partitioned table holds the partition column");
    await Assert.That(await connection.QueryAsync<string>(
        "SELECT indexrelid::regclass::text FROM pg_index WHERE indrelid = 'wh_signals'::regclass AND NOT indisprimary ORDER BY 1"))
      .IsEquivalentTo(["idx_wh_signals_broadcast", "idx_wh_signals_target_instance"])
      .Because("the tail's two partial indexes keep their names on the parent");
    await Assert.That(await connection.ExecuteScalarAsync<string>(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'wh_signals_partition_version'")).IsEqualTo("1");
  }

  [Test]
  public async Task ADurableSignal_IsStillPublishedAndDelivered_AfterConversionAsync() {
    const string wireName = "utest-durable-partitioned-1646";
    var entry = new SignalTypeEntry(typeof(DurableProbe), wireName, SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
      static (sink, ct) => sink.ReceiveAsync<DurableProbe>(default, ct));
    await using var connection = await _openAsync();
    await _signalAsync(connection, "before-conversion", "1 hour");
    await Assert.That(await connection.ExecuteScalarAsync<string>(CONVERT)).IsEqualTo(CONVERTED);
    var instanceId = Guid.CreateVersion7();
    await connection.ExecuteAsync(
      "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id) VALUES (@instanceId, (SELECT max(id) FROM wh_signals))",
      new { instanceId });

    var options = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var configuration = new ConfigurationBuilder().Build();
    var instance = new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(options), configuration, instance, NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null, timeProvider: null);
    var transport = new PostgresSignalTransport(
      Options.Create(options), configuration, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    SignalTypeRegistry.Register(new SingleSource(entry));
    await transport.StartAsync(new CountingSink());
    await transport.PublishAsync(new DurableProbe(1), SignalTarget.Broadcast);

    var sink = new CountingSink();
    var tail = new PgDurableSignalTailWorker(
      Options.Create(options), configuration, instance, sink, NullLogger<PgDurableSignalTailWorker>.Instance);
    tail.UseWireMap(new Dictionary<string, SignalTypeEntry> { [wireName] = entry });
    var plan = NotificationConnectionPlan.Create(null, NotificationConnectionStringResolver.Resolve(options, configuration));
    var delivered = await tail.TickOnceAsync(plan, CancellationToken.None);

    await Assert.That(delivered).IsEqualTo(1);
    await Assert.That(sink.Received).IsEqualTo(1)
      .Because("the tail reads past its cursor by id across every partition, as it read the plain table");
    await Assert.That(await connection.ExecuteScalarAsync<long>(
      "SELECT count(*) FROM wh_signals WHERE wire_name = @wireName", new { wireName })).IsEqualTo(1L);
  }

  [Test]
  public async Task Conversion_InterruptedPartway_LeavesTheTableAsItWas_AndTheNextCycleCompletesItAsync() {
    await using var connection = await _openAsync();
    await _signalAsync(connection, "kept", "1 hour");
    await connection.ExecuteAsync("CREATE TABLE wh_signals_default (id INT)");

    var error = await Assert.That(async () => await connection.ExecuteScalarAsync<string>(CONVERT)).Throws<PostgresException>();

    await Assert.That(error!.SqlState).IsEqualTo(PostgresErrorCodes.DuplicateTable);
    await Assert.That(await connection.ExecuteScalarAsync<string>(RELKIND)).IsEqualTo("r");
    await connection.ExecuteAsync("DROP TABLE wh_signals_default");
    await Assert.That(await connection.ExecuteScalarAsync<string>(CONVERT)).IsEqualTo(CONVERTED);
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo(["kept"]);
  }

  [Test]
  public async Task ACycle_CreatesDaysAhead_AndDropsWhatEndedBeforeTheWindowAsync() {
    await using var connection = await _openAsync();
    await _signalAsync(connection, "old", "1 hour");
    await connection.ExecuteScalarAsync<string>(CONVERT);

    var created = await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_signals', '1 day', 3, '7 days') WHERE action = 'created'");
    var dropped = await connection.QueryAsync<string>(
      "SELECT partition_name FROM wh_partition_maintain('wh_signals', '1 day', 3, '7 days', NOW() + INTERVAL '30 days') WHERE action = 'dropped'");

    await Assert.That(created.Count).IsEqualTo(3)
      .Because("the legacy partition holds today, so the three days after it are created");
    await Assert.That(dropped).Contains("wh_signals_legacy");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_signals WHERE wire_name = 'old'")).IsEqualTo(0L);
  }

  [Test]
  public async Task OnceManaged_TheSweepLeavesPartitionsToTheDrop_AndClearsOnlyTheDefaultAsync() {
    await using var connection = await _openAsync();
    // One moment read from the database, so every partition and row below is placed against the same instant.
    var now = await connection.ExecuteScalarAsync<DateTime>("SELECT NOW()");
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection);
    // Ten days on the legacy partition has ended and is dropped; three days back then gets a partition of its own,
    // and two days back has none, so a row stamped then lands in the default partition.
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_signals', '1 day', 0, '1 day', @now::timestamptz + INTERVAL '10 days')", new { now });
    await connection.ExecuteAsync(
      "SELECT * FROM wh_partition_maintain('wh_signals', '1 day', 0, NULL, @now::timestamptz - INTERVAL '3 days')", new { now });
    await connection.ExecuteAsync(
      "INSERT INTO wh_signals (wire_name, created_at) VALUES ('in-a-partition', @now::timestamptz - INTERVAL '3 days'), " +
      "('in-the-default', @now::timestamptz - INTERVAL '2 days')", new { now });
    await _cursorPastEverythingAsync(connection);
    var placedIn = await connection.ExecuteScalarAsync<string>(
      "SELECT tableoid::regclass::text FROM wh_signals WHERE wire_name = 'in-the-default'");

    var deleted = await _sweeper(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    await Assert.That(placedIn).IsEqualTo("wh_signals_default");
    await Assert.That(deleted).IsEqualTo(1);
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo(["in-a-partition"])
      .Because("an expired partition goes whole when the manager drops it; only the default partition, which no drop reaches, is swept by row");
  }

  [Test]
  public async Task AConvertedTable_WithPartitioningSwitchedOff_IsStillSweptByRowAsync() {
    await using var connection = await _openAsync();
    await _signalAsync(connection, "old", "3 days");
    await _signalAsync(connection, "young", "1 hour");
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection, enabled: false);
    await _cursorPastEverythingAsync(connection);

    var deleted = await _sweeper(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    await Assert.That(deleted).IsEqualTo(1);
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo(["young"]);
  }

  [Test]
  public async Task OnceManaged_WithoutADefaultPartition_TheSweepDeletesNothingAsync() {
    await using var connection = await _openAsync();
    await _signalAsync(connection, "old", "3 days");
    await connection.ExecuteScalarAsync<string>(CONVERT);
    await _enablePartitionMaintenanceAsync(connection);
    await connection.ExecuteAsync("DROP TABLE wh_signals_default");
    await _cursorPastEverythingAsync(connection);

    var deleted = await _sweeper(TimeSpan.FromDays(1)).SweepOnceAsync(CancellationToken.None);

    await Assert.That(deleted).IsEqualTo(0);
    await Assert.That(await _namesAsync(connection)).IsEquivalentTo(["old"]);
  }

  private sealed class SingleSource(SignalTypeEntry entry) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => [entry];
  }
}
