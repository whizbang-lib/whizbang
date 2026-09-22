using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class RowTtlAttributeTests {
  [Test]
  public async Task Constructor_Default_LeavesDaysUnsetAsync() {
    var attribute = new RowTtlAttribute();

    await Assert.That(attribute.Days).IsEqualTo(-1);
  }

  [Test]
  public async Task Constructor_Default_LeavesSecondsUnsetAsync() {
    var attribute = new RowTtlAttribute();

    await Assert.That(attribute.Seconds).IsEqualTo(-1);
  }

  [Test]
  public async Task Days_WhenInitialized_OverridesUnsetSentinelAsync() {
    var attribute = new RowTtlAttribute { Days = 30 };

    await Assert.That(attribute.Days).IsEqualTo(30);
    await Assert.That(attribute.Seconds).IsEqualTo(-1);
  }

  [Test]
  public async Task Seconds_WhenInitialized_OverridesUnsetSentinelAsync() {
    var attribute = new RowTtlAttribute { Seconds = 90 };

    await Assert.That(attribute.Seconds).IsEqualTo(90);
    await Assert.That(attribute.Days).IsEqualTo(-1);
  }

  [Test]
  public async Task AttributeUsage_TargetsClassesOnlyAsync() {
    var usage = typeof(RowTtlAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
