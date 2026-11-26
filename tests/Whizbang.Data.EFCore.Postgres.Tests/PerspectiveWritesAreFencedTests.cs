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
  public async Task TheSink_InsideTheWritesTransaction_OnAHostWithoutMetricsOrLogging_StillFencesAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var tenures = new PartitionTenures();
    await using var host = new ServiceCollection().AddSingleton(tenures).BuildServiceProvider();
    var options = new DbContextOptionsBuilder<WorkCoordinationDbContext>(DbContextOptions).UseApplicationServiceProvider(host).Options;
    await using var context = new WorkCoordinationDbContext(options);
    var stale = Guid.CreateVersion7();
    var current = Guid.CreateVersion7();
    _claim(tenures, stale, 5);
    _claim(tenures, current, 6);
    await _moveOnAsync(conn, 5);
    await using var transaction = await context.Database.BeginTransactionAsync();

    var outcomes = await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, stale), _write(context, current)]);
    await transaction.CommitAsync();

    await Assert.That(outcomes.Select(o => o.Status)).IsEquivalentTo(
      [PerspectiveWriteStatus.Fenced, PerspectiveWriteStatus.Written], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the fenced row is undone to its savepoint and the write's transaction goes on, with no meter or logger to report to");
    await Assert.That(await _rowExistsAsync(conn, current)).IsTrue();
    await Assert.That(await _rowExistsAsync(conn, stale)).IsFalse();
  }

  [Test]
  [Timeout(60_000)]
  public async Task TheSink_AgainstAFlipThatCommitsWhileItWrites_WaitsForIt_AndIsFencedAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 5);
    var pid = await _pidOfAsync(context);
    var (flipConnection, flip) = await _flipUncommittedAsync(5);
    await using var ___ = flipConnection;

    var writing = new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, stream)], cancellationToken);
    await _blockedOrDoneAsync(conn, pid, writing, cancellationToken);
    await flip.CommitAsync(cancellationToken);
    var outcomes = await writing;

    await Assert.That(outcomes.Single().Status).IsEqualTo(PerspectiveWriteStatus.Fenced)
      .Because("the write holds its partition's epoch in its own statement, so a flip committed before the write is fenced, never slipped past");
    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  [Timeout(60_000)]
  public async Task ThePerPairUpsert_AgainstAFlipThatCommitsWhileItWrites_WaitsForIt_AndThrowsAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 5);
    var pid = await _pidOfAsync(context);
    var (flipConnection, flip) = await _flipUncommittedAsync(5);
    await using var ___ = flipConnection;

    var writing = new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "stale", Value = 1 }, _meta(), new PerspectiveScope(), cancellationToken);
    await _blockedOrDoneAsync(conn, pid, writing, cancellationToken);
    await flip.CommitAsync(cancellationToken);

    await Assert.ThrowsAsync<PartitionOwnershipLostException>(() => writing);
    await Assert.That(await _rowExistsAsync(conn, stream)).IsFalse();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task ALocalRebuild_AfterTheStreamsWorkEnded_IsWritten_ThoughItsPartitionMovedByDeadlineAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    var work = _claim(tenures, stream, 5);
    await _moveOnAsync(conn, 5);
    tenures.Forget([work]);

    await new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "rebuilt", Value = 1 }, _meta(), new PerspectiveScope());

    await Assert.That(await _rowExistsAsync(conn, stream)).IsTrue()
      .Because("the claim the moved partition left behind applies no work in flight, so it fences nothing");
    await Assert.That(observer.Total()).IsEqualTo(0L);
  }

  [Test]
  public async Task EveryWriteForm_IsFencedInItsStatement_AndAStreamWithNoWorkInTheSameStatementIsWrittenAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var upserted = Guid.CreateVersion7();
    var updated = Guid.CreateVersion7();
    var purged = Guid.CreateVersion7();
    var freePurge = Guid.CreateVersion7();
    var freeUpsert = Guid.CreateVersion7();
    var sink = new EFCorePostgresPerspectiveWriteSink(context);
    await sink.WriteAsync([_write(context, updated), _write(context, purged), _write(context, freePurge)]);
    var xmin = Convert.ToInt64(await _scalarAsync(conn, $"SELECT xmin::text::bigint FROM {ACTION_TABLE} WHERE id = @id", ("id", updated)),
      System.Globalization.CultureInfo.InvariantCulture);
    foreach (var stream in new[] { upserted, updated, purged }) {
      _claim(tenures, stream, 5);
    }
    await _moveOnAsync(conn, 5);

    var outcomes = await sink.WriteAsync([
      _write(context, upserted, PerspectiveRowVersion.Unchecked),
      _write(context, freeUpsert, PerspectiveRowVersion.Unchecked),
      _write(context, updated, PerspectiveRowVersion.Of(xmin)),
      PendingPerspectiveWrite.Purge(ACTION_TABLE, purged),
      PendingPerspectiveWrite.Purge(ACTION_TABLE, freePurge),
    ]);

    await Assert.That(outcomes.Select(o => o.Status)).IsEquivalentTo([
      PerspectiveWriteStatus.Fenced, PerspectiveWriteStatus.Written, PerspectiveWriteStatus.Fenced,
      PerspectiveWriteStatus.Fenced, PerspectiveWriteStatus.Written,
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _rowExistsAsync(conn, upserted)).IsFalse();
    await Assert.That(await _rowExistsAsync(conn, purged)).IsTrue().Because("a stale owner's purge removes nothing");
    await Assert.That(await _rowExistsAsync(conn, freePurge)).IsFalse();
    await Assert.That(observer.Total()).IsEqualTo(3L);
  }

  [Test]
  public async Task ThePerPairCheckedUpdate_OfAStreamWhosePartitionMoved_ThrowsAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, stream)]);
    var xmin = Convert.ToInt64(await _scalarAsync(conn, $"SELECT xmin::text::bigint FROM {ACTION_TABLE} WHERE id = @id", ("id", stream)),
      System.Globalization.CultureInfo.InvariantCulture);
    _claim(tenures, stream, 5);
    await _moveOnAsync(conn, 5);

    await Assert.ThrowsAsync<PartitionOwnershipLostException>(() => new PostgresUpsertStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "stale", Value = 2 }, _meta(), new PerspectiveScope(),
      forceUpdateScope: false, PerspectiveRowVersion.Of(xmin)));

    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task TheEntityFrameworkWrite_HoldsThePartitionFirst_AndAStaleOwnerThrows_ACurrentOneWritesAsync() {
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
    var strategy = new EntityFrameworkOnlyStrategy();

    await Assert.ThrowsAsync<PartitionOwnershipLostException>(() => strategy.UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stale, new ActionTestModel { Id = stale, Name = "stale", Value = 1 }, _meta(), new PerspectiveScope()));
    await strategy.UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, current, new ActionTestModel { Id = current, Name = "current", Value = 1 }, _meta(), new PerspectiveScope());

    await Assert.That(await _rowExistsAsync(conn, stale)).IsFalse();
    await Assert.That(await _rowExistsAsync(conn, current)).IsTrue();
    await Assert.That(observer.Total()).IsEqualTo(1L);
  }

  [Test]
  public async Task TheEntityFrameworkWrite_InATransaction_HoldsThePartitionUntilItCommitsAsync() {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    _claim(tenures, stream, 6);
    await using var transaction = await context.Database.BeginTransactionAsync();

    await new EntityFrameworkOnlyStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "current", Value = 1 }, _meta(), new PerspectiveScope());
    var blockedWhileOpen = await _flipIsBlockedAsync(6);
    await transaction.CommitAsync();

    await Assert.That(blockedWhileOpen).IsTrue()
      .Because("the hold is taken in the write's transaction, so a flip waits until the write commits");
    await Assert.That(await _flipIsBlockedAsync(6)).IsFalse();
    await Assert.That(await _rowExistsAsync(conn, stream)).IsTrue();
  }

  [Test]
  [Timeout(60_000)]
  public async Task TheEntityFrameworkWrite_WithNoTransaction_HoldsThePartitionUntilItsWriteCommitsAsync(CancellationToken cancellationToken) {
    await using var conn = await _openAsync();
    await _mapModeAsync(conn);
    var (context, tenures, observer) = _context();
    await using var _ = context;
    using var __ = observer;
    var stream = Guid.CreateVersion7();
    await new EFCorePostgresPerspectiveWriteSink(context).WriteAsync([_write(context, Guid.CreateVersion7())], cancellationToken);
    await new EntityFrameworkOnlyStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "first", Value = 1 }, _meta(), new PerspectiveScope(),
      cancellationToken);
    _claim(tenures, stream, 6);
    // Another session holds the row, so the write blocks once it has taken its hold and reached the row.
    await using var rowHolder = await _openAsync();
    await using var held = await rowHolder.BeginTransactionAsync(cancellationToken);
    await _execOnAsync(rowHolder, held, $"SELECT 1 FROM {ACTION_TABLE} WHERE id = @id FOR UPDATE", stream);
    var holderPid = Convert.ToInt32(await _scalarAsync(rowHolder, "SELECT pg_backend_pid()"), System.Globalization.CultureInfo.InvariantCulture);

    var writing = new EntityFrameworkOnlyStrategy().UpsertPerspectiveRowAsync(
      context, ACTION_TABLE, stream, new ActionTestModel { Id = stream, Name = "second", Value = 2 }, _meta(), new PerspectiveScope(),
      cancellationToken);
    while (!writing.IsCompleted && !(bool)(await _scalarAsync(conn,
             "SELECT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE @holder = ANY(pg_blocking_pids(a.pid)))", ("holder", holderPid)))!) {
      cancellationToken.ThrowIfCancellationRequested();
    }
    var flipBlockedDuringTheWrite = await _flipIsBlockedAsync(6);
    await held.RollbackAsync(cancellationToken);
    await writing;

    await Assert.That(flipBlockedDuringTheWrite).IsTrue()
      .Because("with no transaction of the caller's, the write opens one, so its hold spans the write and a flip waits for it");
    await Assert.That(await _flipIsBlockedAsync(6)).IsFalse();
  }

  private static async Task _execOnAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue(nameof(id), id);
    await cmd.ExecuteNonQueryAsync();
  }

  // Whether a flip of the partition would have to wait: NOWAIT answers at once instead of waiting. A flip raises the
  // epoch, a key column since 610 (#2577), so it locks FOR UPDATE, which a write's FOR KEY SHARE hold blocks.
  private async Task<bool> _flipIsBlockedAsync(int partition) {
    await using var flipper = await _openAsync();
    await using var tx = await flipper.BeginTransactionAsync();
    try {
      await using var cmd = flipper.CreateCommand();
      cmd.Transaction = tx;
      cmd.CommandText = $"SELECT 1 FROM wh_partition_ownership WHERE partition_number = {partition} FOR UPDATE NOWAIT";
      await cmd.ExecuteScalarAsync();
      return false;
    } catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable) {
      return true;
    } finally {
      await tx.RollbackAsync();
    }
  }

  /// <summary>The upsert with its single-statement path off, so every write takes the Entity Framework path.</summary>
  private sealed class EntityFrameworkOnlyStrategy : PostgresUpsertStrategy {
    protected override bool UsesAtomicUpsert => false;
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

  // The stream claimed at its partition's epoch (1, as _mapModeAsync opened it), with perspective work fetched on it.
  private static Guid _claim(PartitionTenures tenures, Guid stream, int partition) {
    tenures.RecordClaim(new Dictionary<Guid, long> { [stream] = 1 }, new Dictionary<Guid, int> { [stream] = partition });
    var work = Guid.CreateVersion7();
    tenures.RecordFetchedPerspective(work, stream);
    return work;
  }

  // A flip of the partition, open in its own transaction, so the test decides when it commits.
  private async Task<(NpgsqlConnection Connection, NpgsqlTransaction Flip)> _flipUncommittedAsync(int partition) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var flip = await connection.BeginTransactionAsync();
    await using var cmd = connection.CreateCommand();
    cmd.Transaction = flip;
    cmd.CommandText = "UPDATE wh_partition_ownership SET owner_instance_id = @b, epoch = epoch + 1 WHERE partition_number = @p";
    cmd.Parameters.AddWithValue("b", _b);
    cmd.Parameters.AddWithValue("p", partition);
    await cmd.ExecuteNonQueryAsync();
    return (connection, flip);
  }

  // Completes once the backend is waiting on a lock another session holds, or once the write finished without waiting.
  private static async Task _blockedOrDoneAsync(NpgsqlConnection observer, int pid, Task write, CancellationToken cancellationToken) {
    while (!write.IsCompleted) {
      cancellationToken.ThrowIfCancellationRequested();
      if ((bool)(await _scalarAsync(observer, "SELECT cardinality(pg_blocking_pids(@pid)) > 0", ("pid", pid)))!) {
        return;
      }
    }
  }

  private static async Task<int> _pidOfAsync(DbContext context) {
    await context.Database.OpenConnectionAsync();
    await using var cmd = context.Database.GetDbConnection().CreateCommand();
    cmd.CommandText = "SELECT pg_backend_pid()";
    return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static PerspectiveMetadata _meta() => new() {
    EventType = "FenceTestEvent",
    EventId = Guid.CreateVersion7().ToString("D"),
    Timestamp = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc),
  };

  private static PendingPerspectiveWrite _write(DbContext context, Guid id) => _write(context, id, PerspectiveRowVersion.Absent);

  private static PendingPerspectiveWrite _write(DbContext context, Guid id, PerspectiveRowVersion version) =>
    new PostgresUpsertStrategy().PreparePendingWrite(context, new BaseUpsertStrategy.UpsertRowArgs<ActionTestModel>(ACTION_TABLE, id,
      new ActionTestModel { Id = id, Name = "set-based", Value = 1 }, _meta(), new PerspectiveScope(), null, false, version))!;

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
