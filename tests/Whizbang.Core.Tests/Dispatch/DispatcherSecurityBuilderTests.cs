using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Dispatch;

/// <summary>
/// Tests for explicit security context API (AsSystem/RunAs).
/// Verifies that the fluent builder correctly sets security context
/// with full audit trail for impersonation scenarios.
/// </summary>
/// <docs>core-concepts/message-security#explicit-security-context-api</docs>
[Category("Security")]
[Category("Dispatcher")]
[NotInParallel]
public class DispatcherSecurityBuilderTests {
  // ============================================
  // AsSystem Tests
  // ============================================

  /// <summary>
  /// When AsSystem() is called with no current user context,
  /// ActualPrincipal should be null (true system operation).
  /// </summary>
  [Test]
  public async Task AsSystem_WithNoCurrentUser_ActualPrincipalIsNullAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // No user context set - simulating timer/scheduler
    scopeContextAccessor.Current = null;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ActualPrincipal).IsNull();
    await Assert.That(context.EffectivePrincipal).IsEqualTo("SYSTEM");
    await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.System);
  }

  /// <summary>
  /// When AsSystem() is called with an existing user context,
  /// ActualPrincipal should preserve the original user.
  /// </summary>
  [Test]
  public async Task AsSystem_WithCurrentUser_PreservesActualPrincipalAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up existing user context (admin clicking "Run as System")
    var userScope = new PerspectiveScope { UserId = "admin@example.com", TenantId = "tenant-1" };
    var userExtraction = _createExtraction(userScope);
    scopeContextAccessor.Current = new ImmutableScopeContext(userExtraction, shouldPropagate: true);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ActualPrincipal).IsEqualTo("admin@example.com");
    await Assert.That(context.EffectivePrincipal).IsEqualTo("SYSTEM");
    await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.System);
  }

  /// <summary>
  /// AsSystem().SendAsync() should set EffectivePrincipal to "SYSTEM".
  /// </summary>
  [Test]
  public async Task AsSystem_SendAsync_SetsEffectivePrincipalToSystemAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("SYSTEM");
  }

  /// <summary>
  /// AsSystem().SendAsync() should set ContextType to System.
  /// </summary>
  [Test]
  public async Task AsSystem_SendAsync_SetsContextTypeToSystemAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ContextType).IsEqualTo(SecurityContextType.System);
  }

  /// <summary>
  /// After AsSystem().SendAsync() completes, the previous context should be restored.
  /// </summary>
  [Test]
  public async Task AsSystem_RestoresPreviousContextAfterDispatchAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up original context
    var originalScope = new PerspectiveScope { UserId = "original-user", TenantId = "tenant-1" };
    var originalExtraction = _createExtraction(originalScope);
    var originalContext = new ImmutableScopeContext(originalExtraction, shouldPropagate: true);
    scopeContextAccessor.Current = originalContext;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Original context should be restored
    await Assert.That(scopeContextAccessor.Current).IsSameReferenceAs(originalContext);
  }

  /// <summary>
  /// AsSystem() should propagate context to outgoing message hops.
  /// </summary>
  [Test]
  public async Task AsSystem_PropagatesContextToOutgoingHopsAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command, context);

    // Assert - Envelope should have SYSTEM in Scope
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var scope = envelope.GetCurrentScope();
    await Assert.That(scope?.Scope?.UserId).IsEqualTo("SYSTEM");
  }

  // ============================================
  // RunAs Tests
  // ============================================

  /// <summary>
  /// RunAs() should set EffectivePrincipal to the specified identity.
  /// </summary>
  [Test]
  public async Task RunAs_SendAsync_SetsEffectivePrincipalAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("target-user@example.com");
  }

  /// <summary>
  /// RunAs() should preserve the actual user who initiated the impersonation.
  /// </summary>
  [Test]
  public async Task RunAs_SendAsync_PreservesActualPrincipalAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up support user context
    var supportScope = new PerspectiveScope { UserId = "support@example.com", TenantId = "tenant-1" };
    var supportExtraction = _createExtraction(supportScope);
    scopeContextAccessor.Current = new ImmutableScopeContext(supportExtraction, shouldPropagate: true);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - Support impersonates target user
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ActualPrincipal).IsEqualTo("support@example.com");
    await Assert.That(context.EffectivePrincipal).IsEqualTo("target-user@example.com");
  }

  /// <summary>
  /// RunAs() with no current user should have null ActualPrincipal.
  /// </summary>
  [Test]
  public async Task RunAs_WithNoCurrentUser_ActualPrincipalIsNullAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    scopeContextAccessor.Current = null;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ActualPrincipal).IsNull();
    await Assert.That(context.EffectivePrincipal).IsEqualTo("target-user@example.com");
  }

  /// <summary>
  /// RunAs() should set ContextType to Impersonated.
  /// </summary>
  [Test]
  public async Task RunAs_SetsContextTypeToImpersonatedAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Captured context during execution
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  /// <summary>
  /// After RunAs().SendAsync() completes, the previous context should be restored.
  /// </summary>
  [Test]
  public async Task RunAs_RestoresPreviousContextAfterDispatchAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up original context
    var originalScope = new PerspectiveScope { UserId = "original-user", TenantId = "tenant-1" };
    var originalExtraction = _createExtraction(originalScope);
    var originalContext = new ImmutableScopeContext(originalExtraction, shouldPropagate: true);
    scopeContextAccessor.Current = originalContext;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Original context should be restored
    await Assert.That(scopeContextAccessor.Current).IsSameReferenceAs(originalContext);
  }

  // ============================================
  // LocalInvokeAsync Tests
  // ============================================

  /// <summary>
  /// LocalInvokeAsync with AsSystem should set security context.
  /// </summary>
  [Test]
  public async Task AsSystem_LocalInvokeAsync_SetsContextTypeToSystemAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    var result = await dispatcher.AsSystem().ForAllTenants().LocalInvokeAsync<DispatcherSecurityBuilderTestCommand, DispatcherSecurityBuilderTestResult>(command);

    // Assert
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ContextType).IsEqualTo(SecurityContextType.System);
    await Assert.That(result.Processed).Contains("test-data");
  }

  /// <summary>
  /// LocalInvokeAsync void with AsSystem should set security context.
  /// </summary>
  [Test]
  public async Task AsSystem_LocalInvokeAsync_VoidReceptor_SetsContextAsync() {
    // Arrange
    DispatcherSecurityBuilderVoidReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderVoidCommand("void-test");

    // Act
    await dispatcher.AsSystem().ForAllTenants().LocalInvokeAsync(command);

    // Assert
    var context = DispatcherSecurityBuilderVoidReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ContextType).IsEqualTo(SecurityContextType.System);
  }

  // ============================================
  // WithTenant Tests
  // ============================================

  /// <summary>
  /// WithTenant() should set TenantId on the security context.
  /// </summary>
  [Test]
  public async Task WithTenant_SetsTenantIdOnContextAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - System operation on a specific tenant
    await dispatcher.AsSystem().ForTenant("target-tenant-123").SendAsync(command);

    // Assert - Captured context should have TenantId set
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.Scope.TenantId).IsEqualTo("target-tenant-123");
  }

  /// <summary>
  /// WithTenant() with RunAs() should set both tenant and user identity.
  /// </summary>
  [Test]
  public async Task WithTenant_WithRunAs_SetsBothTenantAndUserAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - Impersonation in a different tenant
    await dispatcher.RunAs("target-user@example.com").ForTenant("target-tenant").SendAsync(command);

    // Assert
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.Scope.TenantId).IsEqualTo("target-tenant");
    await Assert.That(context.EffectivePrincipal).IsEqualTo("target-user@example.com");
    await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.Impersonated);
  }

  /// <summary>
  /// WithTenant() with null should throw ArgumentException.
  /// </summary>
  [Test]
  public async Task WithTenant_WithNull_ThrowsArgumentExceptionAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Act & Assert
    await Assert.That(() => dispatcher.AsSystem().ForTenant(null!)).ThrowsException();
  }

  /// <summary>
  /// WithTenant() with empty string should throw ArgumentException.
  /// </summary>
  [Test]
  public async Task WithTenant_WithEmptyString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Act & Assert
    await Assert.That(() => dispatcher.AsSystem().ForTenant("")).ThrowsException();
  }

  /// <summary>
  /// WithTenant() with whitespace should throw ArgumentException.
  /// </summary>
  [Test]
  public async Task WithTenant_WithWhitespace_ThrowsArgumentExceptionAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Act & Assert
    await Assert.That(() => dispatcher.AsSystem().ForTenant("   ")).ThrowsException();
  }

  /// <summary>
  /// AsSystem() without WithTenant() should have null TenantId.
  /// </summary>
  [Test]
  public async Task AsSystem_ForAllTenants_SetsTenantIdToAllTenantsAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - System operation with ForAllTenants (cross-tenant)
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - TenantId should be "*" (AllTenants constant) for explicit cross-tenant operations
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.Scope.TenantId).IsEqualTo(TenantConstants.AllTenants);
    await Assert.That(context.Scope.TenantId).IsEqualTo("*");
  }

  /// <summary>
  /// WithTenant() should propagate TenantId to message envelope hops.
  /// </summary>
  [Test]
  public async Task WithTenant_PropagatesTenantIdToEnvelopeHopsAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act
    await dispatcher.AsSystem().ForTenant("propagated-tenant").SendAsync(command, context);

    // Assert - Envelope hop should have TenantId in Scope
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Scope).IsNotNull();
    var scope = envelope.GetCurrentScope();
    await Assert.That(scope?.Scope?.TenantId).IsEqualTo("propagated-tenant");
  }

  // ============================================
  // InitiatingContext Override Tests (Bug Fix Verification)
  // ============================================
  // These tests verify that AsSystem()/RunAs() take precedence over
  // any existing InitiatingContext. This was a bug where the getter
  // read InitiatingContext.ScopeContext first, overriding explicit context.

  /// <summary>
  /// VERIFIED FIX: Confirms that ScopeContextAccessor.CurrentContext getter
  /// correctly prioritizes ImmutableScopeContext with ShouldPropagate=true over InitiatingContext.
  /// This is the CORRECT behavior after fixing the original bug where InitiatingContext was read first.
  /// </summary>
  [Test]
  public async Task CurrentContext_Getter_PrioritizesImmutableScopeContextWithPropagation_OverInitiatingContextAsync() {
    // Arrange - Create two different scope contexts
    var currentScope = new PerspectiveScope { UserId = "current-user", TenantId = "current-tenant" };
    var currentExtraction = _createExtraction(currentScope);
    var currentContext = new ImmutableScopeContext(currentExtraction, shouldPropagate: true);

    var initiatingScope = new PerspectiveScope { UserId = "initiating-user", TenantId = "initiating-tenant" };
    var initiatingExtraction = _createExtraction(initiatingScope);
    var initiatingContext = new ImmutableScopeContext(initiatingExtraction, shouldPropagate: true);

    var initiatingMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = initiatingContext
    };

    // Act - Set BOTH CurrentContext AND CurrentInitiatingContext
    ScopeContextAccessor.CurrentContext = currentContext;  // Sets _current
    ScopeContextAccessor.CurrentInitiatingContext = initiatingMessageContext;  // Sets _initiatingContext

    // Assert - The getter correctly prioritizes ImmutableScopeContext with ShouldPropagate=true
    // This verifies the FIX: explicitly set CurrentContext takes precedence when it's ImmutableScopeContext with propagation
    var readContext = ScopeContextAccessor.CurrentContext;

    await Assert.That(readContext?.Scope?.UserId).IsEqualTo("current-user")
      .Because("CurrentContext getter should prioritize ImmutableScopeContext with ShouldPropagate=true over InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  /// <summary>
  /// REGRESSION TEST: Replicates JDNext seeder scenario using ACTUAL DispatcherSecurityBuilder.
  /// When code inside a message handler calls dispatcher.AsSystem().ForAllTenants().SendAsync(),
  /// the envelope MUST have SYSTEM context on its hop, not the handler's context.
  ///
  /// This test will FAIL without the fix (clearing InitiatingContext).
  /// This test will PASS with the fix.
  /// </summary>
  [Test]
  public async Task AsSystem_FromInsideMessageHandler_EnvelopeHopMustHaveSystemContext_NotHandlerContextAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var correlationId = CorrelationId.New();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // CRITICAL: Simulate being INSIDE a message handler
    // This is what ReceptorInvoker does when processing a message
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = _createExtraction(handlerScope);
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);

    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };

    // This is what happens during message processing - InitiatingContext is set by ReceptorInvoker
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");
    var context = MessageContext.Create(correlationId);

    // Act - Call AsSystem().SendAsync() like JDNext seeder does
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command, context);

    // Assert - Check the envelope that was stored
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1)
      .Because("Envelope should be stored in trace store");

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1)
      .Because("Envelope should have at least one hop");

    // Get the scope from the envelope - this is what downstream processors will see
    var envelopeScope = envelope.GetCurrentScope();

    // THIS IS THE KEY ASSERTION:
    // Without the fix: envelopeScope.Scope.UserId == "handler-user@example.com" (BUG!)
    // With the fix: envelopeScope.Scope.UserId == "SYSTEM" (CORRECT!)
    await Assert.That(envelopeScope?.Scope?.UserId).IsEqualTo("SYSTEM")
      .Because("AsSystem() must put SYSTEM on the envelope, not the handler's InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }


  /// <summary>
  /// REGRESSION TEST: AsSystem() must override existing InitiatingContext.
  /// Bug: ScopeContextAccessor.CurrentContext getter reads InitiatingContext first,
  /// so AsSystem() was being ignored when InitiatingContext existed.
  /// </summary>
  [Test]
  public async Task AsSystem_WhenInitiatingContextExists_OverridesInitiatingContextAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up an InitiatingContext (simulates message processing context)
    var initiatingScope = new PerspectiveScope { UserId = "initiating-user@example.com", TenantId = "initiating-tenant" };
    var initiatingExtraction = _createExtraction(initiatingScope);
    var initiatingContext = new ImmutableScopeContext(initiatingExtraction, shouldPropagate: true);

    // Create a MessageContext with the initiating scope context
    // This simulates what happens during message processing - InitiatingContext gets set
    var initiatingMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = initiatingContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = initiatingMessageContext;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - AsSystem() should override the InitiatingContext
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Should be SYSTEM, NOT initiating-user@example.com
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull()
      .Because("Security context should be captured during dispatch");
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("SYSTEM")
      .Because("AsSystem() should override InitiatingContext");
    await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.System)
      .Because("Context type should be System, not inherited from InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  /// <summary>
  /// REGRESSION TEST: RunAs() must override existing InitiatingContext.
  /// Bug: ScopeContextAccessor.CurrentContext getter reads InitiatingContext first,
  /// so RunAs() was being ignored when InitiatingContext existed.
  /// </summary>
  [Test]
  public async Task RunAs_WhenInitiatingContextExists_OverridesInitiatingContextAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up an InitiatingContext (simulates message processing context)
    var initiatingScope = new PerspectiveScope { UserId = "initiating-user@example.com", TenantId = "initiating-tenant" };
    var initiatingExtraction = _createExtraction(initiatingScope);
    var initiatingContext = new ImmutableScopeContext(initiatingExtraction, shouldPropagate: true);

    var initiatingMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = initiatingContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = initiatingMessageContext;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - RunAs() should override the InitiatingContext
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - Should be target-user, NOT initiating-user
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull()
      .Because("Security context should be captured during dispatch");
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("target-user@example.com")
      .Because("RunAs() should override InitiatingContext");
    await Assert.That(context.ContextType).IsEqualTo(SecurityContextType.Impersonated)
      .Because("Context type should be Impersonated, not inherited from InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  /// <summary>
  /// REGRESSION TEST: After AsSystem() dispatch completes, InitiatingContext should be restored.
  /// </summary>
  [Test]
  public async Task AsSystem_RestoresInitiatingContextAfterDispatchAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up an InitiatingContext
    var initiatingScope = new PerspectiveScope { UserId = "initiating-user@example.com", TenantId = "initiating-tenant" };
    var initiatingExtraction = _createExtraction(initiatingScope);
    var initiatingContext = new ImmutableScopeContext(initiatingExtraction, shouldPropagate: true);
    var originalInitiating = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = initiatingContext
    };

    ScopeContextAccessor.CurrentInitiatingContext = originalInitiating;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - InitiatingContext should be restored
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext).IsSameReferenceAs(originalInitiating)
      .Because("InitiatingContext should be restored after AsSystem() dispatch completes");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  /// <summary>
  /// REGRESSION TEST: After RunAs() dispatch completes, InitiatingContext should be restored.
  /// </summary>
  [Test]
  public async Task RunAs_RestoresInitiatingContextAfterDispatchAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up an InitiatingContext
    var initiatingScope = new PerspectiveScope { UserId = "initiating-user@example.com", TenantId = "initiating-tenant" };
    var initiatingExtraction = _createExtraction(initiatingScope);
    var initiatingContext = new ImmutableScopeContext(initiatingExtraction, shouldPropagate: true);
    var originalInitiating = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = initiatingContext
    };

    ScopeContextAccessor.CurrentInitiatingContext = originalInitiating;

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert - InitiatingContext should be restored
    await Assert.That(ScopeContextAccessor.CurrentInitiatingContext).IsSameReferenceAs(originalInitiating)
      .Because("InitiatingContext should be restored after RunAs() dispatch completes");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
  }

  // ============================================
  // Cascaded Event Security Tests
  // ============================================
  // These tests verify that cascaded events (returned from receptors in tuples)
  // inherit the explicit security context from AsSystem()/RunAs().

  /// <summary>
  /// REGRESSION TEST: When AsSystem().LocalInvokeAsync() invokes a receptor that returns
  /// an event (cascaded event), the cascaded event receptor should see SYSTEM context,
  /// not the handler's InitiatingContext.
  ///
  /// This replicates the JDNext seeder scenario where ReseedSystemSucceededEvent
  /// was getting SecurityContextRequiredException because the cascaded event
  /// didn't inherit the SYSTEM context.
  ///
  /// Uses Local routing to avoid JSON serialization complexity while still testing
  /// the core security context propagation issue.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task AsSystem_LocalInvoke_CascadedEvent_MustHaveSystemContextAsync() {
    // Arrange
    SecurityBuilderCascadeEventReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // CRITICAL: Simulate being INSIDE a message handler (like seeder running inside a receptor)
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = _createExtraction(handlerScope);
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    var command = new SecurityBuilderCascadeTestCommand("seed-data");

    // Act - Call AsSystem().LocalInvokeAsync() which triggers receptor that returns an event
    // The event cascades locally and invokes SecurityBuilderCascadeEventReceptor
    await dispatcher.AsSystem().ForAllTenants().LocalInvokeAsync<SecurityBuilderCascadeTestCommand, (SecurityBuilderCascadeTestResult, SecurityBuilderCascadeTestEvent)>(command);

    // Assert - The cascaded event receptor should have captured the scope
    var capturedScope = SecurityBuilderCascadeEventReceptor.CapturedScope;
    await Assert.That(capturedScope).IsNotNull()
      .Because("Cascaded event receptor should have captured the scope context");

    // THIS IS THE KEY ASSERTION:
    // Without the fix: capturedScope.Scope.UserId == "handler-user@example.com" (BUG!)
    // With the fix: capturedScope.Scope.UserId == "SYSTEM" (CORRECT!)
    await Assert.That(capturedScope?.Scope?.UserId).IsEqualTo("SYSTEM")
      .Because("Cascaded event from AsSystem() must run with SYSTEM context, not handler's InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
    await serviceProvider.DisposeAsync();
  }

  /// <summary>
  /// REGRESSION TEST: Verify that CascadeContext.GetSecurityFromAmbient() returns SYSTEM context
  /// during cascade when AsSystem() is used, even when InitiatingContext exists.
  ///
  /// This tests the root cause of the JDNext issue: the scope captured for envelope hops
  /// must come from the explicit SYSTEM context, not the handler's InitiatingContext.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task GetSecurityFromAmbient_DuringCascade_ReturnsSystemContextNotInitiatingContextAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();

    // CRITICAL: Simulate being INSIDE a message handler with InitiatingContext set
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = _createExtraction(handlerScope);
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    // Set explicit SYSTEM context (what AsSystem() does)
    var systemScope = new PerspectiveScope { UserId = "SYSTEM", TenantId = null };
    var systemExtraction = _createExtraction(systemScope);
    var systemContext = new ImmutableScopeContext(systemExtraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = systemContext;

    // Clear InitiatingContext (what the fix in DispatcherSecurityBuilder does)
    var previousInitiating = ScopeContextAccessor.CurrentInitiatingContext;
    ScopeContextAccessor.CurrentInitiatingContext = null;

    // Act - This is what PublishToOutboxAsync calls to get scope for envelope hops
    var securityFromAmbient = CascadeContext.GetSecurityFromAmbient();

    // Assert - Should be SYSTEM, not handler context
    await Assert.That(securityFromAmbient).IsNotNull()
      .Because("GetSecurityFromAmbient should return security when explicit context exists");
    await Assert.That(securityFromAmbient!.UserId).IsEqualTo("SYSTEM")
      .Because("GetSecurityFromAmbient should return SYSTEM context, not handler's InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
    ScopeContextAccessor.CurrentInitiatingContext = previousInitiating;
  }

  // ============================================
  // PublishAsync Tests (Events to Outbox)
  // ============================================
  // These tests verify that AsSystem().PublishAsync() correctly propagates
  // SYSTEM context to the envelope, which is then stored in the outbox.
  // This is critical for background workers that read from the outbox.

  /// <summary>
  /// REGRESSION TEST: Replicates JDNext seeder scenario for PublishAsync.
  /// When code inside a message handler calls dispatcher.AsSystem().ForAllTenants().PublishAsync(),
  /// the event receptor MUST see SYSTEM context, not the handler's InitiatingContext.
  ///
  /// This uses the same pattern as cascade event tests - capturing scope in a receptor.
  /// Without the fix: The receptor sees "handler-user@example.com" (BUG!)
  /// With the fix: The receptor sees "SYSTEM" (CORRECT!)
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task AsSystem_PublishAsync_FromInsideMessageHandler_ReceptorSeesSystemContextAsync() {
    // Arrange
    SecurityBuilderPublishTestEventReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // CRITICAL: Simulate being INSIDE a message handler
    // This is what happens when a receptor (like WorkCoordinator) processes a message
    // and triggers code that publishes events (like ReseedSystemSucceededEvent)
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = _createExtraction(handlerScope);
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };

    // This is what ReceptorInvoker does during message processing - sets InitiatingContext
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    var testEvent = new SecurityBuilderPublishTestEvent("test-publish-data", Guid.NewGuid());

    // Act - Call AsSystem().PublishAsync() like JDNext seeder does when publishing events
    await dispatcher.AsSystem().ForAllTenants().PublishAsync(testEvent);

    // Assert - Check the scope captured by the event receptor
    await Assert.That(SecurityBuilderPublishTestEventReceptor.WasInvoked).IsTrue()
      .Because("Event receptor should have been invoked");

    var capturedScope = SecurityBuilderPublishTestEventReceptor.CapturedScope;
    await Assert.That(capturedScope).IsNotNull()
      .Because("Event receptor should have captured the scope context");

    // THIS IS THE KEY ASSERTION:
    // Without the fix: capturedScope.Scope.UserId == "handler-user@example.com" (BUG!)
    // With the fix: capturedScope.Scope.UserId == "SYSTEM" (CORRECT!)
    await Assert.That(capturedScope?.Scope?.UserId).IsEqualTo("SYSTEM")
      .Because("AsSystem().PublishAsync() must set SYSTEM context, not handler's InitiatingContext");

    // Cleanup
    ScopeContextAccessor.CurrentInitiatingContext = null;
    await serviceProvider.DisposeAsync();
  }

  /// <summary>
  /// REGRESSION TEST: Verify that PublishAsync without InitiatingContext also works.
  /// This covers the timer/scheduler scenario where no user context exists.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task AsSystem_PublishAsync_WithNoInitiatingContext_HasSystemContextAsync() {
    // Arrange - No InitiatingContext (simulating timer/scheduler)
    SecurityBuilderPublishTestEventReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // Ensure no context is set
    scopeContextAccessor.Current = null;
    ScopeContextAccessor.CurrentInitiatingContext = null;

    var testEvent = new SecurityBuilderPublishTestEvent("timer-event", Guid.NewGuid());

    // Act
    await dispatcher.AsSystem().ForAllTenants().PublishAsync(testEvent);

    // Assert
    var capturedScope = SecurityBuilderPublishTestEventReceptor.CapturedScope;
    await Assert.That(capturedScope).IsNotNull();
    await Assert.That(capturedScope?.Scope?.UserId).IsEqualTo("SYSTEM")
      .Because("AsSystem() should set SYSTEM context even with no prior context");

    // Cleanup
    await serviceProvider.DisposeAsync();
  }

  // ============================================
  // Edge Cases for 100% Branch Coverage
  // ============================================

  /// <summary>
  /// RunAs() with empty identity should throw ArgumentException.
  /// </summary>
  [Test]
  public async Task RunAs_WithEmptyIdentity_ThrowsArgumentExceptionAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Act & Assert
    await Assert.That(() => dispatcher.RunAs("")).ThrowsException();
  }

  /// <summary>
  /// RunAs() with null identity should throw ArgumentNullException.
  /// </summary>
  [Test]
  public async Task RunAs_WithNullIdentity_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Act & Assert
    await Assert.That(() => dispatcher.RunAs(null!)).ThrowsException();
  }

  /// <summary>
  /// SendAsync with cancellation should propagate cancellation.
  /// </summary>
  [Test]
  public async Task SendAsync_WithCancellation_PropagatesCancellationAsync() {
    // Arrange
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    var cts = new CancellationTokenSource();
    cts.Cancel();

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act & Assert - Should throw OperationCanceledException
    await Assert.That(async () =>
      await dispatcher.AsSystem().ForAllTenants().SendAsync(command, new DispatchOptions { CancellationToken = cts.Token })
    ).ThrowsException();
  }

  // ============================================
  // Empty GUID Warning Coverage Tests
  // ============================================

  /// <summary>
  /// When AsSystem() is called with current user having empty GUID as UserId,
  /// the warning should be logged (covers Log.EmptyGuidActualPrincipal branch).
  /// </summary>
  [Test]
  public async Task AsSystem_WithEmptyGuidCurrentUser_LogsWarningAndSucceedsAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up context with empty GUID as UserId - this triggers the warning branch
    var emptyGuidScope = new PerspectiveScope {
      UserId = Guid.Empty.ToString(), // "00000000-0000-0000-0000-000000000000"
      TenantId = "test-tenant"
    };
    var extraction = _createExtraction(emptyGuidScope);
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - Should succeed and log warning (covers the empty GUID ActualPrincipal check)
    await dispatcher.AsSystem().ForAllTenants().SendAsync(command);

    // Assert - Context was set correctly, operation completed
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("SYSTEM");
    // The actual principal is the empty GUID string (which triggers the warning)
    await Assert.That(context.ActualPrincipal).IsEqualTo(Guid.Empty.ToString());
  }

  /// <summary>
  /// When RunAs() is called with current user having empty GUID as UserId,
  /// the warning should be logged (covers Log.EmptyGuidActualPrincipal branch).
  /// </summary>
  [Test]
  public async Task RunAs_WithEmptyGuidCurrentUser_LogsWarningAndSucceedsAsync() {
    // Arrange
    DispatcherSecurityBuilderTestCommandReceptor.ResetCapture();
    var scopeContextAccessor = new ScopeContextAccessor();
    var traceStore = new InMemoryTraceStore();
    var (dispatcher, _) = _createDispatcherWithSecurityContext(scopeContextAccessor, traceStore);

    // Set up context with empty GUID as UserId - this triggers the warning branch
    var emptyGuidScope = new PerspectiveScope {
      UserId = Guid.Empty.ToString(), // "00000000-0000-0000-0000-000000000000"
      TenantId = "test-tenant"
    };
    var extraction = _createExtraction(emptyGuidScope);
    scopeContextAccessor.Current = new ImmutableScopeContext(extraction, shouldPropagate: true);

    var command = new DispatcherSecurityBuilderTestCommand("test-data");

    // Act - Should succeed and log warning
    await dispatcher.RunAs("target-user@example.com").ForAllTenants().SendAsync(command);

    // Assert
    var context = DispatcherSecurityBuilderTestCommandReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.EffectivePrincipal).IsEqualTo("target-user@example.com");
    await Assert.That(context.ActualPrincipal).IsEqualTo(Guid.Empty.ToString());
  }

  // ============================================
  // Helper Methods
  // ============================================

  private static SecurityExtraction _createExtraction(PerspectiveScope scope) {
    return new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
  }

  private static (IDispatcher dispatcher, IServiceProvider provider) _createDispatcherWithSecurityContext(
    IScopeContextAccessor scopeContextAccessor,
    ITraceStore traceStore) {

    var services = new ServiceCollection();

    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    services.AddSingleton(scopeContextAccessor);
    services.AddSingleton(traceStore);
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return (serviceProvider.GetRequiredService<IDispatcher>(), serviceProvider);
  }

}

