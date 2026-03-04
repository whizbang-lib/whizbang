using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for raw SQL execution and direct connection access on lens queries.
/// Use only when LINQ extensions are insufficient for complex queries.
/// </summary>
/// <docs>lenses/raw-sql</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LensQueryConnectionExtensionsTests.cs</tests>
/// <remarks>
/// <para>
/// These extension methods provide escape hatches for advanced scenarios:
/// <list type="bullet">
///   <item><description>Raw SQL queries with typed results</description></item>
///   <item><description>Stored procedure execution</description></item>
///   <item><description>Bulk operations</description></item>
///   <item><description>Database-specific features not exposed via LINQ</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Prefer LINQ when possible.</strong> Raw SQL bypasses EF Core's change tracking
/// and may be harder to maintain. Use these methods only when necessary.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Parameterized SQL (injection safe via FormattableString)
/// var category = "electronics";
/// var limit = 10;
/// var products = await lensQuery.ExecuteSqlAsync&lt;Product, ProductSummary&gt;(
///     $"SELECT id, name, price FROM products WHERE category = {category} LIMIT {limit}");
///
/// // Direct connection for stored procedures
/// await using var connection = await lensQuery.GetConnectionAsync();
/// await using var command = connection.CreateCommand();
/// command.CommandText = "CALL refresh_materialized_view('product_stats')";
/// await command.ExecuteNonQueryAsync();
/// </code>
/// </example>
public static class LensQueryConnectionExtensions {
  /// <summary>
  /// Executes a raw SQL query and returns typed results.
  /// Uses FormattableString for parameterized queries (SQL injection safe).
  /// </summary>
  /// <typeparam name="TModel">The lens query model type (for context resolution)</typeparam>
  /// <typeparam name="TResult">The result type to project into</typeparam>
  /// <param name="lensQuery">The lens query to execute against</param>
  /// <param name="sql">Parameterized SQL using string interpolation</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>List of typed results</returns>
  /// <exception cref="ArgumentNullException">Thrown when sql is null</exception>
  /// <exception cref="InvalidOperationException">Thrown when lens query doesn't support raw SQL</exception>
  /// <remarks>
  /// <para>
  /// The FormattableString parameter allows safe parameterized queries:
  /// <code>
  /// var results = await lensQuery.ExecuteSqlAsync&lt;Order, OrderSummary&gt;(
  ///     $"SELECT id, total FROM orders WHERE status = {status}");
  /// </code>
  /// The interpolated values ({status}) become SQL parameters, not string concatenation.
  /// </para>
  /// </remarks>
  public static Task<List<TResult>> ExecuteSqlAsync<TModel, TResult>(
      this ILensQuery<TModel> lensQuery,
      FormattableString sql,
      CancellationToken cancellationToken = default)
      where TModel : class
      where TResult : class {
    ArgumentNullException.ThrowIfNull(lensQuery);
    ArgumentNullException.ThrowIfNull(sql);

    // Implementation requires access to DbContext from the lens query
    // The actual implementation depends on the concrete lens query type
    if (lensQuery is IDbContextAccessor accessor) {
      return _executeWithContextAsync<TResult>(accessor.DbContext, sql, cancellationToken);
    }

    throw new InvalidOperationException(
        "Raw SQL execution requires an ILensQuery implementation that provides DbContext access. " +
        "Ensure you are using EFCorePostgresLensQuery or a compatible implementation.");
  }

  /// <summary>
  /// Gets the underlying database connection synchronously.
  /// Connection is scoped to the current transaction/request.
  /// </summary>
  /// <typeparam name="TModel">The lens query model type (for context resolution)</typeparam>
  /// <param name="lensQuery">The lens query to get connection from</param>
  /// <returns>The underlying DbConnection</returns>
  /// <exception cref="InvalidOperationException">Thrown when lens query doesn't support connection access</exception>
  /// <remarks>
  /// <para>
  /// The returned connection is managed by EF Core. Do NOT dispose it manually.
  /// Use this for scenarios where you need direct database access, such as:
  /// <list type="bullet">
  ///   <item><description>Stored procedure calls</description></item>
  ///   <item><description>Bulk operations via Npgsql binary import</description></item>
  ///   <item><description>Database-specific commands</description></item>
  /// </list>
  /// </para>
  /// </remarks>
  public static DbConnection GetConnection<TModel>(
      this ILensQuery<TModel> lensQuery)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(lensQuery);

    if (lensQuery is IDbContextAccessor accessor) {
      return accessor.DbContext.Database.GetDbConnection();
    }

    throw new InvalidOperationException(
        "Connection access requires an ILensQuery implementation that provides DbContext access. " +
        "Ensure you are using EFCorePostgresLensQuery or a compatible implementation.");
  }

  /// <summary>
  /// Gets the underlying database connection asynchronously.
  /// Opens the connection if not already open.
  /// </summary>
  /// <typeparam name="TModel">The lens query model type (for context resolution)</typeparam>
  /// <param name="lensQuery">The lens query to get connection from</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The underlying DbConnection (opened)</returns>
  /// <exception cref="InvalidOperationException">Thrown when lens query doesn't support connection access</exception>
  /// <remarks>
  /// <para>
  /// Unlike <see cref="GetConnection{TModel}"/>, this method ensures the connection
  /// is open before returning. Use this when you need immediate database access.
  /// </para>
  /// </remarks>
  public static async Task<DbConnection> GetConnectionAsync<TModel>(
      this ILensQuery<TModel> lensQuery,
      CancellationToken cancellationToken = default)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(lensQuery);

    if (lensQuery is IDbContextAccessor accessor) {
      var connection = accessor.DbContext.Database.GetDbConnection();
      if (connection.State != System.Data.ConnectionState.Open) {
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      }
      return connection;
    }

    throw new InvalidOperationException(
        "Connection access requires an ILensQuery implementation that provides DbContext access. " +
        "Ensure you are using EFCorePostgresLensQuery or a compatible implementation.");
  }

  private static async Task<List<TResult>> _executeWithContextAsync<TResult>(
      DbContext context,
      FormattableString sql,
      CancellationToken cancellationToken)
      where TResult : class {
    // EF Core's FromSqlInterpolated handles parameter extraction from FormattableString
    // This provides SQL injection protection while allowing natural interpolation syntax
    return await context.Database
        .SqlQuery<TResult>(sql)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);
  }
}

/// <summary>
/// Internal interface for lens queries that provide DbContext access.
/// Implemented by EFCorePostgresLensQuery to enable raw SQL operations.
/// </summary>
internal interface IDbContextAccessor {
  /// <summary>
  /// Gets the underlying DbContext for raw database operations.
  /// </summary>
  DbContext DbContext { get; }
}
