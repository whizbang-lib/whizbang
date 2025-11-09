using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Transports;

/// <summary>
/// Auto-discovery for transport subscriptions based on local receptors.
/// Supports explicit type subscription, namespace patterns, and full receptor discovery.
///
/// Usage:
/// options.Transports.AutoSubscribe(discovery => {
///     discovery.DiscoverReceptors();  // Auto-discover all IReceptor implementations
///     discovery.SubscribeToNamespace("MyApp.Orders.*");  // Pattern-based
///     discovery.Subscribe&lt;OrderCreated&gt;();  // Explicit type
/// });
/// </summary>
public class TransportAutoDiscovery {
  private readonly List<NamespacePattern> _patterns = new();
  private readonly List<Type> _explicitTypes = new();

  /// <summary>
  /// Subscribe to all message types matching a namespace pattern.
  /// </summary>
  /// <param name="pattern">Namespace pattern with wildcards (e.g., "MyApp.Orders.*")</param>
  public void SubscribeToNamespace(string pattern) {
    ArgumentNullException.ThrowIfNull(pattern);
    _patterns.Add(new NamespacePattern(pattern));
  }

  /// <summary>
  /// Explicitly subscribe to a specific message type.
  /// </summary>
  public void Subscribe<TMessage>() {
    _explicitTypes.Add(typeof(TMessage));
  }

  /// <summary>
  /// Checks if a message type should be subscribed based on patterns and explicit types.
  /// </summary>
  public bool ShouldSubscribe(Type messageType) {
    ArgumentNullException.ThrowIfNull(messageType);

    // Check explicit types first
    if (_explicitTypes.Contains(messageType)) {
      return true;
    }

    // Check namespace patterns
    return _patterns.Any(p => p.Matches(messageType));
  }

  /// <summary>
  /// Gets all message types that should be subscribed.
  /// Currently returns only explicit types. Receptor discovery will be added in future.
  /// </summary>
  public List<Type> GetMessageTypesToSubscribe() {
    // For now, return explicit types
    // Future: Add receptor discovery using source generator
    return new List<Type>(_explicitTypes);
  }

  /// <summary>
  /// Gets all namespace patterns.
  /// </summary>
  public List<NamespacePattern> GetNamespacePatterns() {
    return new List<NamespacePattern>(_patterns);
  }

  /// <summary>
  /// Gets all explicitly subscribed types.
  /// </summary>
  public List<Type> GetExplicitTypes() {
    return new List<Type>(_explicitTypes);
  }

  /// <summary>
  /// Discovers all IReceptor implementations and subscribes to their message types.
  /// NOTE: This requires source generator integration (future implementation).
  /// For now, this is a placeholder that does nothing.
  /// </summary>
  public void DiscoverReceptors() {
    // TODO: Integrate with source generator to get all IReceptor<TMessage> types
    // For each receptor found:
    //   - Extract TMessage type
    //   - Add to explicit types if ShouldSubscribe(TMessage) returns true
    //
    // This will be implemented once source generator provides receptor registry
  }
}
