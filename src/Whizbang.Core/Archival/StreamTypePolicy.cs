// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// One stream type's configured <see cref="AppendToArchivedPolicy"/>, as the maintenance worker stores it for the write
/// paths (<c>wh_stream_type_policies</c>).
/// </summary>
/// <param name="StreamType">The stream type as the lifecycle ledger records it: the type name of the stream's first
/// event, without its assembly (for example <c>MyApp.Orders.OrderPlaced</c>).</param>
/// <param name="AppendToArchived">What an append to an archived stream of that type does.</param>
/// <docs>fundamentals/events/archive-store#the-policy-per-stream-type</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/StreamLifecycleOptionsBinderTests.cs</tests>
public sealed record StreamTypePolicy(string StreamType, AppendToArchivedPolicy AppendToArchived);
