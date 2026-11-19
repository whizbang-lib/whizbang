// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A type stored while Sourced, then declared ephemeral: its streams leave the lifecycle ledger (#2328).</summary>
public sealed record LifecycleEphemeralProbeTicked : IEvent {
  /// <summary>The session, which is the stream.</summary>
  [StreamId]
  public Guid SessionId { get; init; }
}

/// <summary>
/// #2328: the stream lifecycle ledger holds only streams with a Sourced event, but a stream written while its type was
/// Sourced kept its row after the history walk reclassified it ephemeral, and its <c>approx_bytes</c> kept counting bodies
/// the reaper deleted. The walk now settles the streams it reclassifies, and a one-time release settles the rows the
/// ledger already holds.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#ephemeral-streams</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/443_TheLifecycleLedgerLetsGoOfEphemeralStreams.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StreamLifecycleBackfill.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamLifecycleEphemeralSqlTests : EFCoreTestBase {
  private const string RELEASE_VERSION = "stream_lifecycle_ephemeral_release_version";

  [Test]
  public async Task Reclassifying_AStreamWhole_RemovesItsLedgerRowAsync() {
    await using var conn = await _openAsync();
    var stream = (Guid)TrackedGuid.New();
    await _appendAsync(stream);
    await _appendAsync(stream);
    await Assert.That(await _countAsync(conn, $"SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = '{stream}'")).IsEqualTo(1L)
      .Because("the control: a Sourced stream has its ledger row");

    var result = await new EphemeralHistoryReclassifyStep(NullLogger.Instance, 100, 10).ReclassifyAsync(_services(), CancellationToken.None);

    await Assert.That(result.EventsReclassified).IsEqualTo(2L);
    await Assert.That(await _countAsync(conn, $"SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = '{stream}'")).IsEqualTo(0L)
      .Because("an ephemeral stream is reaped by its time to live and never reaches end of life");
  }

  [Test]
  public async Task Release_RemovesHeldEphemeralRows_InBatches_AndRecountsAMixedStreamAsync() {
    await using var conn = await _openAsync();
    var ephemeral = await _heldStreamAsync(conn, sourced: 0, ephemeral: 2);
    var sourced = await _heldStreamAsync(conn, sourced: 2, ephemeral: 0);
    var mixed = await _heldStreamAsync(conn, sourced: 1, ephemeral: 1);
    var sourcedBytes = await _bytesAsync(conn, sourced);
    await _executeAsync(conn, $"UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{RELEASE_VERSION}'");

    var visited = await _driveAsync(batchSize: 1, maxRounds: 2);
    var markerPartway = await _settingAsync(conn, RELEASE_VERSION);
    visited += await _driveAsync(batchSize: 1, maxRounds: 10);

    await Assert.That(visited).IsEqualTo(3L).Because("each ledger row is visited once, a batch of one at a time");
    await Assert.That(markerPartway).IsEqualTo("0").Because("two rounds did not reach the end");
    await Assert.That(await _settingAsync(conn, RELEASE_VERSION)).IsEqualTo("1");
    await Assert.That(await _countAsync(conn, $"SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = '{ephemeral}'")).IsEqualTo(0L);
    await Assert.That(await _bytesAsync(conn, sourced)).IsEqualTo(sourcedBytes).Because("a Sourced stream is untouched");
    await Assert.That(await _bytesAsync(conn, mixed)).IsEqualTo(await _countedBytesAsync(conn, mixed))
      .Because("a stream holding both kinds counts only the events the ledger counts");
    await Assert.That(await _bytesAsync(conn, mixed)).IsLessThan(1_000_000L);
    await Assert.That(await _driveAsync(batchSize: 1, maxRounds: 10)).IsEqualTo(0L).Because("once done, nothing is visited");
  }

  [Test]
  public async Task Release_WhileAnotherCallerHoldsTheCursor_StepsAsideAsync() {
    await using var conn = await _openAsync();
    await _heldStreamAsync(conn, sourced: 0, ephemeral: 1);
    await _executeAsync(conn, $"UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{RELEASE_VERSION}'");
    await using var holder = await _openAsync();
    await using var hold = await holder.BeginTransactionAsync();
    await _executeAsync(holder, "SELECT 1 FROM wh_settings WHERE setting_key = 'stream_lifecycle_ephemeral_release_cursor' FOR UPDATE");

    var busy = await _countAsync(conn, "SELECT wh_stream_lifecycle_release_ephemeral_batch(10)");
    var driven = await _driveAsync(batchSize: 10, maxRounds: 10);
    await hold.RollbackAsync();

    await Assert.That(busy).IsEqualTo(-1L);
    await Assert.That(driven).IsEqualTo(0L);
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(1L);
  }

  [Test]
  public async Task Backfill_NeverSeedsAnEphemeralStreamAsync() {
    await using var conn = await _openAsync();
    var ephemeral = await _heldStreamAsync(conn, sourced: 0, ephemeral: 2);
    await _executeAsync(conn, $"DELETE FROM wh_stream_lifecycle WHERE stream_id = '{ephemeral}'");
    await _executeAsync(conn, """
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = 'stream_lifecycle_backfill_version';
      UPDATE wh_settings SET setting_value = '00000000-0000-0000-0000-000000000000' WHERE setting_key = 'stream_lifecycle_backfill_cursor';
      """);

    await _driveAsync(batchSize: 10, maxRounds: 10);

    await Assert.That(await _countAsync(conn, $"SELECT count(*) FROM wh_stream_lifecycle WHERE stream_id = '{ephemeral}'")).IsEqualTo(0L);
    await Assert.That(await _settingAsync(conn, "stream_lifecycle_backfill_version")).IsEqualTo("1");
  }

  /// <summary>
  /// A stream with its ledger row as a pre-443 store held it: every event counted while it was written, then the given
  /// number flagged ephemeral by a reclassification that left the row alone.
  /// </summary>
  private static async Task<Guid> _heldStreamAsync(NpgsqlConnection conn, int sourced, int ephemeral) {
    var stream = (Guid)TrackedGuid.New();
    for (var i = 0; i < sourced + ephemeral; i++) {
      var flags = i < sourced ? 0 : 8;
      await _executeAsync(conn, $"""
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
        VALUES ('{(Guid)TrackedGuid.New()}', '{stream}', '{stream}', 'Agg.Held', 'Agg.Held, Tests', {i + 1}, now(), {flags})
        """);
    }
    await _executeAsync(conn, $"""
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      SELECT es.event_id, jsonb_build_object('pad', repeat('x', 400)), jsonb_build_object() FROM wh_event_store es WHERE es.stream_id = '{stream}';
      INSERT INTO wh_stream_lifecycle (stream_id, stream_type, first_appended_at, last_appended_bucket, last_version, approx_bytes)
      VALUES ('{stream}', 'Agg.Held', now(), date_trunc('hour', now(), 'UTC'), {sourced + ephemeral}, 1000000)
      """);
    return stream;
  }

  private async Task<long> _driveAsync(int batchSize, int maxRounds) {
    await using var own = await _openAsync();
    return await StreamLifecycleBackfill.RunAsync(own, "\"public\"", batchSize, maxRounds, NullLogger.Instance, CancellationToken.None);
  }

  private static Task<long> _bytesAsync(NpgsqlConnection conn, Guid stream) =>
    _countAsync(conn, $"SELECT approx_bytes FROM wh_stream_lifecycle WHERE stream_id = '{stream}'");

  private static Task<long> _countedBytesAsync(NpgsqlConnection conn, Guid stream) =>
    _countAsync(conn, $"""
      SELECT COALESCE(SUM(pg_column_size(b.event_data) + pg_column_size(b.metadata)), 0)
      FROM wh_event_store es JOIN wh_event_body b ON b.event_id = es.event_id
      WHERE es.stream_id = '{stream}' AND COALESCE(es.flags, 0) & 24 = 0
      """);

  private async Task _appendAsync(Guid stream) {
    var envelope = new MessageEnvelope<LifecycleEphemeralProbeTicked> {
      MessageId = MessageId.New(),
      Payload = new LifecycleEphemeralProbeTicked { SessionId = stream },
      Hops = [new MessageHop { Type = HopType.Current, Timestamp = DateTimeOffset.UnixEpoch, ServiceInstance = ServiceInstanceInfo.Unknown }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
    await using (var context = CreateDbContext()) {
      await new EFCoreEventStore<WorkCoordinationDbContext>(context).AppendAsync(stream, envelope);
    }
    await using var conn = await _openAsync();
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
  }

  private ServiceProvider _services() =>
    new ServiceCollection()
      .AddSingleton(new PostgresArchiveConnection(async ct => {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
      }, "public"))
      .AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }))
      .AddSingleton<IMessageTypeCatalog>(new Catalog(new MessageTypeCatalogEntry(
        typeof(LifecycleEphemeralProbeTicked), TypeNameFormatter.FormatClrTypeName(typeof(LifecycleEphemeralProbeTicked)), "event", null) {
        Ephemeral = new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow),
      }))
      .BuildServiceProvider();

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private static async Task<string> _settingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
