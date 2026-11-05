// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Tests.Fixtures;

// Ids for the GraphQL id tests. Kept in their own file so the generator has produced them
// before the tests reference them.

/// <summary>An id with the default rule: UUIDv7 only.</summary>
[WhizbangId]
public readonly partial struct GraphQLOrderId;

/// <summary>An id that takes any non-empty Guid version, such as one issued by an external identity provider.</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct GraphQLExternalUserId;
