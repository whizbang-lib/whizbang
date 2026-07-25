using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Verifies that <c>WhizbangNotificationOptions</c> binds from <c>Whizbang:Database</c>
/// in IConfiguration. AOT-safe — uses manual <see cref="IConfigureOptions{T}"/> instead of
/// the reflection-based binder.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class ConfigureWhizbangNotificationOptionsFromConfigurationTests {

  private static WhizbangNotificationOptions _resolveFrom(Dictionary<string, string?> values) {
    var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(config);
    services.AddWhizbangPostgresNotifications();
    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;
  }

  [Test]
  public async Task Bind_FullSettings_AppliesAllFieldsAsync() {
    var opts = _resolveFrom(new() {
      ["Whizbang:Database:SignalingMode"] = "ListenNotify",
      ["Whizbang:Database:ConnectionStringKey"] = "gatewayservice-db",
      ["Whizbang:Database:DirectConnectionString"] = "Host=explicit",
      ["Whizbang:Database:DisableNotifications"] = "false",
      ["Whizbang:Database:PollingFallbackInterval"] = "00:00:45",
      ["Whizbang:Database:ListenKeepaliveInterval"] = "00:00:20",
      ["Whizbang:Database:ListenReconnectInitialDelay"] = "00:00:02",
      ["Whizbang:Database:ListenReconnectMaxDelay"] = "00:01:00",
      ["Whizbang:Database:ListenReconnectBackoffMultiplier"] = "1.5",
    });

    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.ListenNotify);
    await Assert.That(opts.ConnectionStringKey).IsEqualTo("gatewayservice-db");
    await Assert.That(opts.DirectConnectionString).IsEqualTo("Host=explicit");
    await Assert.That(opts.DisableNotifications).IsFalse();
    await Assert.That(opts.PollingFallbackInterval).IsEqualTo(TimeSpan.FromSeconds(45));
    await Assert.That(opts.ListenKeepaliveInterval).IsEqualTo(TimeSpan.FromSeconds(20));
    await Assert.That(opts.ListenReconnectInitialDelay).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(opts.ListenReconnectMaxDelay).IsEqualTo(TimeSpan.FromMinutes(1));
    await Assert.That(opts.ListenReconnectBackoffMultiplier).IsEqualTo(1.5);
  }

  [Test]
  public async Task Bind_PartialSettings_LeavesDefaultsForUnsetKeysAsync() {
    var opts = _resolveFrom(new() {
      ["Whizbang:Database:ConnectionStringKey"] = "gateway-db"
    });

    // Set value applied:
    await Assert.That(opts.ConnectionStringKey).IsEqualTo("gateway-db");
    // Defaults preserved for everything else:
    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.Auto);
    await Assert.That(opts.DirectConnectionString).IsNull();
    await Assert.That(opts.DisableNotifications).IsFalse();
    await Assert.That(opts.PollingFallbackInterval).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(opts.ListenKeepaliveInterval).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task Bind_NoSection_DefaultsAppliedAsync() {
    var opts = _resolveFrom([]);

    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.Auto);
    await Assert.That(opts.ConnectionStringKey).IsNull();
    await Assert.That(opts.DirectConnectionString).IsNull();
    await Assert.That(opts.DisableNotifications).IsFalse();
  }

  [Test]
  public async Task Bind_SignalingMode_AcceptsCaseInsensitiveValuesAsync() {
    var opts = _resolveFrom(new() {
      ["Whizbang:Database:SignalingMode"] = "polling"  // lowercase
    });

    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.Polling);
  }

  [Test]
  public async Task Bind_SignalingMode_InvalidValue_LeavesDefaultAsync() {
    var opts = _resolveFrom(new() {
      ["Whizbang:Database:SignalingMode"] = "NotAValidMode"
    });

    await Assert.That(opts.SignalingMode).IsEqualTo(WorkSignalingMode.Auto);
  }
}
