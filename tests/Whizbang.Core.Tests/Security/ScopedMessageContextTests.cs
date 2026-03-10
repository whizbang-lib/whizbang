using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="ScopedMessageContext"/>.
/// Ensures the scoped message context correctly reads from accessors with proper fallback behavior.
/// </summary>
[Category("Security")]
public class ScopedMessageContextTests {
  // === InitiatingContext Priority Tests (SOURCE OF TRUTH) ===

  [Test]
  public async Task UserId_WithInitiatingContext_ReturnsInitiatingUserIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    // Set InitiatingContext (should be highest priority)
    scopeAccessor.InitiatingContext = new TestMessageContext { UserId = "initiating-user", TenantId = "initiating-tenant" };

    // Set ScopeContext with different value (should be ignored)
    var extraction = _createExtraction("scope-user", "scope-tenant");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set MessageContext with different value (should be ignored)
    messageAccessor.Current = new TestMessageContext { UserId = "message-user", TenantId = "message-tenant" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert - InitiatingContext takes priority (SOURCE OF TRUTH)
    await Assert.That(userId).IsEqualTo("initiating-user");
  }

  [Test]
  public async Task TenantId_WithInitiatingContext_ReturnsInitiatingTenantIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    // Set InitiatingContext (should be highest priority)
    scopeAccessor.InitiatingContext = new TestMessageContext { UserId = "initiating-user", TenantId = "initiating-tenant" };

    // Set ScopeContext with different value (should be ignored)
    var extraction = _createExtraction("scope-user", "scope-tenant");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set MessageContext with different value (should be ignored)
    messageAccessor.Current = new TestMessageContext { UserId = "message-user", TenantId = "message-tenant" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var tenantId = scopedContext.TenantId;

    // Assert - InitiatingContext takes priority (SOURCE OF TRUTH)
    await Assert.That(tenantId).IsEqualTo("initiating-tenant");
  }

  [Test]
  public async Task UserId_WithInitiatingContextNull_FallsBackToScopeContextAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    // InitiatingContext is null - should fall back to ScopeContext
    scopeAccessor.InitiatingContext = null;

    // Set ScopeContext (should be used as fallback)
    var extraction = _createExtraction("scope-user", "scope-tenant");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    messageAccessor.Current = new TestMessageContext { UserId = "message-user" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert - Falls back to ScopeContext
    await Assert.That(userId).IsEqualTo("scope-user");
  }

  [Test]
  public async Task UserId_WithInitiatingContextUserIdNull_FallsBackToScopeContextAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    // InitiatingContext exists but UserId is null
    scopeAccessor.InitiatingContext = new TestMessageContext { UserId = null, TenantId = "initiating-tenant" };

