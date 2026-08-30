using System.Data;
using Microsoft.Data.Sqlite;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.Tests;

/// <summary>
/// Unit tests for the shipped <see cref="Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory"/>.
/// The production type is fully qualified throughout: this test project declares its own
/// <c>SqliteConnectionFactory</c> test double in the same namespace, which would otherwise
/// shadow it and silently exercise the double instead.
/// </summary>
public class DapperSqliteConnectionFactoryTests {
  private const string CONNECTION_STRING = "Data Source=:memory:";

  [Test]
  public async Task Constructor_WithNullConnectionString_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsSqliteConnectionAsync() {
    var factory = new Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection).IsTypeOf<SqliteConnection>();
  }

  [Test]
  public async Task CreateConnectionAsync_UsesConfiguredConnectionStringAsync() {
    var factory = new Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection.ConnectionString).IsEqualTo(CONNECTION_STRING);
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsUnopenedConnectionAsync() {
    var factory = new Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection connection = await factory.CreateConnectionAsync();

    await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed);
  }

  [Test]
  public async Task CreateConnectionAsync_ReturnsADistinctConnectionEachCallAsync() {
    var factory = new Whizbang.Data.Dapper.Sqlite.SqliteConnectionFactory(CONNECTION_STRING);

    using IDbConnection first = await factory.CreateConnectionAsync();
    using IDbConnection second = await factory.CreateConnectionAsync();

    await Assert.That(first).IsNotSameReferenceAs(second);
  }
}
