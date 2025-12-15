using System.Data;
using Whizbang.Core.Data;
using Whizbang.Core.Sequencing;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Base class for Dapper-based ISequenceProvider implementations.
/// Provides common implementation logic while allowing derived classes to provide database-specific SQL.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs</tests>
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
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
  protected abstract string GetUpdateSequenceSql();

  /// <summary>
  /// Gets the SQL command to insert a new sequence or update if it exists.
  /// Should return the current_value after the operation.
  /// This is used when the update returns no rows (sequence doesn't exist yet).
  /// Parameters: @SequenceKey (string), @Now (DateTimeOffset)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  protected abstract string GetInsertOrUpdateSequenceSql();

  /// <summary>
  /// Gets the SQL query to get the current value of a sequence.
  /// Should return the current_value or null if sequence doesn't exist.
  /// Parameters: @SequenceKey (string)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
  protected abstract string GetCurrentSequenceSql();

  /// <summary>
  /// Gets the SQL command to reset a sequence to a specific value.
  /// Should handle both insert (if doesn't exist) and update (if exists).
  /// Parameters: @SequenceKey (string), @NewValue (long), @Now (DateTimeOffset)
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
  protected abstract string GetResetSequenceSql();

  /// <summary>
  /// Gets the next sequence number for a stream, incrementing atomically.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_DifferentStreamKeys_ShouldMaintainSeparateSequencesAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:CancellationToken_WhenCancelled_ShouldThrowAsync</tests>
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

  /// <summary>
  /// Gets the current sequence number for a stream without incrementing. Returns -1 if stream doesn't exist.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_DoesNotIncrement_ShouldReturnSameValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:CancellationToken_WhenCancelled_ShouldThrowAsync</tests>
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

  /// <summary>
  /// Resets a sequence to a specific value.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:CancellationToken_WhenCancelled_ShouldThrowAsync</tests>
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
