using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>Under <c>UseNpgsql(NpgsqlDataSource)</c> Npgsql redacts the password from every
/// <c>ConnectionString</c> surface, so the notification workers cannot recover credentials from a
/// string. Auto-discovery of <see cref="INotificationDataSource"/> walked
/// <c>IConfiguration:ConnectionStrings</c> for a credential-bearing entry and stopped there — a
/// consumer that supplies its data source in code (no <c>ConnectionStrings</c> section at all) got
/// <c>DataSource = null</c>, every worker fell to the redacted string, and the duty elector failed
/// SASL authentication inside the startup pipeline.</para>
/// <para>The application's own data source already holds the credentials. When configuration has
/// nothing usable, auto-discovery reuses it — borrowed, never disposed by the notification stack.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Shard1")]
public class NotificationDataSourceAutoDiscoveryTests {
  private const string CREDENTIAL_BEARING = "Host=localhost;Username=tenant-user;Password=secret-value";

  private static ServiceCollection _host(Dictionary<string, string?>? settings = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration));
    services.AddWhizbangPostgresNotifications();
    return services;
  }

  [Test]
  public async Task AutoDiscovery_NoCredentialInConfiguration_ReusesTheApplicationsDataSourceAsync() {
    await using var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var services = _host();
    services.AddSingleton(applicationDataSource);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsSameReferenceAs(applicationDataSource)
      .Because("the application's data source is the only thing in the host that still holds the "
             + "password; falling to a redacted string is the SASL failure the elector reported");
  }

  [Test]
  public async Task AutoDiscovery_ReusedDataSource_IsBorrowedNotOwnedAsync() {
    var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var services = _host();
    services.AddSingleton(applicationDataSource);
    var provider = services.BuildServiceProvider();
    _ = provider.GetRequiredService<INotificationDataSource>();

    await provider.DisposeAsync();

    // A disposed NpgsqlDataSource refuses to hand out connections; the application's pool must
    // survive the notification stack's disposal.
    using var stillUsable = applicationDataSource.CreateConnection();
    await Assert.That(stillUsable).IsNotNull()
      .Because("the notification stack borrowed the data source; disposing the host must not "
             + "dispose the application's connection pool underneath EF Core");
    await applicationDataSource.DisposeAsync();
  }

  [Test]
  public async Task AutoDiscovery_CredentialInConfiguration_BuildsADedicatedDataSourceAsync() {
    await using var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var services = _host(new Dictionary<string, string?> {
      ["ConnectionStrings:Main"] = CREDENTIAL_BEARING,
    });
    services.AddSingleton(applicationDataSource);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNotNull();
    await Assert.That(ReferenceEquals(notification.DataSource, applicationDataSource)).IsFalse()
      .Because("a credential-bearing configuration entry wins: the dedicated pool keeps the "
             + "LISTEN and advisory-lock connections out of EF Core's pool, as before");
  }

  [Test]
  public async Task AutoDiscovery_NothingUsable_LeavesTheDataSourceNullAsync() {
    var services = _host();
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNull()
      .Because("with no configuration and no application data source there is nothing to borrow; "
             + "the workers fall to the string path and its operator-actionable diagnostic");
  }

  [Test]
  public async Task AutoDiscovery_DbContextDataSourceFallback_WinsOverADiRegisteredDataSourceAsync() {
    // The driver surfaces the DbContext's OWN data source (UseNpgsql(NpgsqlDataSource)) through
    // INotificationDataSourceFallback. It is the one EF Core actually authenticates with, so it
    // outranks a data source that merely happens to be registered in DI.
    await using var dbContextDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    await using var otherDataSource = NpgsqlDataSource.Create("Host=other.local;Username=tenant-user;Password=other-value");
    var services = _host();
    services.AddSingleton(otherDataSource);
    services.AddSingleton<INotificationDataSourceFallback>(new FixedDataSourceFallback(dbContextDataSource));
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsSameReferenceAs(dbContextDataSource);
  }

  [Test]
  public async Task AutoDiscovery_DbContextDataSourceFallbackReturnsNull_FallsThroughToDiAsync() {
    // A string-configured DbContext has no data source to lend; the fallback answers null and the
    // DI-registered data source is the next candidate.
    await using var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var services = _host();
    services.AddSingleton(applicationDataSource);
    services.AddSingleton<INotificationDataSourceFallback>(new FixedDataSourceFallback(null));
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsSameReferenceAs(applicationDataSource);
  }

  [Test]
  public async Task AutoDiscovery_ReusingTheApplicationsDataSource_SaysSoOnceAsync() {
    // The choice is the fact an operator needs when a LISTEN connection later fails to
    // authenticate: which source did the notification stack end up with? Silence here would make
    // that failure unreadable.
    await using var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var sink = new List<string>();
    var services = _host();
    services.AddLogging(b => b.AddProvider(new ListLoggerProvider(sink)));
    services.AddSingleton(applicationDataSource);
    await using var provider = services.BuildServiceProvider();

    _ = provider.GetRequiredService<INotificationDataSource>();

    List<string> lines;
    lock (sink) { lines = [.. sink]; }
    await Assert.That(lines.Count(l => l.Contains("reusing the application's own NpgsqlDataSource", StringComparison.Ordinal))).IsEqualTo(1)
      .Because("the borrow is reported exactly once, at resolution, with the remedies for a dedicated pool");
  }

  [Test]
  public async Task AutoDiscovery_ReuseWorksWithoutLoggingRegisteredAsync() {
    // A host with no ILoggerFactory at all still gets the borrowed data source; logging is a
    // convenience, never a prerequisite for the credential path.
    await using var applicationDataSource = NpgsqlDataSource.Create(CREDENTIAL_BEARING);
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration));
    services.AddWhizbangPostgresNotifications();
    services.AddSingleton(applicationDataSource);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsSameReferenceAs(applicationDataSource);
  }

  private sealed class FixedDataSourceFallback(NpgsqlDataSource? dataSource) : INotificationDataSourceFallback {
    public NpgsqlDataSource? GetDataSource() => dataSource;
  }

  private sealed class ListLoggerProvider(List<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider {
    private readonly List<string> _sink = sink;

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new ListLogger(_sink);

    public void Dispose() {
      // Nothing to release — the sink is owned by the test.
    }

    private sealed class ListLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger {
      private readonly List<string> _sink = sink;

      public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
      public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
      public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        lock (_sink) {
          _sink.Add(formatter(state, exception));
        }
      }
    }
  }
}
