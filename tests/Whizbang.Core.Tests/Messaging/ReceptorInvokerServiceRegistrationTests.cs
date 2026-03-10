using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IReceptorInvoker service registration and scoped dependency resolution.
/// These tests verify that the invoker is properly registered as scoped (not singleton)
/// and that receptors with scoped dependencies work correctly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why these tests matter:</strong> A bug was discovered where the generated
/// <c>AddWhizbangReceptorRegistry()</c> method registered <c>IReceptorInvoker</c> as singleton.
/// This caused "Cannot resolve scoped service from root provider" errors when receptors
/// had dependencies on scoped services (like DbContext, repositories, etc.).
/// </para>
/// <para>
/// The fix was to change the registration from <c>AddSingleton</c> to <c>AddScoped</c>.
/// These tests ensure the bug doesn't regress.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</tests>
[Category("ServiceRegistration")]
[Category("ScopedDependencies")]
public class ReceptorInvokerServiceRegistrationTests {

  /// <summary>
  /// Test message type for service registration tests.
  /// </summary>
  private sealed record TestMessage(string Value) : IMessage;

  /// <summary>
  /// A scoped service that tracks whether it was created and which scope it belongs to.
  /// Used to verify that receptors receive the correct scoped instance.
  /// </summary>
  private sealed class ScopedDependency {
    public Guid ScopeId { get; } = Guid.NewGuid();
    public bool WasAccessed { get; set; }
  }

