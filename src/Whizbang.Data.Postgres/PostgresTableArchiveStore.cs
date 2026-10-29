// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>RED: the Postgres-table archive store, not yet written.</summary>
/// <param name="openConnection">Opens a connection to the service's database.</param>
/// <param name="schema">The schema, unquoted.</param>
/// <param name="logger">Logs each write's outcome.</param>
/// <docs>fundamentals/events/archive-store#the-postgres-table-store</docs>
public sealed class PostgresTableArchiveStore(
  Func<CancellationToken, ValueTask<NpgsqlConnection>> openConnection,
  string? schema = null,
  ILogger<PostgresTableArchiveStore>? logger = null) : IArchiveStore {
  /// <summary>RED.</summary>
  public ArchiveLocator Locator => throw new NotImplementedException($"{openConnection}{schema}{logger}");

  /// <inheritdoc />
  public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) => throw new NotImplementedException();

  /// <inheritdoc />
  public IAsyncEnumerable<ArchivedEvent> ReadAsync(Guid streamId, long fromVersion = 0, CancellationToken cancellationToken = default) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

  /// <inheritdoc />
  public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
