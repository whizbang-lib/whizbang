// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The perspective write fence (#2556), carried in the statement that writes each row: a write of a stream with
/// perspective work in flight names the partition and epoch that work was claimed under, and the statement holds the
/// partition at that epoch (<c>wh_hold_partition_epoch</c>, migration 577) until it commits. A flip cannot commit while
/// the write is open, and one that committed first makes the write raise <see cref="SQL_STATE"/>, so it lands nowhere
/// and the current owner applies the stream. A stream with no work in flight (off map mode, or a rebuild) names nothing,
/// and its statement is as it always was.
/// </summary>
/// <param name="Prefix">The framework schema as a statement prefix (<c>"svc".</c>), or empty.</param>
/// <param name="TenureOf">The tenure a stream's write is fenced by (<see cref="PartitionTenures.OfPerspectiveWrite"/>).</param>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerspectiveWritesAreFencedTests.cs</tests>
public sealed record PerspectiveWriteFence(string Prefix, Func<Guid, PartitionTenure?> TenureOf) {
  /// <summary>The SQLSTATE a fenced write raises.</summary>
  public const string SQL_STATE = "WHF02";

  /// <summary>Whether <paramref name="exception"/> is a write the fence stopped.</summary>
  /// <param name="exception">The exception.</param>
  /// <returns>True for <see cref="SQL_STATE"/>.</returns>
  public static bool IsFenced(Exception exception) => exception is PostgresException { SqlState: SQL_STATE };

  /// <summary>The fence for <paramref name="tenures"/>, or null when the process keeps no ledger.</summary>
  /// <param name="prefix">The framework schema as a statement prefix.</param>
  /// <param name="tenures">The tenure ledger, when registered.</param>
  /// <returns>The fence, or null.</returns>
  public static PerspectiveWriteFence? For(string prefix, PartitionTenures? tenures) =>
    tenures is null ? null : new PerspectiveWriteFence(prefix, tenures.OfPerspectiveWrite);

  /// <summary>
  /// The statement that holds every partition of a batch at its epoch in partition order (#2557, migration 581), binding
  /// <c>@wb_hp</c> and <c>@wb_he</c>: run first in a write's transaction, so it takes its partitions in the order every
  /// flip and every other fenced write takes them, and a flip cannot deadlock against it.
  /// </summary>
  internal string HoldAllStatement() =>
    "SELECT 1 FROM " + Prefix + "wh_partition_ownership y WHERE (y.partition_number, y.epoch) IN "
    + "(SELECT t.p, t.e FROM unnest(@wb_hp, @wb_he) AS t(p, e)) ORDER BY y.partition_number FOR SHARE OF y";

  /// <summary>The call that holds a partition at an epoch, for an update's or a purge's predicate.</summary>
  /// <param name="partition">The partition expression.</param>
  /// <param name="epoch">The epoch expression.</param>
  /// <returns>The SQL.</returns>
  internal string Hold(string partition, string epoch) => Prefix + "wh_hold_partition_epoch(" + partition + ", " + epoch + ")";

  /// <summary>The id expression of a fenced insert or upsert.</summary>
  /// <param name="id">The id expression.</param>
  /// <param name="partition">The partition expression.</param>
  /// <param name="epoch">The epoch expression.</param>
  /// <returns>The SQL.</returns>
  internal string HeldStream(string id, string partition, string epoch) =>
    Prefix + "wh_held_stream(" + id + ", " + partition + ", " + epoch + ")";

  /// <summary>The partition parameter of a fenced row: the ownership table's partition, or null when it names none.</summary>
  /// <param name="name">The parameter name.</param>
  /// <param name="tenure">The row's tenure.</param>
  /// <returns>The parameter.</returns>
  internal static NpgsqlParameter PartitionParameter(string name, PartitionTenure? tenure) =>
    new(name, NpgsqlDbType.Integer) { Value = tenure is { } t ? t.Partition % 10000 : DBNull.Value };

  /// <summary>The epoch parameter of a fenced row, or null when it names none.</summary>
  /// <param name="name">The parameter name.</param>
  /// <param name="tenure">The row's tenure.</param>
  /// <returns>The parameter.</returns>
  internal static NpgsqlParameter EpochParameter(string name, PartitionTenure? tenure) =>
    new(name, NpgsqlDbType.Bigint) { Value = tenure is { } t ? t.Epoch : DBNull.Value };
}
