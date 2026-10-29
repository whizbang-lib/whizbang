// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Postgres-table archive store (#1736, WS-3 S7) on a database the Dapper runner migrated, which has no
/// <c>fk_perspective_cursors_event</c>: the same SQL archives and restores the stream, cursor included, and the driver
/// registers the store so the prefix truncate's archive path writes through it. The EF Core twin, with the refusals, the
/// tombstone and the failure injection, is <c>ArchiveStoreSqlTests</c>.
/// </summary>
/// <docs>fundamentals/events/archive-store</docs>
public class DapperArchiveStoreTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noEntries = [];

  private static string _commit(Guid eventId, Guid streamId) => $$"""
    {
      "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "Whizbang.Tests.LedgerEntry", "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {"b": 2, "a": 1}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 0
      }]
    }
    """;

  private async Task<(NpgsqlConnection Connection, Guid Stream, Guid[] Ids)> _seedAsync(int count) {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var stream = Guid.CreateVersion7();
    var ids = new Guid[count];
    for (var i = 0; i < count; i++) {
      ids[i] = Guid.CreateVersion7();
    }
    Array.Sort(ids);
    foreach (var id in ids) {
      await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)", new { Req = _commit(id, stream) });
    }
    return (connection, stream, ids);
  }

  private static Task<IEnumerable<string>> _hotAsync(NpgsqlConnection connection, Guid stream) =>
    connection.QueryAsync<string>(
      "SELECT concat_ws('|', es.event_id, es.version, eb.event_data::text, eb.metadata::text) FROM wh_event_store es " +
      "JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @stream ORDER BY es.version", new { stream });

  [Test]
  public async Task WholeStream_OnADapperMigratedDatabase_ArchivesAndRestoresAsync() {
    var (connection, stream, ids) = await _seedAsync(3);
    await using var owned = connection;
    await connection.ExecuteAsync(
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) VALUES (@stream, 'P', @id, 0, NOW())",
      new { stream, id = ids[2] });
    var hot = (await _hotAsync(connection, stream)).ToList();
    var store = new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync);

    var written = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var read = new List<string>();
    await foreach (var e in store.ReadAsync(stream)) {
      read.Add($"{e.EventId}|{e.Version}|{e.EventDataJson}|{e.MetadataJson}");
    }

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(read).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the archive reads back byte-identical, in version order, on this engine too");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_perspective_cursors WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(0L).Because("with no foreign key to stop it, the archive must still not leave a cursor on a removed event");

    var restored = await store.RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That((await _hotAsync(connection, stream)).ToList()).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await connection.ExecuteScalarAsync<Guid>(
        "SELECT last_event_id FROM wh_perspective_cursors WHERE stream_id = @stream AND perspective_name = 'P'", new { stream }))
      .IsEqualTo(ids[2]).Because("the parked cursor returns where it was");
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheTableArchiveStore_AndTheTruncateWritesThroughItAsync() {
    var (connection, stream, _) = await _seedAsync(3);
    await using var owned = connection;
    var entries = new ServiceCollection().AddLogging();
    entries.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, _noEntries, configureOptions: null);
    var schemaSql = new ServiceCollection().AddLogging();
    schemaSql.AddWhizbangPostgres(ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);
    foreach (var services in new[] { entries, schemaSql }) {
      await using var provider = services.BuildServiceProvider();
      var store = provider.GetRequiredService<IArchiveStore>();
      await Assert.That(store).IsTypeOf<PostgresTableArchiveStore>();
      await Assert.That(((PostgresTableArchiveStore)store).Locator.ToString())
        .IsEqualTo("postgres-table://wh_event_archive?codec=none&format=1")
        .Because("the Dapper driver resolves its tables through the connection's search path");
    }

    entries.AddWhizbangWorkers();
    await using var pipeline = entries.BuildServiceProvider();
    var result = await pipeline.GetRequiredService<IStreamPrefixTruncator>().TruncateAsync(stream, 2, archive: true);

    await Assert.That(result.Status).IsEqualTo("truncated")
      .Because("the archive path reaches the store through the driver's registration, so it works on this engine too");
    await Assert.That(await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_archive WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(2L);
  }
}
