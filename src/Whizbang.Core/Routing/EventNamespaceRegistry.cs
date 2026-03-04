using System.Collections.Concurrent;

namespace Whizbang.Core.Routing;

/// <summary>
/// Static registry for auto-discovered event namespaces from all loaded assemblies.
/// Uses ModuleInitializer pattern - same as <see cref="Serialization.JsonContextRegistry"/>.
/// AOT-compatible - no reflection, all namespaces are source-generated and registered at module load time.
/// </summary>
/// <remarks>
/// <para>
/// Each library (ECommerce.Contracts, MyApp.Contracts, etc.) uses [ModuleInitializer] to register
/// its source-generated <see cref="IEventNamespaceSource"/> classes. This ensures event namespaces
/// from perspectives and receptors are available for subscription discovery.
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#event-namespace-registry</docs>
public static class EventNamespaceRegistry {
  /// <summary>
  /// Thread-safe collection of registered event namespace sources.
  /// Populated via [ModuleInitializer] methods in each assembly.
  /// </summary>
  private static readonly ConcurrentBag<IEventNamespaceSource> _sources = [];

  /// <summary>
  /// Registers an event namespace source from a user assembly.
  /// Called from [ModuleInitializer] methods - runs before Main().
  /// </summary>
  /// <param name="source">Source-generated event namespace source to register</param>
  public static void Register(IEventNamespaceSource source) {
    ArgumentNullException.ThrowIfNull(source);
    _sources.Add(source);
  }

  /// <summary>
  /// Gets all event namespaces from all registered sources (combined, deduplicated).
  /// </summary>
  /// <returns>Set of all event namespaces from perspectives and receptors</returns>
  public static IReadOnlySet<string> GetAllNamespaces() {
    var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in _sources) {
      foreach (var ns in source.GetAllEventNamespaces()) {
        namespaces.Add(ns);
      }
    }
    return namespaces;
  }

  /// <summary>
  /// Gets perspective event namespaces from all registered sources.
  /// </summary>
  /// <returns>Set of event namespaces from perspectives</returns>
  public static IReadOnlySet<string> GetPerspectiveNamespaces() {
    var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in _sources) {
      foreach (var ns in source.GetPerspectiveEventNamespaces()) {
        namespaces.Add(ns);
      }
    }
    return namespaces;
  }

  /// <summary>
  /// Gets receptor event namespaces from all registered sources.
  /// </summary>
  /// <returns>Set of event namespaces from receptors</returns>
  public static IReadOnlySet<string> GetReceptorNamespaces() {
    var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var source in _sources) {
      foreach (var ns in source.GetReceptorEventNamespaces()) {
        namespaces.Add(ns);
      }
    }
    return namespaces;
  }

  /// <summary>
  /// Gets the count of registered sources (for diagnostics/testing).
  /// </summary>
  public static int RegisteredCount => _sources.Count;

  /// <summary>
  /// Clears all registered sources. For testing purposes only.
  /// </summary>
  internal static void Clear() => _sources.Clear();
}
