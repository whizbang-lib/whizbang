// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The schema initialization lock, held for the length of a migration pass on a connection of its own: the same
/// advisory key (<see cref="SchemaInitializationLockKey"/>) the EF Core driver's DDL transaction takes, so two
/// instances starting together migrate one after the other, whichever driver each runs.
/// </summary>
/// <remarks>
/// <para>
/// Transaction-scoped (<c>pg_advisory_xact_lock</c>) in a transaction that does nothing else, so it cannot leak
/// through a transaction-pooling front end, and the server releases it if this process dies. The migrations run on
/// a different connection, each file in its own transaction, while this one holds the lock.
/// </para>
/// <para>
/// It tries first, and only when the lock is held elsewhere tells the observers and then waits on the server for
/// it. The wait has no client timeout: a migration can take as long as it takes, and the wait ends when the holder
/// commits, rolls back or dies, or when initialization is canceled.
/// </para>
/// </remarks>
/// <docs>data/turnkey-initialization#concurrent-starts</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperSchemaStartupTests.cs:HostStart_WaitsForTheSchemaLock_WhileAnotherSessionHoldsItAsync</tests>
internal sealed class SchemaInitializationLock : IAsyncDisposable {
  private readonly NpgsqlConnection _connection;
  private readonly NpgsqlTransaction _transaction;

  private SchemaInitializationLock(NpgsqlConnection connection, NpgsqlTransaction transaction) {
    _connection = connection;
    _transaction = transaction;
  }

  /// <summary>Takes the lock for <paramref name="schema"/>, waiting for it when another session holds it.</summary>
  /// <param name="connectionString">The database; the lock gets a connection of its own.</param>
  /// <param name="schema">The schema being initialized, unquoted.</param>
  /// <param name="observers">Told when the lock is held elsewhere, before the wait.</param>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>The held lock; disposing it releases it.</returns>
  public static async Task<SchemaInitializationLock> AcquireAsync(
      string connectionString, string schema, IReadOnlyList<ISchemaInitializationObserver> observers,
      CancellationToken cancellationToken) {
    var key = SchemaInitializationLockKey.Compute(schema);
    var connection = new NpgsqlConnection(connectionString);
    var held = false;
    try {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      await using (var attempt = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction)) {
        attempt.Parameters.AddWithValue("key", key);
        if (await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
          foreach (var observer in observers) {
            await observer.OnSchemaLockContendedAsync(schema, cancellationToken).ConfigureAwait(false);
          }
          await using var wait = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction) {
            CommandTimeout = 0,
          };
          wait.Parameters.AddWithValue("key", key);
          await wait.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
      }
      held = true;
      return new SchemaInitializationLock(connection, transaction);
    } finally {
      // Disposing the connection rolls back whatever it began, so a failure here leaves nothing held.
      if (!held) {
        await connection.DisposeAsync().ConfigureAwait(false);
      }
    }
  }

  /// <summary>Releases the lock: the transaction holding it ends, and the connection with it.</summary>
  public async ValueTask DisposeAsync() {
    await _transaction.DisposeAsync().ConfigureAwait(false);
    await _connection.DisposeAsync().ConfigureAwait(false);
  }
}
