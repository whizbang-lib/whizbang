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
/// <docs>fundamentals/events/events</docs>
public interface IEventTypeProvider {
  /// <summary>
  /// Gets all known event types in the application that implement IEvent.
  /// Used for polymorphic event deserialization in GetEventsBetweenPolymorphicAsync.
  /// </summary>
  /// <returns>A read-only list of all event types.</returns>
  IReadOnlyList<Type> GetEventTypes();

  /// <summary>False only for the framework fallback registered when no generated provider is present.
  /// Consumers that would otherwise skip on a null provider consult this instead.</summary>
  bool IsAvailable => true;
}

/// <summary>
/// The provider registered when no generated provider is present. It reports itself unavailable
/// so consumers keep the "no event types known" path they took on a null provider; an empty list
/// alone would not do, because at least one consumer classifies events differently when a
/// provider exists than when it does not.
/// </summary>
/// <docs>fundamentals/events/events</docs>
public sealed class NullEventTypeProvider : IEventTypeProvider, INullDefault {
  /// <summary>The shared instance; the type carries no state.</summary>
  public static NullEventTypeProvider Instance { get; } = new();
  private NullEventTypeProvider() { }
  /// <inheritdoc />
  public bool IsAvailable => false;
  /// <inheritdoc />
  public IReadOnlyList<Type> GetEventTypes() => [];
}
