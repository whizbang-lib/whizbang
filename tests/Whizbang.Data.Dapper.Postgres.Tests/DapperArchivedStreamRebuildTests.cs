// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Data;
using Whizbang.Core.Messaging;
using Whizbang.Core.Policies;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Custom;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The database side of a rebuild's archived-data choice (#1931) on the Dapper driver, as on the EF Core driver: the
/// driver registers what the lifecycle ledger says of each stream's archive, and its rebuild cursor completer parks the
/// cursor of a stream archived whole, which the stream's restore brings back.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
public class DapperArchivedStreamRebuildTests : PostgresTestBase {
  private const string PERSPECTIVE = "Tests.ParkedCursorPerspective";

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheArchivedStreamExtents_ThatNameTheStreamsArchivedWholeAndThroughAPrefixAsync() {
    var hot = await _streamAsync(2);
    var whole = await _streamAsync(2);
    var prefix = await _streamAsync(2);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.Prefix(prefix, 1))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    await using var provider = services.BuildServiceProvider();

    var extents = await provider.GetRequiredService<IArchivedStreamExtents>().GetExtentsAsync([hot, whole, prefix]);

    await Assert.That(extents).IsEquivalentTo(new Dictionary<Guid, ArchivedStreamExtent> {
      [whole] = ArchivedStreamExtent.Whole,
      [prefix] = ArchivedStreamExtent.Prefix,
    });
  }

  [Test]
  public async Task Completer_TheCursorOfAStreamArchivedWhole_IsParked_AndTheRestoreBringsItBackAsync() {
    var whole = await _streamAsync(2);
    var last = await _lastEventAsync(whole);
    await Assert.That((await _archive().WriteAsync(ArchiveBatch.WholeStream(whole))).Status).IsEqualTo(ArchiveStatus.ARCHIVED);

    await new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString).CompleteAsync([_completion(whole, last)]);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @whole", new { whole }))
      .IsEqualTo(0L);
    await Assert.That(await conn.ExecuteScalarAsync<string>(
        "SELECT cursor_row ->> 'last_event_id' FROM wh_event_archive_cursors WHERE stream_id = @whole AND perspective_name = @p",
        new { whole, p = PERSPECTIVE })).IsEqualTo(last.ToString());

    await Assert.That((await _archive().RestoreAsync(whole)).Status).IsEqualTo(ArchiveStatus.RESTORED);

    await Assert.That(await conn.ExecuteScalarAsync<Guid>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @whole AND perspective_name = @p",
        new { whole, p = PERSPECTIVE })).IsEqualTo(last);
  }

  [Test]
  public async Task Completer_TheCursorOfAHotStream_IsWrittenLiveAsync() {
    var hot = await _streamAsync(2);
    var last = await _lastEventAsync(hot);

    await new DapperPostgresPerspectiveCheckpointCompleter(ConnectionString).CompleteAsync([_completion(hot, last)]);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Assert.That(await conn.ExecuteScalarAsync<Guid>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @hot AND perspective_name = @p",
        new { hot, p = PERSPECTIVE })).IsEqualTo(last);
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_archive_cursors WHERE stream_id = @hot", new { hot }))
      .IsEqualTo(0L);
  }

  // ===== Fixtures =====

  private static PerspectiveCursorCompletion _completion(Guid stream, Guid lastEvent) => new() {
    StreamId = stream,
    PerspectiveName = PERSPECTIVE,
    LastEventId = lastEvent,
    Status = PerspectiveProcessingStatus.Completed,
  };

  private PostgresTableArchiveStore _archive() => new(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

  private async Task<Guid> _streamAsync(int events) {
    var stream = (Guid)TrackedGuid.New();
    var store = _eventStore();
    for (var i = 0; i < events; i++) {
      await store.AppendAsync(stream, new DapperMixedStreamProbeNoted { StreamId = stream });
    }
    return stream;
  }

  private async Task<Guid> _lastEventAsync(Guid stream) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    return await conn.ExecuteScalarAsync<Guid>(
      "SELECT event_id FROM wh_event_store WHERE stream_id = @stream ORDER BY version DESC LIMIT 1", new { stream });
  }

  private DapperPostgresEventStore _eventStore() {
    var jsonOptions = Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions();
    return new DapperPostgresEventStore(
      ConnectionFactory,
      Executor,
      jsonOptions,
      new EventEnvelopeJsonbAdapter(jsonOptions),
      new JsonbSizeValidator(NullLogger<JsonbSizeValidator>.Instance),
      new PolicyEngine(),
      null,
      NullLogger<DapperPostgresEventStore>.Instance);
  }
}
