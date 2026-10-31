// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;
using ArchiveStatus = Whizbang.Core.Archival.ArchiveStatus;

namespace Whizbang.Archive.AzureBlob;

/// <summary>
/// The production <see cref="IArchiveStore"/> (WS-3 S8): archived events leave the database as Brotli-compressed JSON
/// lines in Azure Blob Storage, one object per chunk of at most <see cref="AzureBlobArchiveOptions.MaxEventsPerChunk"/>
/// events, listed in the service database's <c>wh_archive_manifest</c> (migration 268).
/// </summary>
/// <remarks>
/// <para>
/// A write runs in one database transaction. <c>wh_archive_stream</c> (the table store's SQL, with every guard it has)
/// moves the batch into <c>wh_event_archive</c> and leaves the tombstone; the store uploads the staged rows as objects,
/// writes one manifest row per object, removes the staged rows and only then commits. A failure anywhere before the
/// commit rolls the whole archive back, so the stream stays fully hot; an object already uploaded is named by no
/// manifest row, and a retry writes the same bytes under the same name (the name carries the content hash).
/// </para>
/// <para>
/// A read, a restore and a delete go through the manifest only, never a listing of the container, and every object is
/// checked against its recorded length and SHA-256 before it is decoded. Events archived by the Postgres-table store
/// before this store was registered stay readable, restorable and deletable: the store reads them from
/// <c>wh_event_archive</c> alongside its own objects, and the next write of the stream moves them into an object.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/archive-store#the-azure-blob-store</docs>
/// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs</tests>
public sealed partial class AzureBlobArchiveStore : IArchiveStore {
  /// <summary>The store name written in this store's locators.</summary>
  public const string STORE_NAME = "azure-blob";

  // The emit chains', the direct append's and the table store's per-stream key (262), so nothing is appended to a
  // stream while its archive moves, and a restore or delete never interleaves with a write of the same stream.
  private const string STREAM_LOCK_PREFIX = "wh_event_store:";

  private const string STAGED_COLUMNS =
    "event_id, stream_id, version, event_type, event_data::text, metadata::text, scope::text, aggregate_id, aggregate_type, " +
    "created_at, flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order";

  private readonly PostgresArchiveConnection _database;
  private readonly BlobContainerClient _container;
  private readonly int _quality;
  private readonly int _maxEventsPerChunk;
  private readonly PostgresTableArchiveStore _table;
  private readonly string _prefix;
  private readonly ILogger _logger;
  private int _containerReady;

  /// <summary>Creates the store.</summary>
  /// <param name="database">The service's database, where the manifest and the staged rows live.</param>
  /// <param name="container">The container the objects are written to.</param>
  /// <param name="options">The compression quality and chunk size.</param>
  /// <param name="logger">Logs each outcome, and each object a restore or delete could not remove.</param>
  /// <exception cref="ArgumentOutOfRangeException">The quality is outside 0 to 11, or the chunk size is below 1.</exception>
  public AzureBlobArchiveStore(
      PostgresArchiveConnection database,
      BlobContainerClient container,
      AzureBlobArchiveOptions options,
      ILogger<AzureBlobArchiveStore>? logger = null) {
    ArgumentNullException.ThrowIfNull(database);
    ArgumentNullException.ThrowIfNull(container);
    ArgumentNullException.ThrowIfNull(options);
    if (options.CompressionLevel is < AzureBlobArchiveOptions.MIN_COMPRESSION_LEVEL or > AzureBlobArchiveOptions.MAX_COMPRESSION_LEVEL) {
      throw new ArgumentOutOfRangeException(nameof(options), options.CompressionLevel,
        $"CompressionLevel is a Brotli quality, {AzureBlobArchiveOptions.MIN_COMPRESSION_LEVEL} to {AzureBlobArchiveOptions.MAX_COMPRESSION_LEVEL}.");
    }
    if (options.MaxEventsPerChunk < 1) {
      throw new ArgumentOutOfRangeException(nameof(options), options.MaxEventsPerChunk, "MaxEventsPerChunk is 1 or more.");
    }
    _database = database;
    _container = container;
    _quality = options.CompressionLevel;
    _maxEventsPerChunk = options.MaxEventsPerChunk;
    _table = new PostgresTableArchiveStore(database.Open, database.Schema);
    _prefix = database.Schema is null ? string.Empty : "\"" + database.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".";
    _logger = logger ?? (ILogger)NullLogger.Instance;
  }

