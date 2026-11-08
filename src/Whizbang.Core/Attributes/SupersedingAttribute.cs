// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares an event type <em>superseding</em> (latest wins): each event holds the whole state for its key, so once a
/// newer event of the same type and key is stored on the same stream, the older one is redundant. Maintenance deletes an
/// older event, its body and its digest contribution once every consumer has processed it.
/// </summary>
/// <remarks>
/// <para>
/// The key is the property marked <see cref="SupersedingKeyAttribute"/>; a type with no such property has one key per
/// stream, so only the newest event of the type on each stream is kept. An event whose key property is
/// <see langword="null"/> is never superseded and never supersedes another.
/// </para>
/// <para>
/// Use it only for full-state payloads: every event must carry everything a consumer needs for its key, because the
/// events it replaced are gone. A rebuild or a rewind replays the newest event of each key and never an older one, so
/// state as of a point before the newest event of a key cannot be reconstructed for this type.
/// </para>
/// <para>
/// The attribute is read at compile time: the message-type catalog generator registers each superseding type with
/// <see cref="Lifecycle.SupersedingEvents"/>, so every path that stores the event stamps
/// <see cref="Messaging.EventFlags.Superseding"/> and the key without reflection. It is inherited. An ephemeral event
/// cannot be superseding (WHIZ135): it is reaped once consumed already.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Superseding]
/// public sealed record DocumentEmbeddingUpdated(
///     [property: StreamId] Guid DocumentId,
///     [property: SupersedingKey] string Model,
///     float[] Vector) : IEvent;
/// </code>
/// </example>
/// <docs>fundamentals/events/superseding-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_SupersedingEvent_StampsTheEntryAndRegistersTheKeyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/SupersedingEventsTests.cs:AttributedTypesInThisAssembly_AreRegisteredByTheGenerator_WithTheirKeyAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class SupersedingAttribute : Attribute {
  /// <summary>
  /// The superseding group the type belongs to (#2124), or <see langword="null"/> (the default) for none: the type is its
  /// own group. The types of one group supersede each other, so the newest event of any of them replaces the older
  /// events of every one of them, per stream, group and key.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The name is stored with every event of the type, so it is global and never derived from the type's name: choose
  /// a stable name, and keep it when a type is renamed. An empty name is no group.
  /// </para>
  /// <para>
  /// The types of a group must agree on their key: all without a <see cref="SupersedingKeyAttribute"/>, or all keyed by
  /// the same type (WHIZ138). Every event of every type in the group must carry the whole state for its key, since the
  /// newest event of any of them is the only one kept; a consumer that handles several of the types must need only the
  /// newest.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// [Superseding(Group = "document-embedding")]
  /// public sealed record DocumentEmbeddingCreated([property: StreamId] Guid DocumentId, float[] Vector) : IEvent;
  ///
  /// [Superseding(Group = "document-embedding")]
  /// public sealed record DocumentEmbeddingUpdated([property: StreamId] Guid DocumentId, float[] Vector) : IEvent;
  /// </code>
  /// </example>
  /// <docs>fundamentals/events/superseding-events#superseding-groups</docs>
  /// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_ASupersedingGroup_IsRegisteredWithEachTypeAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SupersedingGroupSqlTests.cs:Emit_ANewerEventOfAnotherTypeInTheGroup_QueuesTheOlder_InEitherOrderAsync</tests>
  public string? Group { get; set; }
}
