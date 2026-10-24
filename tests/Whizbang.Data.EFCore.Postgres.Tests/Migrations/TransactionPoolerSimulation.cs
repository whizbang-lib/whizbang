// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Stands in for a transaction-pooling pooler in front of a reconcile, at its worst: every statement outside a
/// transaction runs on a backend no earlier statement used, and a transaction runs on one backend from its start to its
/// commit. Every backend stays open until disposal, as a pooler keeps its server connections open, so a session lock
/// left on one stays held for the rest of the test.
/// </summary>
/// <param name="connectionString">The database the backends connect to.</param>
/// <param name="claimsDirect">What <see cref="IsDirectAsync"/> answers: true stands in for a pooler the check cannot see.</param>
internal sealed class TransactionPoolerSimulation(string connectionString, bool claimsDirect) : IReconcileBackend, IAsyncDisposable {
  private readonly List<NpgsqlConnection> _backends = [];
  private int _transactions;

  /// <summary>Runs once, after the first transaction commits and before anything else reaches the database.</summary>
  public Func<Task>? AfterFirstTransaction { get; init; }

  /// <summary>How many backends the reconcile reached.</summary>
  public int Backends => _backends.Count;

  public async Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken) {
    var backend = await _nextAsync(cancellationToken);
    T result;
    await using (var transaction = await backend.BeginTransactionAsync(cancellationToken)) {
      result = await work(backend, cancellationToken);
      await transaction.CommitAsync(cancellationToken);
    }
    if (++_transactions == 1 && AfterFirstTransaction is { } after) {
      await after();
    }
    return result;
  }

  public async Task<T> StatementAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> statement, CancellationToken cancellationToken) =>
    await statement(await _nextAsync(cancellationToken), cancellationToken);

  public Task<bool> IsDirectAsync(CancellationToken cancellationToken) => Task.FromResult(claimsDirect);

  private async Task<NpgsqlConnection> _nextAsync(CancellationToken cancellationToken) {
    // Unpooled, so each one is a server session of its own and disposing it ends that session.
    var backend = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
    await backend.OpenAsync(cancellationToken);
    _backends.Add(backend);
    return backend;
  }

  public async ValueTask DisposeAsync() {
    foreach (var backend in _backends) {
      await backend.DisposeAsync();
    }
  }
}