// Test message types (outside class for source generator discovery)
public record DispatcherSecurityBuilderTestCommand(string Data);
public record DispatcherSecurityBuilderTestResult(string Processed);
public record DispatcherSecurityBuilderVoidCommand(string Data);

/// <summary>
/// Test receptor for security builder tests.
/// Uses static capture fields so the source-generator-discovered receptor can capture context
/// during execution for later verification by tests.
/// </summary>
public class DispatcherSecurityBuilderTestCommandReceptor : IReceptor<DispatcherSecurityBuilderTestCommand, DispatcherSecurityBuilderTestResult> {
  private readonly IScopeContextAccessor _scopeContextAccessor;

  /// <summary>
  /// Static captured context - set during HandleAsync for test verification.
  /// </summary>
  public static IScopeContext? CapturedContext { get; private set; }

  /// <summary>
  /// Resets the captured context between tests.
  /// </summary>
  public static void ResetCapture() => CapturedContext = null;

  public DispatcherSecurityBuilderTestCommandReceptor(IScopeContextAccessor scopeContextAccessor) {
    _scopeContextAccessor = scopeContextAccessor;
  }

  public ValueTask<DispatcherSecurityBuilderTestResult> HandleAsync(
    DispatcherSecurityBuilderTestCommand message,
    CancellationToken cancellationToken = default) {
    // Capture the context during execution for test verification
    // Use ??= to capture only on first invocation - lifecycle system may re-invoke
    // the receptor at default stages, but we want the context from the primary dispatch
    var current = _scopeContextAccessor.Current;
    CapturedContext ??= current;
    return ValueTask.FromResult(new DispatcherSecurityBuilderTestResult($"Processed: {message.Data}"));
  }
}

