// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The catalog and cursor reads both Postgres work coordinators run: the type-definition fingerprints and their lineage,
/// the not-yet-ephemeral history of a type, the ephemeral pairs that still need a snapshot, the perspective cursors read
/// in a batch and those flagged for a rewind, and the local service id.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's inert defaults for every
/// one of them, so on that driver the type-definition reconciler saw an empty catalog, an ephemeral reclassification
/// reported no drift, no ephemeral body was ever snapshotted ahead of its reap, the cursor cache was never warmed in a
/// batch, a cursor flagged for a rewind was never rewound, and the service read as having no id (#2051).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperLifecycleAndCatalogTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the object names are constants. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class CoordinatorCatalogSql {
  /// <summary>The <c>wh_settings</c> watermark the type-definition reconcile is claimed through.</summary>
  internal const string TYPE_DEFINITION_RECONCILE_KEY = "type_definition_reconcile_last_run";

  /// <summary>The description written with <see cref="TYPE_DEFINITION_RECONCILE_KEY"/>.</summary>
  internal const string TYPE_DEFINITION_RECONCILE_DESCRIPTION =
    "Last claimed type-definition reconcile: the first instance to CAS this watermark walks the catalog; siblings skip.";

  /// <summary>Every registered type definition with its fingerprints in hex.</summary>
  internal static async Task<IReadOnlyList<TypeDefinitionInfo>> TypeDefinitionsAsync(DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT definition_id, event_type, encode(settings_hash, 'hex'), encode(schema_hash, 'hex'), schema_version FROM {prefix}wh_type_definitions";
    var list = new List<TypeDefinitionInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new TypeDefinitionInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
    }
    return list;
  }

  /// <summary>Registers a definition (<c>register_type_definition</c>): its id, whether it is new, and the one it supersedes.</summary>
  internal static async Task<TypeDefinitionRegistration> RegisterTypeDefinitionAsync(
      DbCommand cmd, string prefix, string eventTypeName, string settingsHashHex, string schemaHashHex, int schemaVersion,
      CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT definition_id, is_new, previous_definition_id FROM {prefix}register_type_definition(@t, @sh, @sch, @v)";
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = eventTypeName });
    cmd.Parameters.Add(new NpgsqlParameter("sh", NpgsqlDbType.Bytea) { Value = Convert.FromHexString(settingsHashHex) });
    cmd.Parameters.Add(new NpgsqlParameter("sch", NpgsqlDbType.Bytea) { Value = Convert.FromHexString(schemaHashHex) });
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Integer) { Value = schemaVersion });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return TypeDefinitionRegistration.None;
    }
    var previous = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? (int?)null : reader.GetInt32(2);
    return new TypeDefinitionRegistration(reader.GetInt32(0), reader.GetBoolean(1), previous);
  }

  /// <summary>Records how one definition relates to the one it replaced (<c>record_definition_lineage</c>).</summary>
  internal static async Task RecordDefinitionLineageAsync(
      DbCommand cmd, string prefix, int fromDefinitionId, int toDefinitionId, DefinitionRelationship relationship, string? migrationRef,
      CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}record_definition_lineage(@from, @to, @rel, @ref)";
    cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Integer) { Value = fromDefinitionId });
    cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Integer) { Value = toDefinitionId });
    cmd.Parameters.Add(new NpgsqlParameter("rel", NpgsqlDbType.Smallint) { Value = (short)relationship });
    cmd.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = (object?)migrationRef ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The stored events of the named types not yet stamped ephemeral (flag 8), matched through
  /// <c>normalize_event_type</c>. The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<long> CountSourcedEventsForTypesAsync(
      DbCommand cmd, string prefix, IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT count(*) FROM {prefix}wh_event_store es WHERE (es.flags & 8) = 0 " +
      $"AND es.event_type IN (SELECT {prefix}normalize_event_type(t) FROM unnest(@p_names) AS t)";
    cmd.Parameters.Add(new NpgsqlParameter("p_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypeNames.ToArray() });
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// The (stream, perspective) pairs with an ephemeral body past its rewind grace, consumed, that no snapshot covers by
  /// version and whose row the perspective has not purged (#2294): the snapshots to take before the body is reaped.
  /// </summary>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PurgedRowEphemeralHistorySqlTests.cs:PairsNeedingSnapshot_APurgedPair_IsNotATarget_AndItsBodyIsOfferedForDestructionAsync</tests>
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperLifecycleAndCatalogTests.cs:EphemeralPairsNeedingSnapshot_APairWhoseRowThePerspectivePurged_IsNotATargetAsync</tests>
  internal static async Task<IReadOnlyList<EphemeralSnapshotTarget>> EphemeralPairsNeedingSnapshotAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText =
      "SELECT DISTINCT es.stream_id, ma.target_name, c.last_event_id " +
      $"FROM {prefix}wh_event_body eb " +
      $"JOIN {prefix}wh_event_store es ON es.event_id = eb.event_id " +
      $"LEFT JOIN {prefix}wh_ephemeral_type_grace g ON g.event_type = es.event_type " +
      $"JOIN {prefix}wh_message_associations ma ON ma.normalized_message_type = es.event_type AND ma.association_type = 'perspective' " +
      $"JOIN {prefix}wh_perspective_cursors c ON c.stream_id = es.stream_id AND c.perspective_name = ma.target_name " +
      // #13b4 safety gate: only EPHEMERAL events; consumed sourced bodies are never snapshot targets.
      "WHERE (es.flags & 8) = 8 " +
      "AND es.created_at < NOW() - (COALESCE(g.grace_seconds, " +
      $"    (SELECT setting_value::int FROM {prefix}wh_settings WHERE setting_key = 'ephemeral_rewind_grace_seconds'), 300) " +
      "  * INTERVAL '1 second') " +
      "AND c.last_event_id IS NOT NULL " +
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}wh_perspective_events pe WHERE pe.event_id = eb.event_id AND pe.processed_at IS NULL) " +
      // 209: coverage is read by version, the stream's own counter, never by commit sequence.
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}wh_perspective_snapshots s WHERE s.stream_id = es.stream_id " +
      "  AND s.perspective_name = ma.target_name AND s.snapshot_version >= es.version) " +
      // 403 (#2294): a row the perspective purged has nothing to snapshot, and its marker covers the stream's bodies, so
      // the pair is no target. Selecting it again every cycle only repeated a bootstrap that found no row.
      $"AND NOT EXISTS (SELECT 1 FROM {prefix}wh_stream_purge_markers pm WHERE pm.stream_id = es.stream_id " +
      "  AND pm.perspective_name = ma.target_name)";
    var list = new List<EphemeralSnapshotTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new EphemeralSnapshotTarget(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2)));
    }
    return list;
  }

  /// <summary>
  /// The resume cursors of the streams (<c>wh_perspective_resume_cursors</c>: the live cursor, else the one a
  /// whole-stream archive parked, #1734) with the commit sequence of each cursor's last event, which the cursor cache's
  /// inversion check reads. The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<List<PerspectiveCursorInfo>> PerspectiveCursorsBatchAsync(
      DbCommand cmd, string prefix, Guid[] streamIds, CancellationToken cancellationToken) {
    cmd.CommandText =
        "SELECT c.stream_id, c.perspective_name, c.last_event_id, c.status, c.rewind_trigger_event_id, e.commit_sequence "
      + $"FROM {prefix}wh_perspective_resume_cursors(@p_stream_ids, NULL) c "
      + $"LEFT JOIN {prefix}wh_event_store e ON e.event_id = c.last_event_id";
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds });
    var results = new List<PerspectiveCursorInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new PerspectiveCursorInfo {
        StreamId = reader.GetGuid(0),
        PerspectiveName = reader.GetString(1),
        LastEventId = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
        Status = (PerspectiveProcessingStatus)reader.GetInt32(3),
        RewindTriggerEventId = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(4),
        LastCommitSequence = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5)
      });
    }
    return results;
  }

  /// <summary>The cursors flagged RewindRequired (status bit 32), in stream and perspective order.</summary>
  internal static async Task<IReadOnlyList<RewindCursorInfo>> CursorsRequiringRewindAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $@"
      SELECT stream_id, perspective_name, last_event_id, rewind_trigger_event_id
      FROM {prefix}wh_perspective_cursors
      WHERE (status & 32) = 32
      ORDER BY stream_id, perspective_name";
    var results = new List<RewindCursorInfo>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new RewindCursorInfo(
        reader.GetGuid(0),
        reader.GetString(1),
        await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(2),
        await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetGuid(3)));
    }
    return results;
  }

  /// <summary>The service id in <c>wh_service_config</c>, or <see cref="Guid.Empty"/> when there is none.</summary>
  internal static async Task<Guid> LocalServiceIdAsync(DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT service_id FROM {prefix}wh_service_config LIMIT 1";
    return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id ? id : Guid.Empty;
  }
}
