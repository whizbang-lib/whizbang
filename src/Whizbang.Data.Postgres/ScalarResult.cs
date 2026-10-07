// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Reads the scalar a store's SQL returns (<c>ExecuteScalar</c>, <c>SqlQueryRaw&lt;T&gt;</c>) as its
/// typed value. The functions the stores call always return one, so the "no value" answer (null,
/// DBNull, another type) is a fallback no live query takes; deciding it here, once, keeps every call
/// site a plain read and lets the fallback be tested directly.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Notifications/ScalarResultTests.cs</tests>
internal static class ScalarResult {
  /// <summary>True only for a boolean <see langword="true"/>.</summary>
  /// <param name="value">The scalar.</param>
  internal static bool IsTrue(object? value) => value is true;

  /// <summary>The <see cref="int"/> the scalar carries, else zero.</summary>
  /// <param name="value">The scalar.</param>
  internal static int IntOrZero(object? value) => value is int n ? n : 0;

  /// <summary>The <see cref="long"/> the scalar carries, else zero.</summary>
  /// <param name="value">The scalar.</param>
  internal static long LongOrZero(object? value) => value is long n ? n : 0L;
}
