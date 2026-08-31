using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

[Category("Core")]
[Category("Attributes")]
[Category("Signals")]
public class WireNameAttributeTests {
  [Test]
  public async Task Constructor_WithWireName_SetsWireNameAsync() {
    var attribute = new WireNameAttribute("order-placed-v2");

    await Assert.That(attribute.WireName).IsEqualTo("order-placed-v2");
  }

  [Test]
  public async Task Constructor_WithNullWireName_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new WireNameAttribute(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AttributeUsage_TargetsClassesAndStructsAsync() {
    var usage = typeof(WireNameAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Class)).IsTrue();
    await Assert.That(usage.ValidOn.HasFlag(AttributeTargets.Struct)).IsTrue();
    await Assert.That(usage.AllowMultiple).IsFalse();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
