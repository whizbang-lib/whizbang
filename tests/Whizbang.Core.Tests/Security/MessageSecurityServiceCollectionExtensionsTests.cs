using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for MessageSecurityServiceCollectionExtensions.
/// </summary>
/// <docs>core-concepts/message-security#registration</docs>
public class MessageSecurityServiceCollectionExtensionsTests {
  // ========================================
  // AddWhizbangMessageSecurity Tests
  // ========================================

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersRequiredServicesAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    // Assert - IScopeContextAccessor is registered
    var accessor = provider.GetService<IScopeContextAccessor>();
    await Assert.That(accessor).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersMessageContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var accessor = scope.ServiceProvider.GetService<IMessageContextAccessor>();

    // Assert
    await Assert.That(accessor).IsNotNull();
    await Assert.That(accessor).IsTypeOf<MessageContextAccessor>();
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersIMessageContextAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var context = scope.ServiceProvider.GetService<IMessageContext>();

    // Assert - IMessageContext should be resolvable
    await Assert.That(context).IsNotNull();
  }

  [Test]
  public async Task IMessageContext_ReadsUserIdFromScopeContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var scopeContextAccessor = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
    var messageContext = scope.ServiceProvider.GetRequiredService<IMessageContext>();

    // Set up security context with UserId
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "test-user-123", TenantId = "tenant-456" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act & Assert - IMessageContext.UserId should read from scope context
    await Assert.That(messageContext.UserId).IsEqualTo("test-user-123");
  }

  [Test]
  public async Task IMessageContext_ReadsFromMessageContextAccessorAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var messageContextAccessor = scope.ServiceProvider.GetRequiredService<IMessageContextAccessor>();
    var messageContext = scope.ServiceProvider.GetRequiredService<IMessageContext>();

    // Set up message context
    var correlationId = CorrelationId.New();
    var messageId = MessageId.New();
    var causationId = MessageId.New();
    var timestamp = DateTimeOffset.UtcNow;

    messageContextAccessor.Current = new MessageContext {
      MessageId = messageId,
      CorrelationId = correlationId,
      CausationId = causationId,
      Timestamp = timestamp
    };

    // Act & Assert - IMessageContext should read from message context accessor
    await Assert.That(messageContext.MessageId).IsEqualTo(messageId);
    await Assert.That(messageContext.CorrelationId).IsEqualTo(correlationId);
    await Assert.That(messageContext.CausationId).IsEqualTo(causationId);
    await Assert.That(messageContext.Timestamp).IsEqualTo(timestamp);
  }

  [Test]
  public async Task IMessageContext_UserIdPrefersSecurityContext_OverMessageContextAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var scopeContextAccessor = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
    var messageContextAccessor = scope.ServiceProvider.GetRequiredService<IMessageContextAccessor>();
    var messageContext = scope.ServiceProvider.GetRequiredService<IMessageContext>();

    // Set up security context with UserId
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "security-user" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Also set up message context with different UserId
    messageContextAccessor.Current = new MessageContext {
      UserId = "message-user"
    };

    // Act & Assert - UserId should come from security context (higher priority)
    await Assert.That(messageContext.UserId).IsEqualTo("security-user");
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    // Assert - Options are registered
    var options = provider.GetService<MessageSecurityOptions>();
    await Assert.That(options).IsNotNull();
    await Assert.That(options!.AllowAnonymous).IsFalse(); // Default value
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_WithConfiguration_AppliesOptionsAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity(options => {
      options.AllowAnonymous = true;
      options.Timeout = TimeSpan.FromSeconds(30);
      options.EnableAuditLogging = false;
    });
    var provider = services.BuildServiceProvider();

    // Assert
    var options = provider.GetRequiredService<MessageSecurityOptions>();
    await Assert.That(options.AllowAnonymous).IsTrue();
    await Assert.That(options.Timeout).IsEqualTo(TimeSpan.FromSeconds(30));
    await Assert.That(options.EnableAuditLogging).IsFalse();
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersDefaultExtractorAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var extractors = scope.ServiceProvider.GetServices<ISecurityContextExtractor>();

    // Assert - MessageHopSecurityExtractor is registered
    await Assert.That(extractors.Count()).IsGreaterThanOrEqualTo(1);
    await Assert.That(extractors.Any(e => e is MessageHopSecurityExtractor)).IsTrue();
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_RegistersProviderAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var securityProvider = scope.ServiceProvider.GetService<IMessageSecurityContextProvider>();

    // Assert
    await Assert.That(securityProvider).IsNotNull();
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_ReturnsSameServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddWhizbangMessageSecurity();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddWhizbangMessageSecurity_WithNullConfiguration_DoesNotThrowAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act & Assert - should not throw
    services.AddWhizbangMessageSecurity(null);
    var provider = services.BuildServiceProvider();
    var options = provider.GetService<MessageSecurityOptions>();
    await Assert.That(options).IsNotNull();
  }

  // ========================================
  // AddSecurityExtractor Tests
  // ========================================

  [Test]
  public async Task AddSecurityExtractor_RegistersExtractorAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();

    // Act
    services.AddSecurityExtractor<TestSecurityExtractor>();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var extractors = scope.ServiceProvider.GetServices<ISecurityContextExtractor>();

    // Assert
    await Assert.That(extractors.Any(e => e is TestSecurityExtractor)).IsTrue();
  }

  [Test]
  public async Task AddSecurityExtractor_ReturnsSameServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddSecurityExtractor<TestSecurityExtractor>();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddSecurityExtractor_MultipleExtractors_AllRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();

    // Act
    services.AddSecurityExtractor<TestSecurityExtractor>();
    services.AddSecurityExtractor<AnotherTestExtractor>();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var extractors = scope.ServiceProvider.GetServices<ISecurityContextExtractor>().ToList();

    // Assert - both custom extractors + default MessageHopSecurityExtractor
    await Assert.That(extractors.Count).IsGreaterThanOrEqualTo(3);
    await Assert.That(extractors.Any(e => e is TestSecurityExtractor)).IsTrue();
    await Assert.That(extractors.Any(e => e is AnotherTestExtractor)).IsTrue();
  }

  // ========================================
  // AddSecurityContextCallback Tests
  // ========================================

  [Test]
  public async Task AddSecurityContextCallback_RegistersCallbackAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();

    // Act
    services.AddSecurityContextCallback<TestSecurityCallback>();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var callbacks = scope.ServiceProvider.GetServices<ISecurityContextCallback>();

    // Assert
    await Assert.That(callbacks.Any(c => c is TestSecurityCallback)).IsTrue();
  }

  [Test]
  public async Task AddSecurityContextCallback_ReturnsSameServiceCollectionAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act
    var result = services.AddSecurityContextCallback<TestSecurityCallback>();

    // Assert
    await Assert.That(result).IsEqualTo(services);
  }

  [Test]
  public async Task AddSecurityContextCallback_MultipleCallbacks_AllRegisteredAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();

    // Act
    services.AddSecurityContextCallback<TestSecurityCallback>();
    services.AddSecurityContextCallback<AnotherTestCallback>();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var callbacks = scope.ServiceProvider.GetServices<ISecurityContextCallback>().ToList();

    // Assert
    await Assert.That(callbacks.Count).IsEqualTo(2);
    await Assert.That(callbacks.Any(c => c is TestSecurityCallback)).IsTrue();
    await Assert.That(callbacks.Any(c => c is AnotherTestCallback)).IsTrue();
  }

  // ========================================
  // Provider Scope Tests
  // ========================================

  [Test]
  public async Task Provider_IsScopedAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    // Act - create two scopes and resolve providers
    using var scope1 = provider.CreateScope();
    using var scope2 = provider.CreateScope();

    var provider1 = scope1.ServiceProvider.GetService<IMessageSecurityContextProvider>();
    var provider2 = scope2.ServiceProvider.GetService<IMessageSecurityContextProvider>();

    // Assert - different instances in different scopes
    await Assert.That(provider1).IsNotNull();
    await Assert.That(provider2).IsNotNull();
    await Assert.That(ReferenceEquals(provider1, provider2)).IsFalse();
  }

  [Test]
  public async Task ScopeContextAccessor_IsScopedAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    // Act - create two scopes and resolve accessors
    using var scope1 = provider.CreateScope();
    using var scope2 = provider.CreateScope();

    var accessor1 = scope1.ServiceProvider.GetService<IScopeContextAccessor>();
    var accessor2 = scope2.ServiceProvider.GetService<IScopeContextAccessor>();

    // Assert - different instances in different scopes (scoped registration)
    await Assert.That(accessor1).IsNotNull();
    await Assert.That(accessor2).IsNotNull();
    await Assert.That(ReferenceEquals(accessor1, accessor2)).IsFalse();
  }

  [Test]
  public async Task ScopeContextAccessor_StaticAccessorWorksWithoutDiAsync() {
    // Arrange - set context via static accessor (for singleton services like Dispatcher)
    var testContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope { UserId = "test-user" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: false);

    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      // Act
      ScopeContextAccessor.CurrentContext = testContext;

      // Assert
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope?.UserId).IsEqualTo("test-user");
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  [Test]
  public async Task ScopeContextAccessor_StaticAndInstanceAccessSameAsyncLocalAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var accessor = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();

    var testContext = new ImmutableScopeContext(
      new SecurityExtraction {
        Scope = new PerspectiveScope { UserId = "shared-user" },
        Roles = new HashSet<string>(),
        Permissions = new HashSet<Permission>(),
        SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
        Claims = new Dictionary<string, string>(),
        Source = "Test"
      },
      shouldPropagate: false);

    var previousContext = ScopeContextAccessor.CurrentContext;
    try {
      // Act - set via instance
      accessor.Current = testContext;

      // Assert - read via static accessor sees the same value
      await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
      await Assert.That(ScopeContextAccessor.CurrentContext!.Scope?.UserId).IsEqualTo("shared-user");

      // Assert - read via instance also sees the same value
      await Assert.That(accessor.Current!.Scope?.UserId).IsEqualTo("shared-user");
    } finally {
      ScopeContextAccessor.CurrentContext = previousContext;
    }
  }

  // ========================================
  // IScopeContext Factory Exception Tests
  // ========================================

  [Test]
  public async Task IScopeContext_WhenNoContextSet_ThrowsInvalidOperationExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();

    // Act & Assert - IScopeContext should throw when no context is set
    await Assert.That(() => scope.ServiceProvider.GetRequiredService<IScopeContext>())
      .Throws<InvalidOperationException>()
      .WithMessageMatching("*IScopeContext is not available*");
  }

  [Test]
  public async Task IScopeContext_WhenContextIsSet_ReturnsScopeContextAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    var provider = services.BuildServiceProvider();

    using var scope = provider.CreateScope();
    var accessor = scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>();
    var extraction = new SecurityExtraction {
      Scope = new PerspectiveScope { UserId = "scope-user", TenantId = "scope-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    accessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Act
    var scopeContext = scope.ServiceProvider.GetRequiredService<IScopeContext>();

    // Assert
    await Assert.That(scopeContext).IsNotNull();
    await Assert.That(scopeContext.Scope.UserId).IsEqualTo("scope-user");
  }

  // ========================================
  // Duplicate Registration Tests (TryAdd behavior)
  // ========================================

  [Test]
  public async Task AddWhizbangMessageSecurity_CalledTwice_FirstOptionsWinAsync() {
    // Arrange
    var services = new ServiceCollection();

    // Act - register twice with different options
    services.AddWhizbangMessageSecurity(o => o.AllowAnonymous = true);
    services.AddWhizbangMessageSecurity(o => o.AllowAnonymous = false);
    var provider = services.BuildServiceProvider();

    // Assert - first registration wins (TryAddSingleton)
    var options = provider.GetRequiredService<MessageSecurityOptions>();
    await Assert.That(options.AllowAnonymous).IsTrue();
  }

  // ========================================
  // Test Doubles
  // ========================================

  private sealed class TestSecurityExtractor : ISecurityContextExtractor {
    public int Priority => 50;

    public ValueTask<SecurityExtraction?> ExtractAsync(
      Whizbang.Core.Observability.IMessageEnvelope envelope,
      MessageSecurityOptions options,
      CancellationToken cancellationToken = default) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }
  }

  private sealed class AnotherTestExtractor : ISecurityContextExtractor {
    public int Priority => 60;

    public ValueTask<SecurityExtraction?> ExtractAsync(
      Whizbang.Core.Observability.IMessageEnvelope envelope,
      MessageSecurityOptions options,
      CancellationToken cancellationToken = default) {
      return ValueTask.FromResult<SecurityExtraction?>(null);
    }
  }

  private sealed class TestSecurityCallback : ISecurityContextCallback {
    public ValueTask OnContextEstablishedAsync(
      IScopeContext context,
      Whizbang.Core.Observability.IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  private sealed class AnotherTestCallback : ISecurityContextCallback {
    public ValueTask OnContextEstablishedAsync(
      IScopeContext context,
      Whizbang.Core.Observability.IMessageEnvelope envelope,
      IServiceProvider scopedProvider,
      CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }
}