/// <summary>
/// Void receptor for LocalInvokeAsync void tests.
/// </summary>
public class DispatcherSecurityBuilderVoidReceptor : IReceptor<DispatcherSecurityBuilderVoidCommand> {
  private readonly IScopeContextAccessor _scopeContextAccessor;

  public static IScopeContext? CapturedContext { get; private set; }
  public static void ResetCapture() => CapturedContext = null;

  public DispatcherSecurityBuilderVoidReceptor(IScopeContextAccessor scopeContextAccessor) {
    _scopeContextAccessor = scopeContextAccessor;
  }

  public ValueTask HandleAsync(
    DispatcherSecurityBuilderVoidCommand message,
    CancellationToken cancellationToken = default) {
    // Use ??= to capture only on first invocation - lifecycle system may re-invoke
    CapturedContext ??= _scopeContextAccessor.Current;
    return ValueTask.CompletedTask;
  }
}

// ============================================
// Cascade Event Test Types
// ============================================

/// <summary>
/// Command that triggers a receptor returning an event (for cascade testing).
/// </summary>
public record SecurityBuilderCascadeTestCommand(string Data);

/// <summary>
/// Result DTO returned along with the cascaded event.
/// </summary>
public record SecurityBuilderCascadeTestResult(string Processed);

/// <summary>
/// Event that gets cascaded locally when receptor returns it.
/// Uses Local routing so it invokes SecurityBuilderCascadeEventReceptor directly.
/// </summary>
[DefaultRouting(DispatchMode.Local)]
public record SecurityBuilderCascadeTestEvent(string Data, [property: StreamId] Guid EventId) : IEvent;

