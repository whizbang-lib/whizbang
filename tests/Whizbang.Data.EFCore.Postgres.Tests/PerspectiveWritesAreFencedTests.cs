// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A perspective row is never written by an instance whose partition's ownership has moved on since it claimed the
/// stream's work (#2556): the set-based sink reports such a write <see cref="PerspectiveWriteStatus.Fenced"/> and writes
/// the batch's other rows, and the per-pair upsert throws <see cref="PartitionOwnershipLostException"/> before writing.
/// Either way the write is counted and the current owner applies the stream. A stream the instance holds no tenure for
/// (off map mode) is written as before, with no extra round trip.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePerspectiveWriteFence.cs</code-under-test>
[Category("Shard5")]
public class PerspectiveWritesAreFencedTests : EFCoreTestBase {
  private const string ACTION_TABLE = "wh_per_action_test";
  private const string SUPPORTS_MAP = """{"partition_ownership_level":1}""";
  private static readonly Guid _a = new("00000000-0000-0000-0000-00000000000a");
  private static readonly Guid _b = new("00000000-0000-0000-0000-00000000000b");

  [Test]
  public async Task TheSink_FencesAWriteWhosePartitionMoved_AndWritesTheRestAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stale = Guid.CreateVersion7();
    var current = Guid.CreateVersion7();
    _claim(tenures, stale, 5);
    _claim(tenures, current, 6);
    await _moveOnAsync(conn, 5);

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, stale), _write(context, current)]);

    await Assert.That(outcomes.Select(o => o.Status)).IsEquivalentTo(
      [PerspectiveWriteStatus.Fenced, PerspectiveWriteStatus.Written], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _rowExistsAsync(conn, stale)).IsFalse().Because("a stale owner's model is never written");
    await Assert.That(await _rowExistsAsync(conn, current)).IsTrue();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task TheSink_WhenEveryWriteIsCurrent_WritesThemAllAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 6);

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, stream)]);

    await Assert.That(outcomes.Single().Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(observer.Total()).IsEqualTo(0L);
  }

  [Test]
  public async Task ThePerPairUpsert_OfAStreamWhosePartitionMoved_ThrowsBeforeWritingAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 5);
    await _moveOnAsync(conn, 5);

    var thrown = await Assert.ThrowsAsync<PartitionOwnershipLostException>(() => new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "stale", Value = 1 }, _meta(), new PerspectiveScope()));

    await Assert.That(thrown!.StreamId).IsEqualTo(stream);
    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task AStreamWithNoTenure_IsWrittenAsBefore_EvenOnAContextWithoutAHostAsync() {
    await using var conn = await _openAsync();
    await using var context = CreateDbContext();
    var stream = Guid.CreateVersion7();

    await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "off", Value = 1 }, _meta(), new PerspectiveScope());
    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, Guid.CreateVersion7())]);

    await Assert.That(await _rowExistsAsync(conn, stream)).IsTrue();
    await Assert.That(outcomes.Single().Status).IsEqualTo(PerspectiveWriteStatus.Written);
  }

  [Test]
  public async Task TheFence_InsideTheWritesTransaction_OnAHostWithoutMetricsOrLogging_StillFencesAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var tenures = new PartitionTenures();
    await using var host = new ServiceCollection().AddSingleton(tenures).BuildServiceProvider();
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions).UseApplicationServiceProvider(host).Options;
    await using var context = new WorkCoordinationDbContext(options);
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 5);
    await _moveOnAsync(conn, 5);
    await using var transaction = await context.Database.BeginTransactionAsync();

    var stale = await EFCorePerspectiveWriteFence.StaleAsync(context, [stream], CancellationToken.None);

    await Assert.That(stale).IsEquivalentTo([stream]).Because("the check runs in the write's own transaction, and fences without a meter or a logger");
  }

  [Test]
  public async Task TheException_CarriesItsStreamAndTheStandardConstructorsAsync() {
    await Assert.That(new PartitionOwnershipLostException().StreamId).IsEqualTo(Guid.Empty);
    await Assert.That(new PartitionOwnershipLostException("m").Message).IsEqualTo("m");
    await Assert.That(new PartitionOwnershipLostException("m", new InvalidOperationException()).InnerException).IsNotNull();
  }

  // --- helpers --------------------------------------------------------------------------------------

  private (WorkCoordinationDbContext Context, PartitionTenures Tenures, CounterObserver Observer) _context() {
    var tenures = new PartitionTenures();
    var services = new ServiceCollection().AddMetrics().AddLogging();
    services.AddSingleton(tenures);
    services.AddSingleton(sp => new WorkCoordinatorMetrics(new WhizbangMetrics(meterFactory: sp.GetRequiredService<IMeterFactory>())));
    var host = services.BuildServiceProvider();
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions).UseApplicationServiceProvider(host).Options;
    return (new WorkCoordinationDbContext(options), tenures, new CounterObserver(host.GetRequiredService<WorkCoordinatorMetrics>()));
  }

  private static void _claim(PartitionTenures tenures, Guid stream, int partition) =>
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 1 }, new Dictionary<Guid, int> { [stream] = partition });

  private static PerspectiveMetadata _meta() => new() {
    EventType = "FenceTestEvent",
    EventId = Guid.CreateVersion7().ToString("D"),
    Timestamp = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc),
  };

  private static PendingPerspectiveWrite _write(DbContext context, Guid id) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<ActionTestModel>(ACTION_TABLE, id,
      new ActionTestModel { Id = id, Name = "set-based", Value = 1 }, _meta(), new PerspectiveScope(), null, false, PerspectiveRowVersion.Absent))!;

  private sealed class CounterObserver : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<long> _cells = [];

    public CounterObserver(WorkCoordinatorMetrics metrics) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, metrics.CommitHandlerFallbacks.Meter)
            && instrument.Name == "whizbang.work_coordinator.fenced_writes") {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, _, _) => {
        lock (_cells) { _cells.Add(value); }
      });
      _listener.Start();
    }

    public long Total() {
      lock (_cells) { _cells.Clear(); }
      _listener.RecordObservableInstruments();
      lock (_cells) { return _cells.Sum(); }
    }

    public void Dispose() => _listener.Dispose();
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task _mapModeAsync(NpgsqlConnection conn) {
    foreach (var instance in new[] { _a, _b }) {
      await _execAsync(conn, "SELECT record_heartbeat(@inst, 'test', 'test-host', 1, @metadata::jsonb, NULL, NULL, NULL, 'pooled')",
        ("inst", instance), ("metadata", SUPPORTS_MAP));
    }
    var epoch = Convert.ToInt64(await _scalarAsync(conn,
      "SELECT epoch FROM wh_vote_role(@role, @a, INTERVAL '30 seconds', INTERVAL '5 seconds', NULL, ARRAY[1, 0], NULL) v WHERE v.outcome = 'granted'",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a)), System.Globalization.CultureInfo.InvariantCulture);
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT wh_publish_partition_assignment(@role, @a, @epoch, @members, INTERVAL '90 seconds')";
      cmd.Parameters.AddWithValue("role", PartitionAssignerOptions.ROLE);
      cmd.Parameters.AddWithValue("a", _a);
      cmd.Parameters.AddWithValue(nameof(epoch), epoch);
      cmd.Parameters.Add(new NpgsqlParameter("members", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { _a } });
      await cmd.ExecuteNonQueryAsync();
    }
    _ = await _scalarAsync(conn, "SELECT wh_open_partition_ownership_by_map(@role, @a, @epoch)",
      ("role", PartitionAssignerOptions.ROLE), ("a", _a), ("epoch", epoch));
  }

  private static Task _moveOnAsync(NpgsqlConnection conn, int partition) =>
    _execAsync(conn, "UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1 WHERE partition_number = @p",
      ("b", _b), ("p", partition));

  private static async Task<bool> _rowExistsAsync(NpgsqlConnection conn, Guid id) =>
    (long)(await _scalarAsync(conn, $"SELECT count(*) FROM {ACTION_TABLE} WHERE id = @id", ("id", id)))! > 0;

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return await cmd.ExecuteScalarAsync();
  }

  private static async Task _execAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    await cmd.ExecuteNonQueryAsync();
  }
}
