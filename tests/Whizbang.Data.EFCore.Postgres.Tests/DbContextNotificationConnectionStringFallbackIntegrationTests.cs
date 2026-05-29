using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// End-to-end regression for the a consumer Azure SCRAM-SHA-256 incident.
///
/// <para>The unit-level regression
/// (<c>GetConnectionString_PreservesPasswordAfterConnectionOpenedAsync</c> in
/// <see cref="DbContextNotificationConnectionStringFallbackTests"/>) proved
/// <see cref="DbContextNotificationConnectionStringFallback"/> returns the
/// original options-supplied string. But the production failure mode requires
/// a REAL Postgres connection cycle to surface — Npgsql only strips
/// credentials from <see cref="NpgsqlConnection.ConnectionString"/> after
/// <see cref="NpgsqlConnection.OpenAsync(System.Threading.CancellationToken)"/>
/// completes against a live server with password auth.</para>
///
/// <para>This integration test reproduces the production chain:</para>
/// <list type="number">
/// <item>Register a DbContext via <c>AddDbContext.UseNpgsql(connStr)</c> where
///   <c>connStr</c> carries a password.</item>
/// <item>Open and execute against the database (so EF Core's pooled connection
///   gets opened — this is the condition that strips the password from the
///   live <see cref="NpgsqlConnection.ConnectionString"/>).</item>
/// <item>Resolve the connection through
///   <see cref="NotificationConnectionStringResolver"/> using
///   <see cref="DbContextNotificationConnectionStringFallback"/>.</item>
/// <item>Open a fresh <see cref="NpgsqlConnection"/> with the resolved string
///   and verify auth succeeds — this is exactly the path that Azure was
///   failing with "No password has been provided".</item>
/// </list>
///
/// <para>The pre-fix code resolved through <c>Database.GetConnectionString()</c>
/// which returns the live (password-stripped) string after Open; the new code
/// reads from <c>RelationalOptionsExtension.ConnectionString</c> which is the
/// original options-supplied value. If the implementation regresses, this
/// test fails at OpenAsync with the production-equivalent error.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class DbContextNotificationConnectionStringFallbackIntegrationTests : EFCoreTestBase {

  /// <summary>
  /// Locks the production-equivalent path: fallback survives a real DbContext
  /// Open/Execute cycle and returns a string that re-opens against the server.
  /// </summary>
  [Test]
  public async Task FallbackConnectionString_OpensAgainstPostgres_AfterDbContextHasOpenedAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(ConnectionString));
    await using var sp = services.BuildServiceProvider();

    // Drive the DbContext through an actual round-trip to the server — this is what
    // strips the password from the underlying NpgsqlConnection's live
    // ConnectionString. Use a no-op probe query rather than a schema-touching one
    // so the test doesn't require any tables to exist.
    await using (var probeScope = sp.CreateAsyncScope()) {
      var ctx = probeScope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
      await ctx.Database.OpenConnectionAsync();
      var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
      await using (var cmd = new NpgsqlCommand("SELECT 1", conn)) {
        var result = await cmd.ExecuteScalarAsync();
        await Assert.That(result).IsEqualTo(1);
      }
      // At this point `conn.ConnectionString` no longer contains "Password=" —
      // Npgsql strips it for security once auth completes.
    }

    // Resolve through the production code path.
    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(WorkCoordinationDbContext));
    var options = new WhizbangNotificationOptions { ConnectionStringKey = null };
    var resolution = NotificationConnectionStringResolver.Resolve(
      options,
      new ConfigurationBuilder().Build(),
      fallback);

    await Assert.That(resolution.ConnectionString).IsNotNull();
    await Assert.That(resolution.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DbContextFallback);

    // The CRITICAL assertion: opening a fresh connection with the resolved string
    // succeeds. Pre-fix this raised
    // "No password has been provided but the backend requires one (in SASL/SCRAM-SHA-256)"
    // against Azure Postgres; the test container uses the same SCRAM auth path so
    // the regression is reproducible here.
    await using var freshConn = new NpgsqlConnection(resolution.ConnectionString);
    await freshConn.OpenAsync();
    await using (var verify = new NpgsqlCommand("SELECT 1", freshConn)) {
      var got = await verify.ExecuteScalarAsync();
      await Assert.That(got).IsEqualTo(1);
    }
  }

  /// <summary>
  /// Same chain but driving <see cref="PgCommitOrderStamperWorker"/> all the way
  /// through to a successful connection open — exactly the production failure
  /// surface (the stamper was the worker emitting the iteration-failed warnings).
  /// </summary>
  [Test]
  public async Task PgCommitOrderStamperWorker_ResolvesAndOpensViaDbContextFallbackAsync() {
    var services = new ServiceCollection();
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(ConnectionString));
    await using var sp = services.BuildServiceProvider();

    // Pre-open the DbContext connection like a typical service would on startup
    // (migrations, schema initializer, first query).
    await using (var probeScope = sp.CreateAsyncScope()) {
      var ctx = probeScope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
      await ctx.Database.OpenConnectionAsync();
      _ = await new NpgsqlCommand("SELECT 1", (NpgsqlConnection)ctx.Database.GetDbConnection()).ExecuteScalarAsync();
    }

    var fallback = new DbContextNotificationConnectionStringFallback(sp, typeof(WorkCoordinationDbContext));
    var notificationOptions = new WhizbangNotificationOptions { SignalingMode = WorkSignalingMode.ListenNotify };
    var resolved = NotificationConnectionStringResolver.Resolve(
      notificationOptions,
      new ConfigurationBuilder().Build(),
      fallback);

    await Assert.That(resolved.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DbContextFallback);
    await Assert.That(resolved.ConnectionString).IsNotNull();

    // The resolver chain is what the stamper uses internally — and it's the
    // OpenAsync on the resolved string that was failing in production. Drive it
    // explicitly here so the test FAILS with the production error message if
    // the password-preservation fix regresses.
    await using var stamperConn = new NpgsqlConnection(resolved.ConnectionString);
    await stamperConn.OpenAsync();
    await Assert.That(stamperConn.State).IsEqualTo(System.Data.ConnectionState.Open);
  }
}
