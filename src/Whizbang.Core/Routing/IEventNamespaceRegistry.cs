namespace Whizbang.Core.Routing;

/// <summary>
/// Zero-reflection registry for discovering event namespaces from perspectives and receptors (AOT-compatible).
/// Implemented by source-generated EventNamespaceRegistry in {AssemblyName}.Generated namespace.
/// </summary>
/// <remarks>
/// <para>
/// This registry is used at transport startup to auto-discover event namespaces
/// that the service should subscribe to based on its registered perspectives and receptors.
/// </para>
/// <para>
/// The source generator discovers:
/// - Event types from IPerspectiveFor&lt;TModel, TEvent1, ...&gt; implementations
/// - Event types from IReceptor&lt;TEvent&gt; implementations (where TEvent : IEvent)
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/routing#event-namespace-registry</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/EventNamespaceRegistryTests.cs:GetAllNamespaces_AfterRegisteringSource_ContainsAllNamespacesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/EventNamespaceRegistryTests.cs:GetReceptorNamespaces_CombinesFromMultipleSourcesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Routing/EventNamespaceRegistryTests.cs:GetAllNamespaces_WithDuplicates_DeduplicatesNamespacesAsync</tests>
public interface IEventNamespaceRegistry {
  /// <summary>
  /// Gets all event namespaces discovered from perspectives in this assembly.
  /// </summary>
  /// <returns>Set of lowercase namespace strings (e.g., "myapp.orders.events").</returns>
  IReadOnlySet<string> GetPerspectiveEventNamespaces();

  /// <summary>
  /// Gets all event namespaces discovered from receptors handling events in this assembly.
  /// </summary>
  /// <returns>Set of lowercase namespace strings (e.g., "myapp.orders.events").</returns>
  IReadOnlySet<string> GetReceptorEventNamespaces();

  /// <summary>
  /// Gets all unique event namespaces from both perspectives and receptors.
  /// </summary>
  /// <returns>Combined set of lowercase namespace strings.</returns>
  IReadOnlySet<string> GetAllEventNamespaces();
}

/// <summary>
/// The registry registered when no consumer supplies one: it reads the process-wide static
/// <see cref="EventNamespaceRegistry"/>, which is the fallback every consumer applied by hand while
/// this dependency was optional. Making it a service makes that fallback replaceable.
/// </summary>
/// <docs>fundamentals/dispatcher/routing#event-namespace-registry</docs>
public sealed class StaticEventNamespaceRegistry : IEventNamespaceRegistry {
  /// <inheritdoc />
  public IReadOnlySet<string> GetPerspectiveEventNamespaces() => EventNamespaceRegistry.GetPerspectiveNamespaces();
  /// <inheritdoc />
  public IReadOnlySet<string> GetReceptorEventNamespaces() => EventNamespaceRegistry.GetReceptorNamespaces();
  /// <inheritdoc />
  public IReadOnlySet<string> GetAllEventNamespaces() => EventNamespaceRegistry.GetAllNamespaces();
}
