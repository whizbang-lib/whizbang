#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for the <c>WithEFCore&lt;TDbContext&gt;(string connectionStringName)</c> overload on
/// both <see cref="WhizbangBuilder"/> (unified API) and <see cref="WhizbangPerspectiveBuilder"/>
/// (legacy API) — the parameterless <c>WithEFCore&lt;TDbContext&gt;()</c> overload is covered by
/// <see cref="EFCoreExtensionsTests"/>. Both overloads are pure registration helpers: they build a
/// <see cref="EFCoreDriverSelector"/> against a plain <see cref="ServiceCollection"/>, with no
/// server, no DbContext instantiation, and no I/O anywhere.
/// </summary>
[Category("Shard1")]
public class EFCoreExtensionsCoverageTests {

  // Registering EF Core with an explicit connection-string-name override is how a consumer running
  // several DbContexts against different databases tells Whizbang which named connection string to
  // resolve at startup. If the name were silently dropped instead of reaching the selector, the
  // driver would fall back to the [WhizbangDbContext] attribute's default — a service would connect
  // to the wrong database with no error anywhere.
  [Test]
  public async Task WhizbangBuilder_WithEFCore_ConnectionStringName_IsCarriedOnTheSelectorAsync() {
    var services = new ServiceCollection();
    var builder = new WhizbangBuilder(services);

    var selector = builder.WithEFCore<SampleDbContext>("named-connection");

    await Assert.That(selector).IsNotNull();
    await Assert.That(selector.Services).IsSameReferenceAs(services);
    await Assert.That(selector.DbContextType).IsEqualTo(typeof(SampleDbContext));
    await Assert.That(selector.ConnectionStringName).IsEqualTo("named-connection")
      .Because("the whole point of this overload is to override the [WhizbangDbContext] "
             + "attribute's connection string name — losing it here silently reconnects to the "
             + "wrong database");
  }

  [Test]
  public async Task WhizbangBuilder_WithEFCore_NullConnectionStringName_ThrowsAsync() {
    var builder = new WhizbangBuilder(new ServiceCollection());

    await Assert.That(() => builder.WithEFCore<SampleDbContext>(connectionStringName: null!))
      .Throws<ArgumentException>()
      .Because("a null connection string name can never resolve to a real connection; failing at "
             + "registration time is far cheaper than failing the first time the driver opens it");
  }

  [Test]
  public async Task WhizbangPerspectiveBuilder_WithEFCore_ConnectionStringName_IsCarriedOnTheSelectorAsync() {
    var services = new ServiceCollection();
    var builder = new WhizbangPerspectiveBuilder(services);

    var selector = builder.WithEFCore<SampleDbContext>("named-connection");

    await Assert.That(selector).IsNotNull();
    await Assert.That(selector.Services).IsSameReferenceAs(services);
    await Assert.That(selector.DbContextType).IsEqualTo(typeof(SampleDbContext));
    await Assert.That(selector.ConnectionStringName).IsEqualTo("named-connection")
      .Because("the legacy perspective-builder overload must plumb the override through exactly "
             + "like the unified builder does — a discrepancy here would make the two APIs disagree "
             + "on which database a perspective's DbContext resolves against");
  }

  [Test]
  public async Task WhizbangPerspectiveBuilder_WithEFCore_WhitespaceConnectionStringName_ThrowsAsync() {
    var builder = new WhizbangPerspectiveBuilder(new ServiceCollection());

    await Assert.That(() => builder.WithEFCore<SampleDbContext>("   "))
      .Throws<ArgumentException>()
      .Because("a whitespace-only name is just as unresolvable as a null one and must be rejected "
             + "the same way");
  }
}
