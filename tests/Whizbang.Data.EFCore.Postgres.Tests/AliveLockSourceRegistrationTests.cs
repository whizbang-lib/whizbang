// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The Postgres notifications register their shared connection as the instance's alive-lock source (#1286), so the
/// heartbeat runs on its slow cadence while the lock is held. Composition only: nothing starts, nothing connects.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Shard3")]
public class AliveLockSourceRegistrationTests {
  private static ServiceProvider _build(Action<IServiceCollection> configure) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    services.AddLogging();
    services.AddSingleton<IServiceInstanceProvider, ServiceInstanceProvider>();
    services.AddSingleton<IWorkNotificationListener, NoOpWorkNotificationListener>();
    services.AddSingleton<ISchemaReadyGate>(SchemaReadyGate.AlreadyReady());
    configure(services);
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task AddWhizbangPostgresNotifications_RegistersTheSharedConnectionAsTheAliveLockSourceAsync() {
    await using var sp = _build(services => services.AddWhizbangPostgresNotifications());

    await Assert.That(sp.GetRequiredService<IInstanceAliveLockSource>())
      .IsSameReferenceAs(sp.GetRequiredService<PgSharedNotifyConnection>())
      .Because("the shared connection takes the alive-lock, so it is the source the heartbeat's cadence reads");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AddWhizbangPostgresNotifications_DisplacesTheNullDefault_InEitherOrderAsync(bool defaultsFirst) {
    await using var sp = _build(services => {
      if (defaultsFirst) {
        services.TryAddWhizbangDefaults();
        services.AddWhizbangPostgresNotifications();
      } else {
        services.AddWhizbangPostgresNotifications();
        services.TryAddWhizbangDefaults();
      }
    });

    await Assert.That(sp.GetRequiredService<IInstanceAliveLockSource>())
      .IsSameReferenceAs(sp.GetRequiredService<PgSharedNotifyConnection>());
  }

  [Test]
  public async Task AHostsOwnAliveLockSource_IsLeftInPlaceAsync() {
    var own = new HostAliveLockSource();
    await using var sp = _build(services => {
      services.AddSingleton<IInstanceAliveLockSource>(own);
      services.AddWhizbangPostgresNotifications();
    });

    await Assert.That(sp.GetRequiredService<IInstanceAliveLockSource>()).IsSameReferenceAs(own);
  }

  [Test]
  public async Task WithoutTheNotifications_TheNullDefaultHoldsNoLockAsync() {
    await using var sp = _build(services => services.TryAddWhizbangDefaults());

    var source = sp.GetRequiredService<IInstanceAliveLockSource>();

    await Assert.That(source).IsSameReferenceAs(NullInstanceAliveLockSource.Instance);
    await Assert.That(source.IsAliveLockHeld).IsFalse()
      .Because("a deployment with no direct connection keeps the fast cadence, as before");
  }

  [Test]
  public async Task ASharedConnectionThatHasNotStarted_HoldsNoLockAsync() {
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    using var shared = new PgSharedNotifyConnection(
      Options.Create(new WhizbangNotificationOptions()), cfg, new ServiceInstanceProvider(cfg),
      NullLogger<PgSharedNotifyConnection>.Instance);

    await Assert.That(shared.IsAliveLockHeld).IsFalse();
    await Assert.That(shared.ConnectionMode).IsEqualTo(InstanceConnectionMode.Pooled);
  }

  private sealed class HostAliveLockSource : IInstanceAliveLockSource {
    public bool IsAliveLockHeld => true;
  }
}
