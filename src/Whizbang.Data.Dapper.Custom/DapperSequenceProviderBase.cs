using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Sequencing;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based ISequenceProvider implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
public abstract class DapperSequenceProviderBase : ISequenceProvider {
  protected readonly IDbConnectionFactory _connectionFactory;
  protected readonly IDbExecutor _executor;

  protected DapperSequenceProviderBase(IDbConnectionFactory connectionFactory, IDbExecutor executor) {
    ArgumentNullException.ThrowIfNull(connectionFactory);
    ArgumentNullException.ThrowIfNull(executor);

    _connectionFactory = connectionFactory;
    _executor = executor;
  }

  /// <summary>
  /// Ensures the connection is open. Handles both pre-opened and closed connections.
  /// </summary>
  protected static void EnsureConnectionOpen(IDbConnection connection) {
    if (connection.State != ConnectionState.Open) {
      connection.Open();
    }
  }

  /// <summary>
  /// Gets the SQL command to update an existing sequence and return the new value.
  /// Should return the new current_value after increment.
  /// Parameters: @SequenceKey (string), @Now (DateTimeOffset)
  /// </summary>
  protected abstract string GetUpdateSequenceSql();

  /// <summary>
  /// Gets the SQL command to insert a new sequence or update if it exists.
  /// Should return the current_value after the operation.
  /// This is used when the update returns no rows (sequence doesn't exist yet).
  /// Parameters: @SequenceKey (string), @Now (DateTimeOffset)
  /// </summary>
  protected abstract string GetInsertOrUpdateSequenceSql();

  /// <summary>
  /// Gets the SQL query to get the current value of a sequence.
  /// Should return the current_value or null if sequence doesn't exist.
  /// Parameters: @SequenceKey (string)
  /// </summary>
  protected abstract string GetCurrentSequenceSql();

  /// <summary>
  /// Gets the SQL command to reset a sequence to a specific value.
  /// Should handle both insert (if doesn't exist) and update (if exists).
  /// Parameters: @SequenceKey (string), @NewValue (long), @Now (DateTimeOffset)
  /// </summary>
  protected abstract string GetResetSequenceSql();

  public async Task<long> GetNextAsync(string streamKey, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(ct);
      EnsureConnectionOpen(connection);

      using var transaction = connection.BeginTransaction();

      try {
        // Try to increment existing sequence
        var updateSql = GetUpdateSequenceSql();

        var updatedValue = await _executor.ExecuteScalarAsync<long?>(
          connection,
          updateSql,
          new {
            SequenceKey = streamKey,
            Now = DateTimeOffset.UtcNow
          },
          transaction,
          ct);

        if (updatedValue.HasValue) {
          transaction.Commit();
          return updatedValue.Value;
        }

        // Sequence doesn't exist, insert new one starting at 0
        var insertSql = GetInsertOrUpdateSequenceSql();

        var newValue = await _executor.ExecuteScalarAsync<long>(
          connection,
          insertSql,
          new {
            SequenceKey = streamKey,
            Now = DateTimeOffset.UtcNow
          },
          transaction,
          ct);

        transaction.Commit();
        return newValue;
      } catch {
        transaction.Rollback();
        throw;
      }
    } catch (TaskCanceledException) {
      // Convert TaskCanceledException to OperationCanceledException for contract compliance
      throw new OperationCanceledException("The operation was canceled.", ct);
    }
  }

  public async Task<long> GetCurrentAsync(string streamKey, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(ct);
      EnsureConnectionOpen(connection);

      var sql = GetCurrentSequenceSql();

      var currentValue = await _executor.ExecuteScalarAsync<long?>(
        connection,
        sql,
        new { SequenceKey = streamKey },
        cancellationToken: ct);

      return currentValue ?? -1;
    } catch (TaskCanceledException) {
      // Convert TaskCanceledException to OperationCanceledException for contract compliance
      throw new OperationCanceledException("The operation was canceled.", ct);
    }
  }

  public async Task ResetAsync(string streamKey, long newValue = 0, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(streamKey);

    try {
      using var connection = await _connectionFactory.CreateConnectionAsync(ct);
      EnsureConnectionOpen(connection);

      var sql = GetResetSequenceSql();

      await _executor.ExecuteAsync(
        connection,
        sql,
        new {
          SequenceKey = streamKey,
          NewValue = newValue,
          Now = DateTimeOffset.UtcNow
        },
        cancellationToken: ct);
    } catch (TaskCanceledException) {
      // Convert TaskCanceledException to OperationCanceledException for contract compliance
      throw new OperationCanceledException("The operation was canceled.", ct);
    }
  }
}
