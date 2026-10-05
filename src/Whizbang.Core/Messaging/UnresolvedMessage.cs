// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stands in for an element of a nested polymorphic <see cref="IMessage"/> member whose type this
/// service cannot resolve: its discriminator names a type no registered context holds metadata for.
/// </summary>
/// <remarks>
/// <para>
/// A composite's inner list is the case this exists for. A consumer that handles only some of a
/// composite's inner event types has no metadata for the others, and without a stand-in one such
/// element failed the whole composite, taking the inner events the consumer does handle with it.
/// </para>
/// <para>
/// It carries nothing: a type this service cannot name is a type no consumer here can handle.
/// Composite fan-out drops it as an unsubscribed child and logs the count, and it serializes as an
/// empty object, which reads back as another placeholder. It never reaches a receptor.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/composite-events#unresolved-inner-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/CompositeUnresolvedInnerMessageTests.cs</tests>
// WHIZ110 asks for a pinned identity so a stored type survives renames. This type is never stored,
// published or registered: it exists only in memory, between reading a composite and fanning it out.
#pragma warning disable WHIZ110
internal sealed class UnresolvedMessage : IMessage;
#pragma warning restore WHIZ110
