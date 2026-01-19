using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for FireAtAttribute - attribute that controls when receptors fire at lifecycle stages.
/// Tests verify single attribute, multiple attributes, and attribute discovery.
/// </summary>
public class FireAtAttributeTests {
  /// <summary>
  /// Tests that FireAtAttribute stores the lifecycle stage correctly.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_Constructor_StoresLifecycleStageAsync() {
    // Arrange & Act
    var attribute = new FireAtAttribute(LifecycleStage.PostPerspectiveInline);

    // Assert
    await Assert.That(attribute.Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  /// <summary>
  /// Tests that FireAtAttribute can be applied to a class.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_AppliedToClass_CanBeRetrievedAsync() {
    // Arrange & Act
    var attributes = typeof(TestReceptorWithFireAt)
      .GetCustomAttributes(typeof(FireAtAttribute), false)
      .Cast<FireAtAttribute>()
      .ToList();

    // Assert
    await Assert.That(attributes.Count).IsEqualTo(1);
    await Assert.That(attributes[0].Stage).IsEqualTo(LifecycleStage.PostPerspectiveAsync);
  }

  /// <summary>
  /// Tests that multiple FireAtAttribute instances can be applied to a single class.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_MultipleAttributes_AllRetrievedAsync() {
    // Arrange & Act
    var attributes = typeof(TestReceptorWithMultipleFireAt)
      .GetCustomAttributes(typeof(FireAtAttribute), false)
      .Cast<FireAtAttribute>()
      .OrderBy(a => a.Stage)
      .ToList();

    // Assert
    await Assert.That(attributes.Count).IsEqualTo(3);
    await Assert.That(attributes[0].Stage).IsEqualTo(LifecycleStage.ImmediateAsync);
    await Assert.That(attributes[1].Stage).IsEqualTo(LifecycleStage.PostPerspectiveAsync);
    await Assert.That(attributes[2].Stage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
  }

  /// <summary>
  /// Tests that FireAtAttribute has AllowMultiple = true in its AttributeUsage.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_AttributeUsage_AllowsMultipleAsync() {
    // Arrange & Act
    var attributeUsage = typeof(FireAtAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.AllowMultiple).IsTrue();
  }

  /// <summary>
  /// Tests that FireAtAttribute targets classes.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_AttributeUsage_TargetsClassAsync() {
    // Arrange & Act
    var attributeUsage = typeof(FireAtAttribute)
      .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
      .Cast<AttributeUsageAttribute>()
      .FirstOrDefault();

    // Assert
    await Assert.That(attributeUsage).IsNotNull();
    await Assert.That(attributeUsage!.ValidOn).IsEqualTo(AttributeTargets.Class);
  }

  // Test receptors for attribute discovery tests
  [FireAt(LifecycleStage.PostPerspectiveAsync)]
  internal sealed class TestReceptorWithFireAt : IReceptor<TestMessage> {
    public ValueTask HandleAsync(TestMessage message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  [FireAt(LifecycleStage.ImmediateAsync)]
  [FireAt(LifecycleStage.PostPerspectiveAsync)]
  [FireAt(LifecycleStage.PostPerspectiveInline)]
  internal sealed class TestReceptorWithMultipleFireAt : IReceptor<TestMessage> {
    public ValueTask HandleAsync(TestMessage message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  internal sealed record TestMessage : IMessage;
}
