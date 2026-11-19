// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Archive locations (#2137, migration 353) against a database the EF Core driver migrated: legacy locators are split
/// into a location per container and a path from the stream, an older instance's locator-only write is given the same,
/// the configured lookup takes or merges a container's legacy location and never creates one on a read, a location holds
/// logical information only, and a repoint updates the one row or merges it into the configured location.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/353_ArchiveLocations.sql</code-under-test>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
[Category("Integration")]
[Category("Shard5")]
public class ArchiveLocationSqlTests : EFCoreTestBase {
  private const string LEDGER = "Whizbang.Tests.LedgerEntry";
  private const string CHECK_VIOLATION = "23514";
  private const string SHA = "0000000000000000000000000000000000000000000000000000000000000000";

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  // A stream archived whole by the table store: its lifecycle row is the parent every manifest row needs.
  private async Task<Guid> _archivedStreamAsync(NpgsqlConnection connection) {
    var stream = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{LEDGER}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
        }]
      }
      """;
    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
      call.Parameters.AddWithValue("req", request);
      _ = await call.ExecuteScalarAsync();
    }
    _ = await new PostgresTableArchiveStore(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync)
      .WriteAsync(ArchiveBatch.WholeStream(stream));
    return stream;
  }

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, params object?[] values) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    foreach (var value in values) {
      c.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
    }
    _ = await c.ExecuteNonQueryAsync();
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection connection, string sql, params object?[] values) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    foreach (var value in values) {
      c.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
    }
    var result = await c.ExecuteScalarAsync();
    return result is DBNull ? null : result;
  }

  private static async Task<List<string>> _rowsAsync(NpgsqlConnection connection, string sql) {
    await using var c = connection.CreateCommand();
    c.CommandText = sql;
    var rows = new List<string>();
    await using var reader = await c.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
    }
    return rows;
  }

  // A manifest row as an instance of a release before 353 wrote it: a full locator, no location.
  private static Task _legacyRowAsync(NpgsqlConnection connection, Guid stream, long firstVersion, string locator) =>
    _executeAsync(connection,
      "INSERT INTO wh_archive_manifest (stream_id, first_version, last_version, event_count, locator, byte_length, sha256, origin_generation) " +
      $"VALUES ($1, {firstVersion}, {firstVersion}, 1, $2, 10, '{SHA}', 1)", stream, locator);

  // A manifest row as this release writes it: a location and a path from the stream, no locator.
  private static Task _rowAsync(NpgsqlConnection connection, Guid stream, long firstVersion, long locationId, string relativePath) =>
    _executeAsync(connection,
      "INSERT INTO wh_archive_manifest (stream_id, first_version, last_version, event_count, location_id, relative_path, byte_length, sha256, origin_generation) " +
      $"VALUES ($1, {firstVersion}, {firstVersion}, 1, $2, $3, 10, '{SHA}', 1)", stream, locationId, relativePath);

  private static async Task<long?> _locationIdAsync(NpgsqlConnection connection, string? endpoint, string? name, string container, bool create) =>
    (long?)await _scalarAsync(connection, "SELECT wh_archive_location_id('azure-blob', $1, $2, $3, $4)", endpoint, name, container, create);

  private static Task<List<string>> _locationsAsync(NpgsqlConnection connection) => _rowsAsync(connection,
    "SELECT concat_ws('|', COALESCE(account_endpoint, '-'), COALESCE(connection_name, '-'), container) FROM wh_archive_locations ORDER BY location_id");

  private static string _brotli(string relativePath) => $"azure-blob://{relativePath}?codec=brotli&format=1";

  [Test]
  public async Task Backfill_SplitsEveryLegacyLocator_IntoALocationPerContainerAndARelativePath_AndRunsOnceAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);
    await _executeAsync(connection, "ALTER TABLE wh_archive_manifest DISABLE TRIGGER wh_archive_manifest_location_from_locator_trg");
    await _legacyRowAsync(connection, stream, 1, _brotli($"archive-a/{stream:D}/0000000001-0000000001-aa.jsonl.br"));
    await _legacyRowAsync(connection, stream, 2, _brotli($"archive-a/{stream:D}/0000000002-0000000002-bb.jsonl.br"));
    await _legacyRowAsync(connection, stream, 3, _brotli($"archive-b/{stream:D}/0000000003-0000000003-cc.jsonl.br"));
    await _legacyRowAsync(connection, stream, 4, "postgres-table://wh_event_archive?codec=none&format=1");
    await _executeAsync(connection, "ALTER TABLE wh_archive_manifest ENABLE TRIGGER wh_archive_manifest_location_from_locator_trg");
    await _executeAsync(connection, "INSERT INTO wh_archive_object_deletions (locator, stream_id) VALUES ($1, $2)",
      _brotli($"archive-b/{stream:D}/0000000009-0000000009-dd.jsonl.br"), stream);

    var changed = (long)(await _scalarAsync(connection, "SELECT wh_backfill_archive_locations()"))!;
    var again = (long)(await _scalarAsync(connection, "SELECT wh_backfill_archive_locations()"))!;

    await Assert.That(changed).IsEqualTo(4L).Because("three manifest rows and one queued deletion had an Azure Blob locator");
    await Assert.That(again).IsEqualTo(0L).Because("a re-run finds nothing left to backfill");
    await Assert.That(await _locationsAsync(connection)).IsEquivalentTo(["-|-|archive-a", "-|-|archive-b"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("one location per distinct container; the database never knew the endpoint or the connection name");
    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', m.first_version, COALESCE(l.container, '-'), COALESCE(m.relative_path, '-'), m.locator) " +
        "FROM wh_archive_manifest m LEFT JOIN wh_archive_locations l ON l.location_id = m.location_id ORDER BY m.first_version"))
      .IsEquivalentTo([
        $"1|archive-a|{stream:D}/0000000001-0000000001-aa.jsonl.br|{_brotli($"archive-a/{stream:D}/0000000001-0000000001-aa.jsonl.br")}",
        $"2|archive-a|{stream:D}/0000000002-0000000002-bb.jsonl.br|{_brotli($"archive-a/{stream:D}/0000000002-0000000002-bb.jsonl.br")}",
        $"3|archive-b|{stream:D}/0000000003-0000000003-cc.jsonl.br|{_brotli($"archive-b/{stream:D}/0000000003-0000000003-cc.jsonl.br")}",
        "4|-|-|postgres-table://wh_event_archive?codec=none&format=1",
      ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the container goes to the location and the rest, from the stream, to the record; the legacy text stays readable");
    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', l.container, q.relative_path) FROM wh_archive_object_deletions q JOIN wh_archive_locations l USING (location_id)"))
      .IsEquivalentTo([$"archive-b|{stream:D}/0000000009-0000000009-dd.jsonl.br"]);
  }

  [Test]
  public async Task OlderInstanceWrite_WithOnlyALocator_IsGivenALocationAndPathAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);

    await _legacyRowAsync(connection, stream, 1, _brotli($"archive-a/{stream:D}/one.jsonl.br"));
    await _legacyRowAsync(connection, stream, 2, _brotli($"archive-a/{stream:D}/two.jsonl.br"));
    await _legacyRowAsync(connection, stream, 3, "postgres-table://wh_event_archive?codec=none&format=1");

    await Assert.That(await _locationsAsync(connection)).IsEquivalentTo(["-|-|archive-a"]);
    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', m.first_version, COALESCE(l.container, '-'), COALESCE(m.relative_path, '-')) " +
        "FROM wh_archive_manifest m LEFT JOIN wh_archive_locations l ON l.location_id = m.location_id ORDER BY m.first_version"))
      .IsEquivalentTo([$"1|archive-a|{stream:D}/one.jsonl.br", $"2|archive-a|{stream:D}/two.jsonl.br", "3|-|-"],
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a rolling deploy's older instance still writes a locator; only an Azure Blob one names an object");
  }

  [Test]
  public async Task LocationId_AdoptsItsContainersLegacyLocation_ThenFindsIt_AndALookupNeverCreatesOneAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);

    var nothingYet = await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: false);
    var locationsAfterALookup = await _locationsAsync(connection);
    await _legacyRowAsync(connection, stream, 1, _brotli($"archive-a/{stream:D}/one.jsonl.br"));
    await _legacyRowAsync(connection, stream, 2, _brotli($"archive-b/{stream:D}/two.jsonl.br"));
    var legacy = (long)(await _scalarAsync(connection, "SELECT location_id FROM wh_archive_manifest WHERE first_version = 1"))!;
    var adopted = await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: false);
    var found = await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: false);
    var elsewhere = await _locationIdAsync(connection, "https://b.blob.core.windows.net", "archive", "archive-a", create: false);
    var created = await _locationIdAsync(connection, "https://b.blob.core.windows.net", "archive", "archive-a", create: true);

    await Assert.That(nothingYet).IsNull();
    await Assert.That(locationsAfterALookup).IsEmpty().Because("a read, restore or delete never creates a location");
    await Assert.That(adopted).IsEqualTo(legacy).Because("the store configured for the container takes its legacy location");
    await Assert.That(found).IsEqualTo(legacy);
    await Assert.That(elsewhere).IsNull().Because("once taken, the location is at one endpoint; another endpoint is another location");
    await Assert.That(created.HasValue).IsTrue();
    await Assert.That(created!.Value).IsNotEqualTo(legacy);
    await Assert.That(await _locationsAsync(connection)).IsEquivalentTo(
      ["https://a.blob.core.windows.net|archive|archive-a", "-|-|archive-b", "https://b.blob.core.windows.net|archive|archive-a"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("another container's legacy location is never taken by a store configured for a different container");
  }

  [Test]
  public async Task LocationId_ALegacyLocationBesideTheConfiguredOne_IsMergedIntoItAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);
    var configured = (await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: true))!.Value;
    await _rowAsync(connection, stream, 1, configured, $"{stream:D}/one.jsonl.br");
    // An older instance wrote since, during a rolling deploy: its row lands in the container's legacy location.
    await _legacyRowAsync(connection, stream, 2, _brotli($"archive-a/{stream:D}/two.jsonl.br"));
    var legacy = (long)(await _scalarAsync(connection, "SELECT location_id FROM wh_archive_manifest WHERE first_version = 2"))!;
    await _executeAsync(connection, "INSERT INTO wh_archive_object_deletions (stream_id, location_id, relative_path) VALUES ($1, $2, 'dup'), ($1, $3, 'dup'), ($1, $3, 'own')",
      stream, configured, legacy);

    var found = await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: false);

    await Assert.That(found).IsEqualTo(configured);
    await Assert.That(await _rowsAsync(connection, "SELECT DISTINCT location_id::text FROM wh_archive_manifest"))
      .IsEquivalentTo([configured.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    await Assert.That(await _locationsAsync(connection)).IsEquivalentTo(["https://a.blob.core.windows.net|archive|archive-a"]);
    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', location_id, relative_path) FROM wh_archive_object_deletions ORDER BY relative_path"))
      .IsEquivalentTo([$"{configured}|dup", $"{configured}|own"], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an object queued in both locations is one object once they are one location");
  }

  [Test]
  [Arguments("https://a.blob.core.windows.net/?sv=1&sig=c2ln", "archive", "archive-a")]
  [Arguments("https://user:pass@a.blob.core.windows.net", "archive", "archive-a")]
  [Arguments("https://a.blob.core.windows.net", "AccountName=a;AccountKey=k", "archive-a")]
  [Arguments("https://a.blob.core.windows.net", "", "archive-a")]
  [Arguments("https://a.blob.core.windows.net", "archive", "archive-a/stream")]
  [Arguments("https://a.blob.core.windows.net", "archive", "")]
  public async Task Locations_RefuseAnythingThatIsNotLogicalInformationAsync(string endpoint, string name, string container) {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);

    var refused = await Assert.That(() => _executeAsync(connection,
        "INSERT INTO wh_archive_locations (provider, account_endpoint, connection_name, container) VALUES ('azure-blob', $1, $2, $3)",
        endpoint, name, container))
      .Throws<PostgresException>();

    await Assert.That(refused!.SqlState).IsEqualTo(CHECK_VIOLATION)
      .Because("a signature, user information, a connection string or a path never reaches the locations table");
  }

  [Test]
  public async Task Repoint_UpdatesTheOneRow_OrMergesItIntoTheConfiguredLocationAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);
    var source = (await _locationIdAsync(connection, "https://source.blob.core.windows.net", "archive", "archive-s", create: true))!.Value;
    var other = (await _locationIdAsync(connection, "https://other.blob.core.windows.net", "archive", "archive-o", create: true))!.Value;
    var target = (await _locationIdAsync(connection, "https://target.blob.core.windows.net", "archive", "archive-t", create: true))!.Value;
    await _rowAsync(connection, stream, 1, source, $"{stream:D}/one.jsonl.br");
    await _rowAsync(connection, stream, 2, other, $"{stream:D}/two.jsonl.br");
    await _executeAsync(connection, "INSERT INTO wh_archive_object_deletions (stream_id, location_id, relative_path) VALUES ($1, $2, 'dup'), ($1, $3, 'dup')",
      stream, other, target);
    const string REPOINT = "SELECT wh_repoint_archive_location($1, 'azure-blob', $2, $3, $4)";

    var updated = (long?)await _scalarAsync(connection, REPOINT, source, "https://moved.blob.core.windows.net", "archive", "archive-m");
    var merged = (long?)await _scalarAsync(connection, REPOINT, other, "https://target.blob.core.windows.net", "archive", "archive-t");
    var itself = (long?)await _scalarAsync(connection, REPOINT, target, "https://target.blob.core.windows.net", "archive", "archive-t");
    var missing = (long?)await _scalarAsync(connection, REPOINT, 999_999L, "https://nowhere.blob.core.windows.net", "archive", "archive-n");
    var missingIntoExisting = (long?)await _scalarAsync(connection, REPOINT, 999_999L, "https://target.blob.core.windows.net", "archive", "archive-t");

    await Assert.That(updated).IsEqualTo(source).Because("no location is configured there yet, so the one row changes");
    await Assert.That(merged).IsEqualTo(target).Because("the configured location exists, so the records join it");
    await Assert.That(itself).IsEqualTo(target);
    await Assert.That(missing).IsNull();
    await Assert.That(missingIntoExisting).IsEqualTo(target);
    await Assert.That(await _locationsAsync(connection)).IsEquivalentTo(
      ["https://moved.blob.core.windows.net|archive|archive-m", "https://target.blob.core.windows.net|archive|archive-t"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', first_version, location_id, relative_path) FROM wh_archive_manifest ORDER BY first_version"))
      .IsEquivalentTo([$"1|{source}|{stream:D}/one.jsonl.br", $"2|{target}|{stream:D}/two.jsonl.br"],
        TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("no record's path is rewritten: only which location it names, and only on a merge");
    await Assert.That(await _rowsAsync(connection, "SELECT concat_ws('|', location_id, relative_path) FROM wh_archive_object_deletions"))
      .IsEquivalentTo([$"{target}|dup"]);
  }

  [Test]
  public async Task Purge_QueuesEachObjectsLocationAndRelativePathAsync() {
    await using var ctx = CreateDbContext();
    var connection = await _openAsync(ctx);
    var stream = await _archivedStreamAsync(connection);
    var location = (await _locationIdAsync(connection, "https://a.blob.core.windows.net", "archive", "archive-a", create: true))!.Value;
    await _rowAsync(connection, stream, 1, location, $"{stream:D}/one.jsonl.br");

    _ = await new PostgresStreamPurger(NpgsqlDataSource.Create(ConnectionString).OpenConnectionAsync).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [stream], RequestedBy = "operator-1", Reason = "test" });

    await Assert.That(await _rowsAsync(connection,
        "SELECT concat_ws('|', location_id, relative_path, COALESCE(locator, '<none>')) FROM wh_archive_object_deletions"))
      .IsEquivalentTo([$"{location}|{stream:D}/one.jsonl.br|<none>"])
      .Because("the queue keeps what the store checks before it deletes: the location and the path from the stream");
  }
}
