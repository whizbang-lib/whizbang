using Whizbang.Core.Security;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for the MessageContextAccessor implementation.
/// Validates AsyncLocal isolation semantics — especially that child scope writes
/// never corrupt the parent's context (the bug fixed in PR #176).
/// </summary>
/// <tests>MessageContextAccessor</tests>
[NotInParallel]
public class MessageContextAccessorTests {
  [After(Test)]
  public Task CleanupAsync() {
    MessageContextAccessor.CurrentContext = null;
    return Task.CompletedTask;
  }

  // === Basic Property Tests ===

  [Test]
  public async Task CurrentContext_Initially_ReturnsNullAsync() {
    await Assert.That(MessageContextAccessor.CurrentContext).IsNull();
  }

  [Test]
  public async Task CurrentContext_AfterSet_ReturnsContextAsync() {
    // Arrange
    var context = _createContext("tenant-1", "user-1");

    // Act
    MessageContextAccessor.CurrentContext = context;

    // Assert
    await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
    await Assert.That(MessageContextAccessor.CurrentContext!.TenantId).IsEqualTo("tenant-1");
    await Assert.That(MessageContextAccessor.CurrentContext!.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task CurrentContext_SetToNull_ReturnsNullAsync() {
    // Arrange
    MessageContextAccessor.CurrentContext = _createContext("tenant-1", "user-1");

    // Act
    MessageContextAccessor.CurrentContext = null;

    // Assert
    await Assert.That(MessageContextAccessor.CurrentContext).IsNull();
  }

  // === Instance/Static Equivalence ===

  [Test]
  public async Task Current_InstanceProperty_ReadsSameValueAsStaticCurrentContextAsync() {
    // Arrange
    var accessor = new MessageContextAccessor();
    var context = _createContext("tenant-eq", "user-eq");

    // Act
    MessageContextAccessor.CurrentContext = context;

    // Assert
    await Assert.That(accessor.Current).IsSameReferenceAs(MessageContextAccessor.CurrentContext);
  }

  [Test]
  public async Task Current_InstanceSet_WritesToStaticCurrentContextAsync() {
    // Arrange
    var accessor = new MessageContextAccessor();
    var context = _createContext("tenant-inst", "user-inst");

    // Act
    accessor.Current = context;

    // Assert
    await Assert.That(MessageContextAccessor.CurrentContext).IsSameReferenceAs(context);
  }

  // === AsyncLocal Propagation Tests ===

  [Test]
  public async Task CurrentContext_AcrossAsyncCalls_PropagatesAsync() {
    // Arrange
    var context = _createContext("tenant-async", "user-async");
    MessageContextAccessor.CurrentContext = context;

    // Act — access from async method
    var tenantId = await Task.Run(() => MessageContextAccessor.CurrentContext?.TenantId);

    // Assert
    await Assert.That(tenantId).IsEqualTo("tenant-async");
  }

  // === THE CRITICAL BUG TEST ===

  [Test]
  public async Task CurrentContext_ChildScopeSetsDifferentContext_ParentContextIsNotCorruptedAsync() {
    // Arrange — parent sets context
    var parentContext = _createContext("parent-tenant", "parent-user");
    MessageContextAccessor.CurrentContext = parentContext;

    // Act — child scope sets a DIFFERENT context (simulates local-dispatch cascade)
    await Task.Run(() => {
      MessageContextAccessor.CurrentContext = _createContext("child-tenant", "child-user");
    });

    // Assert — parent's context must survive unchanged
    // With the buggy holder pattern, the child setter nulls holder.Context on the shared
    // reference before creating a new holder, corrupting the parent's context to null.
    await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
    await Assert.That(MessageContextAccessor.CurrentContext!.TenantId).IsEqualTo("parent-tenant");
    await Assert.That(MessageContextAccessor.CurrentContext!.UserId).IsEqualTo("parent-user");
  }

  [Test]
  public async Task CurrentContext_ChildScopeSetsThenClears_ParentRetainsContextAsync() {
    // Arrange
    var parentContext = _createContext("parent-tenant", "parent-user");
    MessageContextAccessor.CurrentContext = parentContext;

    // Act — child sets a context then clears it
    await Task.Run(() => {
      MessageContextAccessor.CurrentContext = _createContext("child-tenant", "child-user");
      MessageContextAccessor.CurrentContext = null;
    });

    // Assert — parent still has its original context
    await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
    await Assert.That(MessageContextAccessor.CurrentContext!.TenantId).IsEqualTo("parent-tenant");
  }

  [Test]
  public async Task CurrentContext_MultipleCascadeLevels_EachParentRetainsContextAsync() {
    // Arrange — simulates: parent → child → grandchild cascade
    var parentContext = _createContext("L0-tenant", "L0-user");
    MessageContextAccessor.CurrentContext = parentContext;

    // Act — child creates grandchild
    string? childSeenAfterGrandchild = null;
    await Task.Run(async () => {
      MessageContextAccessor.CurrentContext = _createContext("L1-tenant", "L1-user");

      // Grandchild scope
      await Task.Run(() => {
        MessageContextAccessor.CurrentContext = _createContext("L2-tenant", "L2-user");
      });

      // Child checks its own context after grandchild completes
      childSeenAfterGrandchild = MessageContextAccessor.CurrentContext?.TenantId;
    });

    // Assert — parent retains L0 context
    await Assert.That(MessageContextAccessor.CurrentContext).IsNotNull();
    await Assert.That(MessageContextAccessor.CurrentContext!.TenantId).IsEqualTo("L0-tenant");

    // Assert — child retained L1 context after grandchild set L2
    await Assert.That(childSeenAfterGrandchild).IsEqualTo("L1-tenant");
  }

  [Test]
  public async Task CurrentContext_InParallelTasks_HasIsolatedContextsAsync() {
    // Arrange
    var results = new string?[5];

    // Act — parallel tasks each set their own context
    var tasks = Enumerable.Range(0, 5).Select(async i => {
      MessageContextAccessor.CurrentContext = _createContext($"tenant-{i}", $"user-{i}");
      // Yield to allow interleaving
      await Task.Yield();
      results[i] = MessageContextAccessor.CurrentContext?.TenantId;
    }).ToArray();

    await Task.WhenAll(tasks);

    // Assert — each task should see its own tenant ID
    for (var i = 0; i < 5; i++) {
      await Assert.That(results[i]).IsEqualTo($"tenant-{i}");
    }
  }

  private static MessageContext _createContext(string tenantId, string userId) =>
    new() {
      TenantId = tenantId,
      UserId = userId
    };
}
