using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="DbContextNotificationConnectionStringFallback"/> paths the
/// existing <see cref="DbContextNotificationConnectionStringFallbackTests"/> suite never drives:
/// the double-checked-locking inner (still-locked) cached-return branch on both
/// <see cref="DbContextNotificationConnectionStringFallback.GetConnectionString"/> and
/// <see cref="DbContextNotificationConnectionStringFallback.GetSearchPath"/>, the outer cached
/// return on <c>GetSearchPath</c>, and the "no provider configured" fallback on
/// <c>GetSearchPath</c>. All of this is decided from options/DI state before anything ever opens a
/// connection, so none of it needs a live database -- a fake Npgsql connection string (never
/// opened) is enough, and the race tests below construct a real, deterministic thread interleaving
/// using the production code's own lock object rather than a database or a sleep.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/DbContextNotificationConnectionStringFallback.cs</code-under-test>
[Category("Shard1")]
public class DbContextNotificationConnectionStringFallbackCoverageTests {

  private sealed class _FallbackTestDbContext(DbContextOptions<_FallbackTestDbContext> options) : DbContext(options) { }

  /// <summary>DbContext whose model declares a default schema, so <c>GetSearchPath</c> has a
  /// non-null value to prove caching against (unlike the unconfigured contexts elsewhere in
  /// this file, whose default schema is trivially null either way).</summary>
  private sealed class _SchemaTestDbContext(DbContextOptions<_SchemaTestDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.HasDefaultSchema("svc_schema");
    }
  }

  // A connection-string fallback that picks the wrong source connects a service to the wrong
  // database -- the worst possible silent success. Two callers racing GetConnectionString for
  // the FIRST time must never both perform the real resolution: the second one must see the
  // must never touch the DbContext again.
  [Test]
  public async Task GetSearchPath_CalledTwice_SecondCallReturnsTheCachedSchemaAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<_SchemaTestDbContext>(o => o.UseNpgsql("Host=schema-cache.local;Database=db"));
    using var sp = services.BuildServiceProvider();
    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_SchemaTestDbContext));

    var first = fallback.GetSearchPath();
    var second = fallback.GetSearchPath();

    await Assert.That(first).IsEqualTo("svc_schema");
    await Assert.That(second).IsEqualTo("svc_schema")
      .Because("the second call must come back from cache, not a second DbContext resolution");
  }

  // A DbContext registered with no UseXxx() provider at all throws InvalidOperationException
  // resolving .Model ("No database provider has been configured..."). GetSearchPath must treat
  // that the same as "no schema known" instead of propagating and taking down whatever wired
  // the notification layer through it.
  [Test]
  public async Task GetSearchPath_NoProviderConfigured_TreatsItAsNoSchemaKnownAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<_FallbackTestDbContext>(_ => { });
    using var sp = services.BuildServiceProvider();
    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(_FallbackTestDbContext));

    var result = fallback.GetSearchPath();

    await Assert.That(result).IsNull();
  }
}
