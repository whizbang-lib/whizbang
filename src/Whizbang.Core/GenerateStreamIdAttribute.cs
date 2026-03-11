namespace Whizbang.Core;

/// <summary>
/// Marks an event type or property for automatic StreamId generation at dispatch time.
/// Applied alongside <see cref="StreamIdAttribute"/> to opt-in to auto-generation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Usage patterns:</strong>
/// </para>
/// <para>
/// Stream-initiating event (ALWAYS gets new StreamId):
/// <code>
/// public record OrderCreatedEvent : IEvent {
///   [StreamId] [GenerateStreamId]
///   public Guid OrderId { get; set; }
/// }
/// </code>
/// </para>
/// <para>
/// Flexible event (inherits parent StreamId in cascades, generates if standalone):
/// <code>
/// public record InventoryReserved : IEvent {
///   [StreamId] [GenerateStreamId(OnlyIfEmpty = true)]
///   public Guid ReservationId { get; set; }
/// }
/// </code>
/// </para>
/// <para>
/// Class-level (for inherited [StreamId] from a base class):
/// <code>
/// [GenerateStreamId]
/// public record OrderCreatedEvent : BaseEvent {
///   // [StreamId] inherited from BaseEvent.StreamId
/// }
/// </code>
/// </para>
/// <para>
/// Events with [StreamId] but WITHOUT [GenerateStreamId] must have a StreamId provided
/// before dispatch — the StreamIdGuard will throw if it's Guid.Empty.
/// </para>
/// </remarks>
/// <docs>attributes/generatestreamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/GenerateStreamIdGeneratorTests.cs</tests>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class GenerateStreamIdAttribute : Attribute {
  /// <summary>
  /// When false (default), always generates a new StreamId regardless of existing value.
  /// When true, only generates a StreamId if the current value is Guid.Empty.
  /// Use OnlyIfEmpty = true for events that may receive a StreamId from a parent cascade
  /// but should generate their own if dispatched independently.
  /// </summary>
  public bool OnlyIfEmpty { get; init; }
}
