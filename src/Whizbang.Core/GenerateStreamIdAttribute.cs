// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// The policy follows the stream id. A derived type with no [StreamId] of its own inherits its base type's
/// [StreamId] and its [GenerateStreamId]. A derived type that declares its own [StreamId] (plain or
/// <c>Override = true</c>) does not inherit the base's [GenerateStreamId]; declare [GenerateStreamId] on it to
/// auto-generate its own stream id.
/// </para>
/// <para>
/// Events with [StreamId] but WITHOUT [GenerateStreamId] must have a StreamId provided
/// before dispatch — the StreamIdGuard will throw if it's Guid.Empty.
/// </para>
/// </remarks>
/// <docs>extending/attributes/generatestreamid</docs>
/// <tests>tests/Whizbang.Generators.Tests/GenerateStreamIdGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherStreamIdGenerationTests.cs:SendAsync_CommandWithGenerateStreamId_AutoGeneratesStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherStreamIdGenerationTests.cs:PublishAsync_EventWithGenerateStreamId_AutoGeneratesStreamIdAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherStreamIdGenerationTests.cs:SendAsync_CommandWithoutGenerateStreamId_DoesNotAutoGenerateAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdDerivedFirstGeneratorTests.cs:ADerivedTypeWithItsOwnStreamIdAndNoGenerateStreamId_DoesNotInheritTheBasesGenerateStreamIdAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/StreamIdDerivedFirstGeneratorTests.cs:ADerivedTypeWithNoStreamIdOfItsOwn_InheritsTheBasesGenerateStreamIdAsync</tests>
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
