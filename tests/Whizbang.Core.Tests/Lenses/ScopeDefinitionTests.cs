using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Direct unit tests for <see cref="ScopeDefinition"/>'s constructor + every
/// setter. The existing <c>ScopedLensFactoryTests</c> exercises the type
/// through the factory but doesn't lock the individual property surfaces —
/// this file pins them so a refactor that breaks one property's behavior
/// surfaces here instead of cascading into the lens factory.
/// </summary>
/// <docs>fundamentals/lenses/scoped-lenses#scope-definition</docs>
public class ScopeDefinitionTests {

  [Test]
  public async Task Constructor_NullName_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new ScopeDefinition(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_SetsNameAsync() {
    var def = new ScopeDefinition("Tenant");
    await Assert.That(def.Name).IsEqualTo("Tenant");
  }

  [Test]
  public async Task Constructor_DefaultPropertyValuesAsync() {
    var def = new ScopeDefinition("Tenant");

    await Assert.That(def.FilterPropertyName).IsNull();
    await Assert.That(def.ContextKey).IsNull();
    await Assert.That(def.FilterMode).IsEqualTo(FilterMode.Equals);
    await Assert.That(def.NoFilter).IsFalse();
    await Assert.That(def.FilterInterfaceType).IsNull();
  }

  [Test]
  public async Task FilterPropertyName_SetAndGetAsync() {
    var def = new ScopeDefinition("Tenant") { FilterPropertyName = "TenantId" };
    await Assert.That(def.FilterPropertyName).IsEqualTo("TenantId");
  }

  [Test]
  public async Task ContextKey_SetAndGetAsync() {
    var def = new ScopeDefinition("Tenant") { ContextKey = "tenant_id_claim" };
    await Assert.That(def.ContextKey).IsEqualTo("tenant_id_claim");
  }

  [Test]
  public async Task FilterMode_SetToInAsync() {
    var def = new ScopeDefinition("Hierarchy") { FilterMode = FilterMode.In };
    await Assert.That(def.FilterMode).IsEqualTo(FilterMode.In);
  }

  [Test]
  public async Task NoFilter_SetTrueAsync() {
    var def = new ScopeDefinition("Global") { NoFilter = true };
    await Assert.That(def.NoFilter).IsTrue();
  }

  [Test]
  public async Task FilterInterfaceType_SetAndGetAsync() {
    var def = new ScopeDefinition("Tenant") {
      FilterInterfaceType = typeof(_ITenantScopedMarker),
    };
    await Assert.That(def.FilterInterfaceType).IsEqualTo(typeof(_ITenantScopedMarker));
  }

  [Test]
  public async Task ComposedSetters_FormCanonicalTenantScopeAsync() {
    // Lock the canonical example from the type's XML doc: the Tenant scope.
    var def = new ScopeDefinition("Tenant") {
      FilterPropertyName = "TenantId",
      ContextKey = "TenantId",
      FilterInterfaceType = typeof(_ITenantScopedMarker),
    };

    await Assert.That(def.Name).IsEqualTo("Tenant");
    await Assert.That(def.FilterPropertyName).IsEqualTo("TenantId");
    await Assert.That(def.ContextKey).IsEqualTo("TenantId");
    await Assert.That(def.FilterInterfaceType).IsEqualTo(typeof(_ITenantScopedMarker));
    await Assert.That(def.FilterMode).IsEqualTo(FilterMode.Equals);
    await Assert.That(def.NoFilter).IsFalse();
  }

  /// <summary>Marker only — no members; lives here so the FilterInterfaceType tests don't have to depend on a production type.</summary>
  private interface _ITenantScopedMarker { }
}
