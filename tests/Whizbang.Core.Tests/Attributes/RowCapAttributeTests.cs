using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class RowCapAttributeTests {
  [Test]
  public async Task Constructor_Default_LeavesPerScopeUnsetAsync() {
    var attribute = new RowCapAttribute();

    await Assert.That(attribute.PerScope).IsEqualTo(-1);
  }

  [Test]
  public async Task Constructor_Default_LeavesPerTenantUnsetAsync() {
    var attribute = new RowCapAttribute();

    await Assert.That(attribute.PerTenant).IsEqualTo(-1);
  }

  [Test]
  public async Task PerScope_WhenInitialized_OverridesUnsetSentinelAsync() {
    var attribute = new RowCapAttribute { PerScope = 500 };

    await Assert.That(attribute.PerScope).IsEqualTo(500);
    await Assert.That(attribute.PerTenant).IsEqualTo(-1);
  }

  [Test]
  public async Task PerTenant_WhenInitialized_OverridesUnsetSentinelAsync() {
    var attribute = new RowCapAttribute { PerTenant = 1000 };

    await Assert.That(attribute.PerTenant).IsEqualTo(1000);
    await Assert.That(attribute.PerScope).IsEqualTo(-1);
  }

  [Test]
  public async Task AttributeUsage_TargetsClassesOnlyAsync() {
    var usage = typeof(RowCapAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
