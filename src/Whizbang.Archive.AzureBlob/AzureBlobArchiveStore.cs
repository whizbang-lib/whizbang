// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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
/// <para>
/// Configuration drives every operation (#2137, migration 353). Each manifest row names its location
/// (<c>wh_archive_locations</c>: provider, account endpoint, logical connection name, container) by id and holds only
/// the object's path from its stream. A read, a restore and a delete compare every row's location with
/// <see cref="ConfiguredLocation"/> first and throw <see cref="ArchiveLocationMismatchException"/> on any difference,
/// before a request reaches storage, and a write of a stream whose objects are recorded elsewhere is refused the same
/// way, so a stream never spans two locations; the deletion queue is drained only for the configured location. A record
/// never redirects an operation, and nothing is deleted outside the configured location.
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
  private readonly AccessTier? _accessTier;
  private readonly PostgresTableArchiveStore _table;
  private readonly string _prefix;
  private readonly ILogger _logger;
  private int _containerReady;

  // Reads, restores and deletes look the configured location up and never create it; a write creates it.
  private const bool CREATE = true;
  private const bool LOOK_UP = false;

  /// <summary>Creates the store.</summary>
  /// <param name="database">The service's database, where the manifest and the staged rows live.</param>
  /// <param name="container">The container the objects are written to.</param>
  /// <param name="options">The compression quality, the chunk size and the access tier.</param>
  /// <param name="logger">Logs each outcome, and each object a restore or delete could not remove.</param>
  /// <exception cref="ArgumentOutOfRangeException">The quality is outside 0 to 11, the chunk size is below 1, or the access
  /// tier is not Hot, Cool or Cold.</exception>
  /// <exception cref="ArgumentException">The connection name is empty or contains <c>;</c> or <c>=</c>.</exception>
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
    if (AzureBlobArchiveOptions.RefusalFor(options.AccessTier) is { } refusal) {
      throw new ArgumentOutOfRangeException(nameof(options), options.AccessTier, refusal);
    }
    if (!AzureBlobArchiveOptions.IsLogicalName(options.ConnectionName)) {
      // Never echoed: a value that looks like a connection string may be one.
      throw new ArgumentException("ConnectionName is a logical name: not empty, and without ';' or '='.", nameof(options));
    }
    _database = database;
    _container = container;
    _quality = options.CompressionLevel;
    _maxEventsPerChunk = options.MaxEventsPerChunk;
    _accessTier = options.AccessTier;
    _table = new PostgresTableArchiveStore(database.Open, database.Schema);
    _prefix = database.Schema is null ? string.Empty : "\"" + database.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".";
    _logger = logger ?? (ILogger)NullLogger.Instance;
    ConfiguredLocation = new ArchiveLocation(STORE_NAME, EndpointOf(container), options.ConnectionName, container.Name);
  }

  /// <summary>
  /// The location this store reads, restores, deletes and writes in, from its configuration: the account endpoint of
  /// its container client (never a key, a signature or a connection string), the logical connection name and the
  /// container. Every archive record is checked against it.
  /// </summary>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs</tests>
  public ArchiveLocation ConfiguredLocation { get; }

  /// <summary>
  /// Acknowledges that the objects of a location now live where this store is configured (a database copied or
  /// restored to another environment, disaster recovery to another account, a container renamed): the location row
  /// <paramref name="fromLocationId"/> is updated to <see cref="ConfiguredLocation"/>, or, when that location already
  /// exists, its records join it. No record's path is rewritten and no object is moved or deleted; copy the objects
  /// first if they are not there yet.
  /// </summary>
  /// <param name="fromLocationId">The location to repoint: <see cref="ArchiveLocationMismatchException.RecordedLocationId"/>.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The id of the location the records now name.</returns>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromLocationId"/> is below 1.</exception>
  /// <exception cref="ArgumentException">No location has the id <paramref name="fromLocationId"/>.</exception>
  /// <docs>fundamentals/events/archive-store#repointing-an-environment</docs>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:RepointLocation_MakesAMismatchedDatabaseConsistent_ForAnotherContainerAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:RepointLocation_AfterTheCopyArchivedItsOwnStreams_MergesTheSourceLocationAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Tests/AzureBlobArchiveLocationTests.cs:RepointLocation_RefusesAnIdThatCannotNameARowAsync</tests>
  public async Task<long> RepointLocationAsync(long fromLocationId, CancellationToken cancellationToken = default) {
    ArgumentOutOfRangeException.ThrowIfLessThan(fromLocationId, 1);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_repoint_archive_location($1, $2, $3, $4, $5)", connection);
    command.Parameters.AddWithValue(fromLocationId);
    _addLocation(command, ConfiguredLocation);
    var location = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?
      ?? throw new ArgumentException($"No archive location {fromLocationId} exists to repoint.", nameof(fromLocationId));
    LogRepoint(_logger, fromLocationId, location, ConfiguredLocation.ToString());
    return location;
  }

  /// <summary>
  /// The storage account endpoint a container client reaches: scheme, host, port and any account path segment (an
  /// emulator's), without the container, the query (where a signature sits) or user information.
  /// </summary>
  internal static string EndpointOf(BlobContainerClient container) {
    var uri = container.Uri;
    var path = uri.AbsolutePath.TrimEnd('/');
    return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + path[..path.LastIndexOf('/')];
  }

  /// <summary>
  /// The locator a stream's tombstone records: <c>azure-blob://{stream}?codec=brotli&amp;format=1</c>. It says which
  /// store archived the stream and names no container or account, so it never goes stale when a location is repointed:
  /// each object is found by its manifest row's location and path from the stream (#2137), in
  /// <see cref="ConfiguredLocation"/> only.
  /// </summary>
  /// <param name="streamId">The stream.</param>
  /// <returns>The locator.</returns>
  public static ArchiveLocator LocatorFor(Guid streamId) =>
    new(STORE_NAME, streamId.ToString("D"), ArchiveCompression.Brotli);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_WholeStream_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_TheUploadFails_TheStreamStaysFullyHot_AndNothingIsListedAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_ACrashAfterTheUploadBeforeTheCommit_IsRetriedWithoutADuplicateAsync</tests>
  /// <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_OfAStreamWhoseObjectsAreElsewhere_ThrowsTheMismatch_AndWritesNothing_UntilRepointedAsync</tests>
  /// <exception cref="ArchiveLocationMismatchException">The stream's earlier objects are recorded in a location other
  /// than <see cref="ConfiguredLocation"/>; nothing is written.</exception>
  public async Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(batch);
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    // #2137: a stream never spans two locations. One whose objects are recorded anywhere but the configured location is
    // refused before anything moves and before any request reaches storage, under the stream's lock (the same key
    // wh_archive_stream takes, so a restore or delete cannot change its manifest in between).
    await _lockStreamAsync(connection, transaction, batch.StreamId, cancellationToken).ConfigureAwait(false);
    var recorded = await _manifestAsync(connection, transaction, batch.StreamId, fromVersion: 0, cancellationToken).ConfigureAwait(false);
    await _checkLocationAsync(connection, transaction, batch.StreamId, "write", recorded, cancellationToken).ConfigureAwait(false);
    await _ensureContainerAsync(cancellationToken).ConfigureAwait(false);

    var moved = await _moveIntoStagingAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
    if (!moved.Archived) {
      // A refusal, nothing to archive, or debug mode: nothing moved, and the transaction ends with nothing to keep.
      LogWrite(_logger, batch.StreamId, batch.ThroughVersion, moved.Status, 0, 0);
      return moved;
    }

    var location = (long)(await _locationScalarAsync(connection, transaction, CREATE, cancellationToken).ConfigureAwait(false))!;
    var objects = await _uploadStagedAsync(connection, transaction, batch.StreamId, cancellationToken).ConfigureAwait(false);
    await _recordManifestAsync(connection, transaction, batch.StreamId, location, objects, moved.OriginGeneration, cancellationToken)
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
      await _checkLocationAsync(connection, transaction, streamId, "restore", objects, cancellationToken).ConfigureAwait(false);
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
      await _checkLocationAsync(connection, transaction, streamId, "delete", objects, cancellationToken).ConfigureAwait(false);
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
      // #1897: the configured tier, or none (the account's default).
      AccessTier = _accessTier,
    };
    _ = await _container.GetBlobClient(name).UploadAsync(BinaryData.FromBytes(bytes), upload, cancellationToken).ConfigureAwait(false);
    return new ManifestRow(chunk[0].Version, chunk[^1].Version, chunk.Count, name, bytes.LongLength, sha256, LocationId: null, Recorded: null);
  }

  // One row per object: its location and its path from the stream (353), never a locator naming a container.
  private async Task _recordManifestAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, Guid streamId, long location, List<ManifestRow> objects,
      long originGeneration, CancellationToken cancellationToken) {
    await using (var command = new NpgsqlCommand(
        $"INSERT INTO {_prefix}wh_archive_manifest " +
        "(stream_id, first_version, last_version, event_count, location_id, relative_path, byte_length, sha256, origin_generation) " +
        "SELECT $1, m.first_version, m.last_version, m.event_count, $9, m.relative_path, m.byte_length, m.sha256, $8 " +
        "FROM unnest($2::bigint[], $3::bigint[], $4::int[], $5::text[], $6::bigint[], $7::text[]) " +
        "AS m(first_version, last_version, event_count, relative_path, byte_length, sha256)",
        connection, transaction)) {
      command.Parameters.AddWithValue(streamId);
      command.Parameters.AddWithValue(objects.Select(o => o.FirstVersion).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.LastVersion).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.EventCount).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.RelativePath).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.ByteLength).ToArray());
      command.Parameters.AddWithValue(objects.Select(o => o.Sha256).ToArray());
      command.Parameters.AddWithValue(originGeneration);
      command.Parameters.AddWithValue(location);
      _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    await _executeAsync(connection, transaction, $"DELETE FROM {_prefix}wh_event_archive WHERE stream_id = $1", streamId,
      cancellationToken).ConfigureAwait(false);
    // The same events archived again write the same object names. An earlier restore may have queued those names for
    // deletion; they are live again, so they leave the queue in this transaction, under the stream's lock.
    await using var dequeue = new NpgsqlCommand(
      $"DELETE FROM {_prefix}wh_archive_object_deletions WHERE location_id = $1 AND relative_path = ANY($2)", connection, transaction);
    dequeue.Parameters.AddWithValue(location);
    dequeue.Parameters.AddWithValue(objects.Select(o => o.RelativePath).ToArray());
    _ = await dequeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  // ── Locations (#2137) ────────────────────────────────────────────────────────────────────────────────

  // The configured location's id (353's wh_archive_location_id): created by a write, only looked up otherwise, so a read,
  // restore or delete in a database that never knew this location finds none and refuses every record.
  private async Task<long?> _locationIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken) =>
    await _locationScalarAsync(connection, transaction, LOOK_UP, cancellationToken).ConfigureAwait(false) as long?;

  private async Task<object?> _locationScalarAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, bool create, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_archive_location_id($1, $2, $3, $4, $5)", connection, transaction);
    _addLocation(command, ConfiguredLocation);
    command.Parameters.AddWithValue(create);
    return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
  }

  private static void _addLocation(NpgsqlCommand command, ArchiveLocation location) {
    command.Parameters.AddWithValue(location.Provider);
    command.Parameters.AddWithValue(location.Endpoint!);
    command.Parameters.AddWithValue(location.ConnectionName!);
    command.Parameters.AddWithValue(location.Container);
  }

  // Every row in the configured location, or the refusal naming the first that is not, before any request to storage.
  private async Task _checkLocationAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid streamId, string operation, List<ManifestRow> objects,
      CancellationToken cancellationToken) {
    if (objects.Count == 0) {
      return;
    }
    var configured = await _locationIdAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    var mismatch = objects.Find(o => o.LocationId is null || o.LocationId != configured);
    if (mismatch is not null) {
      throw new ArchiveLocationMismatchException(streamId, operation, mismatch.LocationId, mismatch.Recorded, ConfiguredLocation);
    }
  }

  // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────

  private async IAsyncEnumerable<ArchivedEvent> _readObjectsAsync(
      Guid streamId, long fromVersion, [EnumeratorCancellation] CancellationToken cancellationToken) {
    List<ManifestRow> objects;
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      objects = await _manifestAsync(connection, transaction: null, streamId, fromVersion, cancellationToken).ConfigureAwait(false);
      await _checkLocationAsync(connection, transaction: null, streamId, "read", objects, cancellationToken).ConfigureAwait(false);
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
      "SELECT m.first_version, m.last_version, m.event_count, m.relative_path, m.byte_length, m.sha256, m.location_id, " +
      "       l.provider, l.account_endpoint, l.connection_name, l.container " +
      $"FROM {_prefix}wh_archive_manifest m LEFT JOIN {_prefix}wh_archive_locations l ON l.location_id = m.location_id " +
      "WHERE m.stream_id = $1 AND m.last_version >= $2 ORDER BY m.first_version",
      connection, transaction);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(fromVersion);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var provider = _textOrNull(reader, 7);
      rows.Add(new ManifestRow(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), _textOrNull(reader, 3), reader.GetInt64(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        provider is null ? null : new ArchiveLocation(provider, _textOrNull(reader, 8), _textOrNull(reader, 9), reader.GetString(10))));
    }
    return rows;
  }

  // The object, in the configured container only (its row passed the location check), checked against its manifest row
  // before a byte of it is decoded.
  private async Task<IReadOnlyList<ArchiveChunkLine>> _downloadAsync(Guid streamId, ManifestRow row, CancellationToken cancellationToken) {
    if (!ArchiveChunk.TryReadCompression(row.RelativePath, out var compression)) {
      throw new InvalidDataException($"The archive manifest names '{row.RelativePath}', which is not an object this store wrote.");
    }
    var bytes = (await _container.GetBlobClient(row.RelativePath).DownloadContentAsync(cancellationToken).ConfigureAwait(false))
      .Value.Content.ToArray();
    var sha256 = ArchiveChunk.Sha256(bytes);
    if (bytes.LongLength != row.ByteLength || !string.Equals(sha256, row.Sha256, StringComparison.Ordinal)) {
      throw new ArchiveObjectCorruptedException(streamId, row.RelativePath!, row.ByteLength, bytes.LongLength, row.Sha256, sha256);
    }
    return ArchiveChunk.Decode(bytes, compression);
  }

  // ── Deleting objects no manifest row names ───────────────────────────────────────────────────────

  /// <summary>
  /// Deletes up to <paramref name="maxObjects"/> due objects from the deletion queue (<c>wh_archive_object_deletions</c>,
  /// migration 269): objects a restore, a delete or a stream purge stopped naming. A row goes once storage confirms the
  /// object is gone (a missing object counts); a failed delete stays queued, counts the attempt and waits one minute,
  /// doubling to at most an hour, before the next try. An object whose stream is being archived right now is left for a
  /// later run. Only objects queued in <see cref="ConfiguredLocation"/> are deleted: a row of any other location (a
  /// database copied from another environment, or not yet repointed) stays queued, untouched.
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
    var due = new List<(string RelativePath, Guid StreamId)>();
    long location;
    await using (var connection = await _database.Open(cancellationToken).ConfigureAwait(false)) {
      // A database that never knew the configured location (a copy, or one not yet repointed) has nothing to delete here:
      // every queued row names another location, and stays queued, untouched.
      if (await _locationIdAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false) is not { } configured) {
        return 0;
      }
      location = configured;
      await using var command = new NpgsqlCommand(
        $"SELECT relative_path, stream_id FROM {_prefix}wh_archive_object_deletions " +
        "WHERE location_id = $3 AND next_attempt_at <= NOW() AND ($1::uuid IS NULL OR stream_id = $1) " +
        "ORDER BY next_attempt_at, relative_path LIMIT $2",
        connection);
      command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)streamId ?? DBNull.Value });
      command.Parameters.AddWithValue(maxObjects);
      command.Parameters.AddWithValue(location);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        due.Add((reader.GetString(0), reader.GetGuid(1)));
      }
    }
    var deleted = 0;
    foreach (var (relativePath, stream) in due) {
      if (await _deleteQueuedAsync(location, relativePath, stream, cancellationToken).ConfigureAwait(false)) {
        deleted++;
      }
    }
    return deleted;
  }

  // One queued object, under its stream's lock: a write that uploads an object of the same name holds that lock until its
  // manifest row (and the dequeue) commit, so a live object is never deleted. A busy lock, or a row a write already took
  // back, leaves the object for a later run without counting an attempt.
  private async Task<bool> _deleteQueuedAsync(long location, string relativePath, Guid streamId, CancellationToken cancellationToken) {
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    await using (var claim = new NpgsqlCommand(
        "SELECT pg_try_advisory_xact_lock(hashtext($2)) " +
        $"AND EXISTS (SELECT 1 FROM {_prefix}wh_archive_object_deletions WHERE location_id = $3 AND relative_path = $1)",
        connection, transaction)) {
      claim.Parameters.AddWithValue(relativePath);
      claim.Parameters.AddWithValue(STREAM_LOCK_PREFIX + streamId.ToString("D"));
      claim.Parameters.AddWithValue(location);
      if (!(bool)(await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!) {
        return false;
      }
    }
    string sql;
    var confirmed = false;
    string? error = null;
    try {
      // DeleteIfExists: an object that is already gone counts as deleted.
      _ = await _container.GetBlobClient(relativePath).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
      confirmed = true;
      sql = $"DELETE FROM {_prefix}wh_archive_object_deletions WHERE relative_path = $1 AND location_id = $2";
    } catch (RequestFailedException ex) {
      error = ex.Message;
      LogObjectNotDeleted(_logger, ex, relativePath, streamId);
      sql = $"UPDATE {_prefix}wh_archive_object_deletions SET attempts = attempts + 1, last_error = $3, " +
        "next_attempt_at = NOW() + LEAST(interval '1 minute' * power(2, attempts), interval '1 hour') " +
        "WHERE relative_path = $1 AND location_id = $2";
    }
    await using (var record = new NpgsqlCommand(sql, connection, transaction)) {
      record.Parameters.AddWithValue(relativePath);
      record.Parameters.AddWithValue(location);
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

  /// <summary>
  /// One manifest row: an object's path from its stream, its version range and event count, what its bytes must hash to,
  /// and its location (id and description), when it names one.
  /// </summary>
  private sealed record ManifestRow(
    long FirstVersion, long LastVersion, int EventCount, string? RelativePath, long ByteLength, string Sha256, long? LocationId,
    ArchiveLocation? Recorded);

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
    Message = "Archive object {RelativePath} of stream {StreamId} could not be deleted; it stays queued and is retried after a backoff")]
  static partial void LogObjectNotDeleted(ILogger logger, Exception exception, string relativePath, Guid streamId);

  [LoggerMessage(Level = LogLevel.Warning,
    Message = "Archive location {FromLocationId} repointed by an operator to {Configured}; its records now name location {LocationId}")]
  static partial void LogRepoint(ILogger logger, long fromLocationId, long locationId, string configured);
}
