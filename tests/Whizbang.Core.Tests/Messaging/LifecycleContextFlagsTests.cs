using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Unit tests for the two rewind-aware convenience flags on
/// <see cref="ILifecycleContext"/> / <see cref="LifecycleExecutionContext"/>:
/// <c>IsReplay</c> and <c>IsNewEvent</c>.
/// </summary>
public class LifecycleContextFlagsTests {

  [Test]
  public async Task IsReplay_NullProcessingMode_ReturnsFalseAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = null
    };
    await Assert.That(ctx.IsReplay).IsFalse();
  }

  [Test]
  public async Task IsReplay_LiveProcessingMode_ReturnsFalseAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Live
    };
    await Assert.That(ctx.IsReplay).IsFalse();
  }

  [Test]
  public async Task IsReplay_ReplayProcessingMode_ReturnsTrueAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay
    };
    await Assert.That(ctx.IsReplay).IsTrue();
  }

  [Test]
  public async Task IsReplay_RebuildProcessingMode_ReturnsTrueAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Rebuild
    };
    await Assert.That(ctx.IsReplay).IsTrue();
  }

  [Test]
  public async Task IsNewEvent_DefaultsToTrueAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline
    };
    await Assert.That(ctx.IsNewEvent).IsTrue()
      .Because("Live processing, trigger events, and post-rewind arrivals are all 'new' by default.");
  }

  [Test]
  public async Task IsNewEvent_CanBeSetFalseAsync() {
    var ctx = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay,
      IsNewEvent = false
    };
    await Assert.That(ctx.IsNewEvent).IsFalse();
    await Assert.That(ctx.IsReplay).IsTrue();
  }

  [Test]
  public async Task WithExpression_IsNewEvent_PropagatesOnRecordCopyAsync() {
    var original = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      ProcessingMode = ProcessingMode.Replay,
      IsNewEvent = false
    };
    var updated = original with { CurrentStage = LifecycleStage.PostAllPerspectivesInline };
    await Assert.That(updated.IsNewEvent).IsFalse()
      .Because("Context is a record — 'with' must carry IsNewEvent forward unchanged.");
  }
}
