using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests that all awaiter classes correctly implement <see cref="IAwaiterIdentity"/>.
/// Verifies unique ID generation and stability.
/// </summary>
/// <tests>Whizbang.Core/IAwaiterIdentity.cs</tests>
[Category("PerspectiveSync")]
public class AwaiterIdentityTests {
  // ==========================================================================
  // PerspectiveSyncAwaiter
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_HasNonEmptyAwaiterIdAsync() {
    var awaiter = _createPerspectiveSyncAwaiter();
    await Assert.That(awaiter.AwaiterId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_TwoInstances_HaveDifferentIdsAsync() {
    var awaiter1 = _createPerspectiveSyncAwaiter();
    var awaiter2 = _createPerspectiveSyncAwaiter();
    await Assert.That(awaiter1.AwaiterId).IsNotEqualTo(awaiter2.AwaiterId);
  }

  [Test]
  public async Task PerspectiveSyncAwaiter_AwaiterIdIsStableAsync() {
    var awaiter = _createPerspectiveSyncAwaiter();
    var id1 = awaiter.AwaiterId;
    var id2 = awaiter.AwaiterId;
    await Assert.That(id1).IsEqualTo(id2);
  }

  // ==========================================================================
  // EventCompletionAwaiter
  // ==========================================================================

  [Test]
  public async Task EventCompletionAwaiter_HasNonEmptyAwaiterIdAsync() {
    var awaiter = new EventCompletionAwaiter(new SyncEventTracker());
    await Assert.That(awaiter.AwaiterId).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task EventCompletionAwaiter_TwoInstances_HaveDifferentIdsAsync() {
    var tracker = new SyncEventTracker();
    var awaiter1 = new EventCompletionAwaiter(tracker);
    var awaiter2 = new EventCompletionAwaiter(tracker);
    await Assert.That(awaiter1.AwaiterId).IsNotEqualTo(awaiter2.AwaiterId);
  }

  // ==========================================================================
  // IAwaiterIdentity interface contract
  // ==========================================================================

  [Test]
  public async Task PerspectiveSyncAwaiter_ImplementsIAwaiterIdentityAsync() {
    var awaiter = _createPerspectiveSyncAwaiter();
    await Assert.That(awaiter is IAwaiterIdentity).IsTrue();
  }

  [Test]
  public async Task EventCompletionAwaiter_ImplementsIAwaiterIdentityAsync() {
    var awaiter = new EventCompletionAwaiter(new SyncEventTracker());
    await Assert.That(awaiter is IAwaiterIdentity).IsTrue();
  }

  // ==========================================================================
  // Helpers
  // ==========================================================================

  private static PerspectiveSyncAwaiter _createPerspectiveSyncAwaiter() {
    return new PerspectiveSyncAwaiter(
        new MockWorkCoordinator(),
        new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
        NullLogger<PerspectiveSyncAwaiter>.Instance,
        new SyncEventTracker());
  }
}
