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
  /// The name says "WithNoCallback" but this test keeps the process's real callbacks in place: the
  /// list is process-static and this assembly's generated module initializer has already filled
  /// it, so an absolute "the collection is empty" assertion would simply be false. What is
  /// asserted here is the delta: a baseline invocation, then the same invocation with one no-op
  /// callback appended, must produce the same count. The genuinely empty registry is covered
  /// separately by <see cref="InvokeRegistration_WithTheRegistryEmpty_TouchesNothingAsync"/>,
  /// which empties the list through the registry's own take/restore seam. Renaming would break
  /// the &lt;tests&gt; link in PerspectiveRunnerCallbackRegistry.cs.
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

  // A host with no perspectives anywhere gets an empty registry, and driver extensions still call
  // this on every startup. The early return is what keeps it from opening a per-collection
  // invocation-tracking entry — a ConditionalWeakTable entry keyed on a ServiceCollection — for a
  // collection nothing will ever be registered into.
  [Test]
  public async Task InvokeRegistration_WithTheRegistryEmpty_TouchesNothingAsync() {
    var taken = PerspectiveRunnerCallbackRegistry.TakeCallbacksForTests();
    try {
      var services = new ServiceCollection();

      PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

      await Assert.That(services.Count).IsEqualTo(0)
        .Because("with no callbacks there is nothing to register, so the collection must come back "
          + "exactly as it went in");
    } finally {
      PerspectiveRunnerCallbackRegistry.RestoreCallbacksForTests(taken);
    }
  }

  // The control: the same call with one callback in place does register, so the untouched
  // collection above is the empty-registry answer and not the method having stopped working.
  [Test]
  public async Task InvokeRegistration_WithOneCallback_RunsItAsync() {
    var taken = PerspectiveRunnerCallbackRegistry.TakeCallbacksForTests();
    try {
      var ran = 0;
      PerspectiveRunnerCallbackRegistry.RegisterCallback(_ => ran++);
      var services = new ServiceCollection();

      PerspectiveRunnerCallbackRegistry.InvokeRegistration(services);

      await Assert.That(ran).IsEqualTo(1)
        .Because("a registered callback is exactly what this method exists to run");
    } finally {
      PerspectiveRunnerCallbackRegistry.RestoreCallbacksForTests(taken);
    }
  }
}
