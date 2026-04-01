using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="LifecycleStage"/> enum.
/// </summary>
/// <tests>src/Whizbang.Core/Messaging/LifecycleStage.cs</tests>
public class LifecycleStageTests {
  // ==========================================================================
  // Basic enum definition tests
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_HasTwentyFiveValuesAsync() {
    // 24 lifecycle stages + 1 special AfterReceptorCompletion for tag hooks
    var values = Enum.GetValues<LifecycleStage>();
    await Assert.That(values.Length).IsEqualTo(25);
  }

  // ==========================================================================
  // Special tag hook stage
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_AfterReceptorCompletion_IsDefinedAsync() {
    var value = LifecycleStage.AfterReceptorCompletion;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_AfterReceptorCompletion_HasNegativeValueAsync() {
    // AfterReceptorCompletion is -1 to distinguish it from real lifecycle stages
    var value = (int)LifecycleStage.AfterReceptorCompletion;
    await Assert.That(value).IsEqualTo(-1);
  }

  // ==========================================================================
  // Immediate stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_ImmediateDetached_IsDefinedAsync() {
    var value = LifecycleStage.ImmediateDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // LocalImmediate stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_LocalImmediateDetached_IsDefinedAsync() {
    var value = LifecycleStage.LocalImmediateDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_LocalImmediateInline_IsDefinedAsync() {
    var value = LifecycleStage.LocalImmediateInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // Distribute stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PreDistributeDetached_IsDefinedAsync() {
    var value = LifecycleStage.PreDistributeDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreDistributeInline_IsDefinedAsync() {
    var value = LifecycleStage.PreDistributeInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_DistributeDetached_IsDefinedAsync() {
    var value = LifecycleStage.DistributeDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostDistributeDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostDistributeDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostDistributeInline_IsDefinedAsync() {
    var value = LifecycleStage.PostDistributeInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // Outbox stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PreOutboxDetached_IsDefinedAsync() {
    var value = LifecycleStage.PreOutboxDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreOutboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PreOutboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostOutboxDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostOutboxDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostOutboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PostOutboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // Inbox stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PreInboxDetached_IsDefinedAsync() {
    var value = LifecycleStage.PreInboxDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreInboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PreInboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostInboxDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostInboxDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostInboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PostInboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // Perspective stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PrePerspectiveDetached_IsDefinedAsync() {
    var value = LifecycleStage.PrePerspectiveDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PrePerspectiveInline_IsDefinedAsync() {
    var value = LifecycleStage.PrePerspectiveInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostPerspectiveDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostPerspectiveDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostPerspectiveInline_IsDefinedAsync() {
    var value = LifecycleStage.PostPerspectiveInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // PostAllPerspectives stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PostAllPerspectivesDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostAllPerspectivesDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostAllPerspectivesInline_IsDefinedAsync() {
    var value = LifecycleStage.PostAllPerspectivesInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // PostLifecycle stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_PostLifecycleDetached_IsDefinedAsync() {
    var value = LifecycleStage.PostLifecycleDetached;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostLifecycleInline_IsDefinedAsync() {
    var value = LifecycleStage.PostLifecycleInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // Enum ordering tests
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_ImmediateDetached_IsFirstValueAsync() {
    var value = (int)LifecycleStage.ImmediateDetached;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task LifecycleStage_ImmediateDetached_IsDefaultAsync() {
    var value = default(LifecycleStage);
    await Assert.That(value).IsEqualTo(LifecycleStage.ImmediateDetached);
  }
}
