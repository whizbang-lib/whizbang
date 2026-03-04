namespace Whizbang.Core.Routing;

/// <summary>
/// Source of event namespaces discovered at compile-time (AOT-compatible).
/// Implemented by source-generated classes in each user assembly.
/// </summary>
/// <remarks>
/// <para>
/// This interface is implemented by source-generated <c>EventNamespaceSource</c> classes
/// that are registered with <see cref="EventNamespaceRegistry"/> via [ModuleInitializer].
/// </para>
/// <para>
/// The source generator discovers:
/// - Event types from IPerspectiveFor&lt;TModel, TEvent1, ...&gt; implementations
/// - Event types from IReceptor&lt;TEvent&gt; implementations (where TEvent : IEvent)
/// </para>
/// </remarks>
/// <docs>core-concepts/routing#event-namespace-source</docs>
public interface IEventNamespaceSource {
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
