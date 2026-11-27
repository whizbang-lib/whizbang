// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Generators;

/// <summary>
/// The generator's view of <c>Whizbang.Core.UuidVersions</c>, which it cannot reference: a mask whose bit <c>n</c>
/// is UUID version <c>n</c>, for versions 1 to 8 (#2516).
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/WhizbangIdVersionsTests.cs</tests>
internal static class UuidVersionMask {
  /// <summary>UUIDv7 only: the default.</summary>
  public const int V7 = 1 << 7;

  /// <summary>Every version a flag exists for, 1 to 8.</summary>
  public const int ALL = 0x1FE;

  /// <summary>The versions in a mask, ascending.</summary>
  public static IEnumerable<int> VersionsIn(int mask) => Enumerable.Range(1, 8).Where(v => (mask & (1 << v)) != 0);

  /// <summary>The mask as prose for messages and doc comments: <c>UUIDv5 or UUIDv7</c>.</summary>
  public static string Describe(int mask) => string.Join(" or ", VersionsIn(mask).Select(v => $"UUIDv{v}"));

  /// <summary>The mask as a C# constant pattern over <c>Guid.Version</c>: <c>(5 or 7)</c>.</summary>
  public static string Pattern(int mask) => $"({string.Join(" or ", VersionsIn(mask))})";
}
