// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Archive.AzureBlob.Integration.Tests;

/// <summary>
/// The Azure Blob archive store against Azurite and a database the Dapper runner migrated: a byte-identical round trip
/// through Brotli objects, writes that are safe to retry (a failed upload or a crash before the commit leaves the stream
/// hot and never lists an object twice), corruption detected by hash, restore, delete, a purge, reads of what the table
/// store archived, and the production caller (the driver's prefix truncate) writing through the registration.
/// </summary>
/// <docs>fundamentals/events/archive-store#the-azure-blob-store</docs>
[NotInParallel("PostgreSQL")]
public class AzureBlobArchiveStoreTests {
  private const string LEDGER = "Whizbang.Tests.LedgerEntry";

  private PerTestDatabase _db;
  private string _azurite = null!;
  private string _containerName = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _db = await PerTestDatabaseFactory.CreateAsync("blob_archive");
    await new PostgresSchemaInitializer(_db.ConnectionString).InitializeSchemaAsync();
    _azurite = await AzuriteFixture.EnsureStartedAsync();
    _containerName = $"archive-{Guid.NewGuid():N}";
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_db.Name);

  // ── Arrange ────────────────────────────────────────────────────────────────────────────────────────

  private PostgresArchiveConnection _database() => new(async ct => {
    var connection = new NpgsqlConnection(_db.ConnectionString);
    await connection.OpenAsync(ct);
    return connection;
  }, Schema: null);

  private BlobContainerClient _container(string? name = null, HttpPipelinePolicy? fault = null) {
    var options = new BlobClientOptions();
    if (fault is not null) {
      options.AddPolicy(fault, HttpPipelinePosition.PerCall);
    }
    return new BlobContainerClient(_azurite, name ?? _containerName, options);
  }

  private AzureBlobArchiveStore _store(
      int maxEventsPerChunk = 2, HttpPipelinePolicy? fault = null, string? container = null, ILogger<AzureBlobArchiveStore>? logger = null) =>
    new(_database(), _container(container, fault), new AzureBlobArchiveOptions { MaxEventsPerChunk = maxEventsPerChunk }, logger);

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(_db.ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  // Events through the emit chain (versions 1, 2, 3...), with commit positions stamped, and a perspective cursor on the
  // last one.
  private async Task<(Guid Stream, Guid[] Ids)> _seedAsync(int count) {
    await using var connection = await _openAsync();
    var stream = Guid.CreateVersion7();
    var ids = Enumerable.Range(0, count).Select(_ => Guid.CreateVersion7()).Order().ToArray();
    foreach (var id in ids) {
      var request = $$"""
        {
          "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
          "new_outbox_messages": [{
            "MessageId": "{{id}}", "Destination": "out", "MessageType": "{{LEDGER}}", "EnvelopeType": null,
            "Envelope": {"Payload": {"OrderId": 42, "Note": "zßé \"q\" a\nb"}, "MessageId": "{{id}}", "Hops": []},
            "Metadata": {"z": 1, "a": "two"}, "Scope": {"t": "tenant-1"}, "StreamId": "{{stream}}", "IsEvent": true, "Flags": 0
          }]
        }
        """;
      await _executeAsync(connection, "SELECT commit_handler_result(@p::jsonb)", request);
    }
    await _executeAsync(connection, "SELECT stamp_pending_commit_sequences(1000)");
    await _executeAsync(connection,
      "INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at) " +
      $"VALUES ('{stream}', 'P', '{ids[^1]}', 0, '2026-01-02T03:04:05Z')");
    return (stream, ids);
  }

  // Awaits a call for its exception only, so an assertion can take it as a plain task.
  private static async Task _completedAsync<T>(Task<T> call) => _ = await call;

  private static async Task _executeAsync(NpgsqlConnection connection, string sql, string? p = null) {
    await using var command = new NpgsqlCommand(sql, connection);
    if (p is not null) {
      command.Parameters.AddWithValue(nameof(p), p);
    }
    _ = await command.ExecuteNonQueryAsync();
  }

  private async Task<long> _countAsync(string sql, Guid stream) {
    await using var connection = await _openAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("s", stream);
    return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private async Task<string?> _generationAsync() {
    await using var connection = await _openAsync();
    await using var command = new NpgsqlCommand(
      "SELECT setting_value FROM wh_settings WHERE setting_key = 'integrity_origin_generation'", connection);
    return (string?)await command.ExecuteScalarAsync();
  }

  private async Task<List<string>> _rowsAsync(string sql, Guid stream) {
    await using var connection = await _openAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("s", stream);
    var rows = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
    }
    return rows;
  }

  // Every stored column of the stream's hot events, in version order: what a restore must put back exactly.
  private Task<List<string>> _hotAsync(Guid stream) => _rowsAsync(
    "SELECT concat_ws('|', es.event_id, es.version, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text, " +
    "es.aggregate_id, es.aggregate_type, es.created_at, es.flags, es.commit_sequence, es.insert_order) " +
    "FROM wh_event_store es JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @s ORDER BY es.version", stream);

  private static string _read(ArchivedEvent e) =>
    string.Join('|', e.EventId, e.Version, e.EventType, e.EventDataJson, e.MetadataJson, e.ScopeJson, e.AggregateId, e.AggregateType, e.Flags);

  // The same columns as a read returns them, from the hot store, for the byte comparison.
  private Task<List<string>> _hotAsReadAsync(Guid stream) => _rowsAsync(
    "SELECT concat_ws('|', es.event_id, es.version, es.event_type, eb.event_data::text, eb.metadata::text, es.scope::text, " +
    "es.aggregate_id, es.aggregate_type, es.flags) " +
    "FROM wh_event_store es JOIN wh_event_body eb ON eb.event_id = es.event_id WHERE es.stream_id = @s ORDER BY es.version", stream);

  private static async Task<List<string>> _readAllAsync(AzureBlobArchiveStore store, Guid stream, long fromVersion = 0) {
    var read = new List<string>();
    await foreach (var e in store.ReadAsync(stream, fromVersion)) {
      read.Add(_read(e));
    }
    return read;
  }

  private Task<List<string>> _manifestAsync(Guid stream) => _rowsAsync(
    "SELECT concat_ws('|', first_version, last_version, event_count, locator, sha256) FROM wh_archive_manifest " +
    "WHERE stream_id = @s ORDER BY first_version", stream);

  private async Task<List<string>> _objectsAsync(string? container = null) {
    var names = new List<string>();
    var client = _container(container);
    if (!(await client.ExistsAsync()).Value) {
      return names;
    }
    await foreach (var item in client.GetBlobsAsync()) {
      names.Add(item.Name);
    }
    return names;
  }

  private Task<List<string>> _tombstoneAsync(Guid stream) => _rowsAsync(
    "SELECT concat_ws('|', state, COALESCE(archive_locator, '<none>'), last_version) FROM wh_stream_lifecycle WHERE stream_id = @s", stream);

  // ── Write and read ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Write_WholeStream_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync() {
    var (stream, _) = await _seedAsync(5);
    var hot = await _hotAsReadAsync(stream);
    var store = _store(maxEventsPerChunk: 2);

    var written = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(written.EventsArchived).IsEqualTo(5L);
    await Assert.That(written.LastVersion).IsEqualTo(5L);
    await Assert.That(written.Locator).IsEqualTo(store.LocatorFor(stream));
    await Assert.That(await _readAllAsync(store, stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the archive reads back byte-identical, in version order, across every object");
    await Assert.That(await _readAllAsync(store, stream, fromVersion: 4)).IsEquivalentTo(hot[3..], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("fromVersion is inclusive, inside an object as well as at its edge");

    var manifest = await _manifestAsync(stream);
    var objects = await _objectsAsync();
    await Assert.That(manifest.Select(m => string.Join('|', m.Split('|')[..3]))).IsEquivalentTo(["1|2|2", "3|4|2", "5|5|1"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching).Because("at most MaxEventsPerChunk events per object, in version order");
    await Assert.That(manifest.All(m => m.Contains($"azure-blob://{_containerName}/{stream:D}/", StringComparison.Ordinal)
      && m.Contains("?codec=brotli&format=1", StringComparison.Ordinal))).IsTrue().Because("each object records its own codec and format");
    await Assert.That(objects).Count().IsEqualTo(3);
    foreach (var name in objects) {
      var bytes = (await _container().GetBlobClient(name).DownloadContentAsync()).Value.Content.ToArray();
      await Assert.That(manifest).Contains(m => m.EndsWith(ArchiveChunk.Sha256(bytes), StringComparison.Ordinal))
        .Because("the manifest records the SHA-256 of the stored bytes");
      await Assert.That(name).EndsWith($"-{ArchiveChunk.Sha256(bytes)}.jsonl.br").Because("the name follows the content");
    }
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", stream)).IsEqualTo(0L);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_archive WHERE stream_id = @s", stream)).IsEqualTo(0L)
      .Because("the staged rows leave with the commit; the events live in the objects only");
    await Assert.That(await _tombstoneAsync(stream)).IsEquivalentTo([$"Archived|{store.LocatorFor(stream)}|5"]);
  }

  [Test]
  public async Task Write_TwoPrefixes_EachAddsItsObjects_AndAReadSpansThemAsync() {
    var (stream, _) = await _seedAsync(5);
    var hot = await _hotAsReadAsync(stream);
    var store = _store(maxEventsPerChunk: 10);

    var first = await store.WriteAsync(ArchiveBatch.Prefix(stream, 2));
    var second = await store.WriteAsync(ArchiveBatch.Prefix(stream, 4));

    await Assert.That(first.Archived && second.Archived).IsTrue();
    await Assert.That(first.LastVersion).IsNull().Because("a prefix leaves no tombstone version");
    await Assert.That((await _manifestAsync(stream)).Select(m => string.Join('|', m.Split('|')[..3])))
      .IsEquivalentTo(["1|2|2", "3|4|2"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _readAllAsync(store, stream)).IsEquivalentTo(hot[..4], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", stream)).IsEqualTo(1L)
      .Because("the carry-forward survives a prefix archive");
  }

  [Test]
  public async Task Write_ARefusal_KeepsItsStatus_AndUploadsNothingAsync() {
    var (stream, ids) = await _seedAsync(3);
    await using (var connection = await _openAsync()) {
      await _executeAsync(connection,
        $"INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES ('{ids[0]}', '2999-01-01T00:00:00Z')");
    }
    var hot = await _hotAsync(stream);

    var written = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(written.Status).IsEqualTo(ArchiveStatus.HELD);
    await Assert.That(written.Locator).IsNull();
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(await _objectsAsync()).IsEmpty();
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
  }

  [Test]
  public async Task Write_TheUploadFails_TheStreamStaysFullyHot_AndNothingIsListedAsync() {
    var (stream, _) = await _seedAsync(3);
    var hot = await _hotAsync(stream);
    var tombstone = await _tombstoneAsync(stream);
    var generation = await _generationAsync();
    var fault = new FaultObjectWrites(afterSending: false);

    await Assert.That(() => _completedAsync(_store(fault: fault).WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<RequestFailedException>();

    await Assert.That(fault.Faulted).IsEqualTo(1).Because("the failure must come from the upload, after the database moved the events");
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the archive's transaction never committed, so every event is hot with every column as it was");
    await Assert.That(await _tombstoneAsync(stream)).IsEquivalentTo(tombstone).Because("no tombstone is left behind");
    await Assert.That(await _generationAsync()).IsEqualTo(generation).Because("nothing moved, so integrity has nothing to re-verify");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_archive WHERE stream_id = @s", stream)).IsEqualTo(0L);
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(await _objectsAsync()).IsEmpty();
  }

  [Test]
  public async Task Write_ACrashAfterTheUploadBeforeTheCommit_IsRetriedWithoutADuplicateAsync() {
    var (stream, _) = await _seedAsync(3);
    var hot = await _hotAsReadAsync(stream);
    var fault = new FaultObjectWrites(afterSending: true);
    var store = _store(maxEventsPerChunk: 10, fault: fault);

    await Assert.That(() => _completedAsync(store.WriteAsync(ArchiveBatch.WholeStream(stream)))).Throws<RequestFailedException>();
    var orphaned = await _objectsAsync();
    await Assert.That(orphaned).Count().IsEqualTo(1).Because("the object reached the store; only the client never heard");
    await Assert.That(await _manifestAsync(stream)).IsEmpty().Because("an object no manifest row names is not part of any archive");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", stream)).IsEqualTo(3L);

    fault.Armed = false;
    var retried = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    await Assert.That(retried.Status).IsEqualTo(ArchiveStatus.ARCHIVED);
    await Assert.That(await _objectsAsync()).IsEquivalentTo(orphaned)
      .Because("the retry writes the same bytes under the same name, so there is still one object, not two");
    await Assert.That(await _manifestAsync(stream)).Count().IsEqualTo(1);
    await Assert.That(await _readAllAsync(store, stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  // ── Integrity ──────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Read_ACorruptedObject_IsDetectedByItsHash_AndNothingIsDecodedAsync() {
    var (stream, _) = await _seedAsync(3);
    var store = _store(maxEventsPerChunk: 10);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var name = (await _objectsAsync()).Single();
    var blob = _container().GetBlobClient(name);
    var bytes = (await blob.DownloadContentAsync()).Value.Content.ToArray();
    var flipped = (byte[])bytes.Clone();
    flipped[^1] ^= 0xFF;

    _ = await blob.UploadAsync(BinaryData.FromBytes(flipped), overwrite: true);
    var sameLength = await Assert.That(() => _completedAsync(_readAllAsync(store, stream))).Throws<ArchiveObjectCorruptedException>();
    _ = await blob.UploadAsync(BinaryData.FromBytes(bytes[..^1]), overwrite: true);
    var truncated = await Assert.That(() => _completedAsync(_readAllAsync(store, stream))).Throws<ArchiveObjectCorruptedException>();
    var restore = await Assert.That(() => _completedAsync(store.RestoreAsync(stream))).Throws<ArchiveObjectCorruptedException>();

    await Assert.That(sameLength!.ExpectedSha256).IsEqualTo(ArchiveChunk.Sha256(bytes));
    await Assert.That(sameLength.ActualSha256).IsEqualTo(ArchiveChunk.Sha256(flipped)).Because("a changed byte is caught by the hash");
    await Assert.That(truncated!.Message).Contains($"recorded {bytes.Length} bytes").Because("a short object is caught by its length");
    await Assert.That(restore!.StreamId).IsEqualTo(stream);
    await Assert.That(await _manifestAsync(stream)).Count().IsEqualTo(1).Because("a restore that cannot read the archive changes nothing");
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_store WHERE stream_id = @s", stream)).IsEqualTo(0L);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_archive WHERE stream_id = @s", stream)).IsEqualTo(0L);
  }

  [Test]
  public async Task Read_AManifestRowThisStoreDidNotWrite_IsRefusedAsync() {
    var (stream, _) = await _seedAsync(3);
    var store = _store();
    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 1));
    await using (var connection = await _openAsync()) {
      await _executeAsync(connection,
        "UPDATE wh_archive_manifest SET locator = 'postgres-table://wh_event_archive?codec=none&format=1' " +
        $"WHERE stream_id = '{stream}'");
    }

    await Assert.That(() => _completedAsync(_readAllAsync(store, stream))).Throws<InvalidDataException>()
      .Because("a locator another store wrote is never read as one of this store's objects");
  }

  [Test]
  public async Task Read_AnObjectInTheContainerItsLocatorNames_AfterTheSettingChangedAsync() {
    var (stream, _) = await _seedAsync(3);
    var hot = await _hotAsReadAsync(stream);
    _ = await _store().WriteAsync(ArchiveBatch.WholeStream(stream));
    var other = $"archive-{Guid.NewGuid():N}";

    var read = await _readAllAsync(_store(container: other), stream);

    await Assert.That(read).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("each object's locator names its container, so changing the setting never strands older archives");
  }

  // ── What the table store archived ──────────────────────────────────────────────────────────────────

  [Test]
  public async Task Read_EventsTheTableStoreArchived_ComeBackInVersionOrderWithTheObjectsAsync() {
    var (stream, _) = await _seedAsync(6);
    var hot = await _hotAsReadAsync(stream);
    var table = new PostgresTableArchiveStore(_database().Open);
    var store = _store(maxEventsPerChunk: 10);

    _ = await table.WriteAsync(ArchiveBatch.Prefix(stream, 2));
    var tableOnly = await _readAllAsync(store, stream);
    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 3));
    var moved = await _manifestAsync(stream);
    _ = await table.WriteAsync(ArchiveBatch.Prefix(stream, 5));
    var both = await _readAllAsync(store, stream);

    await Assert.That(tableOnly).IsEquivalentTo(hot[..2], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an archive written before the store was registered stays readable through it");
    await Assert.That(moved.Select(m => string.Join('|', m.Split('|')[..3]))).IsEquivalentTo(["1|3|3"])
      .Because("this store's next write moves the table store's rows into its object, so the archive ends in objects");
    await Assert.That(both).IsEquivalentTo(hot[..5], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("rows the table store archived since sit above every object, so objects then rows is version order");
  }

  [Test]
  public async Task Write_ARowArchivedBeforeTheStoreKeptEveryColumn_MovesIntoAnObject_WithItsNullsAsync() {
    // A row with only the columns migration 085's copy required (no body, no flags, no position), naming an origin lane,
    // so every nullable column is seen both empty (this row) and set (the hot events) on its way into an object.
    var (stream, _) = await _seedAsync(3);
    var lane = Guid.CreateVersion7();
    await using (var connection = await _openAsync()) {
      await _executeAsync(connection,
        "INSERT INTO wh_event_archive (event_id, stream_id, event_type, version, origin_service_id, origin_commit_sequence) " +
        $"VALUES ('{Guid.CreateVersion7()}', '{stream}', 'Old.Type', 0, '{lane}', 7)");
    }
    var store = _store(maxEventsPerChunk: 10);

    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 1));
    var read = new List<ArchivedEvent>();
    await foreach (var e in store.ReadAsync(stream)) {
      read.Add(e);
    }
    var stored = ArchiveChunk.Decode(
      (await _container().GetBlobClient((await _objectsAsync()).Single()).DownloadContentAsync()).Value.Content.ToArray(),
      Whizbang.Core.Archival.ArchiveCompression.Brotli);

    await Assert.That(read.Select(e => e.Version)).IsEquivalentTo([0L, 1L], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the table store's row moved into the object with the prefix, under it in version order");
    await Assert.That(read[0].EventDataJson).IsNull();
    await Assert.That(read[0].MetadataJson).IsNull();
    await Assert.That(read[0].ScopeJson).IsNull();
    await Assert.That(read[0].AggregateId).IsNull();
    await Assert.That(read[0].AggregateType).IsNull();
    await Assert.That(read[0].CreatedAt).IsNull();
    await Assert.That(read[0].Flags).IsNull().Because("a missing column reads as null, never as a default");
    await Assert.That(read[1].EventDataJson).IsNotNull();
    await Assert.That((stored[0].OriginServiceId, stored[0].OriginCommitSequence, stored[0].CommitSequence, stored[0].InsertOrder))
      .IsEqualTo(((Guid?)lane, (long?)7L, (long?)null, (long?)null))
      .Because("the object keeps every column a restore needs, set or not");
    await Assert.That(stored[1].CommitSequence.HasValue).IsTrue();
    await Assert.That(stored[1].OriginServiceId).IsNull();
  }

  // ── Restore and delete ─────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Restore_PutsTheStreamBackExactly_AndRemovesItsObjectsAsync() {
    var (stream, ids) = await _seedAsync(5);
    var hot = await _hotAsync(stream);
    var store = _store(maxEventsPerChunk: 2);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    var restored = await store.RestoreAsync(stream);
    var again = await store.RestoreAsync(stream);

    await Assert.That(restored.Status).IsEqualTo(ArchiveStatus.RESTORED);
    await Assert.That(restored.EventsRestored).IsEqualTo(5L);
    await Assert.That(await _hotAsync(stream)).IsEquivalentTo(hot, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("every event returns at its version and commit position with its exact body");
    await Assert.That(await _rowsAsync("SELECT last_event_id::text FROM wh_perspective_cursors WHERE stream_id = @s", stream))
      .IsEquivalentTo([ids[^1].ToString()]).Because("the parked cursor returns where it was");
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(await _objectsAsync()).IsEmpty().Because("a restored archive's objects are removed after the commit");
    await Assert.That(await _tombstoneAsync(stream)).IsEquivalentTo(["Active|<none>|5"]);
    await Assert.That(again.Status).IsEqualTo(ArchiveStatus.NOTHING_ARCHIVED).Because("a restore is idempotent");
  }

  [Test]
  public async Task Restore_AVersionConflict_KeepsTheArchiveAndItsObjectsAsync() {
    var (stream, _) = await _seedAsync(3);
    var store = _store();
    _ = await store.WriteAsync(ArchiveBatch.Prefix(stream, 2));
    await using (var connection = await _openAsync()) {
      await _executeAsync(connection,
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version) " +
        $"VALUES ('{Guid.CreateVersion7()}', '{stream}', '{stream}', 'Order', '{LEDGER}', 1)");
    }
    var objects = await _objectsAsync();

    var result = await store.RestoreAsync(stream);

    await Assert.That(result.Status).IsEqualTo(ArchiveStatus.VERSION_CONFLICT);
    await Assert.That(result.EventsRestored).IsEqualTo(0L);
    await Assert.That(await _manifestAsync(stream)).Count().IsEqualTo(1).Because("a refused restore keeps the archive as it was");
    await Assert.That(await _objectsAsync()).IsEquivalentTo(objects);
    await Assert.That(await _countAsync("SELECT count(*) FROM wh_event_archive WHERE stream_id = @s", stream)).IsEqualTo(0L)
      .Because("the rows staged for the restore leave with its transaction");
  }

  [Test]
  public async Task Delete_RemovesTheObjectsAndTheManifest_TheTombstoneStaysAsync() {
    var (stream, _) = await _seedAsync(5);
    var store = _store(maxEventsPerChunk: 2);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));

    var deleted = await store.DeleteAsync(stream);

    await Assert.That(deleted).IsEqualTo(5L);
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(await _objectsAsync()).IsEmpty();
    await Assert.That(await _tombstoneAsync(stream)).IsEquivalentTo(["Archived|<none>|5"])
      .Because("the versions were seen downstream, so the tombstone outlives the archive");
    await using var connection = await _openAsync();
    var reuse = await Assert.That(() => _executeAsync(connection,
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version) " +
        $"VALUES ('{Guid.CreateVersion7()}', '{stream}', '{stream}', 'Order', '{LEDGER}', 5)"))
      .Throws<PostgresException>();
    await Assert.That(reuse!.SqlState).IsEqualTo("WHA01");
  }

  [Test]
  public async Task Delete_AnObjectThatCannotBeRemoved_IsLogged_AndTheDeleteStillCompletesAsync() {
    var (stream, _) = await _seedAsync(3);
    var logger = new ListLogger();
    var fault = new FaultObjectDeletes();
    var store = _store(maxEventsPerChunk: 10, fault: fault, logger: logger);
    _ = await store.WriteAsync(ArchiveBatch.WholeStream(stream));
    var objects = await _objectsAsync();

    var deleted = await store.DeleteAsync(stream);

    await Assert.That(deleted).IsEqualTo(3L).Because("the archive is deleted once its manifest is; the object is garbage now");
    await Assert.That(await _manifestAsync(stream)).IsEmpty();
    await Assert.That(await _objectsAsync()).IsEquivalentTo(objects).Because("the delete really failed");
    await Assert.That(logger.Messages).Contains(m => m.Level == LogLevel.Warning && m.Text.Contains(objects[0], StringComparison.Ordinal))
      .Because("an object left behind is named, so an operator can remove it");
  }

  [Test]
  public async Task Purge_AStreamArchivedToBlob_TakesItsManifestRowsAsync() {
    var (stream, _) = await _seedAsync(3);
    _ = await _store(maxEventsPerChunk: 10).WriteAsync(ArchiveBatch.WholeStream(stream));

    _ = await new PostgresStreamPurger(_database().Open).PurgeAsync(
      new StreamPurgeRequest { StreamIds = [stream], RequestedBy = "operator-1", Reason = "test" });

    await Assert.That(await _manifestAsync(stream)).IsEmpty()
      .Because("a purged stream must never be restored from an object a manifest row still names");
    await Assert.That(await _readAllAsync(_store(), stream)).IsEmpty();
    await Assert.That(await _objectsAsync()).Count().IsEqualTo(1)
      .Because("the purge runs in the database and cannot reach blob storage; the object is unreachable, not removed");
  }

  // ── The production caller ──────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Registration_TheDriversPrefixTruncate_ArchivesThroughTheBlobStoreAsync() {
    var (stream, _) = await _seedAsync(3);
    var services = new ServiceCollection().AddLogging();
    services.AddWhizbangPostgres(_db.ConnectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: false,
      perspectiveSchemaSql: null, configureOptions: null);
    services.AddWhizbangAzureBlobArchive(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Archive:AzureBlob:ConnectionString"] = _azurite,
      ["Whizbang:Archive:AzureBlob:ContainerName"] = _containerName,
    }).Build());
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var result = await provider.GetRequiredService<IStreamPrefixTruncator>().TruncateAsync(stream, 2, archive: true);

    await Assert.That(provider.GetRequiredService<IArchiveStore>()).IsTypeOf<AzureBlobArchiveStore>();
    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That((await _manifestAsync(stream)).Select(m => string.Join('|', m.Split('|')[..3]))).IsEquivalentTo(["1|2|2"]);
    await Assert.That(await _objectsAsync()).Count().IsEqualTo(1);
  }

  /// <summary>Fails each object write (a PUT of a blob, not of the container), before or after the service stored it.</summary>
  private sealed class FaultObjectWrites(bool afterSending) : HttpPipelinePolicy {
    public bool Armed { get; set; } = true;
    public int Faulted { get; private set; }

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
      ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) {
      var target = Armed && message.Request.Method == RequestMethod.Put
        && !message.Request.Uri.Query.Contains("restype=container", StringComparison.Ordinal);
      if (target && !afterSending) {
        Faulted++;
        throw new RequestFailedException(503, "injected: the upload failed");
      }
      await ProcessNextAsync(message, pipeline);
      if (target) {
        Faulted++;
        throw new RequestFailedException(503, "injected: the process died after the upload");
      }
    }
  }

  /// <summary>Fails every object delete.</summary>
  private sealed class FaultObjectDeletes : HttpPipelinePolicy {
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
      ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
      message.Request.Method == RequestMethod.Delete
        ? throw new RequestFailedException(503, "injected: the delete failed")
        : ProcessNextAsync(message, pipeline);
  }

  private sealed class ListLogger : ILogger<AzureBlobArchiveStore> {
    public List<(LogLevel Level, string Text)> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add((logLevel, formatter(state, exception)));
  }
}
