// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The statements behind mixed streams (#2255), as both Postgres work coordinators run them: the startup sync of the pairs
/// allowed to share a stream, and the rebuild's state-based check that passes over the allowed ephemeral types a read
/// model does not apply. One copy, so the drivers cannot drift.
/// </summary>
/// <remarks>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with, built by
/// <see cref="PgIdentifier"/>, never from input. Every type name is a bound parameter.
/// </remarks>
/// <docs>fundamentals/events/ephemeral-events#allowing-mixed-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MixedStreamAllowanceSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMixedStreamAllowanceTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only the schema prefix is formatted in, built by PgIdentifier; every value is a bound parameter.")]
internal static class CoordinatorMixedStreamSql {
  /// <summary>Replaces the allowed pairs with <paramref name="pairs"/> (<c>wh_sync_mixed_stream_allowances</c>).</summary>
  internal static async Task SyncAllowancesAsync(
      DbCommand cmd, string prefix, IReadOnlyList<MixedStreamPair> pairs, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}wh_sync_mixed_stream_allowances(@ephemeral, @durable)";
    cmd.Parameters.Add(new NpgsqlParameter("ephemeral", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = pairs.Select(p => p.EphemeralType).ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("durable", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = pairs.Select(p => p.DurableType).ToArray() });
    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The given streams that hold a compaction origin, or an ephemeral event of a type not in
  /// <paramref name="ignoredEphemeralTypes"/>.
  /// </summary>
  internal static async Task<IReadOnlyCollection<Guid>> StateBasedStreamIdsAsync(
      DbCommand cmd, string prefix, IReadOnlyList<Guid> streamIds, IReadOnlyList<string> ignoredEphemeralTypes,
      CancellationToken cancellationToken) {
    // StateBased = Ephemeral (8) OR Compacted (16); an ephemeral event of an ignored type does not count.
    cmd.CommandText = $"SELECT DISTINCT stream_id FROM {prefix}wh_event_store WHERE stream_id = ANY(@ids) AND (flags & 24) <> 0 "
      + "AND NOT ((flags & 16) = 0 AND event_type = ANY(@ignored))";
    cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds.ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("ignored", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ignoredEphemeralTypes.ToArray() });
    var result = new List<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      result.Add(reader.GetGuid(0));
    }
    return result;
  }
}
