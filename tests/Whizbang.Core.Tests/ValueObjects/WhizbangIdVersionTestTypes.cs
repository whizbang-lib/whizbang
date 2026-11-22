// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Ids for the version-acceptance tests. Kept in their own file so the generator has produced them
// before the tests reference them.

/// <summary>An id that takes any non-empty Guid version (name-derived, externally issued, and so on).</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct AnyVersionTestId;

/// <summary>An id with the default rule: UUIDv7 only.</summary>
[WhizbangId]
public readonly partial struct StrictVersionTestId;

/// <summary>An id minted as UUIDv7 or derived by name as UUIDv5, and nothing else (#2516).</summary>
[WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
public readonly partial struct MintedOrNamedTestId;

/// <summary>A UUIDv7 id with two values seeded before the rule, frozen in place (#2517).</summary>
[WhizbangId]
public readonly partial struct GrandfatheredTestId {
  /// <summary>A v4 seeded before ids were UUIDv7.</summary>
  public static readonly Guid SeededV4 = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

  /// <summary>A v8 seeded before ids were UUIDv7.</summary>
  public static readonly Guid SeededV8 = new("a1b2c3d4-e5f6-8789-8abc-def012345678");

  private static readonly System.Collections.Frozen.FrozenSet<Guid> _grandfathered =
    System.Collections.Frozen.FrozenSet.ToFrozenSet([SeededV4, SeededV8, Guid.Empty]);

  // Names Guid.Empty too, to prove the generated code refuses it whatever the hook says. The hook's name is the
  // generator's (a consumer implements it), so this repository's private-member prefix does not apply.
#pragma warning disable IDE1006
  static partial void IsGrandfathered(Guid value, ref bool grandfathered) =>
    grandfathered = _grandfathered.Contains(value);
#pragma warning restore IDE1006
}

/// <summary>An allow-list id (v7 or v5) with one grandfathered v4 (#2516, #2517).</summary>
[WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
public readonly partial struct GrandfatheredAllowListTestId {
  /// <summary>A v4 seeded before the rule.</summary>
  public static readonly Guid SeededV4 = new("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

#pragma warning disable IDE1006 // the generator's hook name, implemented as a consumer would
  static partial void IsGrandfathered(Guid value, ref bool grandfathered) => grandfathered = value == SeededV4;
#pragma warning restore IDE1006
}
