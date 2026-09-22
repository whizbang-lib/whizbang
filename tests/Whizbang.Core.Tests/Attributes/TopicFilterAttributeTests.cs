using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.Attributes;

[Category("Core")]
[Category("Attributes")]
public class TopicFilterAttributeTests {
  private enum TestTopics {
    OrderPlaced,
    OrderShipped
  }

  [Test]
  public async Task Constructor_WithFilter_SetsFilterAsync() {
    var attribute = new TopicFilterAttribute("orders.*");

    await Assert.That(attribute.Filter).IsEqualTo("orders.*");
  }

  [Test]
  public async Task Constructor_WithNullFilter_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new TopicFilterAttribute(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AttributeUsage_AllowsMultipleAndIsInheritedAsync() {
    var usage = typeof(TopicFilterAttribute)
        .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
        .Cast<AttributeUsageAttribute>()
        .Single();

    await Assert.That(usage.ValidOn).IsEqualTo(AttributeTargets.Class);
    await Assert.That(usage.AllowMultiple).IsTrue();
    await Assert.That(usage.Inherited).IsTrue();
  }

  [Test]
  public async Task GenericConstructor_WithEnumValue_SetsEnumValueAsync() {
    var attribute = new TopicFilterAttribute<TestTopics>(TestTopics.OrderPlaced);

    await Assert.That(attribute.EnumValue).IsEqualTo(TestTopics.OrderPlaced);
  }

  [Test]
  public async Task GenericConstructor_WithEnumValue_ForwardsNameToBaseFilterAsync() {
    var attribute = new TopicFilterAttribute<TestTopics>(TestTopics.OrderShipped);

    await Assert.That(attribute.Filter).IsEqualTo(nameof(TestTopics.OrderShipped));
  }

  [Test]
  public async Task GenericAttribute_DerivesFromNonGenericAsync() {
    var attribute = new TopicFilterAttribute<TestTopics>(TestTopics.OrderPlaced);

    await Assert.That(attribute).IsAssignableTo<TopicFilterAttribute>();
  }
}
