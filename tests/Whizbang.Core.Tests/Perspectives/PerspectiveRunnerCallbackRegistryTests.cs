using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for PerspectiveRunnerCallbackRegistry (RegisterCallback, InvokeRegistration).
/// Verifies AOT-compatible perspective runner registration callback mechanism.
/// Target: 100% branch coverage.
/// </summary>
[NotInParallel("PerspectiveRunnerCallbackRegistry tests share static state")]
public class PerspectiveRunnerCallbackRegistryTests {
  [Test]
  public async Task RegisterCallback_WithValidCallback_StoresCallbackAsync() {
    // Arrange
    var wasCalled = false;
    void callback(IServiceCollection services) {
      wasCalled = true;
    }

    // Act
    PerspectiveRunnerCallbackRegistry.RegisterCallback(callback);

    var services = new ServiceCollection();
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    // Assert
    await Assert.That(wasCalled).IsTrue();
  }

  /// <summary>
  /// A callback that registers nothing must contribute nothing: invoking the registry with it in
  /// place yields exactly the same service descriptors as invoking without it.
  /// </summary>
  /// <remarks>
  /// The name says "WithNoCallback" but the registry's callback list is process-static, has no public
  /// reset, and is already populated by this assembly's generated module initializer — so the genuine
  /// zero-callback early return in InvokeRegistration is unreachable from any test here, and an
  /// absolute "the collection is empty" assertion is simply false. What is reachable, and asserted
  /// here, is the delta: a baseline invocation, then the same invocation with one no-op callback
  /// appended, must produce the same count. Renaming would break the &lt;tests&gt; link in
  /// PerspectiveRunnerCallbackRegistry.cs.
  /// </remarks>
  [Test]
  public async Task InvokeRegistration_WithNoCallback_DoesNotThrowAsync() {
    // Arrange - baseline: what the callbacks already registered contribute to a fresh collection.
    // _invoked is keyed per ServiceCollection, so every fresh collection re-runs all callbacks.
    var baselineServices = new ServiceCollection();
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(baselineServices);
    var baselineCount = baselineServices.Count;

    // A callback that deliberately registers nothing
    var noOpCallbackRan = false;
    PerspectiveRunnerCallbackRegistry.RegisterCallback(_ => noOpCallbackRan = true);

    var services = new ServiceCollection();

    // Act
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    // Assert - the no-op callback really ran, so the unchanged count below is not the trivial
    // consequence of the callback never being invoked...
    await Assert.That(noOpCallbackRan).IsTrue();

    // ...and it contributed no descriptors of its own.
    await Assert.That(services.Count).IsEqualTo(baselineCount)
      .Because("a callback that registers nothing must leave the service collection as it found it");
  }

  [Test]
  public async Task InvokeRegistration_PassesCorrectServicesToCallbackAsync() {
    // Arrange
    IServiceCollection? capturedServices = null;

    void callback(IServiceCollection services) {
      capturedServices = services;
    }

    PerspectiveRunnerCallbackRegistry.RegisterCallback(callback);

    var services = new ServiceCollection();

    // Act
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    // Assert
    await Assert.That(capturedServices).IsSameReferenceAs(services);
  }

  [Test]
  public async Task RegisterCallback_MultipleRegistrations_InvokesAllCallbacksAsync() {
    // Arrange
    var firstCalled = false;
    var secondCalled = false;

    void firstCallback(IServiceCollection services) {
      firstCalled = true;
    }

    void secondCallback(IServiceCollection services) {
      secondCalled = true;
    }

    // Act
    PerspectiveRunnerCallbackRegistry.RegisterCallback(firstCallback);
    PerspectiveRunnerCallbackRegistry.RegisterCallback(secondCallback);

    var services = new ServiceCollection();
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    // Assert - Both should be called (different from ModelRegistrationRegistry which only calls latest)
    await Assert.That(firstCalled).IsTrue();
    await Assert.That(secondCalled).IsTrue();
  }

  [Test]
  public async Task InvokeRegistration_SameServiceCollection_OnlyInvokesOncePerCallbackAsync() {
    // Arrange
    var callCount = 0;

    void callback(IServiceCollection services) {
      callCount++;
    }

    PerspectiveRunnerCallbackRegistry.RegisterCallback(callback);

    var services = new ServiceCollection();

    // Act - Invoke twice with same ServiceCollection
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

    // Assert - Should only be called once per ServiceCollection
    await Assert.That(callCount).IsEqualTo(1);
  }

  [Test]
  public async Task InvokeRegistration_DifferentServiceCollections_InvokesForEachAsync() {
    // Arrange
    var callCount = 0;

    void callback(IServiceCollection services) {
      callCount++;
    }

    PerspectiveRunnerCallbackRegistry.RegisterCallback(callback);

    var services1 = new ServiceCollection();
    var services2 = new ServiceCollection();

    // Act - Invoke with different ServiceCollections
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services1);
    PerspectiveRunnerCallbackRegistry.InvokeRegistration(services2);

    // Assert - Should be called once for each ServiceCollection
    await Assert.That(callCount).IsEqualTo(2);
  }
}
