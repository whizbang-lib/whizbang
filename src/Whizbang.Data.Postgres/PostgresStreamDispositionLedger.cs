// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Lifecycle;

namespace Whizbang.Data.Postgres;

/// <summary>The stream lifecycle ledger's side of the scheduled disposition of sealed streams (#1734), on Postgres.</summary>
/// <param name="database">The service's database.</param>
public sealed class PostgresStreamDispositionLedger(PostgresArchiveConnection database) : IStreamDispositionLedger {
  private readonly PostgresArchiveConnection _database = database;

  /// <inheritdoc />
  public Task<int> ScheduleAsync(TimeSpan grace, int limit, DateTimeOffset now, CancellationToken cancellationToken = default) {
    _ = _database;
    return Task.FromResult(0);
  }

  /// <inheritdoc />
  public Task<IReadOnlyList<DueStreamDisposition>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<DueStreamDisposition>>([]);

  /// <inheritdoc />
  public Task<IReadOnlyList<StreamCursorPosition>> GetCursorsAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    Task.FromResult<IReadOnlyList<StreamCursorPosition>>([]);

  /// <inheritdoc />
  public Task SetDueAsync(Guid streamId, DateTimeOffset? due, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
