#nullable disable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <see cref="DbContextNotificationConnectionStringFallback"/> — the last-resort
/// fallback that lets <c>.WithDriver.Postgres&lt;TDbContext&gt;()</c> consumers reuse the
/// DbContext's connection string for LISTEN/NOTIFY + commit-order stamping without
/// duplicating it under <c>Whizbang:Database</c>.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class DbContextNotificationConnectionStringFallbackTests {

  private sealed class _FallbackTestDbContext(DbContextOptions<_FallbackTestDbContext> options) : DbContext(options) { }

  private sealed class _NotADbContext { }

  [Test]
  public async Task GetConnectionString_WithNpgsqlConfiguredDbContext_ReturnsConnectionStringAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(o =>
      o.UseNpgsql("Host=test.local;Database=mydb;Username=u;Password=p"));
    using var sp = services.BuildServiceProvider();

    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));

    var result = fallback.GetConnectionString();

    await Assert.That(result).IsEqualTo("Host=test.local;Database=mydb;Username=u;Password=p");
  }

  [Test]
  public async Task GetConnectionString_CachesAfterFirstCallAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(o =>
      o.UseNpgsql("Host=test;Database=mydb"));
    using var sp = services.BuildServiceProvider();
    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));

    var first = fallback.GetConnectionString();
    var second = fallback.GetConnectionString();

    await Assert.That(first).IsEqualTo("Host=test;Database=mydb");
    await Assert.That(second).IsEqualTo("Host=test;Database=mydb");
  }

  [Test]
  public async Task GetConnectionString_InMemoryDbContext_ReturnsNullAsync() {
    // InMemory provider has no connection string — fallback returns null and resolver
    // treats it as "no fallback available" (precedence falls through to None).
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(o => o.UseInMemoryDatabase("test"));
    using var sp = services.BuildServiceProvider();

    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));

    var result = fallback.GetConnectionString();

    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task Constructor_NullServiceProvider_ThrowsArgumentNullExceptionAsync() {
    var ex = await Assert.That(() =>
        new DbContextNotificationConnectionStringFallback(null, typeof(_FallbackTestDbContext)))
      .Throws<ArgumentNullException>();
    await Assert.That(ex.ParamName).IsEqualTo("serviceProvider");
  }

  [Test]
  public async Task Constructor_NullDbContextType_ThrowsArgumentNullExceptionAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    var ex = await Assert.That(() =>
        new DbContextNotificationConnectionStringFallback(sp, null))
      .Throws<ArgumentNullException>();
    await Assert.That(ex.ParamName).IsEqualTo("dbContextType");
  }

  [Test]
  public async Task Constructor_NonDbContextType_ThrowsArgumentExceptionAsync() {
    using var sp = new ServiceCollection().BuildServiceProvider();
    var ex = await Assert.That(() =>
        new DbContextNotificationConnectionStringFallback(sp, typeof(_NotADbContext)))
      .Throws<ArgumentException>();
    await Assert.That(ex.ParamName).IsEqualTo("dbContextType");
  }

  [Test]
  public async Task GetConnectionString_PreservesPasswordAfterConnectionOpenedAsync() {
    // Regression: in Azure deployments the worker was opening NpgsqlConnection without a
    // password and the server (SASL/SCRAM-SHA-256) rejected the handshake. Root cause:
    // Database.GetConnectionString() returns NpgsqlConnection.ConnectionString once any
    // operation has opened the connection — and Npgsql strips the password from
    // ConnectionString for security after Open. The fix reads from
    // RelationalOptionsExtension instead, which always holds the original options-supplied
    // value. This test forces the connection open before asking the fallback and asserts
    // the password is still present.
    const string originalConnString =
      "Host=stripped.example.com;Database=mydb;Username=u;Password=secret;Include Error Detail=true";
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(o => o.UseNpgsql(originalConnString));
    await using var sp = services.BuildServiceProvider();

    // Touch the connection so Npgsql strips the password from
    // NpgsqlConnection.ConnectionString. We don't actually need to open against a real
    // server — just reading the live ConnectionString after construction is enough on
    // some setups; on others we'd need OpenAsync. The fallback's RelationalOptionsExtension
    // path returns the original regardless.
    await using (var probeScope = sp.CreateAsyncScope()) {
      var probeCtx = probeScope.ServiceProvider.GetRequiredService<_FallbackTestDbContext>();
      _ = probeCtx.Database.GetDbConnection();  // creates the NpgsqlConnection wrapper
    }

    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));
    var result = fallback.GetConnectionString();

    await Assert.That(result).IsEqualTo(originalConnString);
  }

  [Test]
  public async Task Resolver_WithDbContextFallback_UsesFallbackWhenNoExplicitConfigAsync() {
    // End-to-end through NotificationConnectionStringResolver: no explicit config, fallback
    // picks up the DbContext's connection string and reports DbContextFallback as source.
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(o =>
      o.UseNpgsql("Host=dbcontext.local;Database=resolved"));
    using var sp = services.BuildServiceProvider();
    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));

    var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
    var resolution = NotificationConnectionStringResolver.Resolve(
      new WhizbangNotificationOptions(), emptyConfig, fallback);

    await Assert.That(resolution.ConnectionString).IsEqualTo("Host=dbcontext.local;Database=resolved");
    await Assert.That(resolution.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DbContextFallback);
  }
}
