// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The perspective write fence (#2556): which streams' partitions are no longer owned at the epoch their work was
/// claimed under, by the process's tenure ledger, so their rows are not written and the current owner applies them.
/// One statement for a batch, one primary-key probe of <c>wh_partition_ownership</c> per stream with a tenure; none at
/// all when no stream has one (the service does not own by map), so a write off map mode costs nothing more.
/// </summary>
/// <remarks>
/// The check runs on the write's own connection and transaction, just before the write. A flip that lands between the
/// two is the deadline takeover of an owner stuck at that moment; the handover protocol flips a partition only after its
/// owner has acknowledged, with nothing in flight, so a running owner's write is never caught between them.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs</tests>
public static class PerspectiveWriteFence {
  /// <summary>The streams among <paramref name="streams"/> whose claimed tenure is no longer current.</summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="transaction">The write's transaction, if any.</param>
  /// <param name="schemaPrefix">The framework schema as a statement prefix (<c>"svc".</c>), or empty.</param>
  /// <param name="tenures">The tenure ledger, when registered.</param>
  /// <param name="streams">The streams about to be written.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The fenced streams; empty when none is, or none has a tenure.</returns>
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "The schema prefix cannot be a bind parameter, so it is interpolated; it is built by PgIdentifier, which doubles any embedded quote. Every value is a bound parameter.")]
  public static async Task<IReadOnlySet<Guid>> StaleAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction? transaction,
      string schemaPrefix,
      PartitionTenures? tenures,
      IEnumerable<Guid> streams,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(streams);
    var held = tenures is null
      ? []
      : streams.Distinct().Select(s => (Stream: s, Tenure: tenures.OfStream(s))).Where(t => t.Tenure is not null).ToList();
    if (held.Count == 0) {
      return new HashSet<Guid>();
    }
    await using var cmd = connection.CreateCommand();
    cmd.Transaction = transaction;
    cmd.CommandText =
      "SELECT t.s FROM unnest(@s, @p, @e) AS t(s, p, e) "
      + $"WHERE NOT EXISTS (SELECT 1 FROM {schemaPrefix}wh_partition_ownership o WHERE o.partition_number = t.p % 10000 AND o.epoch = t.e)";
    cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = held.Select(h => h.Stream).ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = held.Select(h => h.Tenure!.Value.Partition).ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = held.Select(h => h.Tenure!.Value.Epoch).ToArray() });
    var stale = new HashSet<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      stale.Add(reader.GetGuid(0));
    }
    return stale;
  }
}