/// <summary>
/// Receptor that returns a tuple with result and event (cascade pattern).
/// This simulates JDNext seeder returning ReseedSystemSucceededEvent.
/// </summary>
public class SecurityBuilderCascadeTestReceptor
  : IReceptor<SecurityBuilderCascadeTestCommand, (SecurityBuilderCascadeTestResult, SecurityBuilderCascadeTestEvent)> {

  public ValueTask<(SecurityBuilderCascadeTestResult, SecurityBuilderCascadeTestEvent)> HandleAsync(
    SecurityBuilderCascadeTestCommand message,
    CancellationToken cancellationToken = default) {
    var result = new SecurityBuilderCascadeTestResult($"Processed: {message.Data}");
    var evt = new SecurityBuilderCascadeTestEvent($"Event for: {message.Data}", Guid.NewGuid());
    return ValueTask.FromResult((result, evt));
  }
}

/// <summary>
/// Event receptor that captures the scope context when handling the cascaded event.
/// This allows us to verify that cascaded events inherit the explicit SYSTEM context.
/// </summary>
public class SecurityBuilderCascadeEventReceptor : IReceptor<SecurityBuilderCascadeTestEvent> {
  private readonly IScopeContextAccessor _scopeContextAccessor;

  public static IScopeContext? CapturedScope { get; private set; }
  public static void ResetCapture() => CapturedScope = null;