  /// <summary>
  /// The locator a stream's tombstone records: <c>azure-blob://{container}/{stream}?codec=brotli&amp;format=1</c>. Each
  /// object's own locator (in the manifest) adds the object's name under the stream.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <returns>The locator.</returns>
  public ArchiveLocator LocatorFor(Guid streamId) =>
    new(STORE_NAME, $"{_container.Name}/{streamId:D}", ArchiveCompression.Brotli);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_WholeStream_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_TheUploadFails_TheStreamStaysFullyHot_AndNothingIsListedAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_ACrashAfterTheUploadBeforeTheCommit_IsRetriedWithoutADuplicateAsync</tests>
  public async Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(batch);
    await _ensureContainerAsync(cancellationToken).ConfigureAwait(false);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

    var moved = await _moveIntoStagingAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
    if (!moved.Archived) {
      // A refusal, nothing to archive, or debug mode: nothing moved, and the transaction ends with nothing to keep.
      LogWrite(_logger, batch.StreamId, batch.ThroughVersion, moved.Status, 0, 0);
      return moved;
    }

    var objects = await _uploadStagedAsync(connection, transaction, batch.StreamId, cancellationToken).ConfigureAwait(false);
    await _recordManifestAsync(connection, transaction, batch.StreamId, objects, moved.OriginGeneration, cancellationToken)
      .ConfigureAwait(false);
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

