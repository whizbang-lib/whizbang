using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The plan must prefer the registered INotificationDataSource (the only path
/// that works under UseNpgsql(NpgsqlDataSource), where Npgsql strips
/// credentials from every public ConnectionString surface) and fall back to
/// the resolved connection string otherwise.
/// </summary>
public class NotificationConnectionPlanTests {
  private static NotificationConnectionStringResolver.Resolution _stringResolution(string? connectionString) =>
    new(
      connectionString,
      connectionString is null
        ? NotificationConnectionStringResolver.ResolutionSource.None
        : NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback);

  [Test]
  public async Task Create_WithRegisteredDataSource_PrefersTheDataSourceAsync() {
    await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Username=tenant-user;Password=secret");
    var wrapper = new NotificationDataSource(dataSource, ownsDataSource: false);

    var plan = NotificationConnectionPlan.Create(wrapper, _stringResolution("Host=localhost;Username=tenant-user"));

    await Assert.That(plan.UsesDataSource).IsTrue()
      .Because("a registered data source must win over the string path even when a string also resolved.");
    await Assert.That(plan.IsAvailable).IsTrue();
    await Assert.That(plan.DataSource).IsSameReferenceAs(dataSource);
  }

  [Test]
  public async Task Create_WithNullDataSourceWrapper_FallsBackToTheConnectionStringAsync() {
    // Auto-discovery registers a wrapper with DataSource=null when it found
    // nothing usable; the plan must treat that like no registration at all.
    var wrapper = new NotificationDataSource(dataSource: null, ownsDataSource: false);

    var plan = NotificationConnectionPlan.Create(wrapper, _stringResolution("Host=localhost;Username=tenant-user;Password=secret"));

    await Assert.That(plan.UsesDataSource).IsFalse();
    await Assert.That(plan.IsAvailable).IsTrue();
    await Assert.That(plan.ConnectionString).Contains("Password=secret");
  }

  [Test]
  public async Task Create_WithNoDataSourceRegistration_UsesTheConnectionStringAsync() {
    var plan = NotificationConnectionPlan.Create(null, _stringResolution("Host=localhost;Username=tenant-user;Password=secret"));

    await Assert.That(plan.UsesDataSource).IsFalse();
    await Assert.That(plan.IsAvailable).IsTrue();
  }

  [Test]
  public async Task Create_WithNeitherPath_IsUnavailableAsync() {
    var plan = NotificationConnectionPlan.Create(null, _stringResolution(null));

    await Assert.That(plan.IsAvailable).IsFalse();
    await Assert.That(async () => await plan.OpenAsync()).Throws<InvalidOperationException>();
  }
}
