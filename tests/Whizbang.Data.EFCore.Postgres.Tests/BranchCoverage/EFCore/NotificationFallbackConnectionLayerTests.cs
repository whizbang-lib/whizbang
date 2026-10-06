// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="DbContextNotificationConnectionStringFallback"/>'s second layer:
/// a context configured with an unopened <see cref="NpgsqlConnection"/> yields that connection's
/// credential-bearing string; an unopened connection with no string, and a non-relational
/// provider with no connection at all, fall through to the last resort. Nothing is ever opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/DbContextNotificationConnectionStringFallback.cs</code-under-test>
[Category("Shard1")]
public class NotificationFallbackConnectionLayerTests {

  [Test]
  public async Task ConfiguredConnection_WithAString_YieldsItWithItsCredentialsAsync() {
    const string connectionString = "Host=layer-two.local;Database=db;Username=svc;Password=secret";
    await using var connection = new NpgsqlConnection(connectionString);

    var result = await _resolveAsync(o => o.UseNpgsql(connection));

    await Assert.That(result).Contains("Password=secret")
      .Because("before the connection is opened its string still carries the password the listener needs");
  }

  [Test]
  public async Task ConfiguredConnection_WithoutAString_FallsThroughToTheLastResortAsync() {
    await using var connection = new NpgsqlConnection();

    var result = await _resolveAsync(o => o.UseNpgsql(connection));

    await Assert.That(string.IsNullOrEmpty(result)).IsTrue()
      .Because("an empty connection supplies no credentials, so nothing usable comes back");
  }

  [Test]
  public async Task NonRelationalProvider_HasNoConnection_AndYieldsNothingAsync() {
    var result = await _resolveAsync(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));

    await Assert.That(result).IsNull()
      .Because("a provider with no relational options has no connection to recover from");
  }

  private static async Task<string?> _resolveAsync(Action<DbContextOptionsBuilder> configure) {
    var services = new ServiceCollection();
    services.AddDbContext<LayerContext>(configure);
    await using var sp = services.BuildServiceProvider();
    return new DbContextNotificationConnectionStringFallback(sp, typeof(LayerContext)).GetConnectionString();
  }

  /// <summary>A DbContext that maps nothing and is never opened.</summary>
  public sealed class LayerContext(DbContextOptions<LayerContext> options) : DbContext(options);
}