  /// <summary>
  /// Wraps a message in an IMessageEnvelope for testing.
  /// </summary>
  private static MessageEnvelope<T> _wrapInEnvelope<T>(T message) where T : notnull {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From(TrackedGuid.NewMedo()),
      Payload = message,
      Hops = []
    };
  }

  /// <summary>
  /// Creates a test registry that resolves a scoped dependency during invocation.
  /// This simulates real receptor behavior where dependencies are resolved from the provider.
  /// </summary>
  private sealed class ScopedDependencyRegistry : IReceptorRegistry {
    private readonly Action<ScopedDependency> _onScopedDependencyResolved;

    public ScopedDependencyRegistry(Action<ScopedDependency> onScopedDependencyResolved) {
      _onScopedDependencyResolved = onScopedDependencyResolved;
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      if (messageType != typeof(TestMessage) || stage != LifecycleStage.PostInboxInline) {
        return [];
      }

      return [
        new ReceptorInfo(
          MessageType: typeof(TestMessage),
          ReceptorId: "ScopedDependencyReceptor",
          InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
            // This is the critical part: resolve a scoped dependency from the provider
            // If the provider is the root provider, this will throw for scoped services
            var scopedDep = sp.GetRequiredService<ScopedDependency>();
            scopedDep.WasAccessed = true;
            _onScopedDependencyResolved(scopedDep);
            return ValueTask.FromResult<object?>(null);
          }
        )
      ];
    }
  }

  /// <summary>
  /// Verifies that IReceptorInvoker can be registered as scoped.
  /// This is the pattern that should be used by AddWhizbangReceptorRegistry().
  /// </summary>
  [Test]
  public async Task ReceptorInvoker_RegisteredAsScoped_HasCorrectLifetimeAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry, EmptyReceptorRegistry>();

    // Register as scoped (the correct pattern)
    services.AddScoped<IReceptorInvoker, ReceptorInvoker>();

    // Act
    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IReceptorInvoker));

    // Assert - Verify scoped lifetime
    await Assert.That(descriptor).IsNotNull();
    await Assert.That(descriptor!.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
  }

  /// <summary>
  /// Verifies that a scoped IReceptorInvoker receives a different service provider
  /// for each scope, allowing proper scoped dependency resolution.
  /// </summary>
  [Test]
  public async Task ReceptorInvoker_ResolvedFromDifferentScopes_GetsDifferentScopedDependenciesAsync() {
    // Arrange
    var resolvedDependencies = new List<ScopedDependency>();
    var registry = new ScopedDependencyRegistry(dep => resolvedDependencies.Add(dep));

    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<ScopedDependency>();
    services.AddScoped<IReceptorInvoker, ReceptorInvoker>();

    using var rootProvider = services.BuildServiceProvider();
    var message = new TestMessage("test");

    // Act - Create two separate scopes and invoke in each
    Guid scope1Id, scope2Id;

    using (var scope1 = rootProvider.CreateScope()) {
      var invoker1 = scope1.ServiceProvider.GetRequiredService<IReceptorInvoker>();
      await invoker1.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);
      scope1Id = resolvedDependencies[0].ScopeId;
    }

    using (var scope2 = rootProvider.CreateScope()) {
      var invoker2 = scope2.ServiceProvider.GetRequiredService<IReceptorInvoker>();
      await invoker2.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);
      scope2Id = resolvedDependencies[1].ScopeId;
    }

    // Assert - Each scope should have its own ScopedDependency instance
    await Assert.That(resolvedDependencies).Count().IsEqualTo(2);
    await Assert.That(scope1Id).IsNotEqualTo(scope2Id);
  }

  /// <summary>
  /// Verifies that a scoped IReceptorInvoker uses the same scoped dependency
  /// for multiple invocations within the same scope.
  /// </summary>
  [Test]
  public async Task ReceptorInvoker_MultipleInvocationsInSameScope_UsesSameScopedDependencyAsync() {
    // Arrange
    var resolvedDependencies = new List<ScopedDependency>();
    var registry = new ScopedDependencyRegistry(dep => resolvedDependencies.Add(dep));

    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<ScopedDependency>();
    services.AddScoped<IReceptorInvoker, ReceptorInvoker>();

    using var rootProvider = services.BuildServiceProvider();
    var message = new TestMessage("test");

    // Act - Invoke multiple times within the same scope
    using var scope = rootProvider.CreateScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();

    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert - All invocations should use the same ScopedDependency instance
    await Assert.That(resolvedDependencies).Count().IsEqualTo(3);
    var firstScopeId = resolvedDependencies[0].ScopeId;
    await Assert.That(resolvedDependencies[1].ScopeId).IsEqualTo(firstScopeId);
    await Assert.That(resolvedDependencies[2].ScopeId).IsEqualTo(firstScopeId);
  }

  /// <summary>
  /// Verifies that attempting to resolve a scoped IReceptorInvoker from the root
  /// provider throws an exception. This documents the expected failure mode.
  /// </summary>
  /// <remarks>
  /// This test documents that you MUST create a scope before resolving IReceptorInvoker.
  /// Workers should always create a scope per message, not resolve from root provider.
  /// </remarks>
  [Test]
  public async Task ReceptorInvoker_ResolvedFromRootProvider_ThrowsForScopedDependencyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry, EmptyReceptorRegistry>();
    services.AddScoped<ScopedDependency>();
    // ValidateScopes = true ensures we get an exception when resolving scoped from root
    using var rootProvider = services.BuildServiceProvider(new ServiceProviderOptions {
      ValidateScopes = true
    });

    // Register scoped invoker
    services.AddScoped<IReceptorInvoker, ReceptorInvoker>();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions {
      ValidateScopes = true
    });

    // Act & Assert - Resolving scoped service from root should throw
    await Assert.That(() => provider.GetRequiredService<IReceptorInvoker>())
      .Throws<InvalidOperationException>();
  }

  /// <summary>
  /// Verifies that a singleton IReceptorInvoker (the BUG scenario) fails when
  /// trying to resolve scoped dependencies in receptors.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This test documents the bug that was fixed. When IReceptorInvoker was registered
  /// as singleton, it captured the root IServiceProvider. When receptors tried to
  /// resolve scoped services from that provider, it failed with:
  /// "Cannot resolve scoped service from root provider"
  /// </para>
  /// <para>
  /// The fix was to register IReceptorInvoker as scoped, so it receives a scoped
  /// IServiceProvider that can resolve scoped dependencies.
  /// </para>
  /// </remarks>
  [Test]
  public async Task ReceptorInvoker_RegisteredAsSingleton_FailsForScopedReceptorDependenciesAsync() {
    // Arrange - This simulates the BUG scenario
    var resolvedDependencies = new List<ScopedDependency>();
    var registry = new ScopedDependencyRegistry(dep => resolvedDependencies.Add(dep));

    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<ScopedDependency>(); // Scoped dependency

    // BUG: Register invoker as singleton (this is what the old generated code did)
    services.AddSingleton<IReceptorInvoker>(sp => new ReceptorInvoker(
      sp.GetRequiredService<IReceptorRegistry>(),
      sp,  // This captures the ROOT provider!
      eventCascader: null
    ));

    using var rootProvider = services.BuildServiceProvider(new ServiceProviderOptions {
      ValidateScopes = true
    });

    var message = new TestMessage("test");

    // Act - Get the singleton invoker and try to invoke
    var invoker = rootProvider.GetRequiredService<IReceptorInvoker>();

    // Assert - Should throw because receptor tries to resolve scoped service from root provider
    await Assert.That(async () =>
      await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline))
      .Throws<InvalidOperationException>()
      .WithMessageContaining("scoped");
  }

  /// <summary>
  /// Verifies the correct pattern: scoped IReceptorInvoker resolved from a scope
  /// can successfully resolve scoped receptor dependencies.
  /// </summary>
  [Test]
  public async Task ReceptorInvoker_RegisteredAsScoped_SucceedsForScopedReceptorDependenciesAsync() {
    // Arrange - This is the CORRECT pattern
    var resolvedDependencies = new List<ScopedDependency>();
    var registry = new ScopedDependencyRegistry(dep => resolvedDependencies.Add(dep));

    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<ScopedDependency>(); // Scoped dependency

    // CORRECT: Register invoker as scoped
    services.AddScoped<IReceptorInvoker, ReceptorInvoker>();

    using var rootProvider = services.BuildServiceProvider(new ServiceProviderOptions {
      ValidateScopes = true
    });

    var message = new TestMessage("test");

    // Act - Create scope, resolve invoker from scope, invoke
    using var scope = rootProvider.CreateScope();
    var invoker = scope.ServiceProvider.GetRequiredService<IReceptorInvoker>();

    // Should NOT throw - scoped provider can resolve scoped dependencies
    await invoker.InvokeAsync(_wrapInEnvelope(message), LifecycleStage.PostInboxInline);

    // Assert
    await Assert.That(resolvedDependencies).Count().IsEqualTo(1);
    await Assert.That(resolvedDependencies[0].WasAccessed).IsTrue();
  }

  /// <summary>
  /// Empty receptor registry for tests that don't need actual receptors.
  /// </summary>
  private sealed class EmptyReceptorRegistry : IReceptorRegistry {
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
  }
}
