using System.Collections.Concurrent;

namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Static registry for event type to perspective mappings.
/// Populated by source-generated code at static initialization.
/// </summary>
/// <remarks>
/// <para>
/// This class provides a thread-safe mechanism for source generators to register
/// event type mappings that are automatically picked up by <see cref="ServiceCollectionExtensions.AddWhizbang"/>.
/// </para>
/// <para>
/// Source generators call <see cref="Register"/> during static initialization,
/// which happens before <c>AddWhizbang()</c> is called.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#auto-registration</docs>
public static class SyncEventTypeRegistrations {
  private static readonly ConcurrentDictionary<Type, HashSet<string>> _mappings = new();
  private static readonly object _lock = new();

  /// <summary>
  /// Registers an event type to perspective mapping.
  /// Called by source-generated code during static initialization.
  /// </summary>
  /// <param name="eventType">The event type to track.</param>
  /// <param name="perspectiveName">The fully qualified perspective type name.</param>
  public static void Register(Type eventType, string perspectiveName) {
    ArgumentNullException.ThrowIfNull(eventType);
    ArgumentNullException.ThrowIfNull(perspectiveName);

    _mappings.AddOrUpdate(
        eventType,
        _ => [perspectiveName],
        (_, existing) => {
          lock (_lock) {
            existing.Add(perspectiveName);
            return existing;
          }
        }
    );

  }

  /// <summary>
  /// Gets all registered mappings as a dictionary.
  /// Called by <see cref="ServiceCollectionExtensions.AddWhizbang"/> to create the registry.
  /// </summary>
  /// <returns>A dictionary mapping event types to perspective names.</returns>
  internal static Dictionary<Type, string[]> GetMappings() {
    var result = new Dictionary<Type, string[]>();
    foreach (var kvp in _mappings) {
      lock (_lock) {
        result[kvp.Key] = kvp.Value.ToArray();
      }
    }

    return result;
  }

  /// <summary>
  /// Clears all registrations. Used for testing only.
  /// </summary>
  internal static void Clear() {
    _mappings.Clear();
  }
}