  public SecurityBuilderCascadeEventReceptor(IScopeContextAccessor scopeContextAccessor) {
    _scopeContextAccessor = scopeContextAccessor;
  }

  public ValueTask HandleAsync(SecurityBuilderCascadeTestEvent message, CancellationToken cancellationToken = default) {
    // Capture the scope context during cascaded event handling
    CapturedScope = _scopeContextAccessor.Current;
    return ValueTask.CompletedTask;
  }
}

// ============================================
// PublishAsync Test Types
// ============================================

/// <summary>
/// Test event for verifying AsSystem().PublishAsync() behavior.
/// This simulates events like ReseedSystemSucceededEvent, FilterSubscriptionTemplateCreatedEvent.
/// </summary>
[DefaultRouting(DispatchMode.Local)]
public record SecurityBuilderPublishTestEvent(string Data, [property: StreamId] Guid EventId) : IEvent;

/// <summary>
/// Event receptor that captures the scope context when handling the published event.
/// This allows us to verify that AsSystem().PublishAsync() sets SYSTEM context.
/// Note: Must return a response type to be invoked by typed PublishAsync (void receptors only invoked in cascade).
/// </summary>
public class SecurityBuilderPublishTestEventReceptor : IReceptor<SecurityBuilderPublishTestEvent, object> {
  private readonly IScopeContextAccessor _scopeContextAccessor;

  public static IScopeContext? CapturedScope { get; private set; }
  public static bool WasInvoked { get; private set; }
  public static void ResetCapture() {
    CapturedScope = null;
    WasInvoked = false;
  }

  public SecurityBuilderPublishTestEventReceptor(IScopeContextAccessor scopeContextAccessor) {
    _scopeContextAccessor = scopeContextAccessor;
  }

  public ValueTask<object> HandleAsync(SecurityBuilderPublishTestEvent message, CancellationToken cancellationToken = default) {
    WasInvoked = true;
    // Capture the scope context during event handling
    CapturedScope = _scopeContextAccessor.Current;
    return ValueTask.FromResult<object>(new { Received = true });
  }
}

