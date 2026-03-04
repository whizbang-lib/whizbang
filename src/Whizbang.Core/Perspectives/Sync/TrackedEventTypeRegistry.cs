namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Default implementation of <see cref="ITrackedEventTypeRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// This implementation can operate in two modes:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <strong>Static mode</strong>: Uses a dictionary provided at construction time.
/// </description></item>
/// <item><description>
/// <strong>Dynamic mode</strong>: Reads from <see cref="SyncEventTypeRegistrations"/> on each call.
/// This mode is used by default when registered via <c>AddWhizbang()</c> to support
/// module initializers that register mappings after the registry is constructed.
/// </description></item>
/// </list>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#type-registry</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/TrackedEventTypeRegistryTests.cs</tests>
public sealed class TrackedEventTypeRegistry : ITrackedEventTypeRegistry {
  private readonly Dictionary<Type, List<string>>? _staticMappings;
  private readonly bool _useDynamicRegistrations;

  /// <summary>
  /// Initializes a registry that reads dynamically from <see cref="SyncEventTypeRegistrations"/>.
  /// This supports module initializers that register mappings after the registry is constructed.
  /// </summary>
  public TrackedEventTypeRegistry() {
    _staticMappings = null;
    _useDynamicRegistrations = true;
  }

  /// <summary>
  /// Initializes the registry with a dictionary mapping event types to perspective names.
  /// </summary>
  /// <param name="mappings">A dictionary mapping each event type to its tracking perspectives.</param>
  public TrackedEventTypeRegistry(IReadOnlyDictionary<Type, string> mappings) {
    ArgumentNullException.ThrowIfNull(mappings);

    _staticMappings = new Dictionary<Type, List<string>>();
    foreach (var (eventType, perspectiveName) in mappings) {
      if (!_staticMappings.TryGetValue(eventType, out var list)) {
        list = [];
        _staticMappings[eventType] = list;
      }
      list.Add(perspectiveName);
    }
    _useDynamicRegistrations = false;
  }

  /// <summary>
  /// Initializes the registry with a dictionary mapping event types to multiple perspective names.
  /// </summary>
  /// <param name="mappings">A dictionary mapping each event type to its tracking perspectives.</param>
  public TrackedEventTypeRegistry(IReadOnlyDictionary<Type, string[]> mappings) {
    ArgumentNullException.ThrowIfNull(mappings);

    _staticMappings = new Dictionary<Type, List<string>>();
    foreach (var (eventType, perspectiveNames) in mappings) {
      _staticMappings[eventType] = [.. perspectiveNames];
    }
    _useDynamicRegistrations = false;
  }

  /// <inheritdoc />
  public bool ShouldTrack(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);

    if (_useDynamicRegistrations) {
      var mappings = SyncEventTypeRegistrations.GetMappings();
      return mappings.ContainsKey(eventType);
    }

    return _staticMappings!.ContainsKey(eventType);
  }

  /// <inheritdoc />
  public string? GetPerspectiveName(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);

    if (_useDynamicRegistrations) {
      var mappings = SyncEventTypeRegistrations.GetMappings();
      return mappings.TryGetValue(eventType, out var perspectives) && perspectives.Length > 0
          ? perspectives[0]
          : null;
    }

    return _staticMappings!.TryGetValue(eventType, out var list) && list.Count > 0
        ? list[0]
        : null;
  }

  /// <inheritdoc />
  public IReadOnlyList<string> GetPerspectiveNames(Type eventType) {
    ArgumentNullException.ThrowIfNull(eventType);

    if (_useDynamicRegistrations) {
      var mappings = SyncEventTypeRegistrations.GetMappings();
      return mappings.TryGetValue(eventType, out var perspectives)
          ? perspectives
          : [];
    }

    return _staticMappings!.TryGetValue(eventType, out var list)
        ? list
        : [];
  }
}
