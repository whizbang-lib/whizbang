// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures.Consumer;

// Consumer ids that share a name with an id Whizbang.Core declares for itself (Whizbang.Core.ValueObjects.StreamId
// and EventId). Both take any Guid version, unlike the framework's, so a test can tell whose scalar a schema uses.

/// <summary>A consumer id named like the framework's <c>StreamId</c>.</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct StreamId;

/// <summary>A consumer id named like the framework's <c>EventId</c>.</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct EventId;
