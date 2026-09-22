using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class StreamGroupAttributeTests {
  [Test]
  public async Task Constructor_WithKey_SetsKeyAsync() {
    var attribute = new StreamGroupAttribute("orders");

    await Assert.That(attribute.Key).IsEqualTo("orders");
  }

  [Test]
  public async Task Constructor_Default_AnnouncesAsync() {
    var attribute = new StreamGroupAttribute("orders");

    await Assert.That(attribute.Announce).IsTrue();
  }

  [Test]
  public async Task Constructor_Default_FollowsAsync() {
    var attribute = new StreamGroupAttribute("orders");

    await Assert.That(attribute.Follow).IsTrue();
  }

  [Test]
  public async Task Constructor_Default_DoesNotBridgeAsync() {
    var attribute = new StreamGroupAttribute("orders");

    await Assert.That(attribute.Bridge).IsFalse();
  }

  [Test]
  public async Task Announce_WhenDisabled_OverridesDefaultAsync() {
    var attribute = new StreamGroupAttribute("orders") { Announce = false };

    await Assert.That(attribute.Announce).IsFalse();
    await Assert.That(attribute.Follow).IsTrue();
  }

  [Test]
  public async Task Follow_WhenDisabled_OverridesDefaultAsync() {
    var attribute = new StreamGroupAttribute("orders") { Follow = false };

    await Assert.That(attribute.Follow).IsFalse();
    await Assert.That(attribute.Announce).IsTrue();
  }

  [Test]
  public async Task Bridge_WhenEnabled_OverridesDefaultAsync() {
    var attribute = new StreamGroupAttribute("orders") { Bridge = true };

    await Assert.That(attribute.Bridge).IsTrue();
  }

  [Test]
  public async Task AttributeUsage_AllowsMultipleOnClassesAsync() {
    var usage = typeof(StreamGroupAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsTrue();
    await Assert.That(usage.Inherited).IsFalse();
  }
}
