// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;

namespace Whizbang.Core.Archival;

/// <summary>
/// The occurrence a recurring prefix-truncate schedule (the temporal engine) fires. The built-in
/// <c>ScheduledPrefixTruncateReceptor</c> reacts to it by calling
/// <see cref="Whizbang.Core.Lifecycle.IStreamPrefixTruncator.TruncateAsync(Guid, long, Whizbang.Core.Lifecycle.Disposition, CancellationToken)"/>
/// with these values. The cut (<see cref="ThroughVersion"/>) is the domain's: set when the schedule is created and
/// advanced each period.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="ICommand"/>, not an event: the target stream is a payload value, not the message's own stream. It rides
/// the temporal engine's occurrences (stored and routed with the schedule's own stream id) and is handled by a built-in
/// receptor, like <c>RebuildPerspectiveCommand</c>.
/// </para>
/// <para>
/// Named <c>ScheduledStreamClose</c> until #1760, which left "close" to closing the books. A schedule stores its
/// occurrence's type name, so the former name is recorded in the pinned-type ledger and a schedule stored under it still
/// loads as this type.
/// </para>
/// </remarks>
/// <param name="StreamId">The stream whose prefix is truncated.</param>
/// <param name="ThroughVersion">The last version truncated.</param>
/// <param name="Archive">Whether the prefix is archived (<see cref="Whizbang.Core.Lifecycle.Disposition.Archive"/>) rather than discarded.</param>
/// <docs>fundamentals/events/ephemeral-events#scheduling-a-prefix-truncate</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ScheduledPrefixTruncateRenameTests.cs</tests>
[PinnedId("b6f3c2a1-7d84-4e59-9c0b-2a1f8e6d3b70")]
public sealed record ScheduledPrefixTruncate(Guid StreamId, long ThroughVersion, bool Archive) : ICommand;
