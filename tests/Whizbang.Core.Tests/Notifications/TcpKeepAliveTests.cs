using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Slice 3 of zero-idle-polling — locks the TCP keepalive contract that
/// keeps the gate's <c>IsAvailable</c> signal fresh under NAT-style silent
/// connection death.
///
/// <para>
/// The gate's <c>IsAvailable</c> property is set from the
/// <see cref="PgSharedNotifyConnection"/> probe + the underlying socket's
/// liveness. Without aggressive TCP keepalive, a connection that's been
/// idle-killed by a middlebox (NAT timeout, firewall idle eviction) won't
/// surface as dead until the OS keepalive defaults kick in — typically
/// ~2 hours on Linux. During that window the gate falsely reports
/// healthy, which under Slice 1's relaxed cadence would push the stamper
/// and the (future Slice 4) <c>BackupTickCoordinator</c> into long sleeps
/// that the system can't be woken from.
/// </para>
///
/// <para>
/// Default settings (<see cref="WhizbangNotificationOptions.TcpKeepAliveTime"/>=60 s,
/// <see cref="WhizbangNotificationOptions.TcpKeepAliveInterval"/>=10 s) give roughly
/// 150 s death detection (60 + 9×10 since Npgsql doesn't expose probe count and
/// Linux defaults to 9). That sits comfortably within the 5-minute
/// <see cref="WhizbangNotificationOptions.PeriodicReprobeInterval"/> so the gate's
/// reprobe loop catches up before the next periodic probe is due.
/// </para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer#tcp-keepalive</docs>
public class TcpKeepAliveTests {

  // ============================================================================
  // ApplyTcpKeepAlive helper — pure mutation, easy to unit test
  // ============================================================================

  [Test]
  public async Task ApplyTcpKeepAlive_Defaults_SetsTcpKeepAliveTrueAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");
    var options = new WhizbangNotificationOptions();  // defaults

    PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, options);

    await Assert.That(builder.TcpKeepAlive).IsTrue()
      .Because("Defaults must enable keepalive — that's the whole point of Slice 3. Operators who explicitly want to disable can set TcpKeepAliveTime=0 (Npgsql interprets 0 as 'OS default', which is the legacy 2-hour behavior).");
  }

  [Test]
  public async Task ApplyTcpKeepAlive_Defaults_SetsTimeTo60Async() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");
    var options = new WhizbangNotificationOptions();

    PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, options);

    await Assert.That(builder.TcpKeepAliveTime).IsEqualTo(60)
      .Because("Default 60 s idle time matches the documented TcpKeepAliveTime default on WhizbangNotificationOptions.");
  }

  [Test]
  public async Task ApplyTcpKeepAlive_Defaults_SetsIntervalTo10Async() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");
    var options = new WhizbangNotificationOptions();

    PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, options);

    await Assert.That(builder.TcpKeepAliveInterval).IsEqualTo(10)
      .Because("Default 10 s probe interval combined with Linux's default 9-probe count gives ~150 s death detection: 60 + 9×10.");
  }

  [Test]
  public async Task ApplyTcpKeepAlive_CustomTime_FlowsToBuilderAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");
    var options = new WhizbangNotificationOptions { TcpKeepAliveTime = 120 };

    PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, options);

    await Assert.That(builder.TcpKeepAliveTime).IsEqualTo(120)
      .Because("Operators must be able to lengthen the idle window on noisy networks without recompiling.");
  }

  [Test]
  public async Task ApplyTcpKeepAlive_CustomInterval_FlowsToBuilderAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");
    var options = new WhizbangNotificationOptions { TcpKeepAliveInterval = 5 };

    PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, options);

    await Assert.That(builder.TcpKeepAliveInterval).IsEqualTo(5)
      .Because("Operators must be able to tighten probe cadence for sub-150 s detection SLAs.");
  }

  [Test]
  public async Task ApplyTcpKeepAlive_NullBuilder_ThrowsAsync() {
    var options = new WhizbangNotificationOptions();

    await Assert.That(() => PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(null!, options))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ApplyTcpKeepAlive_NullOptions_ThrowsAsync() {
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=test");

    await Assert.That(() => PostgresNotificationsServiceCollectionExtensions.ApplyTcpKeepAlive(builder, null!))
      .Throws<ArgumentNullException>();
  }

  // ============================================================================
  // Options binding — Whizbang:Database section flows to TcpKeepAlive* properties
  // ============================================================================

  [Test]
  public async Task ConfigureFromConfiguration_TcpKeepAliveTime_FlowsFromSettingsAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Database:TcpKeepAliveTime"] = "120",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddWhizbangPostgresNotifications();
    services.AddSingleton<IConfiguration>(configuration);

    using var sp = services.BuildServiceProvider();
    var options = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(options.TcpKeepAliveTime).IsEqualTo(120)
      .Because("Whizbang:Database:TcpKeepAliveTime is the documented operator knob for keepalive idle time.");
  }

  [Test]
  public async Task ConfigureFromConfiguration_TcpKeepAliveInterval_FlowsFromSettingsAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Database:TcpKeepAliveInterval"] = "5",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddWhizbangPostgresNotifications();
    services.AddSingleton<IConfiguration>(configuration);

    using var sp = services.BuildServiceProvider();
    var options = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(options.TcpKeepAliveInterval).IsEqualTo(5)
      .Because("Whizbang:Database:TcpKeepAliveInterval is the documented operator knob for keepalive probe interval.");
  }

  [Test]
  public async Task ConfigureFromConfiguration_OmittedSettings_PreserveDefaultsAsync() {
    // No TcpKeepAlive* keys set — defaults should land.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Database:ConnectionStringKey"] = "test-db",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddWhizbangPostgresNotifications();
    services.AddSingleton<IConfiguration>(configuration);

    using var sp = services.BuildServiceProvider();
    var options = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(options.TcpKeepAliveTime).IsEqualTo(60);
    await Assert.That(options.TcpKeepAliveInterval).IsEqualTo(10);
  }
}
