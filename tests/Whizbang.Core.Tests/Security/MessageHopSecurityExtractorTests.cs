using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for MessageHopSecurityExtractor.
/// This extractor obtains security context from the message envelope's hop chain.
/// </summary>
/// <docs>core-concepts/message-security#message-hop-extractor</docs>
public class MessageHopSecurityExtractorTests {
  // ========================================
  // Priority Tests
  // ========================================

  [Test]
  public async Task Priority_ReturnsDefaultPriority_100Async() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();

    // Act
    var priority = extractor.Priority;

    // Assert
    await Assert.That(priority).IsEqualTo(100);
  }

  // ========================================
  // Extract From Hop SecurityContext Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithSecurityContextInHop_ReturnsExtractionAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant-123",
      UserId = "user-456"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-123");
    await Assert.That(result.Scope.UserId).IsEqualTo("user-456");
    await Assert.That(result.Source).IsEqualTo("MessageHop");
  }

  [Test]
  public async Task ExtractAsync_WithOnlyTenantId_ReturnsExtractionWithTenantAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant-only"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-only");
    await Assert.That(result.Scope.UserId).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithOnlyUserId_ReturnsExtractionWithUserAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      UserId = "user-only"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsNull();
    await Assert.That(result.Scope.UserId).IsEqualTo("user-only");
  }

  [Test]
  public async Task ExtractAsync_WithNoSecurityContext_ReturnsNullAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var envelope = _createEnvelopeWithoutSecurityContext();
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithEmptySecurityContext_ReturnsNullAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext(); // No TenantId or UserId
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - empty context should return null (no useful identity info)
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Multi-Hop Tests (Most Recent SecurityContext)
  // ========================================

  [Test]
  public async Task ExtractAsync_WithMultipleHops_ReturnsMostRecentSecurityContextAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var firstHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-1"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
      SecurityContext = new SecurityContext { TenantId = "old-tenant", UserId = "old-user" }
    };

    var secondHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-2"),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = new SecurityContext { TenantId = "new-tenant", UserId = "new-user" }
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [firstHop, secondHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - should get the most recent (second hop) context
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("new-tenant");
    await Assert.That(result.Scope.UserId).IsEqualTo("new-user");
  }

  [Test]
  public async Task ExtractAsync_IgnoresCausationHops_OnlyExtractsFromCurrentHopsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var causationHop = new MessageHop {
      Type = HopType.Causation,
      ServiceInstance = _createServiceInstance("causation-service"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
      SecurityContext = new SecurityContext { TenantId = "causation-tenant", UserId = "causation-user" }
    };

    var currentHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("current-service"),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = new SecurityContext { TenantId = "current-tenant", UserId = "current-user" }
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [causationHop, currentHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - should extract from current hop, not causation
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("current-tenant");
    await Assert.That(result.Scope.UserId).IsEqualTo("current-user");
  }

  // ========================================
  // Extraction Result Property Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_ReturnsEmptyRoles_SinceHopSecurityContextHasNoRolesAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ExtractAsync_ReturnsEmptyPermissions_SinceHopSecurityContextHasNoPermissionsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Permissions.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ExtractAsync_ReturnsEmptySecurityPrincipals_SinceHopSecurityContextHasNoneAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.SecurityPrincipals.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ExtractAsync_ReturnsEmptyClaims_SinceHopSecurityContextHasNoClaimsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Claims.Count).IsEqualTo(0);
  }

  // ========================================
  // Cancellation Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    // Act & Assert
    await Assert.That(async () =>
      await extractor.ExtractAsync(envelope, options, cts.Token)
    ).ThrowsExactly<OperationCanceledException>();
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceInstanceInfo _createServiceInstance(string serviceName = "test-service") => new() {
    ServiceName = serviceName,
    InstanceId = Guid.NewGuid(),
    HostName = "test-host",
    ProcessId = 1234
  };

  private static MessageEnvelope<TestMessage> _createEnvelopeWithSecurityContext(SecurityContext securityContext) {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = securityContext
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithoutSecurityContext() {
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      SecurityContext = null
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private sealed record TestMessage(string Value);
}
