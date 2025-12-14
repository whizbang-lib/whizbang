using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Transports;

/// <summary>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ExactMatch_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardSuffix_ShouldNotMatchDifferentNamespaceAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_WildcardPrefix_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_DoubleWildcard_ShouldMatchNestedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldNotMatchWhenInsufficientSegmentsAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ShouldHandleNullNamespaceAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_Constructor_WithNullPattern_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_Matches_WithNullMessageType_ShouldThrowAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:NamespacePattern_ToString_ShouldReturnPatternAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_SubscribeToNamespace_ShouldStorePatternAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_Subscribe_ShouldStoreExplicitTypeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenExplicitTypeAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldNotSubscribe_WhenTypeNotAddedAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenMatchesPatternAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldNotSubscribe_WhenDoesNotMatchPatternAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenMatchesAnyPatternAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenBothExplicitAndPatternMatchAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_GetMessageTypesToSubscribe_ShouldReturnExplicitTypesAsync</tests>
/// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_DiscoverReceptors_ShouldNotThrowAsync</tests>
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
  private readonly List<NamespacePattern> _patterns = [];
  private readonly List<Type> _explicitTypes = [];

  /// <summary>
  /// Subscribe to all message types matching a namespace pattern.
  /// </summary>
  /// <param name="pattern">Namespace pattern with wildcards (e.g., "MyApp.Orders.*")</param>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_SubscribeToNamespace_ShouldStorePatternAsync</tests>
  public void SubscribeToNamespace(string pattern) {
    ArgumentNullException.ThrowIfNull(pattern);
    _patterns.Add(new NamespacePattern(pattern));
  }

  /// <summary>
  /// Explicitly subscribe to a specific message type.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_Subscribe_ShouldStoreExplicitTypeAsync</tests>
  public void Subscribe<TMessage>() {
    _explicitTypes.Add(typeof(TMessage));
  }

  /// <summary>
  /// Checks if a message type should be subscribed based on patterns and explicit types.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenExplicitTypeAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldNotSubscribe_WhenTypeNotAddedAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenMatchesPatternAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldNotSubscribe_WhenDoesNotMatchPatternAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenMatchesAnyPatternAsync</tests>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_ShouldSubscribe_WhenBothExplicitAndPatternMatchAsync</tests>
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
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_GetMessageTypesToSubscribe_ShouldReturnExplicitTypesAsync</tests>
  public List<Type> GetMessageTypesToSubscribe() {
    // For now, return explicit types
    // Future: Add receptor discovery using source generator
    return [.. _explicitTypes];
  }

  /// <summary>
  /// Gets all namespace patterns.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_SubscribeToNamespace_ShouldStorePatternAsync</tests>
  public List<NamespacePattern> GetNamespacePatterns() {
    return [.. _patterns];
  }

  /// <summary>
  /// Gets all explicitly subscribed types.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_Subscribe_ShouldStoreExplicitTypeAsync</tests>
  public List<Type> GetExplicitTypes() {
    return [.. _explicitTypes];
  }

  /// <summary>
  /// Discovers all IReceptor implementations and subscribes to their message types.
  /// NOTE: This requires source generator integration (future implementation).
  /// For now, this is a placeholder that does nothing.
  /// </summary>
  /// <tests>tests/Whizbang.Transports.Tests/TransportAutoDiscoveryTests.cs:TransportAutoDiscovery_DiscoverReceptors_ShouldNotThrowAsync</tests>
  public static void DiscoverReceptors() {
    // TODO: Integrate with source generator to get all IReceptor<TMessage> types
    // For each receptor found:
    //   - Extract TMessage type
    //   - Add to explicit types if ShouldSubscribe(TMessage) returns true
    //
    // This will be implemented once source generator provides receptor registry
  }
}
