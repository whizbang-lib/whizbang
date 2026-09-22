using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Where the initializer gets a second connection from, in the order it prefers them.
/// </summary>
/// <remarks>
/// <para>
/// Schema SQL carrying a commit boundary is applied on a connection of its own. Failing to find one
/// is quiet: the initializer applies the script whole instead, which succeeds on every database
/// that has nothing to rewrite and fails only on the databases the boundary exists for. So a
/// resolution that silently answers nothing looks like a working build until it meets real data.
/// </para>
/// <para>
/// The case that matters is the data source the context was configured with. A connection string is
/// the obvious source and usually absent: Npgsql redacts the password from every
/// <c>ConnectionString</c> surface once a connection has opened, and the turnkey registration
/// configures the context with an <c>NpgsqlDataSource</c> rather than a string, so there was never a
/// string to redact. A resolution that looked only at strings found nothing on every ordinary
/// deployment, and one that looked only in the container found nothing whenever the caller passed
/// no scope, which is every caller that takes the default.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#statements-that-need-a-commit-between-them</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class SchemaBoundaryConnectionsTests : IAsyncDisposable {
  private NpgsqlDataSource _dataSource = null!;
  private string _connectionString = null!;

  /// <summary>A container that resolves exactly one thing, so what is asked for is unambiguous.</summary>
  private sealed class OneServiceProvider(Type type, object instance) : IServiceProvider {
    public object? GetService(Type serviceType) => serviceType == type ? instance : null;
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _connectionString = SharedPostgresContainer.ConnectionString;
    _dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
  }

  [After(Test)]
  public async ValueTask DisposeAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
    }

    GC.SuppressFinalize(this);
  }

  /// <summary>A context configured the way the turnkey registration configures one.</summary>
  private WorkCoordinationDbContext _dataSourceContext() =>
    new(new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseNpgsql(_dataSource)
      .Options);

  /// <summary>A context configured with a string, which some hosts still do.</summary>
  private WorkCoordinationDbContext _connectionStringContext() =>
    new(new DbContextOptionsBuilder<WorkCoordinationDbContext>()
      .UseNpgsql(_connectionString)
      .Options);

  /// <summary>The factory opens a connection that actually authenticates.</summary>
  private static async Task _canOpenAsync(Func<NpgsqlConnection>? factory) {
    // Compared as a bool: Assert.That(aDelegate) binds to the overload that INVOKES it, so it
    // would assert about the connection it produced rather than about the factory being there.
    await Assert.That(factory is not null).IsTrue();
    await using var connection = factory!();
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand("SELECT 1", connection);
    await Assert.That(await command.ExecuteScalarAsync()).IsEqualTo(1);
  }

  /// <summary>
  /// A context configured with a data source yields a usable connection with no scope at all.
  /// </summary>
  /// <remarks>
  /// The regression, and the reason the context's own data source is preferred to the container's:
  /// this is how the turnkey registration configures every context, and the caller frequently has
  /// no scope to offer. A resolution that needed one failed on every ordinary deployment while the
  /// test suite stayed green.
  /// </remarks>
  [Test]
  public async Task TheContextsOwnDataSourceIsUsedWithNoScopeAsync() {
    await using var context = _dataSourceContext();

    await _canOpenAsync(SchemaBoundaryConnections.Resolve(
      context, initConnectionString: null, serviceProvider: null));
  }

  /// <summary>
  /// A data source in the scope is used when the context carries none of its own.
  /// </summary>
  [Test]
  public async Task AScopeDataSourceIsBorrowedWhenTheContextHasNoneAsync() {
    await using var context = _connectionStringContext();

    await _canOpenAsync(SchemaBoundaryConnections.Resolve(
      context, initConnectionString: null,
      new OneServiceProvider(typeof(NpgsqlDataSource), _dataSource)));
  }

  /// <summary>
  /// The initialization connection string wins over the data source when the host supplied one.
  /// </summary>
  /// <remarks>
  /// It addresses PostgreSQL directly rather than through a pooler, which is what the rest of the
  /// schema pass already prefers, and a rewrite of a whole table is the last thing that should go
  /// through a pooler's timeouts.
  /// </remarks>
  [Test]
  public async Task TheInitializationStringIsPreferredAsync() {
    await using var context = _dataSourceContext();
    var marked = new NpgsqlConnectionStringBuilder(_connectionString) {
      ApplicationName = "init-string-wins",
    }.ConnectionString;

    var factory = SchemaBoundaryConnections.Resolve(
      context, marked, new OneServiceProvider(typeof(NpgsqlDataSource), _dataSource));

    await Assert.That(factory is not null).IsTrue();
    await using var connection = factory!();
    await Assert.That(connection.ConnectionString).Contains(
      "init-string-wins", StringComparison.Ordinal);
  }

  /// <summary>
  /// A context configured with a string yields one even when the scope holds no data source.
  /// </summary>
  [Test]
  public async Task AConfiguredConnectionStringIsUsedWhenThereIsNoDataSourceAsync() {
    await using var context = _connectionStringContext();

    await _canOpenAsync(SchemaBoundaryConnections.Resolve(
      context, initConnectionString: null, serviceProvider: null));
  }

  /// <summary>
  /// A context carrying neither a data source nor a connection string answers nothing.
  /// </summary>
  /// <remarks>
  /// Asserted because the caller's two behaviors are different and both matter: no factory makes it
  /// warn and apply the script whole, which is correct for a database with nothing to rewrite, while
  /// a factory that cannot open would fail the whole schema pass. A resolution that returned
  /// something unusable here would turn a warning into an outage.
  /// </remarks>
  [Test]
  public async Task NothingAvailableAnswersNothingAsync() {
    await using var context = new WorkCoordinationDbContext(
      new DbContextOptionsBuilder<WorkCoordinationDbContext>().UseNpgsql().Options);

    var factory = SchemaBoundaryConnections.Resolve(
      context, initConnectionString: null, serviceProvider: null);

    await Assert.That(factory is null).IsTrue();
  }

  /// <summary>Whitespace is not an initialization connection string.</summary>
  [Test]
  [Arguments("")]
  [Arguments("   ")]
  public async Task AnEmptyInitializationStringIsIgnoredAsync(string initConnectionString) {
    await using var context = _dataSourceContext();

    await _canOpenAsync(SchemaBoundaryConnections.Resolve(
      context, initConnectionString,
      new OneServiceProvider(typeof(NpgsqlDataSource), _dataSource)));
  }

  /// <summary>A missing context is a caller error.</summary>
  [Test]
  public async Task ANullContextIsRefusedAsync() =>
    await Assert.That(() => SchemaBoundaryConnections.Resolve(null!, null, null))
      .Throws<ArgumentNullException>();
}
