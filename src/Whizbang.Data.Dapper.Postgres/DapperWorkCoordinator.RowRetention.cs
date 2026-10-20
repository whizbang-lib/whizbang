// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres;

// RED stubs for #1505: the interface's no-op defaults, surfaced on the class so the tests compile.
public partial class DapperWorkCoordinator {
  /// <inheritdoc />
  public Task SyncPerspectiveRetentionAsync(
      IReadOnlyList<PerspectiveRetentionDeclaration> declarations, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsAboutToReapAsync(
      IReadOnlyCollection<string> clrTypeNames, int perTableLimit = 500, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>([]);

  /// <inheritdoc />
  public Task HoldPerspectiveRowDestructionAsync(
      IReadOnlyCollection<PerspectiveRowRef> rows, DateTimeOffset holdUntil, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task ReleasePerspectiveRowHoldsAsync(IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<int> RecordPerspectiveRowDestructionFailureAsync(
      IReadOnlyCollection<PerspectiveRowRef> rows, TimeSpan retryBackoff, int maxRetries, OnDestroyFailure onDestroyFailure,
      CancellationToken cancellationToken = default) =>
    Task.FromResult(int.MaxValue);

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapEnrolledPerspectiveRowsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) =>
    Task.FromResult(new PerspectiveRowReapResult(0, "unsupported"));

  /// <inheritdoc />
  public Task<PerspectiveRowReapResult> ReapPerspectiveRowCapsAsync(int batchSize = 5000, CancellationToken cancellationToken = default) =>
    Task.FromResult(new PerspectiveRowReapResult(0, "unsupported"));

  /// <inheritdoc />
  public Task<bool> TryClaimRowCapSweepAsync(TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
    Task.FromResult(true);

  /// <inheritdoc />
  public Task AcknowledgeRetentionEnforcementAsync(string clrTypeName, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRetentionAdoption>> AdoptEnrolledPerspectiveRetentionAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveRetentionAdoption>>([]);

  /// <inheritdoc />
  public Task<long> CountPerspectiveRetentionBacklogAsync(string clrTypeName, CancellationToken cancellationToken = default) =>
    Task.FromResult(0L);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowRef>> DrainRowEvictionJournalAsync(int limit = 1000, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveRowRef>>([]);

  /// <inheritdoc />
  public Task RequeueRowEvictionsAsync(IReadOnlyCollection<PerspectiveRowRef> rows, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveTableName>> GetPerspectiveTableNamesAsync(
      IReadOnlyCollection<string> clrTypeNames, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveTableName>>([]);

  /// <inheritdoc />
  public Task<IReadOnlyList<PerspectiveRowDestructionTarget>> GetPerspectiveRowsByIdsAsync(
      string clrTypeName, string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<PerspectiveRowDestructionTarget>>([]);

  /// <inheritdoc />
  public Task<int> CascadeDeletePerspectiveRowsAsync(
      string tableName, IReadOnlyCollection<Guid> rowIds, CancellationToken cancellationToken = default) =>
    Task.FromResult(0);

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables, CancellationToken cancellationToken = default) =>
    Task.FromResult(0);

  /// <inheritdoc />
  public Task<int> ReconcileFollowerPresenceAsync(
      string followerTable, IReadOnlyCollection<string> announcerTables, IReadOnlyCollection<Guid>? streamIds,
      CancellationToken cancellationToken = default) =>
    Task.FromResult(0);
}