    // Set ScopeContext (should be used as fallback for UserId)
    var extraction = _createExtraction("scope-user", "scope-tenant");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert - Falls back to ScopeContext since InitiatingContext.UserId is null
    await Assert.That(userId).IsEqualTo("scope-user");
  }

  // === UserId Tests (Backward Compatibility) ===

  [Test]
  public async Task UserId_WithScopeContext_ReturnsScopeUserIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    var extraction = _createExtraction("scope-user", "tenant-1");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);
    messageAccessor.Current = new TestMessageContext { UserId = "message-user" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert - Scope context takes precedence
    await Assert.That(userId).IsEqualTo("scope-user");
  }

  [Test]
  public async Task UserId_WithoutScopeContext_FallsBackToMessageContextAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor { Current = null };
    var messageAccessor = new MessageContextAccessor();
    messageAccessor.Current = new TestMessageContext { UserId = "message-user" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert - Falls back to message context
    await Assert.That(userId).IsEqualTo("message-user");
  }

  [Test]
  public async Task UserId_WithBothNull_ReturnsNullAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor { Current = null };
    var messageAccessor = new MessageContextAccessor { Current = null };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var userId = scopedContext.UserId;

    // Assert
    await Assert.That(userId).IsNull();
  }

  // === TenantId Tests ===

  [Test]
  public async Task TenantId_WithScopeContext_ReturnsScopeTenantIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();

    var extraction = _createExtraction("user-1", "scope-tenant");
    scopeAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);
    messageAccessor.Current = new TestMessageContext { TenantId = "message-tenant" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var tenantId = scopedContext.TenantId;

    // Assert - Scope context takes precedence
    await Assert.That(tenantId).IsEqualTo("scope-tenant");
  }

  [Test]
  public async Task TenantId_WithoutScopeContext_FallsBackToMessageContextAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor { Current = null };
    var messageAccessor = new MessageContextAccessor();
    messageAccessor.Current = new TestMessageContext { TenantId = "message-tenant" };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var tenantId = scopedContext.TenantId;

    // Assert - Falls back to message context
    await Assert.That(tenantId).IsEqualTo("message-tenant");
  }

  // === MessageId Tests ===

  [Test]
  public async Task MessageId_WithMessageContext_ReturnsContextMessageIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var expectedId = MessageId.New();
    messageAccessor.Current = new TestMessageContext { MessageId = expectedId };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var messageId = scopedContext.MessageId;

    // Assert
    await Assert.That(messageId).IsEqualTo(expectedId);
  }

  [Test]
  public async Task MessageId_WithoutMessageContext_GeneratesNewIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor { Current = null };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var messageId = scopedContext.MessageId;

    // Assert - Should generate a new MessageId
    await Assert.That(messageId.Value).IsNotEqualTo(Guid.Empty);
  }

  // === CorrelationId Tests ===

  [Test]
  public async Task CorrelationId_WithMessageContext_ReturnsContextCorrelationIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var expectedId = CorrelationId.New();
    messageAccessor.Current = new TestMessageContext { CorrelationId = expectedId };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var correlationId = scopedContext.CorrelationId;

    // Assert
    await Assert.That(correlationId).IsEqualTo(expectedId);
  }

  [Test]
  public async Task CorrelationId_WithoutMessageContext_GeneratesNewIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor { Current = null };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var correlationId = scopedContext.CorrelationId;

    // Assert - Should generate a new CorrelationId
    await Assert.That(correlationId.Value).IsNotEqualTo(Guid.Empty);
  }

  // === CausationId Tests ===

  [Test]
  public async Task CausationId_WithMessageContext_ReturnsContextCausationIdAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var expectedId = MessageId.New();
    messageAccessor.Current = new TestMessageContext { CausationId = expectedId };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var causationId = scopedContext.CausationId;

    // Assert
    await Assert.That(causationId).IsEqualTo(expectedId);
  }

  // === Timestamp Tests ===

  [Test]
  public async Task Timestamp_WithMessageContext_ReturnsContextTimestampAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var expectedTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    messageAccessor.Current = new TestMessageContext { Timestamp = expectedTime };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var timestamp = scopedContext.Timestamp;

    // Assert
    await Assert.That(timestamp).IsEqualTo(expectedTime);
  }

  [Test]
  public async Task Timestamp_WithoutMessageContext_ReturnsCurrentTimeAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor { Current = null };
    var beforeCall = DateTimeOffset.UtcNow;

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var timestamp = scopedContext.Timestamp;
    var afterCall = DateTimeOffset.UtcNow;

    // Assert - Should be within the test timeframe
    await Assert.That(timestamp).IsGreaterThanOrEqualTo(beforeCall);
    await Assert.That(timestamp).IsLessThanOrEqualTo(afterCall);
  }

  // === Metadata Tests ===

  [Test]
  public async Task Metadata_WithMessageContext_ReturnsContextMetadataAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor();
    var expectedMetadata = new Dictionary<string, object> { { "key", "value" } };
    messageAccessor.Current = new TestMessageContext { Metadata = expectedMetadata };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var metadata = scopedContext.Metadata;

    // Assert
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata.ContainsKey("key")).IsTrue();
    await Assert.That(metadata["key"]).IsEqualTo("value");
  }

  [Test]
  public async Task Metadata_WithoutMessageContext_ReturnsEmptyDictionaryAsync() {
    // Arrange
    var scopeAccessor = new ScopeContextAccessor();
    var messageAccessor = new MessageContextAccessor { Current = null };

    var scopedContext = new ScopedMessageContext(messageAccessor, scopeAccessor);

    // Act
    var metadata = scopedContext.Metadata;

    // Assert
    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata.Count).IsEqualTo(0);
  }

  // === Helper Methods ===

  private static SecurityExtraction _createExtraction(string? userId, string? tenantId) {
    return new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = userId, TenantId = tenantId },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "TestSource"
    };
  }

  /// <summary>
  /// Test implementation of IMessageContext for test scenarios.
  /// </summary>
  private sealed class TestMessageContext : IMessageContext {
    public MessageId MessageId { get; init; } = MessageId.New();
    public CorrelationId CorrelationId { get; init; } = CorrelationId.New();
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public IScopeContext? ScopeContext { get; init; }
    public ICallerInfo? CallerInfo => null;
  }
}
