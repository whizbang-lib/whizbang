// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Collective;

/// <summary>
/// The lookup after a live write of a row a fan-out targets (#2622): one call of <c>wh_rewind_row_fan_outs_missed</c>
/// (migration 761), which only reads unless the write missed a foreign event the sink applied, and then rewinds the row.
/// </summary>
/// <docs>fundamentals/perspectives/multi-stream#live-comes-out-as-replay</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanOutMissedRewindSqlTests.cs:AMissedAppliedFanOut_RewindsTheRow_AndAnyOtherFanOutDoesNotAsync</tests>
internal static class FanOutMisses {
  /// <summary>Rewinds the row when the write missed an applied fan-out of the given types.</summary>
  /// <param name="database">The service's database.</param>
  /// <param name="eventTypes">The stored names of the model's fan-out types.</param>
  /// <param name="check">The row, its keys and what the write folded; its tenant is set.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The rows flagged for a rewind: 0 or 1.</returns>
  public static async Task<int> RewindAsync(
      PostgresArchiveConnection database, IReadOnlyList<string> eventTypes, FanOutMissCheck check, CancellationToken cancellationToken) {
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      "SELECT " + PgIdentifier.Qualify(database.Schema, "wh_rewind_row_fan_outs_missed")
      + "(@row, @perspective, @trigger, @tenant, @types, @keys, @sequences, @ids, @folded, now())", connection);
    command.Parameters.AddWithValue("row", check.RowId);
    command.Parameters.AddWithValue("perspective", check.PerspectiveName);
    command.Parameters.AddWithValue("trigger", check.TriggerEventId);
    command.Parameters.AddWithValue("tenant", check.TenantId!);
    command.Parameters.Add(new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypes.ToArray() });
    command.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
      Value = check.Keys.Select(k => k.KeyStreamId).ToArray()
    });
    command.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint) {
      Value = check.Keys.Select(k => (object?)k.AfterCommitSequence ?? DBNull.Value).ToArray()
    });
    command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) {
      Value = check.Keys.Select(k => (object?)k.AfterEventId ?? DBNull.Value).ToArray()
    });
    command.Parameters.Add(new NpgsqlParameter("folded", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = check.Folded.ToArray() });
    return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }
}
