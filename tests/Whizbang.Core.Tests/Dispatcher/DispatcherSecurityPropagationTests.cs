using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for automatic security context propagation to outgoing message hops.
/// Verifies that the Dispatcher attaches ScopeDelta from IScopeContextAccessor.Current
/// to MessageHop.Scope when ShouldPropagate is true.
/// </summary>
/// <docs>core-concepts/message-security#automatic-security-propagation</docs>
[Category("Security")]
[Category("Dispatcher")]
public class DispatcherSecurityPropagationTests {
  /// <summary>
  /// When IScopeContextAccessor.Current contains an ImmutableScopeContext with ShouldPropagate=true,
  /// the Dispatcher should attach the SecurityContext to the outgoing message hop.
  /// </summary>
  [Test]
  public async Task Dispatcher_WithScopeContext_PropagatesSecurityToOutgoingHopAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up scope context with propagation enabled
    var scope = new PerspectiveScope {
      UserId = "user-123",
      TenantId = "tenant-456"
    };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    scopeContextAccessor.Current = immutableContext;

    var command = new SecurityPropagationTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.SendAsync(command, context);

    // Assert - Get the envelope from trace store and check the hop's SecurityContext
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var scopeContext = envelope.GetCurrentScope();
    await Assert.That(scopeContext?.Scope?.UserId).IsEqualTo("user-123");
    await Assert.That(scopeContext?.Scope?.TenantId).IsEqualTo("tenant-456");
  }

  /// <summary>
  /// When IScopeContextAccessor.Current contains an ImmutableScopeContext with ShouldPropagate=false,
  /// the Dispatcher should NOT attach the ScopeDelta to the outgoing message hop.
  /// </summary>
  [Test]
  public async Task Dispatcher_WithScopeContextNotPropagate_DoesNotPropagateAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up scope context with propagation DISABLED
    var scope = new PerspectiveScope {
      UserId = "user-123",
      TenantId = "tenant-456"
    };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableContext = new ImmutableScopeContext(extraction, shouldPropagate: false);
    scopeContextAccessor.Current = immutableContext;

    var command = new SecurityPropagationTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.SendAsync(command, context);

    // Assert - Scope should be null because ShouldPropagate=false
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNull();
  }

  /// <summary>
  /// When IScopeContextAccessor.Current is null (no security context established),
  /// the Dispatcher should leave Scope null on the outgoing message hop.
  /// </summary>
  [Test]
  public async Task Dispatcher_WithNoScopeContext_HopHasNullScopeAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Do NOT set any scope context - leave it null
    scopeContextAccessor.Current = null;

    var command = new SecurityPropagationTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.SendAsync(command, context);

    // Assert - Scope should be null because no context is set
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNull();
  }

  /// <summary>
  /// When IScopeContextAccessor is not registered in DI,
  /// the Dispatcher should gracefully handle it and leave Scope null.
  /// </summary>
  [Test]
  public async Task Dispatcher_WithNullScopeContextAccessor_HopHasNullScopeAsync() {
    // Arrange - Create dispatcher WITHOUT IScopeContextAccessor registered
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithoutSecurityContext(traceStore);

    var command = new SecurityPropagationTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.SendAsync(command, context);

    // Assert - Scope should be null because accessor is not registered
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNull();
  }

  /// <summary>
  /// AddWhizbangDispatcher should register IScopeContextAccessor by default.
  /// This enables security context propagation without explicit registration.
  /// </summary>
  [Test]
  public async Task AddWhizbangDispatcher_RegistersScopeContextAccessorByDefaultAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Act - Only call AddWhizbangDispatcher (no explicit IScopeContextAccessor registration)
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var provider = services.BuildServiceProvider();

    // Assert - IScopeContextAccessor should be resolvable
    var accessor = provider.GetService<IScopeContextAccessor>();
    await Assert.That(accessor).IsNotNull();
    await Assert.That(accessor).IsTypeOf<ScopeContextAccessor>();
  }

  /// <summary>
  /// When user registers their own IScopeContextAccessor before AddWhizbangDispatcher,
  /// the default registration should not override it (TryAddSingleton behavior).
  /// </summary>
  [Test]
  public async Task AddWhizbangDispatcher_DoesNotOverrideExistingAccessorRegistrationAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // User registers their own implementation BEFORE AddWhizbangDispatcher
    var customAccessor = new ScopeContextAccessor();
    var customScope = new PerspectiveScope { UserId = "custom-user", TenantId = "custom-tenant" };
    var extraction = new SecurityExtraction {
      Scope = customScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "CustomTest"
    };
    customAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);
    services.AddSingleton<IScopeContextAccessor>(customAccessor);

    // Act - AddWhizbangDispatcher should NOT override the existing registration
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var provider = services.BuildServiceProvider();

    // Assert - Should resolve to the user's custom accessor (same instance)
    var accessor = provider.GetRequiredService<IScopeContextAccessor>();
    await Assert.That(accessor).IsSameReferenceAs(customAccessor);
    await Assert.That(accessor.Current).IsNotNull();
    await Assert.That(accessor.Current!.Scope.UserId).IsEqualTo("custom-user");
  }

  /// <summary>
  /// When propagating security context with UserId set to empty GUID string,
  /// the Dispatcher should log a warning and still propagate the context successfully.
  /// This covers the Log.EmptyGuidUserIdPropagated branch in _getSecurityContextForPropagation.
  /// </summary>
  [Test]
  public async Task Dispatcher_WithEmptyGuidUserId_LogsWarningAndPropagatesAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up scope context with empty GUID as UserId - this triggers the warning branch
    var scope = new PerspectiveScope {
      UserId = Guid.Empty.ToString(), // "00000000-0000-0000-0000-000000000000"
      TenantId = "test-tenant"
    };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    scopeContextAccessor.Current = immutableContext;

    var command = new SecurityPropagationTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act - Should succeed and log warning (covers the empty GUID UserId check)
    await dispatcher.SendAsync(command, context);

    // Assert - Scope should still be propagated with the empty GUID
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var scopeContext = envelope.GetCurrentScope();
    await Assert.That(scopeContext?.Scope?.UserId).IsEqualTo(Guid.Empty.ToString());
    await Assert.That(scopeContext?.Scope?.TenantId).IsEqualTo("test-tenant");
  }

  /// <summary>
  /// Creates a dispatcher with IScopeContextAccessor registered.
  /// </summary>
  private static (IDispatcher dispatcher, IServiceProvider provider) _createDispatcherWithSecurityContext(
    IScopeContextAccessor scopeContextAccessor,
    ITraceStore traceStore) {

    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register security context accessor
    services.AddSingleton(scopeContextAccessor);

    // Register trace store to capture envelopes
    services.AddSingleton(traceStore);

    // Register receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return (serviceProvider.GetRequiredService<IDispatcher>(), serviceProvider);
  }


  /// <summary>
  /// Creates a dispatcher WITHOUT IScopeContextAccessor registered.
  /// </summary>
  private static (IDispatcher dispatcher, IServiceProvider provider) _createDispatcherWithoutSecurityContext(
    ITraceStore traceStore) {

    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Do NOT register IScopeContextAccessor - this is intentional

    // Register trace store to capture envelopes
    services.AddSingleton(traceStore);

    // Register receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return (serviceProvider.GetRequiredService<IDispatcher>(), serviceProvider);
  }

}

// Test message types for security propagation tests (outside class for source generator discovery)
public record SecurityPropagationTestCommand(string Data);
public record SecurityPropagationTestResult(string Processed);

/// <summary>
/// Test receptor for security propagation tests.
/// </summary>
public class SecurityPropagationTestCommandReceptor : IReceptor<SecurityPropagationTestCommand, SecurityPropagationTestResult> {
  public ValueTask<SecurityPropagationTestResult> HandleAsync(SecurityPropagationTestCommand message, CancellationToken cancellationToken = default) {
    return ValueTask.FromResult(new SecurityPropagationTestResult($"Processed: {message.Data}"));
  }
}
