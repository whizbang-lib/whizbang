// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The retention and storage sweeps' statements, as both Postgres work coordinators run them: the offload-claim ledger,
/// the settled apply-path fold, the ancient ephemeral pointer prune, the ephemeral reclassification, the per-type grace
/// sync, the lifecycle-completion cleanup and the exhausted orphan reap, with the watermark keys of the sweeps that are
/// claimed once per window.
/// </summary>
/// <remarks>
/// <para>
/// One copy, so the drivers cannot drift. The Dapper coordinator used to inherit the interface's inert defaults for every
/// one of them, so on that driver a retention setting such as the pointer prune or the lifecycle-completion window was
/// accepted and removed nothing (#2051).
/// </para>
/// <para>
/// Each coordinator supplies a command on its own connection and the prefix it qualifies names with: the quoted schema
/// followed by a dot (<c>"svc".</c>), or empty where the caller resolves through <c>search_path</c>. It is always built
/// by <see cref="PgIdentifier"/>, never from input, and every value is a bound parameter.
/// </para>
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRetentionSweepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OffloadClaimLedgerSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ApplyPathFoldSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralPointerPruneSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EphemeralReclassifyCoordinatorTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EFCoreWorkCoordinatorLifecycleAndJanitorTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Identifiers cannot be parameterized: the schema prefix is built by PgIdentifier, the object names are constants and the swept relation is the name wh_partition_sweep_relation returns from the catalog. Every value is a bound parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3265:Non-flags enums should not be used in bitwise operations", Justification = "NpgsqlDbType composes Array with the element type by design.")]
internal static class CoordinatorRetentionSql {
  /// <summary>The <c>wh_settings</c> watermark the offload-claim sweep is claimed through.</summary>
  internal const string OFFLOAD_SWEEP_KEY = "offload_claim_sweep_last_run";

  /// <summary>The description written with <see cref="OFFLOAD_SWEEP_KEY"/>.</summary>
  internal const string OFFLOAD_SWEEP_DESCRIPTION =
    "Last claimed offload-claim sweep: the first instance to CAS this watermark drains the expired ledger; siblings skip.";

  /// <summary>The <c>wh_settings</c> watermark the settled apply-path fold is claimed through.</summary>
  internal const string SETTLED_FOLD_KEY = "settled_fold_last_run";

  /// <summary>The description written with <see cref="SETTLED_FOLD_KEY"/>.</summary>
  internal const string SETTLED_FOLD_DESCRIPTION =
    "Last claimed settled apply-path fold: the first instance to CAS this watermark folds idle streams; siblings skip.";

  private const string OFFLOAD_CLAIMS = "wh_offload_claims";

  /// <summary>Records an offloaded blob in the ledger; recording the same key again is absorbed.</summary>
  internal static async Task RecordOffloadClaimAsync(
      DbCommand cmd, string prefix, string storageKey, string providerName, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"INSERT INTO {prefix}{OFFLOAD_CLAIMS} (storage_key, provider_name) VALUES (@k, @p) " +
      "ON CONFLICT (storage_key) DO NOTHING";
    cmd.Parameters.Add(new NpgsqlParameter("k", storageKey));
    cmd.Parameters.Add(new NpgsqlParameter("p", providerName));
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The ledger rows uploaded longer ago than <paramref name="olderThan"/>, oldest first. Age is evaluated against the
  /// database clock at query time, so a changed window is retroactive over every existing blob.
  /// </summary>
  internal static async Task<IReadOnlyList<OffloadClaimRecord>> ExpiredOffloadClaimsAsync(
      DbCommand cmd, string prefix, TimeSpan olderThan, int batchSize, CancellationToken cancellationToken) {
    cmd.CommandText =
      $"SELECT storage_key, provider_name FROM {prefix}{OFFLOAD_CLAIMS} " +
      "WHERE uploaded_at < NOW() - make_interval(secs => @s) " +
      "ORDER BY uploaded_at LIMIT @n";
    cmd.Parameters.Add(new NpgsqlParameter("s", olderThan.TotalSeconds));
    cmd.Parameters.Add(new NpgsqlParameter("n", batchSize));
    var results = new List<OffloadClaimRecord>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      results.Add(new OffloadClaimRecord(reader.GetString(0), reader.GetString(1)));
    }
    return results;
  }

