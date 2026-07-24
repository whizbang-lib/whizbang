using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

/// <summary>
/// Locks the constructor + property contract of <see cref="AttributeArgNamingAttribute"/>.
/// Tiny attribute, but it has compile-time downstream consumers (the
/// MessageTagDiscoveryGenerator reads its Convention to choose case-mapping); the
/// property needs to survive code-style refactors.
/// </summary>
public class AttributeArgNamingAttributeTests {

  [Test]
  public async Task Constructor_StoresConventionVerbatimAsync() {
    var attr = new AttributeArgNamingAttribute(AttributeArgNamingConvention.PascalCase);
    await Assert.That(attr.Convention).IsEqualTo(AttributeArgNamingConvention.PascalCase);
  }

  [Test]
  public async Task Constructor_AcceptsIdentityAsync() {
    var attr = new AttributeArgNamingAttribute(AttributeArgNamingConvention.Identity);
    await Assert.That(attr.Convention).IsEqualTo(AttributeArgNamingConvention.Identity);
  }
}
