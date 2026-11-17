// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace Whizbang.Rules;

/// <summary>The hash the rule fingerprint uses (#2362): SHA-256 of the UTF-8 text, as 16 lowercase hex digits.</summary>
/// <docs>resilience/stream-integrity#rule-fingerprint</docs>
/// <tests>tests/Whizbang.Core.Tests/Rules/TypeRulesTests.cs:Hash_IsSixteenLowercaseHexDigits_AndEqualRulesHashEqualAsync</tests>
internal static class RuleHashing {
  /// <summary>The first 8 bytes of the SHA-256 of <paramref name="text"/>, as lowercase hex.</summary>
  internal static string Short(string text) =>
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
}
