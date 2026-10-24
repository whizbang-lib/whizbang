// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// How a reconcile reaches the database. A transaction runs on one backend from its start to its commit. A statement
/// outside a transaction carries no such promise: behind a transaction-pooling pooler each one can run on a different
/// backend, so nothing a reconcile holds may span two calls unless <see cref="IsDirectAsync"/> says every call reaches
/// the same backend.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerPoolerTests.cs</tests>
internal interface IReconcileBackend {
  /// <summary>Runs <paramref name="work"/> in one transaction, committed when it returns and rolled back when it throws.</summary>
  /// <typeparam name="T">What the work returns.</typeparam>
  /// <param name="work">The statements, all on the connection it is given.</param>
  /// <param name="cancellationToken">Cancels the work.</param>
  Task<T> InTransactionAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken);

  /// <summary>Runs one statement outside any transaction.</summary>
  /// <typeparam name="T">What the statement returns.</typeparam>
  /// <param name="statement">The statement, on the connection it is given.</param>
  /// <param name="cancellationToken">Cancels the statement.</param>
  Task<T> StatementAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> statement, CancellationToken cancellationToken);

  /// <summary>True when every statement reaches the backend the connection opened (no pooler in between).</summary>
  /// <param name="cancellationToken">Cancels the check.</param>
  Task<bool> IsDirectAsync(CancellationToken cancellationToken);
}

/// <summary>The reconcile's backend over the one connection its caller owns.</summary>
/// <param name="connection">An open connection, outside any transaction.</param>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs</tests>
internal sealed class ConnectionReconcileBackend(NpgsqlConnection connection) : IReconcileBackend {
  /// <inheritdoc />
  public async Task<T> InTransactionAsync<T>(
      Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken) {
    await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var result = await work(connection, cancellationToken);
    await transaction.CommitAsync(cancellationToken);
    return result;
  }

  /// <inheritdoc />
  public Task<T> StatementAsync<T>(Func<NpgsqlConnection, CancellationToken, Task<T>> statement, CancellationToken cancellationToken) =>
    statement(connection, cancellationToken);

  /// <summary>
  /// A pooler answers the connection's startup itself and reports a process id of its own making, so the backend a
  /// statement reaches is the one the connection opened only when no pooler is in between.
  /// </summary>
  /// <param name="cancellationToken">Cancels the check.</param>
  public async Task<bool> IsDirectAsync(CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
    var serving = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    return serving == connection.ProcessID;
  }
}
