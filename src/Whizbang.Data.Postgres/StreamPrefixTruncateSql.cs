// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The prefix truncate's statements, as both Postgres work coordinators run them: the gated truncate itself
/// (<c>wh_truncate_stream_prefix</c>), the events it is about to remove, the archived detail it preserved, the perspectives
/// that consume the range, an event's per-stream version, and the apply-path fold that runs before the pointers go.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's defaults for every one of
/// them, so on that driver a prefix truncate fired the destruction hook, the Pre stages and any compaction handler and then
/// reported <c>unsupported</c>: a hook was told data was about to be destroyed when nothing was (#2043).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>fundamentals/events/event-streams#prefix-truncate-on-every-data-layer</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StreamCloseSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyPathFoldSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamPrefixTruncateTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier and the object names are constants. Every value is a bound parameter.")]
internal static class StreamPrefixTruncateSql {
  private const string STREAM = "sid";
  private const string THROUGH = "through";

  /// <summary>
  /// The gated prefix truncate (<c>wh_truncate_stream_prefix</c>, migrations 240 onward): removes the stream's detail at
  /// or below <paramref name="throughVersion"/> once the consumption gate holds and a carry-forward event survives above
  /// it, copying it to <c>wh_event_archive</c> first when <paramref name="archive"/> is set.
  /// </summary>
  internal static async Task<StreamTruncateResult> TruncateAsync(
      DbCommand cmd, string prefix, Guid streamId, long throughVersion, bool archive, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT truncate_status, events_truncated FROM {prefix}wh_truncate_stream_prefix(@{STREAM}, @{THROUGH}, @{nameof(archive)})";
    _addRange(cmd, streamId, throughVersion);
    cmd.Parameters.Add(new NpgsqlParameter(nameof(archive), NpgsqlDbType.Boolean) { Value = archive });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new StreamTruncateResult(reader.GetString(0), reader.GetInt64(1));
  }

  /// <summary>
  /// The events the truncate would remove: the truncate's own range (this stream, at or below the truncate point), in
  /// version order.
  /// </summary>
  internal static async Task<IReadOnlyList<EphemeralDestructionTarget>> TargetsAsync(
      DbCommand cmd, string prefix, Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT event_id, stream_id, event_type FROM {prefix}wh_event_store WHERE stream_id = @{STREAM} AND version <= @{THROUGH} ORDER BY version";
    _addRange(cmd, streamId, throughVersion);
    var list = new List<EphemeralDestructionTarget>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new EphemeralDestructionTarget(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
    }
    return list;
  }

  /// <summary>The archived detail of the stream (<c>wh_event_archive</c>), in version order.</summary>
  internal static async Task<IReadOnlyList<ArchivedEvent>> ArchivedEventsAsync(
      DbCommand cmd, string prefix, Guid streamId, CancellationToken cancellationToken) {
    cmd.CommandText =
      "SELECT event_id, stream_id, version, event_type, event_data::text, metadata::text " +
      $"FROM {prefix}wh_event_archive WHERE stream_id = @{STREAM} ORDER BY version";
    cmd.Parameters.Add(new NpgsqlParameter(STREAM, NpgsqlDbType.Uuid) { Value = streamId });
    var list = new List<ArchivedEvent>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(new ArchivedEvent(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
        await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5)));
    }
    return list;
  }

  /// <summary>
  /// The distinct perspective names that consume an event of the stream at or below the truncate point, through the
  /// message associations of those events' types. The full-history guard asks this before a discarding truncate.
  /// </summary>
  internal static async Task<IReadOnlyList<string>> ConsumingPerspectiveNamesAsync(
      DbCommand cmd, string prefix, Guid streamId, long throughVersion, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT DISTINCT ma.target_name FROM {prefix}wh_event_store es " +
      $"JOIN {prefix}wh_message_associations ma ON ma.normalized_message_type = es.event_type AND ma.association_type = 'perspective' " +
      $"WHERE es.stream_id = @{STREAM} AND es.version <= @{THROUGH}";
    _addRange(cmd, streamId, throughVersion);
    var list = new List<string>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      list.Add(reader.GetString(0));
    }
    return list;
  }

  /// <summary>The per-stream version of the event, or <see langword="null"/> when no stored event has that id.</summary>
  internal static async Task<long?> EventVersionAsync(DbCommand cmd, string prefix, Guid eventId, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT version FROM {prefix}wh_event_store WHERE event_id = @id";
    cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = eventId });
    var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    return result is null or DBNull ? null : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// Folds the streams' apply paths into the persisted signature counts (<c>fold_stream_apply_paths</c>, migration 113),
  /// returning the shapes folded. The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<int> FoldStreamApplyPathsAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}fold_stream_apply_paths(@ids)";
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds.ToArray() });
    return ScalarResult.IntOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  private static void _addRange(DbCommand cmd, Guid streamId, long throughVersion) {
    cmd.Parameters.Add(new NpgsqlParameter(STREAM, NpgsqlDbType.Uuid) { Value = streamId });
    cmd.Parameters.Add(new NpgsqlParameter(THROUGH, NpgsqlDbType.Bigint) { Value = throughVersion });
  }
}
