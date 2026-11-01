// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Archival;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How much of each stream is archived, read from the stream lifecycle ledger (<c>wh_stream_lifecycle</c>) for a rebuild
/// (#1931): a stream whose row is <c>Archived</c> is archived whole; an <c>Active</c> one that names where its archive is
/// (<c>archive_locator</c>) is archived through a prefix and still alive. Both stores keep the ledger the same way, so
/// the answer holds for a table archive and a blob archive alike. Both Postgres drivers register it over their
/// <see cref="PostgresArchiveConnection"/>.
/// </summary>
/// <remarks>
/// The ledger is keyed by stream, so each lookup is one primary-key probe per stream. The streams are looked up
/// <see cref="LOOKUP_BATCH_SIZE"/> at a time on one connection, so a rebuild of every stream sends bounded arrays.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Extents_NameTheStreamsArchivedWhole_AndThroughAPrefix_AndNoOtherAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:Extents_OfNoStreams_AskNothingAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs:AddWhizbangPostgres_RegistersTheArchivedStreamExtents_ThatNameTheStreamsArchivedWholeAndThroughAPrefixAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema identifier is formatted in, quoted by PgIdentifier; the stream ids are a bound parameter.")]
internal sealed class PostgresArchivedStreamExtents(PostgresArchiveConnection database) : IArchivedStreamExtents {
  /// <summary>The most streams one lookup names.</summary>
  internal const int LOOKUP_BATCH_SIZE = 1000;

  private readonly PostgresArchiveConnection _database = database ?? throw new ArgumentNullException(nameof(database));

  /// <inheritdoc />
  public async Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(
      IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    var extents = new Dictionary<Guid, ArchivedStreamExtent>();
    if (streamIds.Count == 0) {
      return extents;
    }
    var sql = $"SELECT l.stream_id, l.state = 'Archived' FROM {PgIdentifier.Qualify(_database.Schema, "wh_stream_lifecycle")} l " +
      "WHERE l.stream_id = ANY(@ids) AND (l.state = 'Archived' OR l.archive_locator IS NOT NULL)";
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    foreach (var batch in streamIds.Chunk(LOOKUP_BATCH_SIZE)) {
      await using var command = new NpgsqlCommand(sql, connection);
      command.Parameters.AddWithValue("ids", batch);
      await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        extents[reader.GetGuid(0)] = reader.GetBoolean(1) ? ArchivedStreamExtent.Whole : ArchivedStreamExtent.Prefix;
      }
    }
    return extents;
  }
}
