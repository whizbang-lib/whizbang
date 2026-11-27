// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core;

namespace Whizbang.Transports.HotChocolate.Ids.Tests.Fixtures;

// Ids for the GraphQL id tests. Kept in their own file so the generator has produced them
// before the tests reference them.

/// <summary>An id with the default rule: UUIDv7 only.</summary>
[WhizbangId]
public readonly partial struct GraphQLOrderId;

/// <summary>An id that takes any non-empty Guid version, such as one issued by an external identity provider.</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct GraphQLExternalUserId;

/// <summary>An id minted as UUIDv7 or derived by name as UUIDv5, and nothing else (#2516).</summary>
[WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
public readonly partial struct GraphQLConnectorId;

/// <summary>A UUIDv7 id with one value seeded before the rule, which it grandfathers (#2517).</summary>
[WhizbangId]
public readonly partial struct GraphQLSeededId {
  /// <summary>A v4 seeded before ids were UUIDv7.</summary>
  public static readonly Guid SeededV4 = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

  static partial void _isGrandfathered(Guid value, ref bool grandfathered) => grandfathered = value == SeededV4;
}
