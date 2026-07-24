using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the connection-string-name convention so the turnkey
/// <see cref="PostgresDriverExtensions._deriveConnectionStringName"/> stays in
/// sync with the source generator's
/// <c>EFCoreServiceRegistrationGenerator._deriveConnectionStringName</c>.
///
/// <para>This is the convention a consumer (and any other Whizbang consumer) relies on:
/// a service with a <c>BffServiceDbContext</c> automatically uses
/// <c>ConnectionStrings:appservice-db</c> (and, for notifications, the
/// <c>-direct</c> variant) without having to set
/// <c>Whizbang:Database:ConnectionStringKey</c> in appsettings. If a future
/// refactor changes one of the two implementations, the other becomes a silent
/// regression — a consumer's production SCRAM workaround stops working again.</para>
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class PostgresDriverExtensions_ConnectionStringKeyDerivationTests {

  [Test]
  public async Task BffServiceDbContext_DerivesToBffserviceDashDbAsync() {
    // Direct match for the a consumer BFF production config:
    //   ConnectionStrings:appservice-db (pgbouncer)
    //   ConnectionStrings:appservice-db-direct (notifications)
    await Assert.That(PostgresDriverExtensions._deriveConnectionStringName("BffServiceDbContext"))
      .IsEqualTo("appservice-db");
  }

  [Test]
  public async Task ChatDbContext_DerivesToChatDashDbAsync() {
    // Reference convention from the generator's own example comment.
    await Assert.That(PostgresDriverExtensions._deriveConnectionStringName("ChatDbContext"))
      .IsEqualTo("chat-db");
  }

  [Test]
  public async Task InventoryDbContext_DerivesToInventoryDashDbAsync() {
    // Matches what ECommerce.RabbitMqIntegrationFixture sets for the
    // InventoryHost: ConnectionStrings:inventory-db.
    await Assert.That(PostgresDriverExtensions._deriveConnectionStringName("InventoryDbContext"))
      .IsEqualTo("inventory-db");
  }

  [Test]
  public async Task ClassNameWithoutDbContextSuffix_FallsBackToLowercasePlusDashDbAsync() {
    // The generator's convention strips DbContext only when present.
    // Anything else is taken as-is, lowercased, with -db appended.
    await Assert.That(PostgresDriverExtensions._deriveConnectionStringName("Foo"))
      .IsEqualTo("foo-db");
  }

  [Test]
  public async Task SingleWord_DbContext_LowercasesAndAppendsAsync() {
    await Assert.That(PostgresDriverExtensions._deriveConnectionStringName("MyDbContext"))
      .IsEqualTo("my-db");
  }
}
