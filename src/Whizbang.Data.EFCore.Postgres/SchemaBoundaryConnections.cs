using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;

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
  /// The scope the initializer was called from, used only when the context carries no data source of
  /// its own. Borrowed means used as-is and never disposed here, because the application owns its
  /// lifetime.
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

    // The context's own data source, which is what the turnkey registration configured it with and
    // the only thing here that still holds its credentials. Taken from the options rather than from
    // the container so that a caller which passed no scope still gets one, and so that a container
    // holding a different data source cannot be used to open the wrong database.
#pragma warning disable EF1001 // NpgsqlOptionsExtension is EF-internal, and there is no public way
    // to read the data source a context was configured with. The alternative is a connection string,
    // which Npgsql has already redacted the password from, so the choice is this or no credentials
    // at all. The maintenance pass reads it the same way for the same reason. A break here surfaces
    // as this returning null, which the caller reports rather than failing on.
    foreach (var extension in dbContext.GetService<IDbContextOptions>().Extensions) {
      if (extension is NpgsqlOptionsExtension npgsql && npgsql.DataSource is NpgsqlDataSource own) {
        return own.CreateConnection;
      }
    }
#pragma warning restore EF1001

    if (serviceProvider?.GetService(typeof(NpgsqlDataSource)) is NpgsqlDataSource registered) {
      return registered.CreateConnection;
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
