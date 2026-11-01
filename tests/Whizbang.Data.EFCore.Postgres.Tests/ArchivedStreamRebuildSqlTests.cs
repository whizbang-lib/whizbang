// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The database side of a rebuild's archived-data choice (#1931) on the EF Core driver: what the lifecycle ledger says of
/// each stream's archive (<see cref="IArchivedStreamExtents"/>, registered by the driver), and the rebuild's cursor of a
/// stream archived whole, which is parked with the stream rather than pointed at an event the hot store does not hold,
/// and comes back when the stream is restored.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
[Category("Integration")]
[Category("Shard3")]
public class ArchivedStreamRebuildSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Tests.ParkedCursorPerspective";
  private const string OTHER_PERSPECTIVE = "Tests.OtherParkedCursorPerspective";

  [Test]
  public async Task Extents_NameTheStreamsArchivedWhole_AndThroughAPrefix_AndNoOtherAsync() {
    var hot = await _streamAsync(3);
    var whole = await _streamAsync(3);
    var prefix = await _streamAsync(3);
    var restored = await _streamAsync(3);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(prefix, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(restored, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().RestoreAsync(restored)).Status).IsEqualTo(ArchiveStatus.RESTORED);
    // Many more ids than one lookup takes, so the lookup is made in several.
    var unknown = Enumerable.Range(0, PostgresArchivedStreamExtents.LOOKUP_BATCH_SIZE * 2).Select(_ => Guid.NewGuid()).ToList();

    var extents = await _extents().GetExtentsAsync([.. unknown, hot, whole, prefix, restored]);

    await Assert.That(extents).IsEquivalentTo(new Dictionary<Guid, ArchivedStreamExtent> {
      [whole] = ArchivedStreamExtent.Whole,
      [prefix] = ArchivedStreamExtent.Prefix,
    }).Because("a hot stream, a restored one and a stream the ledger does not know have no archived data");
  }

  // #1958: a deleted archive cleared the ledger's locator, so a stream whose prefix archive was deleted read as one that
  // never had an archive, and a rebuild replayed only its hot events.
  [Test]
  public async Task Extents_TellAStreamWhoseArchiveWasDeleted_FromOneThatNeverHadAnArchiveAsync() {
    var hot = await _streamAsync(3);
    var prefixDeleted = await _streamAsync(3);
    var wholeDeleted = await _streamAsync(3);
    var rearchived = await _streamAsync(4);
    var restored = await _streamAsync(3);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(prefixDeleted, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _archive().DeleteAsync(prefixDeleted)).IsGreaterThan(0L);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(wholeDeleted))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _archive().DeleteAsync(wholeDeleted)).IsEqualTo(3L).Because("the control: the archive held the whole stream");
    // Archived again after its first archive was deleted: the new archive starts where the deleted one ended.
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(rearchived, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _archive().DeleteAsync(rearchived)).IsGreaterThan(0L);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(rearchived, 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    // Restored, then deleted: the restore brought every event back, so the delete deletes nothing.
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(restored, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().RestoreAsync(restored)).Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(await _archive().DeleteAsync(restored)).IsEqualTo(0L);

    var extents = await _extents().GetExtentsAsync([hot, prefixDeleted, wholeDeleted, rearchived, restored]);

    await Assert.That(extents).IsEquivalentTo(new Dictionary<Guid, ArchivedStreamExtent> {
      [prefixDeleted] = ArchivedStreamExtent.PrefixDeleted,
      [wholeDeleted] = ArchivedStreamExtent.WholeDeleted,
      [rearchived] = ArchivedStreamExtent.PrefixDeleted,
    }).Because("a deleted archive's history is gone for good, whatever is archived after it; a hot stream and a restored one lost nothing");
  }

  // A delete before migration 274 cleared the locator and stamped nothing. A whole-stream archive leaves its row Archived,
  // so that row still says its archive is gone: Archived, with no locator to read it from.
  [Test]
  public async Task Extents_AnArchivedRowWithNoLocator_ReadsWholeDeleted_EvenWithoutTheDeletionStampAsync() {
    var whole = await _streamAsync(3);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = new NpgsqlCommand(
        "UPDATE wh_stream_lifecycle SET archive_locator = NULL, archive_deleted_at = NULL WHERE stream_id = @s", conn);
      cmd.Parameters.AddWithValue("s", whole);
      await Assert.That(await cmd.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    var extents = await _extents().GetExtentsAsync([whole]);

    await Assert.That(extents).IsEquivalentTo(new Dictionary<Guid, ArchivedStreamExtent> { [whole] = ArchivedStreamExtent.WholeDeleted })
      .Because("an Archived row names where its archive is; one that names nowhere has lost it");
  }

  // The deletion stamp is never cleared: a restore brings back only what the later archive held, and the history the
  // deleted archive held stays gone.
  [Test]
  public async Task Extents_AStreamRestoredFromALaterArchive_AfterItsEarlierArchiveWasDeleted_StillReadsPrefixDeletedAsync() {
    var stream = await _streamAsync(4);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _archive().DeleteAsync(stream)).IsGreaterThan(0L);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(stream, 2))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    await Assert.That((await _archive().RestoreAsync(stream)).Status).IsEqualTo(ArchiveStatus.RESTORED);

    var extents = await _extents().GetExtentsAsync([stream]);
    await Assert.That(extents).IsEquivalentTo(new Dictionary<Guid, ArchivedStreamExtent> { [stream] = ArchivedStreamExtent.PrefixDeleted })
      .Because("the restore puts back the later archive's events, never the ones the deleted archive held");
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", stream))
      .IsLessThan(4L).Because("the control: the stream's first event is gone for good");
  }

  [Test]
  public async Task Extents_OfNoStreams_AskNothingAsync() {
    await Assert.That(await _extents().GetExtentsAsync([])).IsEmpty();
  }

  [Test]
  public async Task Extents_RefuseANullArchiveConnectionAsync() {
    await Assert.That(() => new PostgresArchivedStreamExtents(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task TheDriver_RegistersTheArchivedStreamExtents_OnItsArchiveConnectionAsync() {
    var whole = await _streamAsync(2);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<ArchiveRegistrationDbContext>(options => options.UseNpgsql(ConnectionString));
    services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
    var selector = new Whizbang.Core.Perspectives.WhizbangPerspectiveBuilder(services).WithEFCore<ArchiveRegistrationDbContext>();
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var extents = await provider.GetRequiredService<IArchivedStreamExtents>().GetExtentsAsync([whole]);

    await Assert.That(extents[whole]).IsEqualTo(ArchivedStreamExtent.Whole);
  }

  // #1963: the driver registers the fence a snapshot of a row with collectives reads, on the same archive connection.
  [Test]
  public async Task TheDriver_RegistersTheCollectiveSnapshotFence_OnItsArchiveConnectionAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<ArchiveRegistrationDbContext>(options => options.UseNpgsql(ConnectionString));
    services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
    var selector = new Whizbang.Core.Perspectives.WhizbangPerspectiveBuilder(services).WithEFCore<ArchiveRegistrationDbContext>();
    _ = selector.WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var reading = await provider.GetRequiredService<Whizbang.Core.Perspectives.ICollectiveSnapshotFence>()
      .ReadAsync("tenant-a", ["Tests.Collective, Tests"], afterSequence: null);

    await Assert.That(reading.CollectiveInFlight).IsFalse().Because("the control: it reads this database, which holds no collective");
  }

  [Test]
  public async Task Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync() {
    var whole = await _streamAsync(3);
    var events = await _eventIdsAsync(whole);
    // One perspective had a cursor when the stream was archived, so the archive parked it; the other had none.
    await _executeAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@s, @p, @e, 2, NOW())",
      whole, PERSPECTIVE, events[0]);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await using var ctx = CreateDbContext();

    await new EFCorePostgresPerspectiveCheckpointCompleter(ctx).CompleteAsync([
      _completion(whole, PERSPECTIVE, events[^1]),
      _completion(whole, OTHER_PERSPECTIVE, events[^1]),
    ]);

    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @s", whole))
      .IsEqualTo(0L).Because("a live cursor naming an archived event is what the EF Core schema's foreign key refuses");
    foreach (var perspective in new[] { PERSPECTIVE, OTHER_PERSPECTIVE }) {
      await Assert.That(await _scalarAsync<string>(
          "SELECT cursor_row ->> 'last_event_id' FROM wh_event_archive_cursors WHERE stream_id = @s AND perspective_name = @p",
          whole, perspective)).IsEqualTo(events[^1].ToString())
        .Because("the rebuilt cursor is parked with the stream, an existing parked cursor moved and a new one added");
    }

    await Assert.That((await _archive().RestoreAsync(whole)).Status).IsEqualTo(ArchiveStatus.RESTORED);

    foreach (var perspective in new[] { PERSPECTIVE, OTHER_PERSPECTIVE }) {
      await Assert.That(await _scalarAsync<Guid>(
          "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", whole, perspective))
        .IsEqualTo(events[^1]).Because("the restore brings the parked cursor back with the events it names");
    }
  }

  [Test]
  public async Task Completer_TheCursorOfAHotStream_IsWrittenLive_AndNothingIsParkedAsync() {
    var hot = await _streamAsync(2);
    var events = await _eventIdsAsync(hot);
    await using var ctx = CreateDbContext();

    await new EFCorePostgresPerspectiveCheckpointCompleter(ctx).CompleteAsync([_completion(hot, PERSPECTIVE, events[^1])]);

    await Assert.That(await _scalarAsync<Guid>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @s AND perspective_name = @p", hot, PERSPECTIVE))
      .IsEqualTo(events[^1]);
    await Assert.That(await _scalarAsync<long>("SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @s", hot))
      .IsEqualTo(0L);
  }

  // ===== Fixtures =====

  // A context with no model of its own: the driver's registration is what is under test.
  private sealed class ArchiveRegistrationDbContext(DbContextOptions<ArchiveRegistrationDbContext> options) : DbContext(options);

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private PostgresArchivedStreamExtents _extents() =>
    new(new PostgresArchiveConnection(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync, Schema: null));

  private static PerspectiveCursorCompletion _completion(Guid stream, string perspective, Guid lastEvent) => new() {
    StreamId = stream,
    PerspectiveName = perspective,
    LastEventId = lastEvent,
    Status = PerspectiveProcessingStatus.Completed,
  };

  private async Task<Guid> _streamAsync(int events) {
    var stream = Guid.CreateVersion7();
    await using var ctx = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(ctx);
    for (var i = 0; i < events; i++) {
      await store.AppendAsync(stream, new MessageEnvelope<RebuildCreditedEvent> {
        MessageId = MessageId.New(),
        Payload = new RebuildCreditedEvent { StreamId = stream, Amount = i + 1 },
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      });
    }
    return stream;
  }

  private async Task<List<Guid>> _eventIdsAsync(Guid stream) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT event_id FROM wh_event_store WHERE stream_id = @s ORDER BY version", conn);
    cmd.Parameters.AddWithValue("s", stream);
    var ids = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }

  private async Task _executeAsync(string sql, Guid stream, string perspective, Guid eventId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("s", stream);
    cmd.Parameters.AddWithValue("p", perspective);
    cmd.Parameters.AddWithValue("e", eventId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task<T> _scalarAsync<T>(string sql, Guid stream, string? perspective = null) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("s", stream);
    if (perspective is not null) {
      cmd.Parameters.AddWithValue("p", perspective);
    }
    return (T)(await cmd.ExecuteScalarAsync())!;
  }
}
