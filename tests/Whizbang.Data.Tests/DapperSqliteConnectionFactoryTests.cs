using System.Data;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Sqlite;

namespace Whizbang.Data.Tests;

/// <summary>
/// Unit tests for <see cref="SqliteConnectionFactory"/>. The factory hands back an
/// unopened connection, so these assertions stay in-process and touch no database.
/// </summary>
public class DapperSqliteConnectionFactoryTests {
  private const string CONNECTION_STRING = "Data Source=:memory:";

  [Test]
  public async Task Constructor_WithNullConnectionString_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new SqliteConnectionFactory(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsSqliteConnectionAsync() {
    var factory = new SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection).IsTypeOf<SqliteConnection>();
  }

  [Test]
  public async Task CreateConnectionAsync_UsesConfiguredConnectionStringAsync() {
    var factory = new SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection.ConnectionString).IsEqualTo(CONNECTION_STRING);
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsUnopenedConnectionAsync() {
    var factory = new SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsADistinctConnectionEachCallAsync() {
    var factory = new SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection first = await factory.CreateConnectionAsync();
    using IDbConnection second = await factory.CreateConnectionAsync();

    await Assert.That(first).IsNotSameReferenceAs(second);
  }
}
