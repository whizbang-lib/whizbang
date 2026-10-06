// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The notification data source registrations' remaining decisions: the explicit registration in a
/// container that has no options infrastructure, the search path taken from the registered
/// connection-string fallback, and auto-discovery borrowing the application's data source in a
/// container with no logging.
/// </summary>
/// <remarks>
/// No database: <c>NpgsqlDataSourceBuilder.Build()</c> only builds a pool descriptor, it never
/// connects.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PostgresNotificationsServiceCollectionExtensions.cs</code-under-test>
[Category("Shard5")]
public class NotificationDataSourceRegistrationBranchTests {
  private const string CONNECTION_STRING = "Host=localhost;Username=tenant-user;Database=branch";

  // AddWhizbangNotificationDataSource is documented as the single line of glue a consumer needs.
  // In a container where nothing registered the options infrastructure, it must build the data
  // source on the default options rather than fail to resolve.
  [Test]
  public async Task ExplicitDataSource_InAContainerWithNoOptions_BuildsOnTheDefaultsAsync() {
    var instance = new ServiceInstanceProvider(Guid.NewGuid(), "ds-svc", "ds-host", processId: 1);
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddWhizbangNotificationDataSource(CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsNotNull();
    await Assert.That(notification.DataSource!.ConnectionString)
      .Contains(PgSharedNotifyConnection.ComputeApplicationName(instance.InstanceId))
      .Because("the per-instance application name is stamped whatever the options say");
  }

  // With no explicit search path, the schema the storage driver reports through the fallback is the
  // one the notification connections must address; otherwise every unqualified function they call
  // resolves against the wrong schema in a multi-schema deployment.
  [Test]
  public async Task ExplicitDataSource_WithoutASearchPathOption_TakesTheFallbacksSearchPathAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.NewGuid(), "ds-svc", "ds-host", processId: 1));
    services.AddSingleton<INotificationConnectionStringFallback>(new SchemaFallback("tenant_from_fallback"));
    services.AddWhizbangNotificationDataSource(CONNECTION_STRING);
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource!.ConnectionString).Contains("tenant_from_fallback");
  }

  // With no credential in configuration, auto-discovery borrows the application's own data source.
  // A container with no logging must still get it; the discovery report is optional.
  [Test]
  public async Task AutoDiscovery_InAContainerWithNoLogging_StillBorrowsTheApplicationDataSourceAsync() {
    await using var application = NpgsqlDataSource.Create(CONNECTION_STRING);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration));
    services.AddSingleton(application);
    services.AddWhizbangPostgresNotifications();
    await using var provider = services.BuildServiceProvider();

    var notification = provider.GetRequiredService<INotificationDataSource>();

    await Assert.That(notification.DataSource).IsSameReferenceAs(application);
  }

  private sealed class SchemaFallback(string schema) : INotificationConnectionStringFallback {
    public string? GetConnectionString() => null;
    public string? GetSearchPath() => schema;
  }
}
