// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Attributes;
using Whizbang.Core.MultiStream;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Commands.System;

/// <summary>
/// A rebuild of one perspective's named streams, worked as a multi-stream command: each stream is rebuilt by the instance
/// that owns it, serialized with that stream's live work there (#1357), and the rebuild's size is not bounded by any one
/// handler call (#1353). Recorded by the instance that received the <see cref="RebuildPerspectiveCommand"/>; never sent
/// over the transport.
/// </summary>
/// <param name="RunId">The run, derived from the rebuild's request id when it has one, so a redelivered rebuild command is one run.</param>
/// <param name="StreamIds">The streams to rebuild.</param>
/// <param name="PerspectiveName">The perspective to rebuild them for.</param>
/// <param name="Origin">Who asked, and under which request id; stamped on every stream's rebuild events.</param>
/// <docs>fundamentals/perspectives/perspectives#rebuild</docs>
/// <tests>tests/Whizbang.Core.Tests/MultiStream/RebuildPerspectiveStreamsHandlerTests.cs:HandleStreamAsync_RebuildsThatOneStreamUnderTheRebuildsOriginAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/MultiStream/RebuildCommandRoutingTests.cs:NamedStreams_WithAStore_AreRecordedAsARunAndRebuiltNothingHereAsync</tests>
[PinnedId("57e609a6-3b62-46c6-bb1b-1d7464a6d224")]
public sealed record RebuildPerspectiveStreamsCommand(
    Guid RunId,
    IReadOnlyList<Guid> StreamIds,
    string PerspectiveName,
    RebuildOrigin Origin
) : IMultiStreamCommand, Messaging.IControlPlaneMessage;
