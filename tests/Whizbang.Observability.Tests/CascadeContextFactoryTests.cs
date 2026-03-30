using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// Tests for CascadeContextFactory - creates CascadeContext from various sources.
/// <tests>tests/Whizbang.Observability.Tests/CascadeContextFactoryTests.cs</tests>
/// </summary>
public class CascadeContextFactoryTests {

  // ========================================
  // CONSTRUCTOR TESTS
  // ========================================

  [Test]
  public async Task Constructor_WithNullEnrichers_CreatesFactoryWithEmptyEnrichersAsync() {
    // Arrange & Act
    var factory = new CascadeContextFactory(null);

    // Assert - Should not throw, creates context without enrichment
    var context = factory.NewRoot();
    await Assert.That(context).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithEmptyEnrichers_CreatesFactoryAsync() {
    // Arrange & Act
    var factory = new CascadeContextFactory([]);

    // Assert
    var context = factory.NewRoot();
    await Assert.That(context).IsNotNull();
  }

  // ========================================
  // NewRoot TESTS
  // ========================================

  [Test]
  public async Task NewRoot_GeneratesNewIdentifiersAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.NewRoot();

    // Assert
    await Assert.That(context.CorrelationId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.CausationId.Value).IsNotEqualTo(Guid.Empty);
    await Assert.That(context.SecurityContext).IsNull();
  }

  [Test]
  public async Task NewRoot_WithAmbientSecurity_InheritsSecurityAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var scopeContext = _createTestScopeContext("user-123", "tenant-abc", shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var context = factory.NewRoot();

      // Assert
      await Assert.That(context.SecurityContext).IsNotNull();
      await Assert.That(context.SecurityContext!.UserId).IsEqualTo("user-123");
      await Assert.That(context.SecurityContext.TenantId).IsEqualTo("tenant-abc");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task NewRoot_AppliesEnrichersAsync() {
    // Arrange
    var enricher = new TestEnricher("key1", "value1");
    var factory = new CascadeContextFactory([enricher]);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.NewRoot();

    // Assert
    await Assert.That(context.Metadata).IsNotNull();
    await Assert.That(context.Metadata!["key1"]).IsEqualTo("value1");
  }

  // ========================================
  // FromEnvelope TESTS
  // ========================================

  [Test]
  public async Task FromEnvelope_ExtractsCorrelationIdFromFirstHopAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var correlationId = CorrelationId.New();
    var envelope = _createTestEnvelope(correlationId, null);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromEnvelope(envelope);

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task FromEnvelope_SetsCausationIdToEnvelopeMessageIdAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var envelope = _createTestEnvelope(CorrelationId.New(), null);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromEnvelope(envelope);

    // Assert
    await Assert.That(context.CausationId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  public async Task FromEnvelope_PrefersAmbientSecurityOverEnvelopeSecurityAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var envelopeSecurity = new SecurityContext { UserId = "envelope-user", TenantId = "envelope-tenant" };
    var envelope = _createTestEnvelope(CorrelationId.New(), envelopeSecurity);

    var scopeContext = _createTestScopeContext("ambient-user", "ambient-tenant", shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var context = factory.FromEnvelope(envelope);

      // Assert - Ambient takes precedence
      await Assert.That(context.SecurityContext).IsNotNull();
      await Assert.That(context.SecurityContext!.UserId).IsEqualTo("ambient-user");
      await Assert.That(context.SecurityContext.TenantId).IsEqualTo("ambient-tenant");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task FromEnvelope_FallsBackToEnvelopeSecurity_WhenNoAmbientAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var envelopeSecurity = new SecurityContext { UserId = "envelope-user", TenantId = "envelope-tenant" };
    var envelope = _createTestEnvelope(CorrelationId.New(), envelopeSecurity);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromEnvelope(envelope);

    // Assert
    await Assert.That(context.SecurityContext).IsNotNull();
    await Assert.That(context.SecurityContext!.UserId).IsEqualTo("envelope-user");
    await Assert.That(context.SecurityContext.TenantId).IsEqualTo("envelope-tenant");
  }

  [Test]
  public async Task FromEnvelope_GeneratesCorrelationId_WhenEnvelopeHasNoneAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var envelope = _createTestEnvelopeWithoutCorrelationId();
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromEnvelope(envelope);

    // Assert - Should generate new correlation ID
    await Assert.That(context.CorrelationId.Value).IsNotEqualTo(Guid.Empty);
  }

  [Test]
  public async Task FromEnvelope_ThrowsOnNullEnvelopeAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);

