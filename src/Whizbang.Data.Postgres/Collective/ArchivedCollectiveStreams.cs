// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// Lists the collective streams a replay cannot find in the hot event store: those archived whole (#1932). An archived
/// stream keeps its lifecycle row (<c>wh_stream_lifecycle</c>, state <c>Archived</c>) with the type of its first event
/// and its tenant, so the collective streams of a tenant are the archived rows of a collective type in that tenant. The
/// replay reads each one through the event store, whose archive seam serves its events.
/// </summary>
/// <remarks>
/// A collective stream archived through a prefix stays <c>Active</c> and keeps hot rows, so the replay finds it in the
/// hot store and its archived prefix through the same read. Only whole-stream archives need this list.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperArchivedCollectiveReplayTests.cs:AddCollectiveEventsDapper_TheReplayReadsTheTenantsArchivedCollectiveStreams_ListedFromTheLedgerAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/CollectiveReplayRebuildIntegrationTests.cs:Rebuild_FoldsACollectiveWhoseStreamIsArchivedWhole_ReadingItThroughTheArchiveAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema identifier is formatted in, quoted by PgIdentifier; the stream types and the tenant are bound parameters.")]
internal static class ArchivedCollectiveStreams {
  /// <summary>
  /// The query for one tenant. A tenant is compared with <c>=</c>, and no tenant with <c>IS NULL</c>, so each form is a
  /// range of the archived-stream index rather than an expression it cannot use.
  /// </summary>
  /// <param name="schema">The schema the framework's tables live in; null resolves them through the search path.</param>
  /// <param name="tenant">The tenant, or null for the streams of no tenant.</param>
  /// <returns>The SQL, taking the stream types as <c>@types</c> and, for a tenant, the tenant as <c>@tenant</c>.</returns>
  internal static string Sql(string? schema, string? tenant) =>
    $"SELECT stream_id FROM {PgIdentifier.Qualify(schema, "wh_stream_lifecycle")} " +
    "WHERE state = 'Archived' AND stream_type = ANY(@types) AND " + (tenant is null ? "tenant IS NULL" : "tenant = @tenant");

  /// <summary>Lists the archived streams of <paramref name="streamTypes"/> in <paramref name="tenant"/>.</summary>
  /// <param name="database">The service's database.</param>
  /// <param name="streamTypes">The stream types: the collective event types' names without their assembly, as the
  /// ledger records a stream's first event.</param>
  /// <param name="tenant">The tenant, or null for the streams of no tenant.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The archived streams' ids.</returns>
  internal static async Task<IReadOnlyList<Guid>> ListAsync(
      PostgresArchiveConnection database, IReadOnlyList<string> streamTypes, string? tenant, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(Sql(database.Schema, tenant), connection);
    command.Parameters.AddWithValue("types", streamTypes.ToArray());
    if (tenant is not null) {
      command.Parameters.AddWithValue(nameof(tenant), tenant);
    }
    var streams = new List<Guid>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      streams.Add(reader.GetGuid(0));
    }
    return streams;
  }
}
