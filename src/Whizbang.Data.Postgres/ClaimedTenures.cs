// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Folds a claim's rows into the tenure of each claimed stream: the partition epoch the instance held when it claimed
/// the stream, and the partition (#2556). Both PostgreSQL drivers' coordinators build <c>WorkBatch.StreamEpochs</c> and
/// <c>WorkBatch.StreamPartitions</c> through here, so the two never disagree about a stream.
/// </summary>
/// <docs>fundamentals/work-coordinator/partition-assignment#fencing</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:ClaimedTenures_FoldOneEntryPerStream_KeepingTheLowerEpoch_AndSkipRowsWithoutOneAsync</tests>
public static class ClaimedTenures {
  /// <summary>
  /// Wraps a claim so each returned row also carries <c>partition_epoch</c> and <c>tenure_partition</c>, read in the
  /// claim's own statement (#2556): the epoch at which <paramref name="instanceParameter"/> owns the row's partition (the
  /// row's, or its stream's when the row names none), in either state; -1 when it does not own it; 0 for work without a
  /// partition; NULL with the service's switch off. The same answers as <c>wh_held_epoch</c>, computed set-wise: the
  /// switch is read once and each row's ownership is a primary-key probe, because a per-row call of the function was not
  /// inlined and cost the claim about 0.1 ms a row.
  /// </summary>
  /// <param name="prefix">The schema prefix (<c>"svc".</c>), or empty.</param>
  /// <param name="columns">The claim's columns to return, each qualified <c>c.</c>.</param>
  /// <param name="claimCall">The claim function call, schema-qualified.</param>
  /// <param name="instanceParameter">The claiming instance's parameter.</param>
  /// <param name="partitionCountParameter">The partition count's parameter.</param>
  /// <param name="epochColumn">The name the epoch column is returned under.</param>
  /// <param name="partitionColumn">The name the tenure's partition is returned under.</param>
  /// <returns>The statement.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:AClaimInMapMode_CarriesTheEpochOfEachStreamsPartitionAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CoordinatorOwnershipNoticesTests.cs:TheClaimsEpochs_AreWhHeldEpochsAnswers_InEveryCaseAsync</tests>
  public static string ProjectEpochs(
      string prefix, string columns, string claimCall, string instanceParameter, string partitionCountParameter,
      string epochColumn = "partition_epoch", string partitionColumn = "tenure_partition") =>
    // Both CTEs are materialized so the switch is read once: inlined, the planner evaluates it per row, and the
    // function is not inlined itself. In map mode each row's ownership is one primary-key probe; off, none is made.
    $"WITH c AS MATERIALIZED (SELECT * FROM {claimCall}), "
    + $"m AS MATERIALIZED (SELECT {prefix}wh_partition_ownership_by_map() AS map_on) "
    + $"SELECT {columns}, "
    + "CASE WHEN m.map_on THEN CASE WHEN t.tp IS NULL THEN 0 ELSE COALESCE(("
    + $"SELECT o.epoch FROM {prefix}wh_partition_ownership o "
    + $"WHERE o.partition_number = t.tp % 10000 AND o.owner_instance_id = {instanceParameter}), -1) END END AS {epochColumn}, "
    + $"t.tp AS {partitionColumn} "
    + "FROM c CROSS JOIN m "
    + "CROSS JOIN LATERAL (SELECT CASE WHEN m.map_on THEN COALESCE(c.partition_number, "
    + $"{prefix}compute_partition(c.work_stream_id, {partitionCountParameter})) END AS tp) t";

  /// <summary>
  /// The epoch of each stream that has one. A stream reported twice keeps the lower epoch, so a write under it can only be
  /// fenced more, never less.
  /// </summary>
  /// <typeparam name="T">The claim row type.</typeparam>
  /// <param name="rows">The claim's rows.</param>
  /// <param name="stream">The row's stream id.</param>
  /// <param name="epoch">The row's partition epoch.</param>
  /// <returns>The epochs by stream id.</returns>
  public static IReadOnlyDictionary<Guid, long> Epochs<T>(IEnumerable<T> rows, Func<T, Guid?> stream, Func<T, long?> epoch) {
    ArgumentNullException.ThrowIfNull(rows);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(epoch);
    var epochs = new Dictionary<Guid, long>();
    foreach (var row in rows) {
      if (stream(row) is not { } id || epoch(row) is not { } value) {
        continue;
      }
      epochs[id] = epochs.TryGetValue(id, out var held) ? Math.Min(held, value) : value;
    }
    return epochs;
  }

  /// <summary>The partition of each stream that has one, the first reported.</summary>
  /// <typeparam name="T">The claim row type.</typeparam>
  /// <param name="rows">The claim's rows.</param>
  /// <param name="stream">The row's stream id.</param>
  /// <param name="partition">The row's partition number.</param>
  /// <returns>The partitions by stream id.</returns>
  public static IReadOnlyDictionary<Guid, int> Partitions<T>(IEnumerable<T> rows, Func<T, Guid?> stream, Func<T, int?> partition) {
    ArgumentNullException.ThrowIfNull(rows);
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(partition);
    var partitions = new Dictionary<Guid, int>();
    foreach (var row in rows) {
      if (stream(row) is { } id && partition(row) is { } value) {
        partitions.TryAdd(id, value);
      }
    }
    return partitions;
  }
}
