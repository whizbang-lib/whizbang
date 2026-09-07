using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PostgresNotificationsServiceCollectionExtensions"/> paths
/// that <see cref="NotificationDataSourceAutoDiscoveryTests"/> doesn't reach: walking past a
/// blank or non-credential-bearing <c>ConnectionStrings</c> entry during auto-discovery, the
/// argument guard on <see cref="PostgresNotificationsServiceCollectionExtensions.AddWhizbangNotificationDataSource"/>,
/// and the resolved search path actually landing on the data source it builds. None of these need
/// a live Postgres -- <c>NpgsqlDataSourceBuilder.Build()</c> only parses/builds a pool descriptor,
/// it never connects.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Shard1")]
public class PostgresNotificationsServiceCollectionExtensionsCoverageTests {

  private static ServiceCollection _host(Dictionary<string, string?>? settings = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration));
    services.AddWhizbangPostgresNotifications();
    return services;
  }

  // An empty ConnectionStrings entry must be skipped, not treated as "nothing left to check" --
  // a real credential could still be sitting behind it.
  [Test]
  public async Task AutoDiscovery_WalksPastABlankConnectionStringsEntryAsync() {
    var services = _host(new Dictionary<string, string?> {
      ["ConnectionStrings:Blank"] = "",
    });
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNull()
      .Because("the only ConnectionStrings entry is blank and nothing else is registered to borrow, so auto-discovery correctly finds nothing usable");
  }

  // A non-empty connection string that carries no Username=/Password=/Pwd= marker doesn't
  // qualify as credential-bearing. Stopping at the first non-empty entry instead of walking past
  // it would mean a later, genuinely credential-bearing entry never gets a chance.
  [Test]
  public async Task AutoDiscovery_WalksPastAConnectionStringWithNoCredentialMarkerAsync() {
    var services = _host(new Dictionary<string, string?> {
      ["ConnectionStrings:NoCredential"] = "Host=localhost;Database=coverage",
    });
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNull()
      .Because("a connection string with no credential marker doesn't qualify, so auto-discovery must keep walking rather than mistakenly adopting it");
  }

  // A blank connection string can never resolve to a real connection; failing at registration
  // time is far cheaper than failing the first time a notification worker tries to open it.
  [Test]
  public async Task AddWhizbangNotificationDataSource_WithWhitespaceConnectionString_ThrowsArgumentExceptionAsync() {
    var services = new ServiceCollection();

    await Assert.That(() => services.AddWhizbangNotificationDataSource("   "))
      .Throws<ArgumentException>()
      .Because("a blank connection string can never resolve to a real connection");
  }

  // A resolved search path must actually land on the connection the notification workers use --
  // otherwise every unqualified table/function reference (record_capability, wh_signals, ...)
  // resolves against the wrong schema in a multi-schema deployment.
  [Test]
  public async Task AddWhizbangNotificationDataSource_WithSearchPathOption_AppliesItToTheBuiltDataSourceAsync() {
    const string connectionString = "Host=localhost;Username=tenant-user;Password=secret-value";
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration));
    services.Configure<WhizbangNotificationOptions>(o => o.SearchPath = "tenant_schema_zzz");
    services.AddWhizbangNotificationDataSource(connectionString);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNotNull();
    await Assert.That(notification.DataSource!.ConnectionString).Contains("tenant_schema_zzz")
      .Because("the resolved search path must reach the connection string the built data source actually uses");
  }
}
