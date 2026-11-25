// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Names why each fetched perspective row without a body cannot be read (#2591), the same way for both Postgres drivers:
/// <see cref="StreamEventData.BODY_REAPED"/> for an ephemeral event, whose body the reap deleted past its retention and
/// which never comes back, and <see cref="StreamEventData.BODY_MISSING"/> for any other, whose body nothing in the
/// framework deletes.
/// </summary>
/// <remarks>
/// The drain's read returns a row whose body is gone with a NULL body and no flags, and its result cannot change under
/// its name, so the flags of those rows alone are read here: one probe by key per row without a body, and no query at all
/// for a batch that has none.
/// </remarks>
/// <docs>operations/workers/perspective-worker#unreadable-stored-events</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ReapedBodyStreamReadTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorWithDataTests.cs:GetStreamEventsAsync_AnEventWhoseBodyWasReaped_IsReadAsUnreadable_NextToAReadableOneAsync</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by PgIdentifier; the ids are a bound parameter.")]
internal static class MissingEventBodySql {
  /// <summary>
  /// Returns <paramref name="rows"/> with each row a driver marked unreadable (any non-null
  /// <see cref="StreamEventData.UnreadableReason"/>) named by whether its event is ephemeral; the same list when none is.
  /// </summary>
  internal static async Task<List<StreamEventData>> NameAsync(
      NpgsqlConnection connection, string prefix, List<StreamEventData> rows, CancellationToken cancellationToken) {
    Guid[] withoutBody = [.. rows.Where(row => row.UnreadableReason is not null).Select(row => row.EventId)];
    if (withoutBody.Length == 0) {
      return rows;
    }
    var ephemeral = new HashSet<Guid>();
    await using (var cmd = connection.CreateCoordinatorCommand()) {
      cmd.CommandText = $"SELECT event_id FROM {prefix}wh_event_store WHERE event_id = ANY(@ids) AND (flags & 8) = 8";
#pragma warning disable RCS1130 // NpgsqlDbType third-party enum; bitwise composition is its documented API.
      cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = withoutBody });
#pragma warning restore RCS1130
      await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        ephemeral.Add(reader.GetGuid(0));
      }
    }
    return rows.ConvertAll(row => row.UnreadableReason is null ? row : row with { UnreadableReason = _reasonFor(row, ephemeral) });
  }

  private static string _reasonFor(StreamEventData row, HashSet<Guid> ephemeral) =>
    ephemeral.Contains(row.EventId) ? StreamEventData.BODY_REAPED : StreamEventData.BODY_MISSING;
}
