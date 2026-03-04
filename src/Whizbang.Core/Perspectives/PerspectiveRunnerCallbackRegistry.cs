using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Core.Perspectives;

/// <summary>
/// Static registry for perspective runner DI registration callbacks.
/// Consumer assemblies register their AddPerspectiveRunners() method via module initializer.
/// This approach is AOT-compatible (no reflection required).
/// Supports multiple assemblies registering callbacks (e.g., InventoryWorker + BFF.API).
/// Tracks which callbacks have been invoked per ServiceCollection to prevent duplicate registrations.
/// Uses ConditionalWeakTable to track per-ServiceCollection without preventing garbage collection.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerCallbackRegistryTests.cs</tests>
public static class PerspectiveRunnerCallbackRegistry {
  private static readonly List<Action<IServiceCollection>> _callbacks = [];
  private static readonly ConditionalWeakTable<IServiceCollection, HashSet<int>> _invoked = [];
  private static readonly object _lock = new();

  /// <summary>
  /// Registers a callback that will register perspective runners with the DI container.
  /// Called by source-generated module initializer in the consumer assembly.
  /// Supports multiple assemblies registering callbacks (thread-safe).
  /// </summary>
  /// <param name="callback">Callback that registers perspective runners with the service collection.</param>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerCallbackRegistryTests.cs:RegisterCallback_WithValidCallback_StoresCallbackAsync</tests>
  public static void RegisterCallback(Action<IServiceCollection> callback) {
    ArgumentNullException.ThrowIfNull(callback);

    lock (_lock) {
      _callbacks.Add(callback);
    }
  }

  /// <summary>
  /// Invokes all registered perspective runner registration callbacks for the given ServiceCollection.
  /// Called by driver extensions (Postgres) to register perspective runners automatically.
  /// If no callbacks have been set (module initializers haven't run or no perspectives found), does nothing gracefully.
  /// Invokes ALL callbacks (unlike ModelRegistrationRegistry which only calls latest) to support
  /// multiple assemblies with perspectives (e.g., BFF.API + InventoryWorker in same process).
  /// Tracks which callbacks have been invoked for each ServiceCollection to prevent duplicate registrations.
  /// Uses ConditionalWeakTable to track per-ServiceCollection, allowing test scenarios where each test creates a new ServiceCollection.
  /// </summary>
  /// <param name="services">The service collection to register services in.</param>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerCallbackRegistryTests.cs:InvokeRegistration_WithNoCallback_DoesNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerCallbackRegistryTests.cs:InvokeRegistration_PassesCorrectServicesToCallbackAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveRunnerCallbackRegistryTests.cs:InvokeRegistration_SameServiceCollection_OnlyInvokesOncePerCallbackAsync</tests>
  public static void InvokeRegistration(IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);

    lock (_lock) {
      if (_callbacks.Count == 0) {
        return;
      }

      // Get or create the invocation tracking set for this ServiceCollection
      // ConditionalWeakTable ensures we don't prevent ServiceCollection from being garbage collected
      if (!_invoked.TryGetValue(services, out var invokedSet)) {
        invokedSet = [];
        _invoked.Add(services, invokedSet);
      }

      // Invoke ALL callbacks that haven't been invoked yet for this ServiceCollection
      // This supports multiple assemblies with perspectives in the same process
      for (var i = 0; i < _callbacks.Count; i++) {
        if (invokedSet.Add(i)) {
          _callbacks[i](services);
        }
      }
    }
  }
}
