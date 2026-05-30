using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// End-to-end proof of the JDX SCRAM-SHA-256 fix.
///
/// <para>JDX configures its DbContext via <c>UseNpgsql(NpgsqlDataSource)</c>.
/// In that configuration Npgsql strips credentials from every public
/// ConnectionString surface (the connection's, the data source's, EF Core's
/// resolved string) — leaving no string-based recovery path. The notification
/// workers must hold a real <see cref="NpgsqlDataSource"/> and call
/// <see cref="NpgsqlDataSource.OpenConnectionAsync(CancellationToken)"/> on it,
/// because Npgsql keeps credentials internally for SCRAM auth.</para>
///
/// <para>This test reproduces the production wiring end-to-end:</para>
/// <list type="number">
///   <item><description>Build an <see cref="NpgsqlDataSource"/> and register
///   the DbContext via <c>UseNpgsql(dataSource)</c> (the JDX failure-mode
///   configuration).</description></item>
///   <item><description>Force the DbContext to open against the live test
///   container — this is the precondition that strips credentials from every
///   public ConnectionString surface.</description></item>
///   <item><description>Call <see cref="PostgresNotificationsServiceCollectionExtensions.AddWhizbangNotificationDataSource"/>
///   — the single line of glue JDX needs.</description></item>
///   <item><description>Resolve <see cref="PgCommitOrderStamperWorker"/>
///   through DI (so it picks up the registered
///   <see cref="INotificationDataSource"/> automatically — same path as
///   production) and start it.</description></item>
///   <item><description>Assert <c>OnBecameLeader</c> fires within ~5s. That
///   only happens if the worker successfully opened a credential-bearing
///   connection and acquired <c>pg_try_advisory_lock</c>. Pre-fix, the worker
///   would never become leader — it would emit
///   "PgCommitOrderStamperWorker iteration failed: No password has been
///   provided but the backend requires one (in SASL/SCRAM-SHA-256)" forever.</description></item>
/// </list>
///
/// <para>If this test ever regresses, JDX will hit the same SCRAM failure in
/// production. The test container uses the same SCRAM-SHA-256 auth method
/// Azure Postgres does, so a green here means a green there.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class NotificationDataSourceJdxConfigurationIntegrationTests : EFCoreTestBase {

  [Test]
  public async Task Stamper_AcquiresLeader_UnderUseNpgsqlDataSourceConfigurationAsync() {
    // 1. Build the EF Core-owned data source — exactly like JDX does.
    var efDataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<WorkCoordinationDbContext>(o => o.UseNpgsql(efDataSource));

    // 2. The fix: register a SEPARATE notification data source. JDX wires this
    // by passing the same connection string — the helper builds its own pool
    // (MaxPoolSize=4) so notification workers don't compete with EF Core's pool.
    services.AddWhizbangNotificationDataSource(ConnectionString);

    // 3. Standard notification + worker plumbing as a JDX-style host would do.
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(
        new ConfigurationBuilder().Build()));
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddOptions<WhizbangNotificationOptions>().Configure(o => {
      o.SignalingMode = WorkSignalingMode.ListenNotify;
    });
    services.AddOptions<CommitOrderStamperOptions>().Configure(o => {
      o.PollingInterval = TimeSpan.FromMilliseconds(100);
      o.LeaderElectionRetry = TimeSpan.FromMilliseconds(100);
      o.BatchSize = 100;
    });

    services.AddSingleton<PgSharedNotifyConnection>();
    services.AddSingleton<ISharedNotifyConnection>(sp => sp.GetRequiredService<PgSharedNotifyConnection>());
    services.AddSingleton<PgCommitOrderStamperWorker>();

    await using var sp = services.BuildServiceProvider();

    // 4. Open the DbContext against the live server — this is the precondition
    // that strips credentials from the EF Core data source's public string
    // surfaces (the production failure mode happens after this point).
    await using (var probeScope = sp.CreateAsyncScope()) {
      var ctx = probeScope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>();
      await ctx.Database.OpenConnectionAsync();
      _ = await new NpgsqlCommand("SELECT 1", (NpgsqlConnection)ctx.Database.GetDbConnection()).ExecuteScalarAsync();
    }

    // Sanity check: the EF Core data source's ConnectionString IS stripped.
    // Anyone trying to recover credentials from it gets a passwordless string.
    await Assert.That(efDataSource.ConnectionString.Contains("Password=", StringComparison.OrdinalIgnoreCase))
      .IsFalse()
      .Because("Npgsql strips credentials from NpgsqlDataSource.ConnectionString eagerly — there's no string recovery path");

    // 5. Resolve PgSharedNotifyConnection + PgCommitOrderStamperWorker through DI.
    // Both should automatically pick up INotificationDataSource and use it for
    // OpenConnectionAsync — bypassing the credential-stripping problem.
    var shared = sp.GetRequiredService<PgSharedNotifyConnection>();
    var stamper = sp.GetRequiredService<PgCommitOrderStamperWorker>();

    var leaderTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    stamper.OnBecameLeader += () => leaderTcs.TrySetResult();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await shared.StartAsync(cts.Token);
    await stamper.StartAsync(cts.Token);

    try {
      // 6. The proof: leader is acquired within 5s. Pre-fix this hangs forever
      // because every OpenAsync throws "No password has been provided".
      await leaderTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
      await Assert.That(stamper.IsLeader).IsTrue()
        .Because("worker must authenticate via INotificationDataSource and acquire the advisory lock");
    } finally {
      await stamper.StopAsync(CancellationToken.None);
      await shared.StopAsync(CancellationToken.None);
    }

    await efDataSource.DisposeAsync();
  }
}