    LogWrite(_logger, batch.StreamId, batch.ThroughVersion, moved.Status, moved.EventsArchived, objects.Count);
    return moved with { Locator = LocatorFor(batch.StreamId) };
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_WholeStream_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Read_EventsTheTableStoreArchived_ComeBackInVersionOrderWithTheObjectsAsync</tests>
  public async IAsyncEnumerable<ArchivedEvent> ReadAsync(
      Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    // Objects first, then rows the table store holds. Every archive takes a stream's lowest hot versions, and every
    // write of this store moves the table store's rows into its objects, so a row still in the table is either the
    // whole archive (written before this store was registered) or above every object (written since, through the
    // coordinator's own archiving truncate). Either way this order is version order.
    await foreach (var archived in _readObjectsAsync(streamId, fromVersion, cancellationToken).ConfigureAwait(false)) {
      yield return archived;
    }
    await foreach (var archived in _table.ReadAsync(streamId, fromVersion, cancellationToken).ConfigureAwait(false)) {
      yield return archived;
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Restore_PutsTheStreamBackExactly_AndRemovesItsObjectsAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Restore_AVersionConflict_KeepsTheArchiveAndItsObjectsAsync</tests>
  public async Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) {
    List<ManifestRow> objects;
    ArchiveRestoreResult result;
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      await _lockStreamAsync(connection, transaction, streamId, cancellationToken).ConfigureAwait(false);
      objects = await _manifestAsync(connection, transaction, streamId, fromVersion: 0, cancellationToken).ConfigureAwait(false);
      foreach (var row in objects) {
        var lines = await _downloadAsync(streamId, row, cancellationToken).ConfigureAwait(false);
        await _stageAsync(connection, transaction, lines, cancellationToken).ConfigureAwait(false);
      }
      await _executeAsync(connection, transaction, $"DELETE FROM {_prefix}wh_archive_manifest WHERE stream_id = $1", streamId,
        cancellationToken).ConfigureAwait(false);
      result = await _restoreStagedAsync(connection, transaction, streamId, cancellationToken).ConfigureAwait(false);
      if (!string.Equals(result.Status, ArchiveStatus.RESTORED, StringComparison.Ordinal)) {
        // version_conflict or nothing_archived: the transaction ends unkept, so the manifest and the objects stay.
        LogRestore(_logger, streamId, result.Status, 0, 0);
        return result;
      }
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
    // The manifest rows' removal queued the objects (269); delete them now, under the stream's lock. One that cannot be
    // deleted stays queued for the maintenance step.
    _ = await _drainAsync(streamId, int.MaxValue, cancellationToken).ConfigureAwait(false);
    LogRestore(_logger, streamId, result.Status, result.EventsRestored, objects.Count);
    return result;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Delete_RemovesTheObjectsAndTheManifest_TheTombstoneStaysAsync</tests>
  public async Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) {
    List<ManifestRow> objects;
    long tableEvents;
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      await _lockStreamAsync(connection, transaction, streamId, cancellationToken).ConfigureAwait(false);
      objects = await _manifestAsync(connection, transaction, streamId, fromVersion: 0, cancellationToken).ConfigureAwait(false);
      await _executeAsync(connection, transaction, $"DELETE FROM {_prefix}wh_archive_manifest WHERE stream_id = $1", streamId,
        cancellationToken).ConfigureAwait(false);
      await using (var command = new NpgsqlCommand($"SELECT {_prefix}wh_delete_stream_archive($1)", connection, transaction)) {
        command.Parameters.AddWithValue(streamId);
        tableEvents = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
      }
      await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
    _ = await _drainAsync(streamId, int.MaxValue, cancellationToken).ConfigureAwait(false);
    var deleted = tableEvents + objects.Sum(o => (long)o.EventCount);
    LogDelete(_logger, streamId, deleted, objects.Count);
    return deleted;
  }

  // ── Writing ──────────────────────────────────────────────────────────────────────────────────────────

  private async Task _ensureContainerAsync(CancellationToken cancellationToken) {
    if (Volatile.Read(ref _containerReady) == 0) {
      _ = await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
      Volatile.Write(ref _containerReady, 1);
    }
  }

  // wh_archive_stream (262): the guards, the copy into wh_event_archive, the truncate, the generation bump and the
  // tombstone, all inside this transaction. The locator it records is this store's.
  private async Task<ArchiveWriteResult> _moveIntoStagingAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, ArchiveBatch batch, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT archive_status, events_archived, last_version, origin_generation FROM {_prefix}wh_archive_stream($1, $2, $3)",
      connection, transaction);
    command.Parameters.AddWithValue(batch.StreamId);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)batch.ThroughVersion ?? DBNull.Value });
    command.Parameters.AddWithValue(LocatorFor(batch.StreamId).ToString());
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new ArchiveWriteResult(
      reader.GetString(0),
      reader.GetInt64(1),
      await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2),
      Locator: null,
      reader.GetInt64(3));
  }

  // Every staged row of the stream, in version order, one object per chunk: the batch just moved, and any row the
  // table store archived earlier, so the stream's archive ends in objects only.
  private async Task<List<ManifestRow>> _uploadStagedAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, Guid streamId, CancellationToken cancellationToken) {
    var objects = new List<ManifestRow>();
    var chunk = new List<ArchiveChunkLine>(Math.Min(_maxEventsPerChunk, 1024));
    await using var command = new NpgsqlCommand(
      $"SELECT {STAGED_COLUMNS} FROM {_prefix}wh_event_archive WHERE stream_id = $1 ORDER BY version", connection, transaction);
    command.Parameters.AddWithValue(streamId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      chunk.Add(_line(reader));
      if (chunk.Count == _maxEventsPerChunk) {
        objects.Add(await _uploadAsync(streamId, chunk, cancellationToken).ConfigureAwait(false));
        chunk.Clear();
      }
    }
    if (chunk.Count > 0) {
      objects.Add(await _uploadAsync(streamId, chunk, cancellationToken).ConfigureAwait(false));
    }
    return objects;
  }

  private async Task<ManifestRow> _uploadAsync(Guid streamId, List<ArchiveChunkLine> chunk, CancellationToken cancellationToken) {
    var bytes = ArchiveChunk.Encode(chunk, ArchiveCompression.Brotli, _quality);
    var sha256 = ArchiveChunk.Sha256(bytes);
    var name = ArchiveChunk.ObjectName(streamId, chunk[0].Version, chunk[^1].Version, sha256, ArchiveCompression.Brotli);
    // Overwrites an object of the same name: the name carries the content hash, so it can only be these same bytes,
    // left by an earlier attempt whose transaction never committed.
    var upload = new BlobUploadOptions {
      // The service checks the request's bytes against this hash, so an object is stored exactly as it was sent.
      TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
    };
    _ = await _container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(bytes), upload, cancellationToken).ConfigureAwait(false);
    var locator = new ArchiveLocator(STORE_NAME, $"{_container.Name}/{name}", ArchiveCompression.Brotli);
    return new ManifestRow(chunk[0].Version, chunk[^1].Version, chunk.Count, locator.ToString(), bytes.LongLength, sha256);
  }

  private async Task _recordManifestAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, Guid streamId, List<ManifestRow> objects, long originGeneration,
      CancellationToken cancellationToken) {
    await using (var command = new NpgsqlCommand(
        $"INSERT INTO {_prefix}wh_archive_manifest " +
        "(stream_id, first_version, last_version, event_count, locator, byte_length, sha256, origin_generation) " +
        "SELECT $1, m.first_version, m.last_version, m.event_count, m.locator, m.byte_length, m.sha256, $8 " +
        "FROM unnest($2::bigint[], $3::bigint[], $4::int[], $5::text[], $6::bigint[], $7::text[]) " +
        "AS m(first_version, last_version, event_count, locator, byte_length, sha256)",
        connection, transaction)) {
      command.Parameters.AddWithValue(streamId);
      command.Parameters.AddWithValue(objects.Select(o => o.FirstVersion).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.LastVersion).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.EventCount).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.Locator).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.ByteLength).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.Sha256).ToArray());
      command.Parameters.AddWithValue(originGeneration);
      _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await _executeAsync(connection, transaction, $"DELETE FROM {_prefix}wh_event_archive WHERE stream_id = $1", streamId,
      cancellationToken).ConfigureAwait(false);
    // The same events archived again write the same object names. An earlier restore may have queued those names for
    // deletion; they are live again, so they leave the queue in this transaction, under the stream's lock.
    await using var dequeue = new NpgsqlCommand(
      $"DELETE FROM {_prefix}wh_archive_object_deletions WHERE locator = ANY($1)", connection, transaction);
    dequeue.Parameters.AddWithValue(objects.Select(o => o.Locator).ToArray());
    _ = await dequeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────

  private async IAsyncEnumerable<ArchivedEvent> _readObjectsAsync(
      Guid streamId, long fromVersion, [EnumeratorCancellation] CancellationToken cancellationToken) {
    List<ManifestRow> objects;
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      objects = await _manifestAsync(connection, transaction: null, streamId, fromVersion, cancellationToken).ConfigureAwait(false);
    }
    foreach (var row in objects) {
      foreach (var line in await _downloadAsync(streamId, row, cancellationToken).ConfigureAwait(false)) {
        if (line.Version >= fromVersion) {
          yield return line.ToArchivedEvent();
        }
      }
    }
  }

  private async Task<List<ManifestRow>> _manifestAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid streamId, long fromVersion, CancellationToken cancellationToken) {
    var rows = new List<ManifestRow>();
    await using var command = new NpgsqlCommand(
      "SELECT first_version, last_version, event_count, locator, byte_length, sha256 " +
      $"FROM {_prefix}wh_archive_manifest WHERE stream_id = $1 AND last_version >= $2 ORDER BY first_version",
      connection, transaction);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(fromVersion);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      rows.Add(new ManifestRow(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
    }
    return rows;
  }

  // The object, checked against its manifest row before a byte of it is decoded.
  private async Task<IReadOnlyList<ArchiveChunkLine>> _downloadAsync(Guid streamId, ManifestRow row, CancellationToken cancellationToken) {
    var (client, compression) = _objectClient(row.Locator);
    var bytes = (await client.DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content.ToArray();
    var sha256 = ArchiveChunk.Sha256(bytes);
    if (bytes.LongLength != row.ByteLength || !string.Equals(sha256, row.Sha256, StringComparison.Ordinal)) {
      throw new ArchiveObjectCorruptedException(streamId, row.Locator, row.ByteLength, bytes.LongLength, row.Sha256, sha256);
    }
    return ArchiveChunk.Decode(bytes, compression);
  }

  // An object in this store's container, or in the container its locator names (the setting may have changed since).
  private (BlobClient Client, ArchiveCompression Compression) _objectClient(string locator) {
    if (!ArchiveChunk.TryReadObjectLocator(locator, out var container, out var name, out var compression)) {
      throw new InvalidDataException($"The archive manifest names '{locator}', which is not an object this store wrote.");
    }
    var containerClient = string.Equals(container, _container.Name, StringComparison.Ordinal)
      ? _container
      : _container.GetParentBlobServiceClient().GetBlobContainerClient(container);
    return (containerClient.GetBlobClient(name), compression);
  }

  // ── Deleting objects no manifest row names ───────────────────────────────────────────────────────

  /// <summary>
  /// Deletes up to <paramref name="maxObjects"/> due objects from the deletion queue (<c>wh_archive_object_deletions</c>,
  /// migration 269): objects a restore, a delete or a stream purge stopped naming. A row goes once storage confirms the
  /// object is gone (a missing object counts); a failed delete stays queued, counts the attempt and waits one minute,
  /// doubling to at most an hour, before the next try. An object whose stream is being archived right now is left for a
  /// later run.
  /// </summary>
  /// <param name="maxObjects">The most objects this call deletes.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>How many objects were confirmed deleted.</returns>
  /// <docs>fundamentals/events/archive-store#purging-a-stream-archived-to-blob</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Purge_QueuesTheObjects_AndTheStepDeletesThemAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Step_AFailingDelete_StaysQueued_AndIsRetriedAfterItsBackoffAsync</tests>
  public Task<int> DeleteQueuedObjectsAsync(int maxObjects, CancellationToken cancellationToken = default) {
    ArgumentOutOfRangeException.ThrowIfLessThan(maxObjects, 1);
    return _drainAsync(streamId: null, maxObjects, cancellationToken);
  }

  private async Task<int> _drainAsync(Guid? streamId, int maxObjects, CancellationToken cancellationToken) {
    var due = new List<(string Locator, Guid StreamId)>();
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      await using var command = new NpgsqlCommand(
        $"SELECT locator, stream_id FROM {_prefix}wh_archive_object_deletions " +
        "WHERE next_attempt_at <= NOW() AND ($1::uuid IS NULL OR stream_id = $1) ORDER BY next_attempt_at, locator LIMIT $2",
        connection);
      command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)streamId ?? DBNull.Value });
      command.Parameters.AddWithValue(maxObjects);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        due.Add((reader.GetString(0), reader.GetGuid(1)));
      }
    }
    var deleted = 0;
    foreach (var (locator, stream) in due) {
      if (await _deleteQueuedAsync(locator, stream, cancellationToken).ConfigureAwait(false)) {
        deleted++;
      }
    }
    return deleted;
  }

  // One queued object, under its stream's lock: a write that uploads an object of the same name holds that lock until its
  // manifest row (and the dequeue) commit, so a live object is never deleted. A busy lock, or a row a write already took
  // back, leaves the object for a later run without counting an attempt.
  private async Task<bool> _deleteQueuedAsync(string locator, Guid streamId, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using (var claim = new NpgsqlCommand(
        "SELECT pg_try_advisory_xact_lock(hashtext($2)) " +
        $"AND EXISTS (SELECT 1 FROM {_prefix}wh_archive_object_deletions WHERE locator = $1)", connection, transaction)) {
      claim.Parameters.AddWithValue(locator);
      claim.Parameters.AddWithValue(STREAM_LOCK_PREFIX + streamId.ToString("D"));
      if (!(bool)(await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) {
        return false;
      }
    }
    string sql;
    var confirmed = false;
    string? error = null;
    try {
      // DeleteIfExists: an object that is already gone counts as deleted.
      _ = await _objectClient(locator).Client.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
      confirmed = true;
      sql = $"DELETE FROM {_prefix}wh_archive_object_deletions WHERE locator = $1";
    } catch (RequestFailedException ex) {
      error = ex.Message;
      LogObjectNotDeleted(_logger, ex, locator, streamId);
      sql = $"UPDATE {_prefix}wh_archive_object_deletions SET attempts = attempts + 1, last_error = $2, " +
        "next_attempt_at = NOW() + LEAST(interval '1 minute' * power(2, attempts), interval '1 hour') WHERE locator = $1";
    }
    await using (var record = new NpgsqlCommand(sql, connection, transaction)) {
      record.Parameters.AddWithValue(locator);
      if (error is not null) {
        record.Parameters.AddWithValue(error);
      }
      _ = await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    return confirmed;
  }

  // ── Restoring and deleting ───────────────────────────────────────────────────────────────────────────

  private static async Task _lockStreamAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, Guid streamId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", connection, transaction);
    command.Parameters.AddWithValue(STREAM_LOCK_PREFIX + streamId.ToString("D"));
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // The object's events back into wh_event_archive, where the table store's restore (262) takes them from.
  private async Task _stageAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<ArchiveChunkLine> lines, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"INSERT INTO {_prefix}wh_event_archive " +
      "(event_id, stream_id, version, event_type, event_data, metadata, scope, aggregate_id, aggregate_type, created_at, flags, " +
      " commit_sequence, origin_service_id, origin_commit_sequence, insert_order) " +
      "SELECT u.event_id, u.stream_id, u.version, u.event_type, u.event_data::jsonb, u.metadata::jsonb, u.scope::jsonb, " +
      "       u.aggregate_id, u.aggregate_type, u.created_at, u.flags, u.commit_sequence, u.origin_service_id, " +
      "       u.origin_commit_sequence, u.insert_order " +
      "FROM unnest($1::uuid[], $2::uuid[], $3::int[], $4::text[], $5::text[], $6::text[], $7::text[], $8::uuid[], $9::text[], " +
      "            $10::timestamptz[], $11::int[], $12::bigint[], $13::uuid[], $14::bigint[], $15::bigint[]) " +
      "AS u(event_id, stream_id, version, event_type, event_data, metadata, scope, aggregate_id, aggregate_type, created_at, " +
      "     flags, commit_sequence, origin_service_id, origin_commit_sequence, insert_order) " +
      "ON CONFLICT (event_id) DO NOTHING",
      connection, transaction);
    command.Parameters.AddWithValue(lines.Select(l => l.EventId).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.StreamId).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => checked((int)l.Version)).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.EventType).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.EventData).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.Metadata).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.Scope).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.AggregateId).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.AggregateType).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.CreatedAt).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.Flags).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.CommitSequence).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.OriginServiceId).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.OriginCommitSequence).ToArray());
    command.Parameters.AddWithValue(lines.Select(l => l.InsertOrder).ToArray());
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private async Task<ArchiveRestoreResult> _restoreStagedAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, Guid streamId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(
      $"SELECT restore_status, events_restored, origin_generation FROM {_prefix}wh_restore_archived_stream($1)", connection, transaction);
    command.Parameters.AddWithValue(streamId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new ArchiveRestoreResult(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  private static async Task _executeAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid streamId, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, connection, transaction);
    command.Parameters.AddWithValue(streamId);
    _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static ArchiveChunkLine _line(NpgsqlDataReader reader) =>
    new(
      reader.GetGuid(0),
      reader.GetGuid(1),
      reader.GetInt32(2),
      reader.GetString(3),
      _textOrNull(reader, 4),
      _textOrNull(reader, 5),
      _textOrNull(reader, 6),
      reader.IsDBNull(7) ? null : reader.GetGuid(7),
      _textOrNull(reader, 8),
      reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
      reader.IsDBNull(10) ? null : reader.GetInt32(10),
      reader.IsDBNull(11) ? null : reader.GetInt64(11),
      reader.IsDBNull(12) ? null : reader.GetGuid(12),
      reader.IsDBNull(13) ? null : reader.GetInt64(13),
      reader.IsDBNull(14) ? null : reader.GetInt64(14));

  private static string? _textOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  /// <summary>One manifest row: an object, its version range and event count, and what its bytes must hash to.</summary>
  private sealed record ManifestRow(long FirstVersion, long LastVersion, int EventCount, string Locator, long ByteLength, string Sha256);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Archive of stream {StreamId} to Azure Blob (through version {ThroughVersion}; none = the whole stream): {Status}, {EventsArchived} event(s) moved in {Objects} object(s)")]
  static partial void LogWrite(ILogger logger, Guid streamId, long? throughVersion, string status, long eventsArchived, int objects);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Restore of stream {StreamId} from Azure Blob: {Status}, {EventsRestored} event(s) back in the hot store, {Objects} object(s) queued for deletion")]
  static partial void LogRestore(ILogger logger, Guid streamId, string status, long eventsRestored, int objects);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Azure Blob archive of stream {StreamId} deleted: {EventsDeleted} event(s), {Objects} object(s) queued for deletion; its tombstone stays")]
  static partial void LogDelete(ILogger logger, Guid streamId, long eventsDeleted, int objects);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Archive object {Locator} of stream {StreamId} could not be deleted; it stays queued and is retried after a backoff")]
  static partial void LogObjectNotDeleted(ILogger logger, Exception exception, string locator, Guid streamId);
}
