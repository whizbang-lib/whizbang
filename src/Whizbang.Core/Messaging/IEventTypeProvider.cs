namespace Whizbang.Core.Messaging;

/// <summary>
/// Provides a list of all known event types in the application.
/// Required for AOT-compatible polymorphic event deserialization in perspectives and lifecycle stages.
/// </summary>
/// <remarks>
/// <para>
/// This interface exists because source-generated JSON serialization cannot deserialize interface types like IEvent directly.
/// Instead, we must deserialize to concrete types using the EventType column in the event store, then cast to IEvent.
/// </para>
/// <para>
/// Implementations should return all event types that implement IEvent in the application.
/// This is typically implemented in the host application (BFF, worker) that knows all contract types.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class ECommerceEventTypeProvider : IEventTypeProvider {
///   private static readonly IReadOnlyList&lt;Type&gt; EventTypes = new[] {
///     typeof(ProductCreatedEvent),
///     typeof(ProductUpdatedEvent),
///     typeof(OrderCreatedEvent),
///     // ... all other event types
///   };
///
///   public IReadOnlyList&lt;Type&gt; GetEventTypes() => EventTypes;
/// }
/// </code>
/// </example>
public interface IEventTypeProvider {
  /// <summary>
  /// Gets all known event types in the application that implement IEvent.
  /// Used for polymorphic event deserialization in GetEventsBetweenPolymorphicAsync.
  /// </summary>
  /// <returns>A read-only list of all event types.</returns>
  IReadOnlyList<Type> GetEventTypes();
}
