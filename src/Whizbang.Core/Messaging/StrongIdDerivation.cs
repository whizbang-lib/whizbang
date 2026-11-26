// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the value of a strong id (a <c>[WhizbangId]</c> type) from the handling it is created in (#2604). Every
/// generated strong id's <c>DeriveFrom(handling, purpose)</c> calls it; it is not meant to be called directly.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#derived-ids</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/StrongIdDerivationTests.cs</tests>
public static class StrongIdDerivation {
  /// <summary>Derives the id the strong id pinned as <paramref name="pinnedId"/> takes for <paramref name="purpose"/> in <paramref name="handling"/>.</summary>
  public static Guid Derive(Handling handling, string pinnedId, string purpose, string idTypeName) =>
    throw new NotImplementedException();

  /// <summary>The exception a strong id with no pinned id throws from <c>DeriveFrom</c>.</summary>
  public static NotSupportedException NotPinned(string idTypeName) => throw new NotImplementedException();
}
