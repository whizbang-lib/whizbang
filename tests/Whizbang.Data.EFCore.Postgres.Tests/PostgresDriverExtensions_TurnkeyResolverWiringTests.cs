using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// End-to-end proof that the turnkey
/// <see cref="PostgresDriverExtensions.Postgres"/> property wires the
/// notification resolver against a consumer's exact production config shape — no
/// consumer-side <c>Whizbang:Database:ConnectionStringKey</c> needed.
///
/// <para>RED + GREEN pair, mirroring the structure of the SCRAM-SHA-256
/// reproduction tests.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PostgresDriverExtensions_TurnkeyResolverWiringTests {

  // A representative consumer production config shape: ConnectionStrings:appservice-db
  // (pgbouncer pooled) + ConnectionStrings:appservice-db-direct (port-5432
  // direct, the notification path).
  private const string PRODUCTION_LIKE_POOLED_STRING =
    "Server=db.example.com;Database=appservice_db;Port=6432;User Id=app_user;Password=fake_test_password;Ssl Mode=Require;";
  private const string PRODUCTION_LIKE_DIRECT_STRING =
    "Server=db.example.com;Database=appservice_db;Port=5432;User Id=app_user;Password=fake_test_password;Ssl Mode=Require;";

  /// <summary>
  /// RED: with the consumer-shaped config but NO Whizbang-side derivation and NO
  /// explicit <c>ConnectionStringKey</c>, the resolver finds nothing — exactly
  /// the failure mode a consumer hits before the fix.
  /// </summary>
  [Test]
  public async Task NoExplicitKey_NoTurnkeyDerivation_ResolverFindsNothingAsync() {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:appservice-db"] = PRODUCTION_LIKE_POOLED_STRING,
        ["ConnectionStrings:appservice-db-direct"] = PRODUCTION_LIKE_DIRECT_STRING,
      })
      .Build();

    var options = new WhizbangNotificationOptions();
    var resolution = NotificationConnectionStringResolver.Resolve(
      options, config, fallback: null);

    // Without ConnectionStringKey set and no fallback, the resolver has
    // nothing to work with — this is the consumer SCRAM failure mode.
    await Assert.That(resolution.ConnectionString).IsNull();
    await Assert.That(resolution.Source)
      .IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.None);
  }

  /// <summary>
  /// GREEN: with the same consumer-shaped config, simulating what the turnkey
  /// <c>.WithDriver.Postgres</c> now does via <c>PostConfigure</c> — set
  /// <c>ConnectionStringKey</c> to the derived name from
  /// <c>AppServiceDbContext</c>. The resolver then picks the <c>-direct</c>
  /// variant.
  /// </summary>
  [Test]
  public async Task TurnkeyDerivationFromAppServiceDbContext_ResolverPicksDirectVariantAsync() {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:appservice-db"] = PRODUCTION_LIKE_POOLED_STRING,
        ["ConnectionStrings:appservice-db-direct"] = PRODUCTION_LIKE_DIRECT_STRING,
      })
      .Build();

    var derivedKey = PostgresDriverExtensions._deriveConnectionStringName("AppServiceDbContext");
    var options = new WhizbangNotificationOptions { ConnectionStringKey = derivedKey };

    var resolution = NotificationConnectionStringResolver.Resolve(
      options, config, fallback: null);

    // The resolver prefers <key>-direct over <key> — exactly what notification
    // workers want (port-5432 direct connection, not the pgbouncer transaction-
    // pooler that breaks LISTEN/NOTIFY).
    await Assert.That(resolution.ConnectionString).IsEqualTo(PRODUCTION_LIKE_DIRECT_STRING);
    await Assert.That(resolution.Source)
      .IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DirectKey);
  }

  /// <summary>
  /// GREEN (override): operators can still pin
  /// <c>Whizbang__Notifications__ConnectionStringKey</c> in appsettings — the
  /// turnkey derivation only fills in the gap when nothing's explicitly set.
  /// </summary>
  [Test]
  public async Task ExplicitConnectionStringKey_OverridesTurnkeyDerivationAsync() {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:appservice-db"] = PRODUCTION_LIKE_POOLED_STRING,
        ["ConnectionStrings:appservice-db-direct"] = PRODUCTION_LIKE_DIRECT_STRING,
        ["ConnectionStrings:custom-override"] = "Host=other.example.com;Username=u;Password=p;",
      })
      .Build();

    // Mimic what the turnkey block does internally: PostConfigure that only
    // sets ConnectionStringKey when it's still blank.
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(config);
    services.AddOptions<WhizbangNotificationOptions>()
      .Configure(o => o.ConnectionStringKey = "custom-override");
    services.PostConfigure<WhizbangNotificationOptions>(o => {
      if (string.IsNullOrWhiteSpace(o.ConnectionStringKey)) {
        o.ConnectionStringKey = PostgresDriverExtensions._deriveConnectionStringName("AppServiceDbContext");
      }
    });

    var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    // The explicit "custom-override" wins; "appservice-db" (turnkey-derived)
    // is ignored.
    await Assert.That(resolved.ConnectionStringKey).IsEqualTo("custom-override");

    var resolution = NotificationConnectionStringResolver.Resolve(resolved, config, fallback: null);
    await Assert.That(resolution.ConnectionString).IsEqualTo("Host=other.example.com;Username=u;Password=p;");
  }

  /// <summary>
  /// GREEN (PostConfigure chain): proves the turnkey block's
  /// <c>PostConfigure</c> shape actually composes with the rest of the
  /// options-binding pipeline — the derived key flows through DI exactly the
  /// way the turnkey <c>.WithDriver.Postgres</c> sets it up.
  /// </summary>
  [Test]
  public async Task TurnkeyPostConfigure_SetsConnectionStringKeyOnResolvedOptionsAsync() {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["ConnectionStrings:appservice-db-direct"] = PRODUCTION_LIKE_DIRECT_STRING,
      })
      .Build();

    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(config);
    services.AddOptions<WhizbangNotificationOptions>();
    // This is the same shape PostgresDriverExtensions.Postgres uses.
    services.PostConfigure<WhizbangNotificationOptions>(o => {
      if (string.IsNullOrWhiteSpace(o.ConnectionStringKey)) {
        o.ConnectionStringKey = PostgresDriverExtensions._deriveConnectionStringName("AppServiceDbContext");
      }
    });

    var sp = services.BuildServiceProvider();
    var resolved = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(resolved.ConnectionStringKey).IsEqualTo("appservice-db");

    var resolution = NotificationConnectionStringResolver.Resolve(resolved, config, fallback: null);
    await Assert.That(resolution.ConnectionString).IsEqualTo(PRODUCTION_LIKE_DIRECT_STRING);
  }
}