    // Act & Assert
    await Assert.That(() => factory.FromEnvelope(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FromEnvelope_AppliesEnrichersWithEnvelopeAsync() {
    // Arrange
    var enricher = new EnvelopeAwareEnricher();
    var factory = new CascadeContextFactory([enricher]);
    var envelope = _createTestEnvelope(CorrelationId.New(), null);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromEnvelope(envelope);

    // Assert - Enricher received the envelope
    await Assert.That(context.Metadata).IsNotNull();
    await Assert.That(context.Metadata!["hasEnvelope"]).IsEqualTo(true);
  }

  // ========================================
  // FromMessageContext TESTS
  // ========================================

  [Test]
  public async Task FromMessageContext_CopiesCorrelationIdAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var correlationId = CorrelationId.New();
    var messageId = MessageId.New();
    var messageContext = new MessageContext {
      MessageId = messageId,
      CorrelationId = correlationId,
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = "tenant-abc"
    };
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromMessageContext(messageContext);

    // Assert
    await Assert.That(context.CorrelationId).IsEqualTo(correlationId);
  }

  [Test]
  public async Task FromMessageContext_SetsCausationIdToMessageContextMessageIdAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var messageId = MessageId.New();
    var messageContext = new MessageContext {
      MessageId = messageId,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    };
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromMessageContext(messageContext);

    // Assert
    await Assert.That(context.CausationId).IsEqualTo(messageId);
  }

  [Test]
  public async Task FromMessageContext_CopiesSecurityFromMessageContextAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = "user-123",
      TenantId = "tenant-abc"
    };
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.FromMessageContext(messageContext);

    // Assert
    await Assert.That(context.SecurityContext).IsNotNull();
    await Assert.That(context.SecurityContext!.UserId).IsEqualTo("user-123");
    await Assert.That(context.SecurityContext.TenantId).IsEqualTo("tenant-abc");
  }

  [Test]
  public async Task FromMessageContext_FallsBackToAmbientSecurity_WhenMessageContextHasNoSecurityAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);
    var messageContext = new MessageContext {
      MessageId = MessageId.New(),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      UserId = null,
      TenantId = null
    };

    var scopeContext = _createTestScopeContext("ambient-user", "ambient-tenant", shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = scopeContext;

    try {
      // Act
      var context = factory.FromMessageContext(messageContext);

      // Assert
      await Assert.That(context.SecurityContext).IsNotNull();
      await Assert.That(context.SecurityContext!.UserId).IsEqualTo("ambient-user");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task FromMessageContext_ThrowsOnNullMessageContextAsync() {
    // Arrange
    var factory = new CascadeContextFactory([]);

    // Act & Assert
    await Assert.That(() => factory.FromMessageContext(null!)).Throws<ArgumentNullException>();
  }

  // ========================================
  // ENRICHER PIPELINE TESTS
  // ========================================

  [Test]
  public async Task EnricherPipeline_AppliesEnrichersInOrderAsync() {
    // Arrange
    var enricher1 = new TestEnricher("order", "1");
    var enricher2 = new TestEnricher("order", "2");  // Should overwrite
    var factory = new CascadeContextFactory([enricher1, enricher2]);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.NewRoot();

    // Assert - Second enricher's value should win
    await Assert.That(context.Metadata!["order"]).IsEqualTo("2");
  }

  [Test]
  public async Task EnricherPipeline_AccumulatesMetadataFromMultipleEnrichersAsync() {
    // Arrange
    var enricher1 = new TestEnricher("key1", "value1");
    var enricher2 = new TestEnricher("key2", "value2");
    var factory = new CascadeContextFactory([enricher1, enricher2]);
    ScopeContextAccessor.CurrentContext = null;

    // Act
    var context = factory.NewRoot();

    // Assert
    await Assert.That(context.Metadata!.Count).IsEqualTo(2);
    await Assert.That(context.Metadata["key1"]).IsEqualTo("value1");
    await Assert.That(context.Metadata["key2"]).IsEqualTo("value2");
  }

  // ========================================
  // HELPER METHODS
  // ========================================

  private static ImmutableScopeContext _createTestScopeContext(string? userId, string? tenantId, bool shouldPropagate) {
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope {
        UserId = userId,
        TenantId = tenantId
      },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "test"
    };
    return new ImmutableScopeContext(extraction, shouldPropagate);
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelope(CorrelationId correlationId, SecurityContext? securityContext) {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          CorrelationId = correlationId,
          Scope = securityContext != null ? ScopeDelta.FromSecurityContext(securityContext) : null
        }
      ]
    };
  }

  private static MessageEnvelope<TestMessage> _createTestEnvelopeWithoutCorrelationId() {
    return new MessageEnvelope<TestMessage> {
      MessageId = MessageId.New(),
      Payload = new TestMessage(),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          CorrelationId = null,
          Scope = null
        }
      ]
    };
  }

  // ========================================
  // TEST TYPES
  // ========================================

  private sealed class TestMessage;

  /// <summary>
  /// Test enricher that adds a key-value pair to metadata.
  /// </summary>
  private sealed class TestEnricher(string key, object value) : ICascadeContextEnricher {
    public CascadeContext Enrich(CascadeContext context, IMessageEnvelope? sourceEnvelope) {
      return context.WithMetadata(key, value);
    }
  }

  /// <summary>
  /// Test enricher that records whether it received an envelope.
  /// </summary>
  private sealed class EnvelopeAwareEnricher : ICascadeContextEnricher {
    public CascadeContext Enrich(CascadeContext context, IMessageEnvelope? sourceEnvelope) {
      return context.WithMetadata("hasEnvelope", sourceEnvelope is not null);
    }
  }
}
