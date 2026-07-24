namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Registry of event types that need to be tracked for perspective sync.
/// </summary>
/// <remarks>
/// <para>
/// This registry is populated by source generators based on <c>[AwaitPerspectiveSync]</c>
/// attributes. When events of tracked types are emitted, they are recorded in the
/// <see cref="ISyncEventTracker"/> for cross-scope synchronization.
/// </para>
/// <para>
/// <b>Example generated code:</b>
/// <code>
/// services.AddSingleton&lt;ITrackedEventTypeRegistry&gt;(new TrackedEventTypeRegistry(
///     new Dictionary&lt;Type, string&gt; {
///         { typeof(StartedEvent), "MyApp.Perspectives.ActivityProjection" },
///         { typeof(CompletedEvent), "MyApp.Perspectives.ActivityProjection" }
///     }
/// ));
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#type-registry</docs>
public interface ITrackedEventTypeRegistry {
  /// <summary>
  /// Check if the given event type should be tracked for perspective sync.
  /// </summary>
  /// <param name="eventType">The event type to check.</param>
  /// <returns>True if the event type is registered for tracking.</returns>
  bool ShouldTrack(Type eventType);

  /// <summary>
  /// Get the perspective name for tracking this event type.
  /// </summary>
  /// <param name="eventType">The event type to look up.</param>
  /// <returns>The perspective name, or null if the type should not be tracked.</returns>
  string? GetPerspectiveName(Type eventType);

  /// <summary>
  /// Get all perspective names that track the given event type.
  /// </summary>
  /// <remarks>
  /// An event type may be tracked by multiple perspectives.
  /// </remarks>
  /// <param name="eventType">The event type to look up.</param>
  /// <returns>All perspective names tracking this event type.</returns>
  IReadOnlyList<string> GetPerspectiveNames(Type eventType);
}
