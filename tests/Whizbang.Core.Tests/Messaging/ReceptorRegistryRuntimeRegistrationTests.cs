using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for runtime registration of receptors via <see cref="IReceptorRegistry.Register{TMessage}"/>
/// and <see cref="IReceptorRegistry.Register{TMessage, TResponse}"/>.
/// These tests use the source-generated <c>GeneratedReceptorRegistry</c> to verify
/// that runtime-registered receptors appear in <see cref="IReceptorRegistry.GetReceptorsFor"/>
/// and can be unregistered.
/// </summary>
/// <remarks>
/// <para>
/// The source generator discovers public IReceptor implementations and registers them at
/// default lifecycle stages (LocalImmediateInline, PreOutboxInline, PostInboxInline).
/// Tests that verify runtime-only behavior use non-default stages like PostOutboxInline
/// or PostPerspectiveInline to avoid interference from compile-time entries.
/// </para>
/// <para>
/// Runtime-registered receptor IDs are prefixed with "runtime_" by the generated registry.
/// Tests filter by this prefix to distinguish from compile-time entries.
/// </para>
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public class ReceptorRegistryRuntimeRegistrationTests {
  /// <summary>
  /// Runtime receptor IDs are prefixed with "runtime_" by the generated registry.
  /// </summary>
  private const string RUNTIME_PREFIX = "runtime_";

  #region Void Receptor Registration

  [Test]
  public async Task Register_VoidReceptor_IsReturnedByGetReceptorsForAsync() {
    // Arrange - use PostOutboxInline (non-default stage, no compile-time entries)
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    // Act
    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);

    // Assert
    await Assert.That(receptors).Count().IsEqualTo(1);
    await Assert.That(receptors[0].ReceptorId).StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal);
    await Assert.That(receptors[0].ReceptorId).Contains("RuntimeRegistrationTestReceptor");
  }

  [Test]
  public async Task Register_VoidReceptor_IsNotReturnedAtDifferentStageAsync() {
    // Arrange
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    // Act - register at PostOutboxInline (non-default)
    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);

    // Query at PostPerspectiveInline (different non-default stage)
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostPerspectiveInline);

    // Assert - should NOT contain any runtime receptors at this stage
    var runtimeReceptors = receptors.Where(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal)).ToList();
    await Assert.That(runtimeReceptors).IsEmpty();
  }

  [Test]
  public async Task Unregister_VoidReceptor_RemovesFromRegistryAsync() {
    // Arrange - use non-default stage to isolate from compile-time entries
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);

    // Verify it's registered
    var beforeUnregister = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);
    await Assert.That(beforeUnregister.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsTrue();

    // Act
    var removed = registry.Unregister<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);

    // Assert
    await Assert.That(removed).IsTrue();
    var afterUnregister = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);
    await Assert.That(afterUnregister.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  public async Task Unregister_NotRegisteredReceptor_ReturnsFalseAsync() {
    // Arrange
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    // Act - unregister without registering first
    var removed = registry.Unregister<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);

    // Assert
    await Assert.That(removed).IsFalse();
  }

  [Test]
  public async Task Register_VoidReceptor_InvokeAsyncCallsHandleAsync() {
    // Arrange - use PostOutboxInline (non-default) so we only get the runtime entry
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);

    var runtimeEntry = receptors.First(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal));
    var testMessage = new RuntimeRegistrationTestEvent(Guid.CreateVersion7(), "hello");

    // Act - invoke the delegate stored in the registry
    // Use a scoped provider since ILifecycleContextAccessor is registered as scoped
    using var sp = _createServiceProvider();
    using var scope = sp.CreateScope();
    await runtimeEntry.InvokeAsync(scope.ServiceProvider, testMessage, null!, null, CancellationToken.None);

    // Assert
    await Assert.That(receptor.WasInvoked).IsTrue();
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.Data).IsEqualTo("hello");
  }

  [Test]
  public async Task Register_MultipleVoidReceptors_AllReturnedAsync() {
    // Arrange - use non-default stage
    var registry = _createRegistry();
    var receptor1 = new RuntimeRegistrationTestReceptor();
    var receptor2 = new RuntimeRegistrationTestReceptor();

    // Act
    registry.Register<RuntimeRegistrationTestEvent>(receptor1, LifecycleStage.PostOutboxInline);
    registry.Register<RuntimeRegistrationTestEvent>(receptor2, LifecycleStage.PostOutboxInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);

    // Assert - both runtime receptors should be present
    var runtimeReceptors = receptors.Where(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal)).ToList();
    await Assert.That(runtimeReceptors).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Register_VoidReceptor_AtMultipleStages_ReturnedAtEachStageAsync() {
    // Arrange - use non-default stages
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationTestReceptor();

    // Act - register at two non-default stages
    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostOutboxInline);
    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    var atPostOutbox = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostOutboxInline);
    var atPostPerspective = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostPerspectiveInline);

    // Assert
    await Assert.That(atPostOutbox.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsTrue();
    await Assert.That(atPostPerspective.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsTrue();
  }

  #endregion

  #region Response Receptor Registration

  [Test]
  public async Task Register_ResponseReceptor_IsReturnedByGetReceptorsForAsync() {
    // Arrange - use non-default stage
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationResponseReceptor();

    // Act
    registry.Register<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult>(
      receptor, LifecycleStage.PostOutboxInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestCommand), LifecycleStage.PostOutboxInline);

    // Assert
    var runtimeReceptors = receptors.Where(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal)).ToList();
    await Assert.That(runtimeReceptors).Count().IsEqualTo(1);
    await Assert.That(runtimeReceptors[0].ReceptorId).Contains("RuntimeRegistrationResponseReceptor");
  }

  [Test]
  public async Task Register_ResponseReceptor_InvokeAsyncReturnsResultAsync() {
    // Arrange - use non-default stage
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationResponseReceptor();

    registry.Register<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult>(
      receptor, LifecycleStage.PostOutboxInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestCommand), LifecycleStage.PostOutboxInline);

    var runtimeEntry = receptors.First(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal));
    var testMessage = new RuntimeRegistrationTestCommand("test-data");

    // Act
    using var sp = _createServiceProvider();
    using var scope = sp.CreateScope();
    var result = await runtimeEntry.InvokeAsync(scope.ServiceProvider, testMessage, null!, null, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result).IsTypeOf<RuntimeRegistrationTestResult>();
    var typedResult = (RuntimeRegistrationTestResult)result!;
    await Assert.That(typedResult.Processed).IsEqualTo("processed:test-data");
  }

  [Test]
  public async Task Unregister_ResponseReceptor_RemovesFromRegistryAsync() {
    // Arrange - use non-default stage
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationResponseReceptor();

    registry.Register<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult>(
      receptor, LifecycleStage.PostOutboxInline);

    // Verify registered
    var before = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestCommand), LifecycleStage.PostOutboxInline);
    await Assert.That(before.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsTrue();

    // Act
    var removed = registry.Unregister<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult>(
      receptor, LifecycleStage.PostOutboxInline);

    // Assert
    await Assert.That(removed).IsTrue();
    var after = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestCommand), LifecycleStage.PostOutboxInline);
    await Assert.That(after.Any(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal))).IsFalse();
  }

  [Test]
  public async Task Unregister_ResponseReceptor_NotRegistered_ReturnsFalseAsync() {
    // Arrange
    var registry = _createRegistry();
    var receptor = new RuntimeRegistrationResponseReceptor();

    // Act
    var removed = registry.Unregister<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult>(
      receptor, LifecycleStage.PostOutboxInline);

    // Assert
    await Assert.That(removed).IsFalse();
  }

  #endregion

  #region IAcceptsLifecycleContext Integration

  [Test]
  public async Task Register_ReceptorWithLifecycleContext_ReceivesContextDuringInvocationAsync() {
    // Arrange - use non-default stage
    var registry = _createRegistry();
    var receptor = new LifecycleContextAwareReceptor();

    registry.Register<RuntimeRegistrationTestEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    var receptors = registry.GetReceptorsFor(typeof(RuntimeRegistrationTestEvent), LifecycleStage.PostPerspectiveInline);
    var runtimeEntry = receptors.First(r => r.ReceptorId.StartsWith(RUNTIME_PREFIX, StringComparison.Ordinal));

    // Set up lifecycle context accessor in the service provider
    // ILifecycleContextAccessor is registered by AddWhizbang() (core services), so we register it
    // manually here since we only use AddReceptors()/AddWhizbangDispatcher() in these tests.
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddScoped<ILifecycleContextAccessor, AsyncLocalLifecycleContextAccessor>();
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostPerspectiveInline,
      StreamId = Guid.CreateVersion7()
    };

    using var sp = services.BuildServiceProvider();
    using var scope = sp.CreateScope();
    var scopedProvider = scope.ServiceProvider;

    // Set the lifecycle context on the accessor (simulating what ReceptorInvoker does)
    var accessor = scopedProvider.GetRequiredService<ILifecycleContextAccessor>();
    accessor.Current = lifecycleContext;

    var testMessage = new RuntimeRegistrationTestEvent(Guid.CreateVersion7(), "context-test");

    // Act - invoke through the delegate (which reads from ILifecycleContextAccessor)
    await runtimeEntry.InvokeAsync(scopedProvider, testMessage, null!, null, CancellationToken.None);

    // Assert
    await Assert.That(receptor.WasInvoked).IsTrue();
    await Assert.That(receptor.ReceivedContext).IsNotNull();
    await Assert.That(receptor.ReceivedContext!.CurrentStage).IsEqualTo(LifecycleStage.PostPerspectiveInline);
    await Assert.That(receptor.ReceivedContext.StreamId).IsEqualTo(lifecycleContext.StreamId);
  }

  #endregion

  #region Helpers

  /// <summary>
  /// Creates a real source-generated IReceptorRegistry via the DI container.
  /// </summary>
  private static IReceptorRegistry _createRegistry() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    var sp = services.BuildServiceProvider();
    return sp.GetRequiredService<IReceptorRegistry>();
  }

  /// <summary>
  /// Creates a minimal service provider for invoking runtime-registered receptor delegates.
  /// </summary>
  private static ServiceProvider _createServiceProvider() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider();
  }

  #endregion

  #region Test Types

  /// <summary>
  /// Test event for void receptor registration tests.
  /// </summary>
  public record RuntimeRegistrationTestEvent([property: StreamId] Guid StreamId, string Data) : IEvent;

  /// <summary>
  /// Test message for response receptor registration tests.
  /// Uses IMessage (not ICommand) to avoid requiring [StreamId] attribute.
  /// </summary>
  public record RuntimeRegistrationTestCommand(string Data) : IMessage;

  /// <summary>
  /// Test result type for response receptor registration tests.
  /// </summary>
  public record RuntimeRegistrationTestResult(string Processed);

  /// <summary>
  /// Simple void receptor that tracks invocation.
  /// The source generator will discover this and register compile-time entries at the default stages
  /// (LocalImmediateInline, PreOutboxInline, PostInboxInline). Tests use non-default stages
  /// (PostOutboxInline, PostPerspectiveInline) and filter by "runtime_" prefix to isolate
  /// runtime-registered entries from compile-time entries.
  /// </summary>
  public class RuntimeRegistrationTestReceptor : IReceptor<RuntimeRegistrationTestEvent> {
    public bool WasInvoked { get; private set; }
    public RuntimeRegistrationTestEvent? LastMessage { get; private set; }

    public ValueTask HandleAsync(RuntimeRegistrationTestEvent message, CancellationToken cancellationToken = default) {
      WasInvoked = true;
      LastMessage = message;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Response receptor that returns a result.
  /// </summary>
  public class RuntimeRegistrationResponseReceptor : IReceptor<RuntimeRegistrationTestCommand, RuntimeRegistrationTestResult> {
    public ValueTask<RuntimeRegistrationTestResult> HandleAsync(
      RuntimeRegistrationTestCommand message,
      CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new RuntimeRegistrationTestResult($"processed:{message.Data}"));
    }
  }

  /// <summary>
  /// Receptor that implements IAcceptsLifecycleContext to receive lifecycle context during invocation.
  /// </summary>
  public class LifecycleContextAwareReceptor : IReceptor<RuntimeRegistrationTestEvent>, IAcceptsLifecycleContext {
    public bool WasInvoked { get; private set; }
    public ILifecycleContext? ReceivedContext { get; private set; }

    public void SetLifecycleContext(ILifecycleContext context) {
      ReceivedContext = context;
    }

    public ValueTask HandleAsync(RuntimeRegistrationTestEvent message, CancellationToken cancellationToken = default) {
      WasInvoked = true;
      return ValueTask.CompletedTask;
    }
  }

  #endregion
}
