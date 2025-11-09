using System.Data;
using Whizbang.Core.Data;
using DapperExtensions = Dapper;

namespace Whizbang.Data.Dapper.Custom;

/// <summary>
/// Dapper-based implementation of IDbExecutor.
/// Provides ORM-agnostic database operations using Dapper.
/// </summary>
public class DapperDbExecutor : IDbExecutor {
  public async Task<IReadOnlyList<T>> QueryAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);

    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    var results = await DapperExtensions.SqlMapper.QueryAsync<T>(connection, commandDefinition);
    return results.ToList();
  }

  public async Task<T?> QuerySingleOrDefaultAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);

    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.QuerySingleOrDefaultAsync<T>(connection, commandDefinition);
  }

  public async Task<int> ExecuteAsync(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);

    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.ExecuteAsync(connection, commandDefinition);
  }

  public async Task<T?> ExecuteScalarAsync<T>(
    IDbConnection connection,
    string sql,
    object? param = null,
    IDbTransaction? transaction = null,
    CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);

    var commandDefinition = new DapperExtensions.CommandDefinition(
      sql,
      param,
      transaction,
      cancellationToken: cancellationToken);

    return await DapperExtensions.SqlMapper.ExecuteScalarAsync<T>(connection, commandDefinition);
  }
}
