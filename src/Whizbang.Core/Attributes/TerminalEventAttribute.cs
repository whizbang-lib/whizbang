// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares an event type <em>terminal</em>: storing one ends its stream. The stream is sealed in the same transaction
/// that stores the event, and every later append to the stream is refused (a direct append throws
/// <see cref="Messaging.StreamSealedException"/>; an outbox or inbox write dead-letters the refused event).
/// </summary>
/// <remarks>
/// <para>
/// The attribute is read at compile time: the message-type catalog generator registers each terminal type with
/// <see cref="Lifecycle.TerminalEvents"/> and stamps it on the catalog entry, so the dispatch path, the receive path and
/// a direct append all stamp <see cref="Messaging.EventFlags.Terminal"/> without reflection. It is inherited: an event
/// derived from a terminal event is terminal too.
/// </para>
/// <para>
/// A terminal event ends a Sourced stream. On an ephemeral event type it has no effect, because an ephemeral stream has
/// no lifecycle row to seal: it ends by its time to live.
/// </para>
/// <para>
/// To seal a stream without appending an event, call <see cref="Lifecycle.IStreamFinalizer.FinalizeAsync"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [TerminalEvent]
/// public sealed record AccountClosed([property: StreamId] Guid AccountId, string Reason) : IEvent;
/// </code>
/// </example>
/// <docs>fundamentals/events/stream-end-of-life#terminal-events</docs>
/// <tests>tests/Whizbang.Generators.Tests/MessageTypeCatalogGeneratorTests.cs:Generator_TerminalEvent_StampsTheEntryAndRegistersTheTypeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/TerminalEventsTests.cs:IsTerminal_AnAttributedTypeInThisAssembly_IsRegisteredByTheGeneratorAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class TerminalEventAttribute : Attribute;
