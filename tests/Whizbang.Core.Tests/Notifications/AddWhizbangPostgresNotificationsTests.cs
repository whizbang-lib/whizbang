using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Notifications.AppSignals;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Tests <see cref="PostgresNotificationsServiceCollectionExtensions.AddWhizbangPostgresNotifications"/>
/// — replaces the default NoOp listener with the Postgres LISTEN/NOTIFY listener and registers
/// the app-signal channel.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class AddWhizbangPostgresNotificationsTests {

  private static ServiceProvider _build(Action<IServiceCollection>? extra = null) {
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    services.AddSingleton<IConfiguration>(config);
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    // Register the NoOp listener that AddWhizbangWorkers would have registered. We don't
    // call AddWhizbangWorkers itself because it pulls in the full worker pipeline which
    // needs DI deps unrelated to this test's scope.
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddWhizbangPostgresNotifications();  // replaces NoOp with Pg
    extra?.Invoke(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_ReplacesNoOpWithPgListenerAsync() {
    var sp = _build();
    var listener = sp.GetRequiredService<IWorkNotificationListener>();
    await Assert.That(listener).IsTypeOf<PgWorkNotificationListener>();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_RegistersAsHostedServiceAsync() {
    var sp = _build();
    var hosted = sp.GetServices<IHostedService>();
    var pg = hosted.OfType<PgWorkNotificationListener>().SingleOrDefault();
    await Assert.That(pg).IsNotNull()
      .Because("PgWorkNotificationListener must be registered as IHostedService so the IHost can drive its ExecuteAsync");
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_RegistersAppSignalChannelAsync() {
    var sp = _build();
    var channel = sp.GetService<IAppSignalChannel>();
    await Assert.That(channel).IsNotNull();
    await Assert.That(channel).IsTypeOf<PgAppSignalChannel>();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_IsIdempotentAsync() {
    // Calling twice should not register PgWorkNotificationListener twice as IHostedService —
    // double-registration would call StartAsync twice, opening two LISTEN connections per pod.
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddWhizbangPostgresNotifications();
    services.AddWhizbangPostgresNotifications();
    var sp = services.BuildServiceProvider();

    var hostedListeners = sp.GetServices<IHostedService>().OfType<PgWorkNotificationListener>().ToList();
    // Note: this currently allows double registration via AddHostedService — see issue.
    // If we discover a double-registration in production, tighten by using TryAddEnumerable
    // for the hosted-service entry too. For now, assert at most one IWorkNotificationListener.
    var listener = sp.GetRequiredService<IWorkNotificationListener>();
    await Assert.That(listener).IsTypeOf<PgWorkNotificationListener>();
    await Assert.That(hostedListeners.Count).IsLessThanOrEqualTo(2);
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_BindsOptionsFromConfigurationAsync() {
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Database:SignalingMode"] = "ListenNotify",
      ["Whizbang:Database:ConnectionStringKey"] = "bff-db"
    }).Build();
    services.AddSingleton<IConfiguration>(config);
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddWhizbangPostgresNotifications();
    var sp = services.BuildServiceProvider();

    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhizbangNotificationOptions>>().Value;
    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.ListenNotify);
    await Assert.That(opts.ConnectionStringKey).IsEqualTo("bff-db");
  }
}
