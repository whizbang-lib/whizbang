using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncContext"/>.
/// SyncContext provides sync status information to handlers that can inject it.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync#sync-context</docs>
public class SyncContextTests {
  /// <summary>
  /// Dummy perspective type for tests.
  /// </summary>
  private sealed class TestPerspective { }

  // ==========================================================================
  // Property storage tests
  // ==========================================================================

  [Test]
  public async Task SyncContext_StoresAllPropertiesAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var elapsed = TimeSpan.FromMilliseconds(150);

    // Act
    var context = new SyncContext {
      StreamId = streamId,
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 5,
      ElapsedTime = elapsed,
      FailureReason = null
    };

    // Assert
    await Assert.That(context.StreamId).IsEqualTo(streamId);
    await Assert.That(context.PerspectiveType).IsEqualTo(typeof(TestPerspective));
    await Assert.That(context.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That(context.EventsAwaited).IsEqualTo(5);
    await Assert.That(context.ElapsedTime).IsEqualTo(elapsed);
    await Assert.That(context.FailureReason).IsNull();
  }

  // ==========================================================================
  // IsSuccess computed property tests
  // ==========================================================================

  [Test]
  public async Task SyncContext_IsSuccess_TrueWhenSyncedAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 1,
      ElapsedTime = TimeSpan.FromMilliseconds(50)
    };

    // Act & Assert
    await Assert.That(context.IsSuccess).IsTrue();
  }

  [Test]
  public async Task SyncContext_IsSuccess_FalseWhenTimedOutAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.TimedOut,
      EventsAwaited = 0,
      ElapsedTime = TimeSpan.FromSeconds(5)
    };

    // Act & Assert
    await Assert.That(context.IsSuccess).IsFalse();
  }

  [Test]
  public async Task SyncContext_IsSuccess_FalseWhenNoPendingEventsAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.NoPendingEvents,
      EventsAwaited = 0,
      ElapsedTime = TimeSpan.FromMilliseconds(10)
    };

    // Act & Assert
    await Assert.That(context.IsSuccess).IsFalse();
  }

  // ==========================================================================
  // IsTimedOut computed property tests
  // ==========================================================================

  [Test]
  public async Task SyncContext_IsTimedOut_TrueOnlyWhenTimedOutAsync() {
    // Arrange - timed out
    var timedOutContext = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.TimedOut,
      EventsAwaited = 0,
      ElapsedTime = TimeSpan.FromSeconds(5)
    };

    // Arrange - synced
    var syncedContext = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 1,
      ElapsedTime = TimeSpan.FromMilliseconds(50)
    };

    // Arrange - no pending events
    var noPendingContext = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.NoPendingEvents,
      EventsAwaited = 0,
      ElapsedTime = TimeSpan.FromMilliseconds(10)
    };

    // Assert
    await Assert.That(timedOutContext.IsTimedOut).IsTrue();
    await Assert.That(syncedContext.IsTimedOut).IsFalse();
    await Assert.That(noPendingContext.IsTimedOut).IsFalse();
  }

  // ==========================================================================
  // FailureReason tests
  // ==========================================================================

  [Test]
  public async Task SyncContext_FailureReason_NullWhenSuccessAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 1,
      ElapsedTime = TimeSpan.FromMilliseconds(50),
      FailureReason = null
    };

    // Assert
    await Assert.That(context.FailureReason).IsNull();
  }

  [Test]
  public async Task SyncContext_FailureReason_SetWhenTimedOutAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.TimedOut,
      EventsAwaited = 0,
      ElapsedTime = TimeSpan.FromSeconds(5),
      FailureReason = "Timeout exceeded waiting for perspective sync"
    };

    // Assert
    await Assert.That(context.FailureReason).IsNotNull();
    await Assert.That(context.FailureReason).Contains("Timeout");
  }

  // ==========================================================================
  // Default value tests
  // ==========================================================================

  [Test]
  public async Task SyncContext_DefaultEventsAwaited_IsZeroAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      ElapsedTime = TimeSpan.Zero
    };

    // Assert
    await Assert.That(context.EventsAwaited).IsEqualTo(0);
  }

  [Test]
  public async Task SyncContext_DefaultElapsedTime_IsZeroAsync() {
    // Arrange
    var context = new SyncContext {
      StreamId = Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced
    };

    // Assert
    await Assert.That(context.ElapsedTime).IsEqualTo(TimeSpan.Zero);
  }
}