  /// <summary>Removes the ledger rows of the given keys. The caller returns before opening a connection for an empty set.</summary>
  internal static async Task RemoveOffloadClaimsAsync(
      DbCommand cmd, string prefix, IReadOnlyCollection<string> storageKeys, CancellationToken cancellationToken) {
    cmd.CommandText = $"DELETE FROM {prefix}{OFFLOAD_CLAIMS} WHERE storage_key = ANY(@keys)";
    cmd.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = storageKeys.ToArray() });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Folds the apply paths of streams idle past the window (<c>fold_settled_apply_paths</c>), returning the shapes folded.</summary>
  internal static async Task<int> FoldSettledApplyPathsAsync(
      DbCommand cmd, string prefix, TimeSpan idleWindow, int limit, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}fold_settled_apply_paths(@idle, @lim)";
    cmd.Parameters.Add(new NpgsqlParameter("idle", (long)idleWindow.TotalSeconds));
    cmd.Parameters.Add(new NpgsqlParameter("lim", limit));
    return ScalarResult.IntOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }

  /// <summary>The deep-maintenance pointer prune (<c>prune_ancient_ephemeral_pointers</c>): rows pruned and the status.</summary>
  internal static async Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(
      DbCommand cmd, string prefix, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT rows_pruned, status FROM {prefix}prune_ancient_ephemeral_pointers()";
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new EphemeralPointerPruneResult(reader.GetInt64(0), reader.GetString(1));
  }

  /// <summary>
  /// Reclassifies the stored history of the named types as ephemeral (<c>reclassify_events_ephemeral</c>). The caller
  /// returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<EphemeralReclassificationResult> ReclassifyEventsEphemeralAsync(
      DbCommand cmd, string prefix, IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT events_reclassified, streams_reclassified, streams_blocked FROM {prefix}reclassify_events_ephemeral(@p_names)";
    cmd.Parameters.Add(new NpgsqlParameter("p_names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypeNames.ToArray() });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      return EphemeralReclassificationResult.Empty;
    }
    return new EphemeralReclassificationResult(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
  }

  /// <summary>Replaces the per-type rewind-grace overrides with the declared set (<c>sync_ephemeral_type_grace</c>).</summary>
  internal static async Task SyncEphemeralTypeGraceAsync(
      DbCommand cmd, string prefix, IReadOnlyList<EphemeralTypeGrace> graceOverrides, CancellationToken cancellationToken) {
    var names = new string[graceOverrides.Count];
    var graces = new int[graceOverrides.Count];
    for (var i = 0; i < graceOverrides.Count; i++) {
      names[i] = graceOverrides[i].EventTypeName;
      graces[i] = graceOverrides[i].GraceSeconds;
    }
    cmd.CommandText = $"SELECT {prefix}sync_ephemeral_type_grace(@names, @graces)";
    cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
    cmd.Parameters.Add(new NpgsqlParameter("graces", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = graces });
    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// The handled ledger's answer for <paramref name="eventIds"/> (<c>wh_receptor_handled_lookup</c>, #2273): the subset it
  /// records as handled and the moment its coverage starts. The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<HandledLedgerAnswer> LookupHandledEventsAsync(
      DbCommand cmd, string prefix, IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT covers_from, handled_ids FROM {prefix}wh_receptor_handled_lookup(@p_ids)";
    cmd.Parameters.Add(new NpgsqlParameter("p_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = eventIds.ToArray() });
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    return new HandledLedgerAnswer(reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<Guid[]>(1).ToHashSet());
  }

  /// <summary>Keeps the handled ledger to <paramref name="keep"/> (<c>wh_receptor_handled_sweep</c>, #2273): the records removed.</summary>
  internal static async Task<long> SweepHandledLedgerAsync(
      DbCommand cmd, string prefix, DateTimeOffset now, TimeSpan keep, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}wh_receptor_handled_sweep(@p_now, @p_keep)";
    cmd.Parameters.Add(new NpgsqlParameter("p_now", NpgsqlDbType.TimestampTz) { Value = now.ToUniversalTime() });
    cmd.Parameters.Add(new NpgsqlParameter("p_keep", NpgsqlDbType.Interval) { Value = keep });
    return (long)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }

  /// <summary>
  /// Deletes the lifecycle-completion markers completed before <paramref name="cutoff"/>: from the whole table, or only
  /// its default partition once the partition manager keeps it, because expired partitions are then dropped whole
  /// (#1646). Nothing when the table is partitioned with no default.
  /// </summary>
  internal static async Task<int> CleanupLifecycleCompletionsAsync(
      Func<DbCommand> newCommand, string prefix, DateTimeOffset cutoff, CancellationToken cancellationToken) {
    string? relation;
    await using (var resolve = newCommand()) {
      resolve.CommandText = $"SELECT {prefix}wh_partition_sweep_relation('wh_lifecycle_completions')";
      relation = await resolve.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
    if (relation is null) {
      return 0;
    }
    await using var delete = newCommand();
    delete.CommandText = $"DELETE FROM {relation} WHERE completed_at < @cutoff";
    delete.Parameters.Add(new NpgsqlParameter(nameof(cutoff), NpgsqlDbType.TimestampTz) { Value = cutoff });
    return await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Deletes the pending perspective-event rows of the streams, leased to the instance, attempted
  /// <paramref name="maxAttempts"/> or more times, whose event is gone (<c>reap_exhausted_orphaned_perspective_rows</c>).
  /// The caller returns before opening a connection for an empty set.
  /// </summary>
  internal static async Task<int> ReapExhaustedOrphanedPerspectiveRowsAsync(
      DbCommand cmd, string prefix, Guid instanceId, IReadOnlyList<Guid> streamIds, int maxAttempts, CancellationToken cancellationToken) {
    cmd.CommandText = $"SELECT {prefix}reap_exhausted_orphaned_perspective_rows(@p_instance_id, @p_stream_ids, @p_max_attempts)";
    cmd.Parameters.Add(new NpgsqlParameter("p_instance_id", instanceId));
    cmd.Parameters.Add(new NpgsqlParameter("p_stream_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = streamIds.ToArray() });
    cmd.Parameters.Add(new NpgsqlParameter("p_max_attempts", maxAttempts));
    return ScalarResult.IntOrZero(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
  }
}
