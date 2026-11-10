// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
  private static readonly List<(string? Assembly, Action<IServiceCollection> Callback)> _callbacks = [];
  private static readonly ConditionalWeakTable<IServiceCollection, HashSet<int>> _invoked = [];
  private static readonly Lock _lock = new();

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
      _callbacks.Add((null, callback));
    }
  }

  /// <summary>
  /// Registers <paramref name="assembly"/>'s perspective runner registration. Called from generated code, so a host
  /// runs only its own service's (#2243).
  /// </summary>
  /// <param name="assembly">The registering assembly's name.</param>
  /// <param name="callback">The registration.</param>
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceScopedRegistrationTests.cs:EverySlot_ReplaysOnlyTheHostsOwnServiceAsync</tests>
  public static void RegisterCallback(string assembly, Action<IServiceCollection> callback) {
    ArgumentNullException.ThrowIfNull(assembly);
    ArgumentNullException.ThrowIfNull(callback);
    lock (_lock) {
      _callbacks.Add((assembly, callback));
    }
  }

  /// <summary>
  /// Test seam: removes every registration callback and hands them back so the caller can
  /// restore them.
  /// </summary>
  /// <remarks>
  /// The callback list is process-global and append-only, and a generated module initializer in
  /// every assembly that declares a perspective fills it before any other code runs. That makes
  /// the "nothing registered" answer in <see cref="InvokeRegistration"/> — what a host with no
  /// perspectives gets, and the reason it does no per-collection bookkeeping for a collection
  /// nothing will be registered into — unreachable in any process that has one. Callers must
  /// restore with <see cref="RestoreCallbacksForTests"/> in a finally, and must serialize against
  /// anything else that touches this registry.
  /// </remarks>
  internal static (string? Assembly, Action<IServiceCollection> Callback)[] TakeCallbacksForTests() {
    lock (_lock) {
      var taken = _callbacks.ToArray();
      _callbacks.Clear();
      return taken;
    }
  }

  /// <summary>Puts back the callbacks taken by <see cref="TakeCallbacksForTests"/>.</summary>
  internal static void RestoreCallbacksForTests((string? Assembly, Action<IServiceCollection> Callback)[] callbacks) {
    ArgumentNullException.ThrowIfNull(callbacks);
    lock (_lock) {
      _callbacks.Clear();
      _callbacks.AddRange(callbacks);
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
  /// <tests>tests/Whizbang.Core.Tests/ServiceScope/ServiceScopedRegistrationTests.cs:AnExcludedRunnerCallback_IsNotSpent_SoALaterAnchorOnItsServiceStillRegistersItAsync</tests>
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

      // Invoke every callback of the host's own service not yet invoked for this ServiceCollection (#2243). Another
      // service's is skipped without being marked, so a later anchor on that service still runs it.
      var scope = ServiceAssemblyScope.For(services);
      for (var i = 0; i < _callbacks.Count; i++) {
        if (scope.Includes(_callbacks[i].Assembly) && invokedSet.Add(i)) {
          _callbacks[i].Callback(services);
        }
      }
    }
  }
}
