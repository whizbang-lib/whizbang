using System.Data;
using Whizbang.Core.Data;
using DapperExtensions = Dapper;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IDbExecutor.
/// Provides ORM-agnostic database operations using Dapper.
/// </summary>
/// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs</tests>
/// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs</tests>
public class DapperDbExecutor : IDbExecutor {
  /// <summary>
  /// Executes a SQL query and returns a list of results.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromEmptyStream_ShouldReturnEmptyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_ShouldReturnEventsInOrderAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:ReadAsync_FromMiddle_ShouldReturnSubsetAsync</tests>
  public Task<IReadOnlyList<T>> QueryAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);
    return _queryAsyncCoreAsync<T>(connection, sql, param, transaction, cancellationToken);
  }

  private static async Task<IReadOnlyList<T>> _queryAsyncCoreAsync<T>(
    IDbConnection connection,
    string sql,
    object? param,
    IDbTransaction? transaction,
    CancellationToken cancellationToken) {
    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    var results = await DapperExtensions.SqlMapper.QueryAsync<T>(connection, commandDefinition);
    return [.. results];
  }

  /// <summary>
  /// Executes a SQL query and returns a single result or default if none found.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  public Task<T?> QuerySingleOrDefaultAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);
    return _querySingleOrDefaultAsyncCoreAsync<T>(connection, sql, param, transaction, cancellationToken);
  }

  private static async Task<T?> _querySingleOrDefaultAsyncCoreAsync<T>(
    IDbConnection connection,
    string sql,
    object? param,
    IDbTransaction? transaction,
    CancellationToken cancellationToken) {
    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.QuerySingleOrDefaultAsync<T>(connection, commandDefinition);
  }

  /// <summary>
  /// Executes a SQL command and returns the number of affected rows.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ShouldStoreEventAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:AppendAsync_ConcurrentAppends_ShouldBeThreadSafeAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithDefaultValue_ShouldResetToZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_WithCustomValue_ShouldResetToSpecifiedValueAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:ResetAsync_MultipleTimes_ShouldAlwaysResetAsync</tests>
  public Task<int> ExecuteAsync(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);
    return _executeAsyncCoreAsync(connection, sql, param, transaction, cancellationToken);
  }

  private static async Task<int> _executeAsyncCoreAsync(
    IDbConnection connection,
    string sql,
    object? param,
    IDbTransaction? transaction,
    CancellationToken cancellationToken) {
    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.ExecuteAsync(connection, commandDefinition);
  }

  /// <summary>
  /// Executes a SQL command and returns a single scalar value.
  /// </summary>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_EmptyStream_ShouldReturnMinusOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperEventStoreTests.cs:GetLastSequenceAsync_AfterAppends_ShouldReturnCorrectSequenceAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_FirstCall_ShouldReturnZeroAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_MultipleCalls_ShouldIncrementMonotonicallyAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_DifferentStreamIds_ShouldMaintainSeparateSequencesAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_WithoutGetNext_ShouldReturnNegativeOneAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetCurrentAsync_AfterGetNext_ShouldReturnLastIssuedSequenceAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ConcurrentCalls_ShouldMaintainMonotonicityAsync</tests>
  /// <tests>tests/Whizbang.Data.Tests/DapperSequenceProviderTests.cs:GetNextAsync_ManyCalls_ShouldNeverSkipOrDuplicateAsync</tests>
  public Task<T?> ExecuteScalarAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);
    return _executeScalarAsyncCoreAsync<T>(connection, sql, param, transaction, cancellationToken);
  }

  private static async Task<T?> _executeScalarAsyncCoreAsync<T>(
    IDbConnection connection,
    string sql,
    object? param,
    IDbTransaction? transaction,
    CancellationToken cancellationToken) {
    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.ExecuteScalarAsync<T>(connection, commandDefinition);
  }
}
