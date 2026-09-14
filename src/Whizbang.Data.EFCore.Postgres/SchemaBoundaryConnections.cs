using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Finds a way to open a connection for schema SQL that has to be applied across a commit boundary.
/// </summary>
/// <remarks>
/// <para>
/// Schema SQL carrying a commit boundary is applied on a connection of its own, so the initializer
/// needs one it can open independently of the one its transaction is running on. Getting that wrong
/// is quiet: the initializer falls back to applying the script whole, which succeeds on every
/// database that has nothing to rewrite, and fails only on the databases the boundary exists for.
/// </para>
/// <para>
/// A connection string is the obvious answer and usually the wrong one. Npgsql redacts the password
/// from every <c>ConnectionString</c> surface once a connection has opened, and the turnkey
/// registration configures the context with an <c>NpgsqlDataSource</c> rather than a string, so
/// there was never a string to redact. The data source is what still holds the credentials.
/// </para>
/// <para>
/// This lives here, rather than inside the generated initializer, so the order it prefers things in
/// is a decision a test can pin rather than one that can only be observed by deploying.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/SchemaBoundaryConnectionsTests.cs</tests>
public static class SchemaBoundaryConnections {
  /// <summary>
  /// A factory producing fresh, unopened connections, or <see langword="null"/> when none of the
  /// sources can provide one.
  /// </summary>
  /// <param name="dbContext">The context being initialized.</param>
  /// <param name="initConnectionString">
  /// The initialization connection string, when the host supplied one. Preferred over everything
  /// else because it addresses PostgreSQL directly rather than through a pooler, which is what the
  /// rest of the schema pass already prefers.
  /// </param>
  /// <param name="serviceProvider">
  /// The scope the initializer was called from, used to borrow the application's
  /// <see cref="NpgsqlDataSource"/>. Borrowed means used as-is and never disposed here, because the
  /// application owns its lifetime.
  /// </param>
  /// <returns>
  /// The factory, or <see langword="null"/>. A null answer is a real possibility rather than a
  /// defect: a context can be configured with a data source this scope cannot see. The caller
  /// reports it and applies the script whole, which is correct for a database with nothing to
  /// rewrite and fails loudly for one that has something.
  /// </returns>
  public static Func<NpgsqlConnection>? Resolve(
      DbContext dbContext,
      string? initConnectionString,
      IServiceProvider? serviceProvider) {
    ArgumentNullException.ThrowIfNull(dbContext);

    if (!string.IsNullOrWhiteSpace(initConnectionString)) {
      var direct = initConnectionString;
      return () => new NpgsqlConnection(direct);
    }

    if (serviceProvider?.GetService(typeof(NpgsqlDataSource)) is NpgsqlDataSource dataSource) {
      return dataSource.CreateConnection;
    }

    foreach (var extension in dbContext.GetService<IDbContextOptions>().Extensions) {
      if (extension is RelationalOptionsExtension relational
          && !string.IsNullOrWhiteSpace(relational.ConnectionString)) {
        var configured = relational.ConnectionString;
        return () => new NpgsqlConnection(configured);
      }
    }

    return null;
  }
}
