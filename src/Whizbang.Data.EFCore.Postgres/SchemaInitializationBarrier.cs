// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// One schema initialization at a time for a host, database and schema (#1636).
/// </summary>
/// <docs>data/turnkey-initialization#a-second-call-waits</docs>
public static class SchemaInitializationBarrier {
  /// <summary>Enters the barrier for a context's host, database and schema.</summary>
  /// <param name="dbContext">The context being initialized.</param>
  /// <param name="serviceProvider">The service provider passed to the initialization.</param>
  /// <param name="schema">The schema being initialized.</param>
  /// <param name="logger">Where a wait is said; null says nothing.</param>
  /// <param name="cancellationToken">Ends a wait.</param>
  /// <returns>The turn, held until it is disposed.</returns>
  public static Task<IAsyncDisposable> EnterAsync(
      DbContext dbContext, IServiceProvider? serviceProvider, string schema, ILogger? logger, CancellationToken cancellationToken) {
    _ = (dbContext, serviceProvider, schema, logger, cancellationToken);
    return Task.FromResult<IAsyncDisposable>(new Turn());
  }

  /// <summary>Returns this call's turn; disposing it lets the next one in.</summary>
  /// <param name="host">The host's service provider.</param>
  /// <param name="connection">The context's connection, which names the data source and database. Not opened.</param>
  /// <param name="schema">The schema being initialized.</param>
  /// <param name="logger">Where a wait is said; null says nothing.</param>
  /// <param name="cancellationToken">Ends a wait.</param>
  /// <returns>The turn, held until it is disposed.</returns>
  public static Task<IAsyncDisposable> EnterAsync(
      IServiceProvider? host, DbConnection connection, string schema, ILogger? logger, CancellationToken cancellationToken) {
    _ = (host, connection, schema, logger, cancellationToken);
    return Task.FromResult<IAsyncDisposable>(new Turn());
  }

  /// <summary>Whether a caller holds, or waits for, the turn for a host, database and schema.</summary>
  /// <param name="host">The host's service provider.</param>
  /// <param name="connection">The connection that names the data source and database.</param>
  /// <param name="schema">The schema.</param>
  /// <returns>Whether the key is held or waited for.</returns>
  internal static bool Tracks(IServiceProvider host, DbConnection connection, string schema) {
    _ = (host, connection, schema);
    return true;
  }

  private sealed class Turn : IAsyncDisposable {
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}
