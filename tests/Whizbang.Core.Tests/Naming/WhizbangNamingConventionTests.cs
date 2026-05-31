using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Naming;

namespace Whizbang.Core.Tests.Naming;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Pins the central <see cref="WhizbangNamingConvention.DeriveConnectionStringName"/>
/// helper that BOTH the EF Core source generator AND the runtime PostConfigure
/// (in Whizbang.Data.EFCore.Postgres) must agree on.
///
/// Regression context: before this central helper, the generator and runtime
/// each ran their own copy of the convention. The runtime copy looked at a
/// different input than the generator copy in one specific call shape
/// (<c>.WithEFCore&lt;TDbContext&gt;()</c> with no explicit name) — so the EF
/// wiring landed at <c>"bffservice-db"</c> (from the
/// <c>[WhizbangDbContext(ConnectionStringName="bffservice-db")]</c> attribute)
/// while notifications landed at <c>"bff-db"</c> (from the runtime falling
/// back to the class-name derivation). JDX silently fell back to the pgbouncer
/// connection and LISTEN/NOTIFY died with self-test probe failures every 5
/// minutes.
/// </summary>
/// <docs>fundamentals/work-coordinator/notifications-and-pgbouncer</docs>
public class WhizbangNamingConventionTests {

  [Test]
  public async Task DeriveConnectionStringName_StripsDbContextSuffix_LowercasesAndAppendsDashDbAsync() {
    // Canonical example used in docs + JDX production.
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("BffServiceDbContext"))
      .IsEqualTo("bffservice-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_ChatDbContext_ReturnsChatDashDbAsync() {
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("ChatDbContext"))
      .IsEqualTo("chat-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_InventoryDbContext_ReturnsInventoryDashDbAsync() {
    // Matches what ECommerce.RabbitMqIntegrationFixture sets for the
    // InventoryHost: ConnectionStrings:inventory-db.
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("InventoryDbContext"))
      .IsEqualTo("inventory-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_SingleWord_DbContext_LowercasesAndAppendsAsync() {
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("MyDbContext"))
      .IsEqualTo("my-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_BffDbContext_ReturnsBffDashDb_NotBffserviceDashDbAsync() {
    // Explicit regression test for the JDX scenario: when the DbContext is
    // named "BffDbContext" but the attribute overrides to "bffservice-db",
    // ONLY this central helper's convention applies — it can't second-guess
    // the attribute. The bug fix is that runtime call sites no longer use
    // this derivation when the [WhizbangDbContext] attribute provides a name;
    // the generator-emitted PostConfigure carries that explicit name through.
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("BffDbContext"))
      .IsEqualTo("bff-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_ClassNameWithoutDbContextSuffix_TakesAsIsAsync() {
    // The convention strips "DbContext" only when present. Anything else is
    // lowercased and gets "-db" appended.
    await Assert.That(WhizbangNamingConvention.DeriveConnectionStringName("Foo"))
      .IsEqualTo("foo-db");
  }

  [Test]
  public async Task DeriveConnectionStringName_Null_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => WhizbangNamingConvention.DeriveConnectionStringName(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DeriveConnectionStringName_EmptyString_ThrowsArgumentExceptionAsync() {
    await Assert.That(() => WhizbangNamingConvention.DeriveConnectionStringName(string.Empty))
      .Throws<ArgumentException>();
  }
}
