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
  public async Task LifecycleStage_ImmediateAsync_IsDefinedAsync() {
    var value = LifecycleStage.ImmediateAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  // ==========================================================================
  // LocalImmediate stages
  // ==========================================================================

  [Test]
  public async Task LifecycleStage_LocalImmediateAsync_IsDefinedAsync() {
    var value = LifecycleStage.LocalImmediateAsync;
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
  public async Task LifecycleStage_PreDistributeAsync_IsDefinedAsync() {
    var value = LifecycleStage.PreDistributeAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreDistributeInline_IsDefinedAsync() {
    var value = LifecycleStage.PreDistributeInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_DistributeAsync_IsDefinedAsync() {
    var value = LifecycleStage.DistributeAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostDistributeAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostDistributeAsync;
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
  public async Task LifecycleStage_PreOutboxAsync_IsDefinedAsync() {
    var value = LifecycleStage.PreOutboxAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreOutboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PreOutboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostOutboxAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostOutboxAsync;
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
  public async Task LifecycleStage_PreInboxAsync_IsDefinedAsync() {
    var value = LifecycleStage.PreInboxAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PreInboxInline_IsDefinedAsync() {
    var value = LifecycleStage.PreInboxInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostInboxAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostInboxAsync;
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
  public async Task LifecycleStage_PrePerspectiveAsync_IsDefinedAsync() {
    var value = LifecycleStage.PrePerspectiveAsync;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PrePerspectiveInline_IsDefinedAsync() {
    var value = LifecycleStage.PrePerspectiveInline;
    await Assert.That(Enum.IsDefined(value)).IsTrue();
  }

  [Test]
  public async Task LifecycleStage_PostPerspectiveAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostPerspectiveAsync;
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
  public async Task LifecycleStage_PostAllPerspectivesAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostAllPerspectivesAsync;
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
  public async Task LifecycleStage_PostLifecycleAsync_IsDefinedAsync() {
    var value = LifecycleStage.PostLifecycleAsync;
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
  public async Task LifecycleStage_ImmediateAsync_IsFirstValueAsync() {
    var value = (int)LifecycleStage.ImmediateAsync;
    await Assert.That(value).IsEqualTo(0);
  }

  [Test]
  public async Task LifecycleStage_ImmediateAsync_IsDefaultAsync() {
    var value = default(LifecycleStage);
    await Assert.That(value).IsEqualTo(LifecycleStage.ImmediateAsync);
  }
}
