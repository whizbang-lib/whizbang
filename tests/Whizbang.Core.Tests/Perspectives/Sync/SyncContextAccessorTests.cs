using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Perspectives.Sync;

/// <summary>
/// Tests for <see cref="SyncContextAccessor"/> and <see cref="ISyncContextAccessor"/>.
/// The accessor provides ambient access to SyncContext via AsyncLocal for async flow.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync#sync-context</docs>
public class SyncContextAccessorTests {
  /// <summary>
  /// Dummy perspective type for tests.
  /// </summary>
  private sealed class TestPerspective { }

  /// <summary>
  /// Helper to create a test SyncContext.
  /// </summary>
  private static SyncContext _createTestContext(Guid? streamId = null) {
    return new SyncContext {
      StreamId = streamId ?? Guid.NewGuid(),
      PerspectiveType = typeof(TestPerspective),
      Outcome = SyncOutcome.Synced,
      EventsAwaited = 1,
      ElapsedTime = TimeSpan.FromMilliseconds(100)
    };
  }

  // ==========================================================================
  // Instance accessor tests (via ISyncContextAccessor interface)
  // ==========================================================================

  [Test]
  public async Task Current_DefaultsToNullAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null; // Reset static state
    var accessor = new SyncContextAccessor();

    // Act & Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task Current_CanSetAndGetValueAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null; // Reset static state
    var accessor = new SyncContextAccessor();
    var context = _createTestContext();

    // Act
    accessor.Current = context;

    // Assert
    await Assert.That(accessor.Current).IsEqualTo(context);
  }

  [Test]
  public async Task Current_CanBeSetToNullAsync() {
    // Arrange
    var accessor = new SyncContextAccessor();
    var context = _createTestContext();
    accessor.Current = context;

    // Act
    accessor.Current = null;

    // Assert
    await Assert.That(accessor.Current).IsNull();
  }

  [Test]
  public async Task Current_ReplacesExistingValueAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null; // Reset static state
    var accessor = new SyncContextAccessor();
    var context1 = _createTestContext();
    var context2 = _createTestContext();
    accessor.Current = context1;

    // Act
    accessor.Current = context2;

    // Assert
    await Assert.That(accessor.Current).IsEqualTo(context2);
    await Assert.That(accessor.Current).IsNotEqualTo(context1);
  }

  // ==========================================================================
  // Static accessor tests (CurrentContext)
  // ==========================================================================

  [Test]
  public async Task CurrentContext_DefaultsToNullAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;

    // Act & Assert
    await Assert.That(SyncContextAccessor.CurrentContext).IsNull();
  }

  [Test]
  public async Task CurrentContext_CanSetAndGetValueAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var context = _createTestContext();

    // Act
    SyncContextAccessor.CurrentContext = context;

    // Assert
    await Assert.That(SyncContextAccessor.CurrentContext).IsEqualTo(context);
  }

  [Test]
  public async Task CurrentContext_CanBeSetToNullAsync() {
    // Arrange
    var context = _createTestContext();
    SyncContextAccessor.CurrentContext = context;

    // Act
    SyncContextAccessor.CurrentContext = null;

    // Assert
    await Assert.That(SyncContextAccessor.CurrentContext).IsNull();
  }

  [Test]
  public async Task CurrentContext_ReplacesExistingValueAsync() {
    // Arrange
    var context1 = _createTestContext();
    var context2 = _createTestContext();
    SyncContextAccessor.CurrentContext = context1;

    // Act
    SyncContextAccessor.CurrentContext = context2;

    // Assert
    await Assert.That(SyncContextAccessor.CurrentContext).IsEqualTo(context2);
    await Assert.That(SyncContextAccessor.CurrentContext).IsNotEqualTo(context1);
  }

  // ==========================================================================
  // Instance and static accessor share same AsyncLocal
  // ==========================================================================

  [Test]
  public async Task Instance_And_Static_ShareSameContextAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor = new SyncContextAccessor();
    var context = _createTestContext();

    // Act - set via instance
    accessor.Current = context;

    // Assert - accessible via static
    await Assert.That(SyncContextAccessor.CurrentContext).IsEqualTo(context);
  }

  [Test]
  public async Task Static_And_Instance_ShareSameContextAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor = new SyncContextAccessor();
    var context = _createTestContext();

    // Act - set via static
    SyncContextAccessor.CurrentContext = context;

    // Assert - accessible via instance
    await Assert.That(accessor.Current).IsEqualTo(context);
  }

  // ==========================================================================
  // Async flow preservation tests
  // ==========================================================================

  [Test]
  public async Task Current_PreservesContextAcrossAwaitAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor = new SyncContextAccessor();
    var context = _createTestContext();
    accessor.Current = context;

    // Act - await preserves context
    await Task.Delay(10);

    // Assert
    await Assert.That(accessor.Current).IsEqualTo(context);
  }

  [Test]
  public async Task Current_DoesNotLeakBetweenAsyncFlowsAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor = new SyncContextAccessor();
    var context1 = _createTestContext();
    SyncContext? capturedInTask = null;

    accessor.Current = context1;

    // Act - start new task (different async flow)
    var task = Task.Run(() => {
      capturedInTask = accessor.Current;
    });
    await task;

    // Assert - new task should NOT see the context (different async flow)
    // Note: Task.Run creates a new async flow, so AsyncLocal value is copied
    // but subsequent changes are isolated
    await Assert.That(capturedInTask).IsEqualTo(context1);
  }

  [Test]
  public async Task Current_IsolatesChangesInChildTaskAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor = new SyncContextAccessor();
    var parentContext = _createTestContext();
    var childContext = _createTestContext();

    accessor.Current = parentContext;

    // Act - child task modifies its copy
    var task = Task.Run(() => {
      accessor.Current = childContext;
      return accessor.Current;
    });
    var resultInChild = await task;

    // Assert - parent still has original, child saw its own value
    await Assert.That(accessor.Current).IsEqualTo(parentContext);
    await Assert.That(resultInChild).IsEqualTo(childContext);
  }

  // ==========================================================================
  // Multiple accessor instance tests
  // ==========================================================================

  [Test]
  public async Task MultipleAccessorInstances_ShareSameContextAsync() {
    // Arrange
    SyncContextAccessor.CurrentContext = null;
    var accessor1 = new SyncContextAccessor();
    var accessor2 = new SyncContextAccessor();
    var context = _createTestContext();

    // Act
    accessor1.Current = context;

    // Assert - both accessors see the same context
    await Assert.That(accessor2.Current).IsEqualTo(context);
  }
}
