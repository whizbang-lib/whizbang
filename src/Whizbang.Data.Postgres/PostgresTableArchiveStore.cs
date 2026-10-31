// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The built-in <see cref="IArchiveStore"/>: archived events stay in this database, in <c>wh_event_archive</c>, shared
/// by both drivers (WS-3 S7). Each write is one call to <c>wh_archive_stream</c> (migration 262), which copies the batch
/// and truncates it in one transaction, bumps the origin generation and, for a whole stream, leaves the tombstone. A
/// blob store replaces it by registering its own <see cref="IArchiveStore"/>.
/// </summary>
/// <remarks>
/// The rows keep their <c>jsonb</c> columns, so the archive stays queryable in place and its codec is
/// <see cref="ArchiveCompression.None"/>: Postgres compresses large values itself (TOAST), and the copy never leaves
/// the database.
/// </remarks>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted; null resolves the tables through the connection's search path, as the
/// driver's other stores do.</param>
/// <param name="logger">Logs each write's outcome.</param>
/// <docs>fundamentals/events/archive-store#the-postgres-table-store</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchiveStoreTests.cs</tests>
public sealed partial class PostgresTableArchiveStore(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  ILogger<PostgresTableArchiveStore>? logger = null) : IArchiveStore {

  /// <summary>The store name written in this store's locators.</summary>
  public const string STORE_NAME = "postgres-table";

  private const string ARCHIVE_TABLE = "wh_event_archive";

  private readonly string _prefix = schema is null ? string.Empty : SqlText.Identifier(schema) + ".";
  private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

  /// <summary>
  /// Where this store's archives live: <c>postgres-table://{schema}.wh_event_archive?codec=none&amp;format=1</c>
  /// (without the schema when the search path resolves it).
  /// </summary>
  public ArchiveLocator Locator { get; } =
    new(STORE_NAME, schema is null ? ARCHIVE_TABLE : $"{schema}.{ARCHIVE_TABLE}", ArchiveCompression.None);

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_WholeStream_LeavesATombstone_AndBumpsTheOriginGenerationAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ARefusedPrefix_KeepsTheTruncatesStatus_AndCopiesNothingAsync</tests>
  public async Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(batch);
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT archive_status, events_archived, last_version, origin_generation FROM {_prefix}wh_archive_stream($1, $2, $3)",
      connection);
    command.Parameters.AddWithValue(batch.StreamId);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = (object?)batch.ThroughVersion ?? DBNull.Value });
    command.Parameters.AddWithValue(Locator.ToString());
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var status = reader.GetString(0);
    var result = new ArchiveWriteResult(
      status,
      reader.GetInt64(1),
      await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2),
      string.Equals(status, ArchiveStatus.ARCHIVED, StringComparison.Ordinal) ? Locator : null,
      reader.GetInt64(3));
    LogWrite(_logger, batch.StreamId, batch.ThroughVersion, result.Status, result.EventsArchived, result.OriginGeneration);
    return result;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ThenRead_ReturnsTheEventsByteIdenticalInVersionOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Write_ThenRead_CarriesEachEventsCommitPositions_ExactlyAsTheHotStoreHeldThemAsync</tests>
  public async IAsyncEnumerable<ArchivedEvent> ReadAsync(
      Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT event_id, stream_id, version, event_type, event_data::text, metadata::text, scope::text, " +
      "aggregate_id, aggregate_type, created_at, flags, commit_sequence, origin_service_id, origin_commit_sequence " +
      $"FROM {_prefix}{ARCHIVE_TABLE} WHERE stream_id = $1 AND version >= $2 ORDER BY version",
      connection);
    command.Parameters.AddWithValue(streamId);
    command.Parameters.AddWithValue(fromVersion);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      yield return new ArchivedEvent(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt32(2),
        reader.GetString(3),
        _textOrNull(reader, 4),
        _textOrNull(reader, 5)) {
        ScopeJson = _textOrNull(reader, 6),
        AggregateId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
        AggregateType = _textOrNull(reader, 8),
        CreatedAt = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        Flags = reader.IsDBNull(10) ? null : reader.GetInt32(10),
        // #1738: the event's commit positions, so a read through the archive orders it with the hot events as before.
        CommitSequence = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        OriginServiceId = reader.IsDBNull(12) ? null : reader.GetGuid(12),
        OriginCommitSequence = reader.IsDBNull(13) ? null : reader.GetInt64(13),
      };
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Restore_PutsTheStreamBackExactly_CursorsDigestsAndPositionsIncludedAsync</tests>
  public async Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT restore_status, events_restored, origin_generation FROM {_prefix}wh_restore_archived_stream($1)", connection);
    command.Parameters.AddWithValue(streamId);
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    var result = new ArchiveRestoreResult(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    LogRestore(_logger, streamId, result.Status, result.EventsRestored, result.OriginGeneration);
    return result;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveStoreSqlTests.cs:Delete_RemovesTheArchivedCopy_TheTombstoneStillBlocksReuseAsync</tests>
  public async Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) {
    await using var connection = await openConnection(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand($"SELECT {_prefix}wh_delete_stream_archive($1)", connection);
    command.Parameters.AddWithValue(streamId);
    var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    LogDelete(_logger, streamId, deleted);
    return deleted;
  }

  private static string? _textOrNull(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Archive of stream {StreamId} (through version {ThroughVersion}; none = the whole stream): {Status}, {EventsArchived} event(s) moved, origin generation {OriginGeneration}")]
  static partial void LogWrite(ILogger logger, Guid streamId, long? throughVersion, string status, long eventsArchived, long originGeneration);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Restore of stream {StreamId}: {Status}, {EventsRestored} event(s) back in the hot store, origin generation {OriginGeneration}")]
  static partial void LogRestore(ILogger logger, Guid streamId, string status, long eventsRestored, long originGeneration);

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Archived copy of stream {StreamId} deleted: {EventsDeleted} event(s); its tombstone stays")]
  static partial void LogDelete(ILogger logger, Guid streamId, long eventsDeleted);
}
