using System.Linq;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for LocalImmediate lifecycle stages - the mediator pattern stages for local dispatch.
/// These stages fire when messages are dispatched locally (no transport involved).
/// </summary>
/// <docs>core-concepts/lifecycle-stages</docs>
public class LocalImmediateLifecycleStageTests {

  /// <summary>
  /// Verifies that LocalImmediateDetached stage exists in the LifecycleStage enum.
  /// This stage is for async processing during local dispatch (mediator pattern).
  /// </summary>
  [Test]
  public async Task LocalImmediateDetached_ShouldExistInLifecycleStageEnumAsync() {
    // Arrange & Act
    var stage = LifecycleStage.LocalImmediateDetached;

    // Assert - Stage exists and can be assigned
    await Assert.That(stage.ToString()).IsEqualTo("LocalImmediateDetached");
  }

  /// <summary>
  /// Verifies that LocalImmediateInline stage exists in the LifecycleStage enum.
  /// This stage is for blocking processing during local dispatch (mediator pattern).
  /// </summary>
  [Test]
  public async Task LocalImmediateInline_ShouldExistInLifecycleStageEnumAsync() {
    // Arrange & Act
    var stage = LifecycleStage.LocalImmediateInline;

    // Assert - Stage exists and can be assigned
    await Assert.That(stage.ToString()).IsEqualTo("LocalImmediateInline");
  }

  /// <summary>
  /// Verifies that LocalImmediate stages have unique enum values.
  /// </summary>
  [Test]
  public async Task LocalImmediateStages_ShouldHaveUniqueValuesAsync() {
    // Arrange
    var asyncStage = (int)LifecycleStage.LocalImmediateDetached;
    var inlineStage = (int)LifecycleStage.LocalImmediateInline;
    var immediateAsync = (int)LifecycleStage.ImmediateDetached;

    // Assert - All should be different
    await Assert.That(asyncStage).IsNotEqualTo(inlineStage);
    await Assert.That(asyncStage).IsNotEqualTo(immediateAsync);
    await Assert.That(inlineStage).IsNotEqualTo(immediateAsync);
  }

  /// <summary>
  /// Verifies that [FireAt] attribute can accept LocalImmediateDetached stage.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_ShouldAcceptLocalImmediateDetachedAsync() {
    // Arrange & Act
    var attribute = new FireAtAttribute(LifecycleStage.LocalImmediateDetached);

    // Assert - FireAtAttribute uses Stage (singular) property
    await Assert.That(attribute.Stage).IsEqualTo(LifecycleStage.LocalImmediateDetached);
  }

  /// <summary>
  /// Verifies that [FireAt] attribute can accept LocalImmediateInline stage.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_ShouldAcceptLocalImmediateInlineAsync() {
    // Arrange & Act
    var attribute = new FireAtAttribute(LifecycleStage.LocalImmediateInline);

    // Assert - FireAtAttribute uses Stage (singular) property
    await Assert.That(attribute.Stage).IsEqualTo(LifecycleStage.LocalImmediateInline);
  }

  /// <summary>
  /// Verifies that multiple [FireAt] attributes can include LocalImmediate stages.
  /// FireAtAttribute uses AllowMultiple=true pattern - apply multiple attributes for multiple stages.
  /// </summary>
  [Test]
  public async Task FireAtAttribute_MultipleAttributes_ShouldIncludeLocalImmediateDetachedAsync() {
    // Arrange - Create attributes as they would appear on a class
    var attributes = new[] {
      new FireAtAttribute(LifecycleStage.LocalImmediateInline),
      new FireAtAttribute(LifecycleStage.PreOutboxInline),
      new FireAtAttribute(LifecycleStage.PostInboxInline)
    };

    // Act - Extract stages from attributes
    var stages = attributes.Select(a => a.Stage).ToArray();

    // Assert
    await Assert.That(stages).Count().IsEqualTo(3);
    await Assert.That(stages).Contains(LifecycleStage.LocalImmediateInline);
    await Assert.That(stages).Contains(LifecycleStage.PreOutboxInline);
    await Assert.That(stages).Contains(LifecycleStage.PostInboxInline);
  }
}
