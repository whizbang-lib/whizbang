// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The collective snapshot fence on Postgres (#1963): one call to <c>wh_collective_snapshot_fence</c> (migration 276), so
/// the mark and the in-flight answer come from one view of the database. Both Postgres drivers register it over their
/// <see cref="PostgresArchiveConnection"/>.
/// </summary>
/// <docs>fundamentals/perspectives/snapshots#collectives</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Collective/CollectiveSnapshotFenceSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchivedStreamRebuildSqlTests.cs:TheDriver_RegistersTheCollectiveSnapshotFence_OnItsArchiveConnectionAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperArchivedStreamRebuildTests.cs:AddWhizbangPostgres_RegistersTheCollectiveSnapshotFenceAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema identifier is formatted in, quoted by PgIdentifier; every value is a bound parameter.")]
internal sealed class PostgresCollectiveSnapshotFence(PostgresArchiveConnection database) : ICollectiveSnapshotFence {
  private readonly PostgresArchiveConnection _database = database ?? throw new ArgumentNullException(nameof(database));

  /// <inheritdoc />
  public async Task<CollectiveSnapshotFenceReading> ReadAsync(
      string? tenantId, IReadOnlyList<string> collectiveEventTypes, long? afterSequence, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(collectiveEventTypes);
    var sql = $"SELECT out_complete_through, out_in_flight FROM {PgIdentifier.Qualify(_database.Schema, "wh_collective_snapshot_fence")}(@tenant, @types, @after)";
    await using var connection = await _database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Text) { Value = (object?)tenantId ?? DBNull.Value });
    command.Parameters.Add(new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = collectiveEventTypes.ToArray() });
    command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint) { Value = (object?)afterSequence ?? DBNull.Value });
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    long? through = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(0);
    return new CollectiveSnapshotFenceReading(through, reader.GetBoolean(1));
  }
}
