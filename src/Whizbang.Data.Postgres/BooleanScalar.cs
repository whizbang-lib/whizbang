// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Reads the result of a query whose SQL returns a boolean by construction: an <c>EXISTS</c>, an advisory lock function,
/// a comparison, or a function declared <c>RETURNS BOOLEAN</c> whose every path returns one.
/// </summary>
/// <remarks>
/// <c>value is true</c> would read anything else as <see langword="false"/>, a silent answer to a question the query never
/// asked. Anything other than a boolean here means the SQL no longer does what its call site says, so it is an error.
/// </remarks>
internal static class BooleanScalar {
  /// <summary>Returns <paramref name="value"/> as a boolean.</summary>
  /// <param name="value">What <c>ExecuteScalarAsync</c> returned.</param>
  /// <param name="query">What the query asks, for the error.</param>
  /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
  /// <tests>tests/Whizbang.Core.Tests/Data/BooleanScalarTests.cs</tests>
  public static bool Read(object? value, string query) =>
    value is bool answer
      ? answer
      : throw new InvalidOperationException(
        $"The {query} query returned '{value}' where its SQL always returns a boolean.");
}
