using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests;

/// <summary>
/// Coverage for two guard clauses in TypeMatcher that the sibling test file never exercised.
/// </summary>
/// <remarks>
/// <para>
/// Two OTHER lines in this file's original target set -- the null/empty guards at the top of the
/// private helpers <c>_stripVersionInfo</c> and <c>_stripAssembly</c> -- are intentionally NOT
/// covered here because they are unreachable from the public API.
/// <c>Matches(string, string, MatchStrictness)</c> returns before applying any flag unless BOTH
/// input strings are already non-null and non-empty, so <c>_stripVersionInfo</c>'s only call site
/// always hands it a non-empty string. Its own output is provably never empty either: the
/// multi-part branch always reconstructs <c>"{parts[0]}, {parts[1]}"</c>, which contains a literal
/// ", " and so can never be <c>string.IsNullOrEmpty</c>; the single/two-part branch returns the
/// (already non-empty) input unchanged. So <c>_stripAssembly</c>'s two call sites -- directly from
/// <c>Matches</c>, and again inside <c>_getSimpleName</c> which is itself guarded by an identical
/// empty check before ever making that call -- also never receive an empty string. Forcing either
/// guard via reflection into a private method would test dead code, not behavior, so it is left
/// uncovered and reported here instead of faked.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/TypeMatcher.cs</code-under-test>
public class TypeMatcherCoverageTests {
  [Test]
  public async Task Matches_RegexPattern_EmptyTypeString_ReturnsFalseAsync() {
    // A type matcher decides whether an incoming message maps to a handler. If this guard
    // regressed, an empty/unresolved type string could spuriously match a broad pattern (e.g.
    // ".*") and route a message with no real type identity to the wrong handler instead of being
    // rejected outright.
    var pattern = new Regex(".*");

    var result = TypeMatcher.Matches(string.Empty, pattern);

    await Assert.That(result).IsFalse()
      .Because("an empty type string must never be considered a pattern match, no matter how permissive the pattern is");
  }

  [Test]
  public async Task Matches_IgnoreAssemblyAndNamespace_BothDegenerateToEmptySimpleName_ConsideredEqualAsync() {
    // Ignoring the assembly first can reduce a malformed "Type, Assembly" string (no type name
    // before the comma) to an empty string before the simple-name step ever runs. If the simple
    // name step's own empty guard regressed and this degenerate value fell into Split/LastIndexOf
    // unguarded, two equally malformed type strings would either throw while matching instead of
    // comparing equal, or silently diverge from the assembly-and-namespace-ignoring intent the
    // caller asked for.
    const string type1 = ",AssemblyA";
    const string type2 = ",AssemblyB";
    var strictness = MatchStrictness.IgnoreAssembly | MatchStrictness.IgnoreNamespace;

    var result = TypeMatcher.Matches(type1, type2, strictness);

    await Assert.That(result).IsTrue()
      .Because("both strings reduce to the same empty simple name once their (missing) type name and assembly are ignored");
  }
}
