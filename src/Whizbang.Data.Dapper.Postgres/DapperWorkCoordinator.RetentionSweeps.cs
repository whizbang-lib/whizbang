// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using System.Runtime.CompilerServices;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// The retention and storage sweeps on the Dapper driver: the offload-claim ledger and its sweep claim, the settled
/// apply-path fold and its claim, the ancient ephemeral pointer prune, the ephemeral reclassification, the per-type
/// grace sync, the lifecycle-completion cleanup and the exhausted orphan reap.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's inert defaults for every member here, so on this driver a retention
/// setting such as the pointer prune or the lifecycle-completion window was accepted and removed nothing (#2051). Each
/// member now runs the statement the EF Core coordinator runs, from <see cref="CoordinatorRetentionSql"/>, qualified with
/// this coordinator's schema, under a slot of the coordinator gate when one is configured. An empty input returns before
/// a connection is opened.
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRetentionSweepTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorDriverParityTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task RecordOffloadClaimAsync(string storageKey, string providerName, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync(async (cmd, prefix) => {
      await CoordinatorRetentionSql.RecordOffloadClaimAsync(cmd, prefix, storageKey, providerName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
      TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorRetentionSql.ExpiredOffloadClaimsAsync(cmd, prefix, olderThan, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RemoveOffloadClaimsAsync(IReadOnlyCollection<string> storageKeys, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(storageKeys);
    if (storageKeys.Count == 0) {
      return Task.CompletedTask;
    }
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await CoordinatorRetentionSql.RemoveOffloadClaimsAsync(cmd, prefix, storageKeys, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<bool> TryClaimOffloadSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.TryClaimWatermarkAsync(
      cmd, prefix, CoordinatorRetentionSql.OFFLOAD_SWEEP_KEY, CoordinatorRetentionSql.OFFLOAD_SWEEP_DESCRIPTION,
      claimWindow, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<int> FoldSettledApplyPathsAsync(TimeSpan idleWindow, int limit = 1000, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorRetentionSql.FoldSettledApplyPathsAsync(cmd, prefix, idleWindow, limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimSettledFoldSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.TryClaimWatermarkAsync(
      cmd, prefix, CoordinatorRetentionSql.SETTLED_FOLD_KEY, CoordinatorRetentionSql.SETTLED_FOLD_DESCRIPTION,
      claimWindow, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<EphemeralPointerPruneResult> PruneAncientEphemeralPointersAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorRetentionSql.PruneAncientEphemeralPointersAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<EphemeralReclassificationResult> ReclassifyEventsEphemeralAsync(
      IReadOnlyList<string> eventTypeNames, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(eventTypeNames);
    if (eventTypeNames.Count == 0) {
      return Task.FromResult(EphemeralReclassificationResult.Empty);
    }
    return _withGatedCommandAsync((cmd, prefix) =>
      CoordinatorRetentionSql.ReclassifyEventsEphemeralAsync(cmd, prefix, eventTypeNames, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMixedStreamAllowanceTests.cs:SyncMixedStreamAllowancesAsync_AllowsThePair_AndAnEmptyListAllowsNothingAsync</tests>
  public Task SyncMixedStreamAllowancesAsync(IReadOnlyList<Whizbang.Core.Perspectives.MixedStreamPair> pairs, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(pairs);
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await CoordinatorMixedStreamSql.SyncAllowancesAsync(cmd, prefix, pairs, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperMixedStreamAllowanceTests.cs:GetStateBasedStreamIdsAsync_PassingOverTheAllowedType_LeavesTheMixedStreamRebuildableAsync</tests>
  public Task<IReadOnlyCollection<Guid>> GetStateBasedStreamIdsAsync(
      IReadOnlyList<Guid> streamIds, IReadOnlyList<string> ignoredEphemeralTypes, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    ArgumentNullException.ThrowIfNull(ignoredEphemeralTypes);
    return streamIds.Count == 0
      ? Task.FromResult<IReadOnlyCollection<Guid>>([])
      : _withGatedCommandAsync((cmd, prefix) =>
        CoordinatorMixedStreamSql.StateBasedStreamIdsAsync(cmd, prefix, streamIds, ignoredEphemeralTypes, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task SyncEphemeralTypeGraceAsync(IReadOnlyList<EphemeralTypeGrace> graceOverrides, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(graceOverrides);
    return _withGatedCommandAsync(async (cmd, prefix) => {
      await CoordinatorRetentionSql.SyncEphemeralTypeGraceAsync(cmd, prefix, graceOverrides, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public async Task<int> CleanupLifecycleCompletionsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default) {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
    var cutoff = DateTimeOffset.UtcNow - retentionPeriod;
    await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
    var connection = __scope.Connection;
    var prefix = await _prefixAsync(connection, cancellationToken);
    return await CoordinatorRetentionSql.CleanupLifecycleCompletionsAsync(
      () => CoordinatorCommandTimeout.Apply(connection.CreateCommand()), prefix, cutoff, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<int> ReapExhaustedOrphanedPerspectiveRowsAsync(
      Guid instanceId, IReadOnlyList<Guid> streamIds, int maxAttempts, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(streamIds);
    if (streamIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withGatedCommandAsync((cmd, prefix) => CoordinatorRetentionSql.ReapExhaustedOrphanedPerspectiveRowsAsync(
      cmd, prefix, instanceId, streamIds, maxAttempts, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// One coordinator statement under a slot of the coordinator gate when one is configured, taken under the calling
  /// member's name: a coordinator connection, this coordinator's schema prefix, and a command with the coordinator
  /// timeout. Every resource is disposed when the statement completes.
  /// </summary>
  private async Task<T> _withGatedCommandAsync<T>(
      Func<DbCommand, string, Task<T>> operation, CancellationToken cancellationToken, [CallerMemberName] string caller = "") {
    using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken, caller).ConfigureAwait(false);
    await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
    var prefix = await _prefixAsync(__scope.Connection, cancellationToken);
    await using var command = CoordinatorCommandTimeout.Apply(__scope.Connection.CreateCommand());
    return await operation(command, prefix).ConfigureAwait(false);
  }
}
