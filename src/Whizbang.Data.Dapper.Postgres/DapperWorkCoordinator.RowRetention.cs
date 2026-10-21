// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres;

/// <summary>
/// Perspective row retention on the Dapper driver: enrollment, the expiry and cap sweeps, the adoption gate, the
/// pre-destruction seam and the stream-group cascade.
/// </summary>
/// <remarks>
/// This coordinator used to inherit the interface's no-op defaults for every member here, so on this driver a declared
/// row TTL or per-scope cap removed nothing (#1505). Each member now runs the statement the EF Core coordinator runs,
/// from <see cref="PerspectiveRowRetentionSql"/>, qualified with this coordinator's schema. An empty input returns
/// before a connection is opened.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperRowRetentionTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperWorkCoordinatorNamedSchemaTests.cs:RowRetention_RunsInTheConfiguredSchemaAsync</tests>
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public async Task SyncPerspectiveRetentionAsync(
      IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(declarations);
    if (declarations.Count == 0) {
      return;
    }
    await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
    var connection = __scope.Connection;
    var prefix = await _prefixAsync(connection, cancellationToken);
    await PerspectiveRowRetentionSql.SyncRetentionAsync(
      () => CoordinatorCommandTimeout.Apply(connection.CreateCommand()), prefix, declarations,
      _reportUnmatchedRetentionDeclaration, cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
      IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>([]);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.CollectReapTargetsAsync(cmd, prefix, clrTypeNames, perTableLimit, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task HoldPerspectiveRowDestructionAsync(
      IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withRetentionCommandAsync(async (cmd, prefix) => {
      await PerspectiveRowRetentionSql.HoldAsync(cmd, prefix, rows, holdUntil, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task ReleasePerspectiveRowHoldsAsync(IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withRetentionCommandAsync(async (cmd, prefix) => {
      await PerspectiveRowRetentionSql.ReleaseHoldsAsync(cmd, prefix, rows, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> RecordPerspectiveRowDestructionFailureAsync(
      IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries, OnDestroyFailure onDestroyFailure,
      CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.FromResult(0);
    }
    return _withRetentionCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.RecordDestructionFailureAsync(
      cmd, prefix, rows, retryBackoff, maxRetries, onDestroyFailure, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.ReapAsync(cmd, prefix, PerspectiveRowRetentionSql.EXPIRY_SWEEP, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.ReapAsync(cmd, prefix, PerspectiveRowRetentionSql.CAP_SWEEP, batchSize, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<bool> TryClaimRowCapSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.TryClaimWatermarkAsync(
      cmd, prefix, PerspectiveRowRetentionSql.ROW_CAP_SWEEP_KEY, PerspectiveRowRetentionSql.ROW_CAP_SWEEP_DESCRIPTION,
      claimWindow, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task AcknowledgeRetentionEnforcementAsync(string clrTypeName, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync(async (cmd, prefix) => {
      await PerspectiveRowRetentionSql.AcknowledgeAsync(cmd, prefix, clrTypeName, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRetentionAdoption>> AdoptEnrolledPerspectiveRetentionAsync(CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.AdoptAsync(cmd, prefix, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<long> CountPerspectiveRetentionBacklogAsync(string clrTypeName, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.CountBacklogAsync(cmd, prefix, clrTypeName, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowRef>> DrainRowEvictionJournalAsync(int limit = 1000, CancellationToken cancellationToken = default) =>
    _withRetentionCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.DrainJournalAsync(cmd, prefix, limit, cancellationToken), cancellationToken);

  /// <inheritdoc />
  public Task RequeueRowEvictionsAsync(IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken = default) {
    if (rows.Count == 0) {
      return Task.CompletedTask;
    }
    return _withRetentionCommandAsync(async (cmd, prefix) => {
      await PerspectiveRowRetentionSql.RequeueAsync(cmd, prefix, rows, cancellationToken).ConfigureAwait(false);
      return true;
    }, cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
      IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) {
    if (clrTypeNames.Count == 0) {
      return Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.TableNamesAsync(cmd, prefix, clrTypeNames, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
      string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>([]);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.RowsByIdsAsync(cmd, prefix, clrTypeName, tableName, rowIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> CascadeDeletePerspectiveRowsAsync(
      string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) {
    if (rowIds.Count == 0) {
      return Task.FromResult(0);
    }
    return _withRetentionCommandAsync((cmd, prefix) =>
      PerspectiveRowRetentionSql.CascadeDeleteAsync(cmd, prefix, tableName, rowIds, cancellationToken), cancellationToken);
  }

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables, CancellationToken cancellationToken = default) =>
    ReconcileFollowerPresenceAsync(followerTable, announcerTables, streamIds: null, cancellationToken);

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables, IReadOnlyCollection<Guid>? streamIds,
      CancellationToken cancellationToken = default) {
    if (announcerTables.Count == 0 || streamIds is { Count: 0 }) {
      return Task.FromResult(0);
    }
    return _withRetentionCommandAsync((cmd, prefix) => PerspectiveRowRetentionSql.ReconcileFollowerPresenceAsync(
      cmd, prefix, followerTable, announcerTables, streamIds, cancellationToken), cancellationToken);
  }

  /// <summary>
  /// One retention statement: a coordinator connection, this coordinator's schema prefix, and a command with the
  /// coordinator timeout. Every resource is disposed when the statement completes.
  /// </summary>
  private async Task<T> _withRetentionCommandAsync<T>(
      Func<DbCommand, string, Task<T>> operation, CancellationToken cancellationToken) {
    await using var __scope = await CoordinatorConnectionScope.AcquireAsync(_connectionString, cancellationToken);
    var prefix = await _prefixAsync(__scope.Connection, cancellationToken);
    await using var command = CoordinatorCommandTimeout.Apply(__scope.Connection.CreateCommand());
    return await operation(command, prefix).ConfigureAwait(false);
  }

  /// <summary>
  /// A declaration that matched no registry row: a key drift, or a perspective whose assembly registered no payload
  /// for the startup reconcile to write (#1548). Either way the perspective is not enrolled, so it is reported.
  /// </summary>
  private void _reportUnmatchedRetentionDeclaration(string clrTypeName) {
    if (_logger is not null) {
      LogRetentionDeclarationMatchedNoRegistryRow(_logger, clrTypeName);
    }
  }

  [LoggerMessage(
    Level = LogLevel.Warning,
    Message = "Row-retention declaration for {ClrTypeName} matched no wh_perspective_registry row: the perspective is NOT enrolled. "
            + "The registry key is the CLR type name (Outer+Model for a nested model); a row keyed in another form is a key drift"
  )]
  static partial void LogRetentionDeclarationMatchedNoRegistryRow(ILogger logger, string clrTypeName);
}
