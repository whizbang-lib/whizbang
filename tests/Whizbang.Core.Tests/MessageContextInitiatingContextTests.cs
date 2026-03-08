using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests;

/// <summary>
/// Tests for MessageContext.New() integration with InitiatingContext.
/// Verifies that MessageContext.New() prioritizes InitiatingContext as the
/// source of truth for security values (UserId, TenantId).
/// </summary>
/// <docs>core-concepts/cascade-context#message-context-new</docs>
/// <tests>Whizbang.Core/MessageContext.cs:New</tests>
public class MessageContextInitiatingContextTests {

  [Before(Test)]
  public void Setup() {
    // Clear all AsyncLocals before each test
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  [After(Test)]
  public void Cleanup() {
    // Clear all AsyncLocals after each test
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
    MessageContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// Verifies that MessageContext.New() reads UserId and TenantId from
  /// InitiatingContext when it is available.
  /// </summary>
  [Test]
  public async Task New_WhenInitiatingContextAvailable_ShouldReadFromItAsync() {
    // Arrange
    var testUserId = "test-user@example.com";
    var testTenantId = "test-tenant-123";

    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = testUserId,
      TenantId = testTenantId
    };

    // Act
    var newContext = MessageContext.New();

    // Assert - should read from InitiatingContext
    await Assert.That(newContext.UserId).IsEqualTo(testUserId);
    await Assert.That(newContext.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that MessageContext.New() falls back to CurrentContext.Scope
  /// when InitiatingContext is null (backward compatibility).
  /// </summary>
  [Test]
  public async Task New_WhenInitiatingContextNull_ShouldFallbackToCurrentContextAsync() {
    // Arrange
    var testUserId = "scope-user@example.com";
    var testTenantId = "scope-tenant-456";

    // Only set CurrentContext, not InitiatingContext
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope {
          UserId = testUserId,
          TenantId = testTenantId
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);

    // Act
    var newContext = MessageContext.New();

    // Assert - should fall back to CurrentContext.Scope
    await Assert.That(newContext.UserId).IsEqualTo(testUserId);
    await Assert.That(newContext.TenantId).IsEqualTo(testTenantId);
  }

  /// <summary>
  /// Verifies that MessageContext.New() works correctly when both
  /// contexts are null (returns null UserId/TenantId).
  /// </summary>
  [Test]
  public async Task New_WhenBothContextsNull_ShouldReturnNullSecurityAsync() {
    // Arrange - both contexts are null (cleared in Setup)

    // Act
    var newContext = MessageContext.New();

    // Assert - should return null security values
    await Assert.That(newContext.UserId).IsNull();
    await Assert.That(newContext.TenantId).IsNull();
    // But other fields should be populated
    await Assert.That(newContext.CorrelationId.Value).IsNotDefault();
    await Assert.That(newContext.CausationId.Value).IsNotDefault();
  }

  /// <summary>
  /// Verifies that when InitiatingContext has values but CurrentContext.Scope
  /// has different values, InitiatingContext wins (source of truth).
  /// </summary>
  [Test]
  public async Task New_WhenBothContextsHaveValues_ShouldPrioritizeInitiatingContextAsync() {
    // Arrange
    var initiatingUserId = "initiating-user@example.com";
    var initiatingTenantId = "initiating-tenant-123";
    var scopeUserId = "scope-user@example.com";
    var scopeTenantId = "scope-tenant-456";

    // Set InitiatingContext (should win)
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = initiatingUserId,
      TenantId = initiatingTenantId
    };

    // Set CurrentContext with different values
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope {
          UserId = scopeUserId,
          TenantId = scopeTenantId
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);

    // Act
    var newContext = MessageContext.New();

    // Assert - InitiatingContext should win (source of truth)
    await Assert.That(newContext.UserId).IsEqualTo(initiatingUserId);
    await Assert.That(newContext.TenantId).IsEqualTo(initiatingTenantId);
  }

  /// <summary>
  /// Verifies that MessageContext.New() works correctly when InitiatingContext
  /// has null security values (should still be considered "available").
  /// </summary>
  [Test]
  public async Task New_WhenInitiatingContextHasNullSecurity_ShouldUseNullNotFallbackAsync() {
    // Arrange - InitiatingContext exists but has null security
    ScopeContextAccessor.CurrentInitiatingContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      Timestamp = DateTimeOffset.UtcNow,
      UserId = null,  // Explicitly null
      TenantId = null  // Explicitly null
    };

    // Set CurrentContext with values (should NOT be used)
    ScopeContextAccessor.CurrentContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope {
          UserId = "should-not-use@example.com",
          TenantId = "should-not-use-tenant"
        },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: true);

    // Act
    var newContext = MessageContext.New();

    // Assert - should use null from InitiatingContext, not fall back
    await Assert.That(newContext.UserId).IsNull();
    await Assert.That(newContext.TenantId).IsNull();
  }
}
