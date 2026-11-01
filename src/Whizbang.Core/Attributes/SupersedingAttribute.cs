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
public sealed class SupersedingAttribute : Attribute;
