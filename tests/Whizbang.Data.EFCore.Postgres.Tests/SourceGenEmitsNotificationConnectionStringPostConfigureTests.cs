using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Consumer-observed regression lock. Before this fix:
///   - The EF source generator baked the connection-string key from the
///     <c>[WhizbangDbContext(ConnectionStringName=...)]</c> attribute into the
///     EF wiring (e.g., <c>"appservice-db"</c>).
///   - The runtime PostConfigure in
///     <c>PostgresDriverExtensions._deriveConnectionStringName</c> derived a
///     DIFFERENT key from the class name (e.g., <c>"app-db"</c> for
///     <c>AppDbContext</c>) and used THAT for
///     <see cref="WhizbangNotificationOptions.ConnectionStringKey"/>.
///   - A consumer wired EF to <c>"appservice-db"</c> (correct) and notifications to
///     <c>"app-db"</c> (didn't exist), so the LISTEN/NOTIFY resolver fell back
///     to the DbContext connection (pgbouncer) and probe-failure-reconnected
///     every 5 minutes in production.
///
/// The fix: the source generator now ALSO emits a
/// <c>services.PostConfigure&lt;WhizbangNotificationOptions&gt;</c> that funnels
/// the EF-side key into the notifications resolver. This test pins that
/// behavior — register <see cref="AttributeOverrideDbContext"/> via the
/// generated turnkey extension and assert
/// <c>WhizbangNotificationOptions.ConnectionStringKey</c> ends up at the
/// attribute value, not the class-name derivation.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class SourceGenEmitsNotificationConnectionStringPostConfigureTests {

  [Test]
  public async Task GeneratedExtension_SetsWhizbangNotificationOptions_ConnectionStringKey_ToAttributeValueAsync() {
    // The generated AddXxxDbContext() extension is the call site that bakes the
    // connection-string key. Today it reads a connection string from
    // configuration (we provide a dummy value so the extension's GetConnectionString
    // call returns non-null and the method completes registration).
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:attribute-override-db"] = "Host=localhost;Database=fake;Username=u;Password=p",
    }).Build();
    services.AddSingleton<IConfiguration>(config);

    // Pre-register options so the IOptions resolver works regardless of whether
    // the generated method registers it. The assertion is on the VALUE, not the
    // presence of the registration.
    services.AddOptions<WhizbangNotificationOptions>();

    // Drive the registration the same way a production host does — via the
    // generated extension method, NOT the runtime PostConfigure path. If the
    // generator has not yet been changed to emit the PostConfigure, ConnectionStringKey
    // stays at its default (empty) and this test fails RED.
    services.AddAttributeOverrideDbContext();

    var sp = services.BuildServiceProvider();
    var notifOpts = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(notifOpts.ConnectionStringKey).IsEqualTo("attribute-override-db");
  }

  [Test]
  public async Task GeneratedExtension_DoesNotOverrideExplicit_ConnectionStringKey_FromAppSettingsAsync() {
    // The generator-emitted PostConfigure must be guarded so an explicit
    // Whizbang:Database:ConnectionStringKey in appsettings still wins — a consumer's
    // override path (the "you can still set it manually" escape hatch) must
    // survive the new turnkey wiring.
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["ConnectionStrings:attribute-override-db"] = "Host=localhost;Database=fake;Username=u;Password=p",
    }).Build();
    services.AddSingleton<IConfiguration>(config);

    // Operator-set override fires FIRST (Configure runs before PostConfigure).
    services.Configure<WhizbangNotificationOptions>(opts => {
      opts.ConnectionStringKey = "operator-override";
    });

    services.AddAttributeOverrideDbContext();

    var sp = services.BuildServiceProvider();
    var notifOpts = sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value;

    await Assert.That(notifOpts.ConnectionStringKey).IsEqualTo("operator-override");
  }
}

/// <summary>
/// Test fixture DbContext mirroring a consumer's shape:
///   - Class name is <c>AttributeOverrideDbContext</c> (which would derive to
///     <c>"_attributeoverride-db"</c> if anyone used the class-name convention)
///   - But the attribute explicitly names it <c>"attribute-override-db"</c>.
/// The generated extension MUST use the attribute value end to end —
/// for EF wiring AND for <c>WhizbangNotificationOptions.ConnectionStringKey</c>.
/// </summary>
[WhizbangDbContext(Schema = "public", ConnectionStringName = "attribute-override-db")]
public partial class AttributeOverrideDbContext(DbContextOptions<AttributeOverrideDbContext> options) : DbContext(options) {
}
