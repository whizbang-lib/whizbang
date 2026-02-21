using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
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
    await dispatcher.AsSystem().SendAsync(command);

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
    await dispatcher.AsSystem().SendAsync(command);

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
    await dispatcher.AsSystem().SendAsync(command);

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
    await dispatcher.AsSystem().SendAsync(command);

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
    await dispatcher.AsSystem().SendAsync(command);

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
    await dispatcher.AsSystem().SendAsync(command, context);

    // Assert - Envelope should have SYSTEM in SecurityContext
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).Count().IsGreaterThanOrEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsGreaterThanOrEqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.SecurityContext).IsNotNull();
    await Assert.That(hop.SecurityContext!.UserId).IsEqualTo("SYSTEM");
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
    await dispatcher.RunAs("target-user@example.com").SendAsync(command);

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
    await dispatcher.RunAs("target-user@example.com").SendAsync(command);

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
    await dispatcher.RunAs("target-user@example.com").SendAsync(command);

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
    await dispatcher.RunAs("target-user@example.com").SendAsync(command);

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
    await dispatcher.RunAs("target-user@example.com").SendAsync(command);

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
    var result = await dispatcher.AsSystem().LocalInvokeAsync<DispatcherSecurityBuilderTestCommand, DispatcherSecurityBuilderTestResult>(command);

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
    await dispatcher.AsSystem().LocalInvokeAsync(command);

    // Assert
    var context = DispatcherSecurityBuilderVoidReceptor.CapturedContext;
    await Assert.That(context).IsNotNull();
    await Assert.That(context!.ContextType).IsEqualTo(SecurityContextType.System);
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
      await dispatcher.AsSystem().SendAsync(command, new DispatchOptions { CancellationToken = cts.Token })
    ).ThrowsException();
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
    CapturedContext = _scopeContextAccessor.Current;
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
    CapturedContext = _scopeContextAccessor.Current;
    return ValueTask.CompletedTask;
  }
}

