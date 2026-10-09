// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// Matches the <c>*</c> and <c>?</c> wildcards of a configured pin (<c>wh_per_*:idx_*_legacy</c>) against
/// <c>table:object</c>, ordinal and case-sensitive like PostgreSQL identifiers.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Schema/GlobTests.cs</tests>
internal static class Glob {
  /// <summary>Whether <paramref name="text"/> matches <paramref name="pattern"/>.</summary>
  public static bool IsMatch(string pattern, string text) {
    ArgumentNullException.ThrowIfNull(pattern);
    ArgumentNullException.ThrowIfNull(text);
    int p = 0, t = 0, star = -1, mark = 0;
    while (t < text.Length) {
      if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == text[t])) {
        p++;
        t++;
      } else if (p < pattern.Length && pattern[p] == '*') {
        star = p++;
        mark = t;
      } else if (star >= 0) {
        p = star + 1;
        t = ++mark;
      } else {
        return false;
      }
    }
    while (p < pattern.Length && pattern[p] == '*') {
      p++;
    }
    return p == pattern.Length;
  }
}
