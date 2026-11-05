// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Tag-bound coalescing on the Dapper driver: the pending groups, a group's pending singles, the fold that stores the
/// composite and completes the singles in one transaction, and the deadline release.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's inert defaults for all four (#2051), so on this driver a coalesced
/// message was parked with its group and floor and never shipped: the ship worker found no group, folded nothing and
/// released nothing. Each member now runs the statements the EF Core coordinator runs, from <see cref="CoalesceSql"/>,
/// qualified with this coordinator's schema. This coordinator has no instance identity, so every caller folds, as the EF
/// Core coordinator does without one.
/// </remarks>
/// <docs>data/drivers#dapper-coordinator-parity</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCoalesceTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WorkCoordinatorDriverParityTests.cs</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task<IReadOnlyList<CoalesceGroupStats>> GetPendingCoalesceGroupStatsAsync(CancellationToken cancellationToken = default) =>
    _withGatedCommandAsync((cmd, prefix) => CoalesceSql.PendingGroupStatsAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<OutboxMessage>> FetchPendingCoalesceAsync(string group, int limit, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);
    return _withGatedCommandAsync((cmd, prefix) =>
      CoalesceSql.FetchPendingAsync(cmd, prefix, group, limit, callerInstanceId: null, _jsonOptions, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  /// <remarks>
  /// One transaction: the composite rows appear and the folded singles complete together. The doorbells the store queued
  /// are rung after the commit (#720).
  /// </remarks>
  public async Task CompleteCoalesceFoldAsync(
      IReadOnlyList<Guid> foldedIds, OutboxMessage[] compositeMessages, int partitionCount, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(foldedIds);
    ArgumentNullException.ThrowIfNull(compositeMessages);
    if (foldedIds.Count == 0) {
      return;
    }
    var json = _serializeNewOutboxMessages(compositeMessages);
    var ids = foldedIds.ToArray();
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      using var __ = _gate is null ? default : await _gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
      await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
      var connection = __scope.Connection;
      var prefix = await _prefixAsync(connection, cancellationToken);
      await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)) {
        await CoalesceSql.StoreCompositeAndCompleteSinglesAsync(
          transaction, prefix, json, DateTime.UtcNow, partitionCount, ids, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
      await DoorbellRinger.RingAsync(connection, prefix + DoorbellRinger.FUNCTION_NAME, _logger, cancellationToken);
    }, logger: _logger, cancellationToken: cancellationToken);
  }

  /// <inheritdoc />
  public async Task<int> ReleaseMaturedCoalesceAsync(string group, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(group);
    var released = 0;
    await PostgresDeadlockRetry.ExecuteAsync(async () =>
      released = await _withGatedCommandAsync((cmd, prefix) =>
        CoalesceSql.ReleaseMaturedAsync(cmd, prefix, group, cancellationToken), cancellationToken).ConfigureAwait(false),
      logger: _logger, cancellationToken: cancellationToken);
    return released;
  }
}
