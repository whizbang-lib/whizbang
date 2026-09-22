using Microsoft.Extensions.Configuration;
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
/// Tests for security context propagation through LocalInvokeAsync.
/// Verifies that MessageContextAccessor.CurrentContext is set with security information
/// from ScopeContextAccessor when commands/events are dispatched locally.
/// </summary>
/// <docs>core-concepts/message-security#local-invoke-security</docs>
/// <tests>Whizbang.Core/Dispatcher.cs:LocalInvokeAsync</tests>
[Category("Security")]
[Category("Dispatcher")]
[NotInParallel]
public class DispatcherLocalInvokeSecurityTests {
  /// <summary>
  /// Verifies that LocalInvokeAsync sets MessageContextAccessor.CurrentContext
  /// with UserId and TenantId from the current ScopeContextAccessor.
  /// This enables cascaded receptors to access security context.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_WithScopeContext_SetsMessageContextAccessorAsync() {
    // Arrange
    GlobalCapturedContext = null; // Clear before test

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // Set up scope context with security
    const string testUserId = "user-123";
    const string testTenantId = "tenant-456";

    var scope = new PerspectiveScope {
      UserId = testUserId,
      TenantId = testTenantId
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
    ScopeContextAccessor.CurrentContext = immutableContext;

    try {
      // Act
      var command = new TestLocalInvokeCommand { Data = "test" };
      await dispatcher.LocalInvokeAsync(command);

      // Assert - Receptor should have captured the message context with security
      await Assert.That(GlobalCapturedContext).IsNotNull();
      await Assert.That(GlobalCapturedContext!.UserId).IsEqualTo(testUserId);
      await Assert.That(GlobalCapturedContext!.TenantId).IsEqualTo(testTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      GlobalCapturedContext = null;
    }
  }

  /// <summary>
  /// Verifies that when no scope context is present, MessageContextAccessor.CurrentContext
  /// is still set but with null UserId and TenantId.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_WithNoScopeContext_SetsMessageContextWithoutSecurityAsync() {
    // Arrange
    GlobalCapturedContext = null; // Clear before test

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    // Ensure no scope context is set
    ScopeContextAccessor.CurrentContext = null;

    try {
      // Act
      var command = new TestLocalInvokeCommand { Data = "test" };
      await dispatcher.LocalInvokeAsync(command);

      // Assert - Receptor should have captured message context without security
      await Assert.That(GlobalCapturedContext).IsNotNull();
      await Assert.That(GlobalCapturedContext!.UserId).IsNull();
      await Assert.That(GlobalCapturedContext!.TenantId).IsNull();
      await Assert.That(GlobalCapturedContext!.MessageId.Value).IsNotEqualTo(Guid.Empty);
    } finally {
      GlobalCapturedContext = null;
    }
  }

  /// <summary>
  /// Verifies that security context propagates through a chain of LocalInvokeAsync calls.
  /// When receptor A dispatches to receptor B, B should see the same security context.
  /// </summary>
  [Test]
  public async Task LocalInvokeAsync_SecurityContextChain_PropagatesThroughCascadeAsync() {
    // Arrange
    GlobalCapturedContext = null; // Clear before test

    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    const string testUserId = "user-chain-test";
    const string testTenantId = "tenant-chain-test";

    var scope = new PerspectiveScope {
      UserId = testUserId,
      TenantId = testTenantId
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
    ScopeContextAccessor.CurrentContext = immutableContext;

    try {
      // Act - First receptor will cascade to second
      var command = new TestCascadingCommand { ShouldCascade = true };
      await dispatcher.LocalInvokeAsync(command);

      // Assert - The cascaded receptor should see the security context
      await Assert.That(GlobalCapturedContext).IsNotNull();
      await Assert.That(GlobalCapturedContext!.UserId).IsEqualTo(testUserId);
      await Assert.That(GlobalCapturedContext!.TenantId).IsEqualTo(testTenantId);
    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      GlobalCapturedContext = null;
    }
  }

  /// <summary>
  /// Test command for LocalInvokeAsync security tests.
  /// </summary>
  public sealed record TestLocalInvokeCommand : ICommand {
    public required string Data { get; init; }
  }

  /// <summary>
  /// Shared static field to capture message context from any receptor invocation.
  /// </summary>
  internal static IMessageContext? GlobalCapturedContext { get; set; }

  /// <summary>
  /// Test command that triggers cascading behavior.
  /// </summary>
  public sealed record TestCascadingCommand : ICommand {
    public required bool ShouldCascade { get; init; }
  }

  /// <summary>
  /// Test receptor that captures MessageContextAccessor.CurrentContext for verification.
  /// </summary>
  public sealed class TestLocalInvokeReceptor : IReceptor<TestLocalInvokeCommand> {
    public ValueTask HandleAsync(TestLocalInvokeCommand message, CancellationToken cancellationToken = default) {
      // Capture the current message context to static field for verification
      GlobalCapturedContext = MessageContextAccessor.CurrentContext;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Test receptor that cascades to another receptor using LocalInvokeAsync.
  /// </summary>
  public sealed class TestCascadingReceptor(IDispatcher dispatcher) : IReceptor<TestCascadingCommand> {
    private readonly IDispatcher _dispatcher = dispatcher;

    public async ValueTask HandleAsync(TestCascadingCommand message, CancellationToken cancellationToken = default) {
      // Cascade to another command
      if (message.ShouldCascade) {
        await _dispatcher.LocalInvokeAsync(new TestLocalInvokeCommand { Data = "cascaded" });
      }
    }
  }
}
