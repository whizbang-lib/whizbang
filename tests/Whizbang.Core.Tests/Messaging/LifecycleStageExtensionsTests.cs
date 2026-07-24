using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Drives every value of <see cref="LifecycleStage"/> through
/// <see cref="LifecycleStageExtensions.IsDetached"/> and
/// <see cref="LifecycleStageExtensions.IsInline"/> so the classifier exits
/// the "partial coverage" zone — when new stages get added (or the
/// detached/inline split is reshuffled), this regression locks the
/// expected category for each existing value AND requires the new value
/// to be added to one of the two tables here.
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
public class LifecycleStageExtensionsTests {

  private static readonly LifecycleStage[] _detached = [
    LifecycleStage.ImmediateDetached,
    LifecycleStage.LocalImmediateDetached,
    LifecycleStage.PreDistributeDetached,
    LifecycleStage.DistributeDetached,
    LifecycleStage.PostDistributeDetached,
    LifecycleStage.PreOutboxDetached,
    LifecycleStage.PostOutboxDetached,
    LifecycleStage.PreInboxDetached,
    LifecycleStage.PostInboxDetached,
    LifecycleStage.PrePerspectiveDetached,
    LifecycleStage.PostPerspectiveDetached,
    LifecycleStage.PostAllPerspectivesDetached,
    LifecycleStage.PostLifecycleDetached,
    LifecycleStage.PreDestructionDetached,
    LifecycleStage.PostDestructionDetached,
  ];

  private static readonly LifecycleStage[] _inline = [
    LifecycleStage.LocalImmediateInline,
    LifecycleStage.PreDistributeInline,
    LifecycleStage.PostDistributeInline,
    LifecycleStage.PreOutboxInline,
    LifecycleStage.PostOutboxInline,
    LifecycleStage.PreInboxInline,
    LifecycleStage.PostInboxInline,
    LifecycleStage.PrePerspectiveInline,
    LifecycleStage.PostPerspectiveInline,
    LifecycleStage.PostAllPerspectivesInline,
    LifecycleStage.PostLifecycleInline,
    LifecycleStage.PreDestructionInline,
    LifecycleStage.PostDestructionInline,
  ];

  /// <summary>
  /// Sentinel: <see cref="LifecycleStage.AfterReceptorCompletion"/> (= -1) is a
  /// tag-hook timing marker, NOT a real stage — neither detached nor inline.
  /// </summary>
  private static readonly LifecycleStage[] _neither = [
    LifecycleStage.AfterReceptorCompletion,
  ];

  [Test]
  public async Task IsDetached_AllDetachedValues_ReturnsTrueAsync() {
    foreach (var stage in _detached) {
      await Assert.That(stage.IsDetached())
        .IsTrue()
        .Because($"{stage} is in the detached list of LifecycleStageExtensions");
    }
  }

  [Test]
  public async Task IsDetached_AllInlineValues_ReturnsFalseAsync() {
    foreach (var stage in _inline) {
      await Assert.That(stage.IsDetached())
        .IsFalse()
        .Because($"{stage} is an inline stage and must not be detached");
    }
  }

  [Test]
  public async Task IsDetached_AfterReceptorCompletion_ReturnsFalseAsync() {
    // Sentinel-style stage isn't classified as Detached.
    foreach (var stage in _neither) {
      await Assert.That(stage.IsDetached())
        .IsFalse()
        .Because($"{stage} is a tag-hook sentinel, not a real lifecycle stage");
    }
  }

  [Test]
  public async Task IsInline_AllInlineValues_ReturnsTrueAsync() {
    foreach (var stage in _inline) {
      await Assert.That(stage.IsInline())
        .IsTrue()
        .Because($"{stage} is in the inline list of LifecycleStageExtensions");
    }
  }

  [Test]
  public async Task IsInline_AllDetachedValues_ReturnsFalseAsync() {
    foreach (var stage in _detached) {
      await Assert.That(stage.IsInline())
        .IsFalse()
        .Because($"{stage} is a detached stage and must not be inline");
    }
  }

  [Test]
  public async Task IsInline_AfterReceptorCompletion_ReturnsFalseAsync() {
    foreach (var stage in _neither) {
      await Assert.That(stage.IsInline())
        .IsFalse()
        .Because($"{stage} is a tag-hook sentinel, not a real lifecycle stage");
    }
  }

  /// <summary>
  /// Invariant: every defined <see cref="LifecycleStage"/> value (apart from
  /// the documented sentinels) belongs to exactly one of the two tables.
  /// This catches added enum values that the maintainer forgot to wire into
  /// the classifier — the new value will fall through to "neither" and fail
  /// this regression lock.
  /// </summary>
  [Test]
  public async Task EveryEnumValue_IsClassifiedDetachedXorInlineAsync() {
    foreach (var stage in Enum.GetValues<LifecycleStage>()) {
      var d = stage.IsDetached();
      var i = stage.IsInline();
      if (Array.IndexOf(_neither, stage) >= 0) {
        await Assert.That(d || i).IsFalse().Because($"{stage} is sentinel — neither");
        continue;
      }
      // Exactly one of (detached, inline) must be true for real stages.
      await Assert.That(d ^ i)
        .IsTrue()
        .Because($"{stage} must be classified as exactly one of detached or inline");
    }
  }
}
