using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "old-tenant", UserId = "old-user" })
    };

    var secondHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-2"),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "new-tenant", UserId = "new-user" })
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
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "causation-tenant", UserId = "causation-user" })
    };

    var currentHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("current-service"),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "current-tenant", UserId = "current-user" })
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
  public async Task ExtractAsync_WithNoCollections_ReturnsEmptyRolesAsync() {
    // Arrange - ScopeDelta with only Values (no Collections)
    var extractor = new MessageHopSecurityExtractor();
    var securityContext = new SecurityContext {
      TenantId = "tenant",
      UserId = "user"
    };
    var envelope = _createEnvelopeWithSecurityContext(securityContext);
    var options = new MessageSecurityOptions();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - no roles in ScopeDelta means empty roles
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Roles.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ExtractAsync_WithNoCollections_ReturnsEmptyPermissionsAsync() {
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
  public async Task ExtractAsync_WithNoCollections_ReturnsEmptySecurityPrincipalsAsync() {
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
  public async Task ExtractAsync_WithNoCollections_ReturnsEmptyClaimsAsync() {
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
  // Full ScopeDelta Extraction Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithRolesInScopeDelta_ExtractsRolesAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();
    var envelope = _createEnvelopeWithScopeAndRoles("user-1", "tenant-1", ["Admin", "User"]);

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("tenant-1");
    await Assert.That(result.Roles).Contains("Admin");
    await Assert.That(result.Roles).Contains("User");
    await Assert.That(result.Roles.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ExtractAsync_WithPermissionsInScopeDelta_ExtractsPermissionsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();
    var envelope = _createEnvelopeWithScopeAndPermissions("user-1", "tenant-1", ["orders.read", "orders.write"]);

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Permissions.Count).IsEqualTo(2);
    await Assert.That(result.Permissions).Contains(new Permission("orders.read"));
    await Assert.That(result.Permissions).Contains(new Permission("orders.write"));
  }

  [Test]
  public async Task ExtractAsync_WithMultipleHops_MergesRolesFromAllHopsAsync() {
    // Arrange - first hop sets roles, second hop adds more
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(new { t = "tenant-1", u = "user-1" });
    string[] firstRolesArray = ["User"];
    string[] addedRolesArray = ["Admin"];
    var firstRoles = System.Text.Json.JsonSerializer.SerializeToElement(firstRolesArray);
    var addedRoles = System.Text.Json.JsonSerializer.SerializeToElement(addedRolesArray);

    var firstHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-1"),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Roles] = new CollectionChanges { Set = firstRoles }
        }
      }
    };

    var secondHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance("service-2"),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Roles] = new CollectionChanges { Add = addedRoles }
        }
      }
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [firstHop, secondHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert - should have merged roles from both hops
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Roles).Contains("User");
    await Assert.That(result.Roles).Contains("Admin");
    await Assert.That(result.Roles.Count).IsEqualTo(2);
  }

  [Test]
  public async Task ExtractAsync_WithActualAndEffectivePrincipal_ExtractsPrincipalsAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(new { t = "tenant-1", u = "user-1" });
    var actualElement = System.Text.Json.JsonSerializer.SerializeToElement("admin@example.com");
    var effectiveElement = System.Text.Json.JsonSerializer.SerializeToElement("service-account");
    var typeElement = System.Text.Json.JsonSerializer.SerializeToElement((int)SecurityContextType.Impersonated);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement,
          [ScopeProp.Actual] = actualElement,
          [ScopeProp.Effective] = effectiveElement,
          [ScopeProp.Type] = typeElement
        }
      }
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.ActualPrincipal).IsEqualTo("admin@example.com");
    await Assert.That(result.EffectivePrincipal).IsEqualTo("service-account");
    await Assert.That(result.ContextType).IsEqualTo(SecurityContextType.Impersonated);
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
  // Argument Validation Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    // Act & Assert
    await Assert.That(async () =>
      await extractor.ExtractAsync(null!, options, CancellationToken.None)
    ).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task ExtractAsync_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var envelope = _createEnvelopeWithoutSecurityContext();

    // Act & Assert
    await Assert.That(async () =>
      await extractor.ExtractAsync(envelope, null!, CancellationToken.None)
    ).ThrowsExactly<ArgumentNullException>();
  }

  // ========================================
  // Null/Empty Hops Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithNullHops_ReturnsNullAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = null!
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithEmptyHops_ReturnsNullAsync() {
    // Arrange
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = []
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Hop.Scope Edge Cases
  // ========================================

  [Test]
  public async Task ExtractAsync_WithHopScopeNull_ReturnsNullAsync() {
    // Arrange - Current hop with Scope = null
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = null
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithHopScopeNoChanges_ReturnsNullAsync() {
    // Arrange - Current hop with ScopeDelta that has no changes (HasChanges = false)
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta() // Empty ScopeDelta - HasChanges is false
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithMixedNullAndValidScopes_ExtractsFromValidAsync() {
    // Arrange - Multiple hops: one with null scope, one with no changes, one with valid scope
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var nullScopeHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2),
      Scope = null
    };

    var noChangesHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
      Scope = new ScopeDelta()
    };

    var validHop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "valid-tenant", UserId = "valid-user" })
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [nullScopeHop, noChangesHop, validHop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("valid-tenant");
    await Assert.That(result.Scope.UserId).IsEqualTo("valid-user");
  }

  // ========================================
  // Logger Tests
  // ========================================

  [Test]
  public async Task ExtractAsync_WithLogger_NoScopeFound_LogsAndReturnsNullAsync() {
    // Arrange - With a logger to cover logging branches
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();
    var envelope = _createEnvelopeWithoutSecurityContext();

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithLogger_NullHops_LogsAndReturnsNullAsync() {
    // Arrange - With logger, null hops to cover HopsNullOrEmpty log
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = null!
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithLogger_EmptyHops_LogsAndReturnsNullAsync() {
    // Arrange
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();
    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = []
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithLogger_ValidExtraction_LogsAndReturnsExtractionAsync() {
    // Arrange
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();
    var envelope = _createEnvelopeWithSecurityContext(new SecurityContext { TenantId = "t1", UserId = "u1" });

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Scope.TenantId).IsEqualTo("t1");
  }

  [Test]
  public async Task ExtractAsync_WithLogger_NullScopeHop_LogsHopScopeNullAsync() {
    // Arrange - with logger to cover HopScopeNull logging branch
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = null
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task ExtractAsync_WithLogger_NoChangesHop_LogsHopScopeValuesNullAsync() {
    // Arrange - with logger to cover HopScopeValuesNull logging branch
    var logger = NullLogger<MessageHopSecurityExtractor>.Instance;
    var extractor = new MessageHopSecurityExtractor(logger);
    var options = new MessageSecurityOptions();

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta() // No changes
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  // ========================================
  // Only Non-Current Hops Test
  // ========================================

  [Test]
  public async Task ExtractAsync_WithOnlyNonCurrentHops_ReturnsNullAsync() {
    // Arrange - All hops are Causation type
    var extractor = new MessageHopSecurityExtractor();
    var options = new MessageSecurityOptions();

    var hop = new MessageHop {
      Type = HopType.Causation,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "t", UserId = "u" })
    };

    var envelope = new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test"),
      Hops = [hop]
    };

    // Act
    var result = await extractor.ExtractAsync(envelope, options, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
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
      Scope = ScopeDelta.FromSecurityContext(securityContext)
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
      Scope = null
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithScopeAndRoles(
      string userId, string tenantId, string[] roles) {
    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(new { t = tenantId, u = userId });
    var rolesElement = System.Text.Json.JsonSerializer.SerializeToElement(roles);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Roles] = new CollectionChanges { Set = rolesElement }
        }
      }
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private static MessageEnvelope<TestMessage> _createEnvelopeWithScopeAndPermissions(
      string userId, string tenantId, string[] permissions) {
    var scopeElement = System.Text.Json.JsonSerializer.SerializeToElement(new { t = tenantId, u = userId });
    var permsElement = System.Text.Json.JsonSerializer.SerializeToElement(permissions);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _createServiceInstance(),
      Timestamp = DateTimeOffset.UtcNow,
      Scope = new ScopeDelta {
        Values = new Dictionary<ScopeProp, System.Text.Json.JsonElement> {
          [ScopeProp.Scope] = scopeElement
        },
        Collections = new Dictionary<ScopeProp, CollectionChanges> {
          [ScopeProp.Perms] = new CollectionChanges { Set = permsElement }
        }
      }
    };

    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage("test-payload"),
      Hops = [hop]
    };
  }

  private sealed record TestMessage(string Value);
}
