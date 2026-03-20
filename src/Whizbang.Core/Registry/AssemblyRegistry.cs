using System.Collections.Concurrent;

namespace Whizbang.Core.Registry;

/// <summary>
/// Generic thread-safe registry for multi-assembly contributions.
/// Uses [ModuleInitializer] pattern - assemblies self-register at load time.
/// Follows the same pattern as JsonContextRegistry but is generic and reusable.
/// </summary>
/// <typeparam name="T">The contribution type (e.g., IStreamIdExtractor)</typeparam>
/// <remarks>
/// <para>
/// This registry solves the multi-assembly source generator discovery problem:
/// When types are defined in a "contracts" assembly but used in a "service" assembly,
/// the service's generated code doesn't know about the contracts' types.
/// </para>
/// <para>
/// <strong>How it works:</strong>
/// <list type="number">
/// <item>Each assembly's [ModuleInitializer] registers its contributions at load time (before Main())</item>
/// <item>Contributions are stored with priority (lower = tried first)</item>
/// <item>Consumers retrieve all contributions ordered by priority</item>
/// </list>
/// </para>
/// <para>
/// <strong>Priority convention:</strong>
/// <list type="bullet">
/// <item>100 = Contracts assemblies (tried first)</item>
/// <item>1000 = Service assemblies (default)</item>
/// </list>
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/assembly-registry</docs>
/// <tests>tests/Whizbang.Core.Tests/Registry/AssemblyRegistryTests.cs</tests>
#pragma warning disable CA1000 // Do not declare static members on generic types - by design for registry pattern
#pragma warning disable S2743 // Static fields in generic types are intentional — each T gets its own registry
public static class AssemblyRegistry<T> where T : class {
  /// <summary>
  /// Thread-safe collection of registered contributions with priorities.
  /// Lower priority = tried first. Populated via [ModuleInitializer].
  /// </summary>
  private static readonly ConcurrentBag<(int Priority, T Contribution)> _contributions = [];

  /// <summary>
  /// Cached ordered list (invalidated on new registration).
  /// </summary>
  private static List<T>? _orderedContributions;
  private static readonly object _lock = new();

  /// <summary>
  /// Register a contribution. Called from [ModuleInitializer] - runs before Main().
  /// </summary>
  /// <param name="contribution">The contribution to register</param>
  /// <param name="priority">Lower = tried first. Contracts assemblies should use 100, services use 1000.</param>
  /// <exception cref="ArgumentNullException">Thrown when contribution is null</exception>
  public static void Register(T contribution, int priority = 1000) {
    ArgumentNullException.ThrowIfNull(contribution);
    _contributions.Add((priority, contribution));

    lock (_lock) {
      _orderedContributions = null; // Invalidate cache
    }
  }

  /// <summary>
  /// Get all contributions ordered by priority (lower first).
  /// </summary>
  /// <returns>Read-only list of contributions ordered by priority</returns>
  public static IReadOnlyList<T> GetOrderedContributions() {
    if (_orderedContributions is not null) {
      return _orderedContributions;
    }

    lock (_lock) {
      if (_orderedContributions is not null) {
        return _orderedContributions;
      }
      // Take a snapshot before iterating to avoid race condition with concurrent Register() calls
      _orderedContributions = [.. _contributions
          .ToArray()
          .OrderBy(c => c.Priority)
          .Select(c => c.Contribution)];
      return _orderedContributions;
    }
  }

  /// <summary>
  /// Count of registered contributions (for diagnostics/testing).
  /// </summary>
  public static int Count => _contributions.Count;

  /// <summary>
  /// Clears all registered contributions.
  /// <strong>ONLY use in tests</strong> - never in production code.
  /// </summary>
  /// <remarks>
  /// This method exists to support test isolation. Since AssemblyRegistry uses
  /// static state, tests may need to reset the registry between test runs.
  /// </remarks>
  internal static void ClearForTesting() {
    lock (_lock) {
      _contributions.Clear();
      _orderedContributions = null;
    }
  }
}
#pragma warning restore CA1000
