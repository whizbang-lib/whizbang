using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Tests the resolution precedence for the LISTEN/NOTIFY connection string:
/// <c>DirectConnectionString</c> → <c>{key}-direct</c> → <c>{key}</c> → null.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class NotificationConnectionStringResolverTests {

  private static IConfiguration _config(Dictionary<string, string?> values) {
    return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
  }

  [Test]
  public async Task Resolve_ExplicitDirectConnectionString_TakesPrecedenceOverConfigKeysAsync() {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = "Host=explicit;Port=5432;Database=test",
      ConnectionStringKey = "should-be-ignored"
    };
    var config = _config(new() {
      ["ConnectionStrings:should-be-ignored-direct"] = "Host=ignored1",
      ["ConnectionStrings:should-be-ignored"] = "Host=ignored2"
    });

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.ConnectionString).IsEqualTo("Host=explicit;Port=5432;Database=test");
    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.ExplicitOption);
  }

  [Test]
  public async Task Resolve_DirectKeyExists_PrefersDirectOverPooledAsync() {
    var opts = new WhizbangNotificationOptions { ConnectionStringKey = "appservice-db" };
    var config = _config(new() {
      ["ConnectionStrings:appservice-db-direct"] = "Host=direct.pg",
      ["ConnectionStrings:appservice-db"] = "Host=pgbouncer.local"
    });

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.ConnectionString).IsEqualTo("Host=direct.pg");
    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DirectKey);
  }

  [Test]
  public async Task Resolve_OnlyPooledKeyExists_FallsBackToPooledAsync() {
    var opts = new WhizbangNotificationOptions { ConnectionStringKey = "appservice-db" };
    var config = _config(new() {
      ["ConnectionStrings:appservice-db"] = "Host=pgbouncer.local"
    });

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.ConnectionString).IsEqualTo("Host=pgbouncer.local");
    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback);
  }

  [Test]
  public async Task Resolve_NoConfigOrExplicit_ReturnsNullAsync() {
    var opts = new WhizbangNotificationOptions();
    var config = _config([]);

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.ConnectionString).IsNull();
    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.None);
  }

  [Test]
  public async Task Resolve_KeySetButNoMatchingConfig_ReturnsNullAsync() {
    var opts = new WhizbangNotificationOptions { ConnectionStringKey = "nonexistent" };
    var config = _config(new() {
      ["ConnectionStrings:other-service-db-direct"] = "Host=other"
    });

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.ConnectionString).IsNull();
    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.None);
  }

  [Test]
  public async Task Resolve_EmptyDirectKeyValue_FallsBackToPooledAsync() {
    // "set but empty" is treated as not set — fallback to pooled key.
    var opts = new WhizbangNotificationOptions { ConnectionStringKey = "appservice-db" };
    var config = _config(new() {
      ["ConnectionStrings:appservice-db-direct"] = "",
      ["ConnectionStrings:appservice-db"] = "Host=pgbouncer.local"
    });

    var result = NotificationConnectionStringResolver.Resolve(opts, config);

    await Assert.That(result.Source).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.PooledKeyFallback);
    await Assert.That(result.ConnectionString).IsEqualTo("Host=pgbouncer.local");
  }
}
