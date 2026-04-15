using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Scoping;

/// <summary>
/// Tests for the <see cref="IPerspectiveScopeFor{TModel}"/> interface contract.
/// </summary>
/// <tests>src/Whizbang.Core/Perspectives/IPerspectiveScopeFor.cs</tests>
public class IPerspectiveScopeForTests {
  [Test]
  public async Task ApplyScope_ReceivesOldAndNewScope_ReturnsNewScopeAsync() {
    // Arrange
    IPerspectiveScopeFor<TestModel> handler = new AcceptAllScopeHandler();
    var current = new PerspectiveScope { TenantId = "old-tenant" };
    var proposed = new PerspectiveScope { TenantId = "new-tenant" };

    // Act
    var result = handler.ApplyScope(current, proposed);

    // Assert
    await Assert.That(result.TenantId).IsEqualTo("new-tenant");
  }

  [Test]
  public async Task ApplyScope_CanRejectProposedScope_ReturnsCurrentAsync() {
    // Arrange
    IPerspectiveScopeFor<TestModel> handler = new RejectScopeHandler();
    var current = new PerspectiveScope { TenantId = "keep-this" };
    var proposed = new PerspectiveScope { TenantId = "reject-this" };

    // Act
    var result = handler.ApplyScope(current, proposed);

    // Assert
    await Assert.That(result.TenantId).IsEqualTo("keep-this");
  }

  [Test]
  public async Task ApplyScope_CanMergeScopes_ReturnsMergedAsync() {
    // Arrange
    IPerspectiveScopeFor<TestModel> handler = new MergeScopeHandler();
    var current = new PerspectiveScope { TenantId = "tenant-A", UserId = "user-1" };
    var proposed = new PerspectiveScope { TenantId = "tenant-B", OrganizationId = "org-new" };

    // Act
    var result = handler.ApplyScope(current, proposed);

    // Assert - keeps current tenant, takes proposed org, keeps current user
    await Assert.That(result.TenantId).IsEqualTo("tenant-A");
    await Assert.That(result.OrganizationId).IsEqualTo("org-new");
    await Assert.That(result.UserId).IsEqualTo("user-1");
  }

  [Test]
  public async Task ApplyScope_WithEmptyCurrentScope_CanAcceptProposedAsync() {
    // Arrange
    IPerspectiveScopeFor<TestModel> handler = new AcceptAllScopeHandler();
    var current = new PerspectiveScope();
    var proposed = new PerspectiveScope { TenantId = "first-scope" };

    // Act
    var result = handler.ApplyScope(current, proposed);

    // Assert
    await Assert.That(result.TenantId).IsEqualTo("first-scope");
  }

  // === Test Models ===

  private sealed class TestModel;

  /// <summary>Accepts the proposed scope as-is.</summary>
  private sealed class AcceptAllScopeHandler : IPerspectiveScopeFor<TestModel> {
    public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) =>
      proposedScope;
  }

  /// <summary>Rejects the proposed scope, keeps current.</summary>
  private sealed class RejectScopeHandler : IPerspectiveScopeFor<TestModel> {
    public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) =>
      currentScope;
  }

  /// <summary>Merges: keeps current tenant + user, takes proposed org.</summary>
  private sealed class MergeScopeHandler : IPerspectiveScopeFor<TestModel> {
    public PerspectiveScope ApplyScope(PerspectiveScope currentScope, PerspectiveScope proposedScope) =>
      new() {
        TenantId = currentScope.TenantId,
        UserId = currentScope.UserId,
        OrganizationId = proposedScope.OrganizationId
      };
  }
}
