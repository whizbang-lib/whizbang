// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using TUnit.Core;
using Whizbang.Rules;

namespace Whizbang.Core.Tests.Rules;

/// <summary>
/// #2343: the rules library has its own version track, bumped only when the rules change. The golden file
/// (<c>src/Whizbang.Rules/rule-semantics.golden.txt</c>) records, per released rules version, the hash of what the rules
/// mean: both versions, the SQL eligibility rule's text, and the canonical form and hash of a reference type and rule
/// set. A change to any of them fails here until a line with a new version is appended and
/// <c>rules-version.txt</c> names it.
/// </summary>
[Category("Rules")]
public class RuleSemanticsGoldenTests {
  [Test]
  public async Task TheRulesSemantics_MatchTheGoldenLineOfTheCurrentRulesVersionAsync() {
    var version = (await _resourceAsync("rules-version.txt")).Trim();
    var golden = (await _resourceAsync("rule-semantics.golden.txt"))
      .Split('\n')
      .Select(l => l.TrimEnd('\r'))
      .Where(l => l.Length > 0 && !l.StartsWith('#'))
      .Select(l => l.Split(' ', 2))
      .ToList();

    await Assert.That(golden.Count).IsGreaterThan(0);
    await Assert.That(golden.Select(g => g[0]).Distinct().Count()).IsEqualTo(golden.Count)
      .Because("each rules version records one meaning; a version is never reused for another");
    await Assert.That(golden.Select(g => g[1]).Distinct().Count()).IsEqualTo(golden.Count)
      .Because("a meaning recorded twice is a version bump without a rule change");
    await Assert.That(golden[^1][0]).IsEqualTo(version)
      .Because("rules-version.txt names the newest golden line");
    await Assert.That(golden[^1][1]).IsEqualTo(Semantics())
      .Because("the rules changed: append a line with a new version and this hash, and set rules-version.txt to it");
  }

  /// <summary>The hash of what the rules mean today.</summary>
  internal static string Semantics() {
    var reference = new TypeRules("Contracts.Reference") {
      Ephemeral = "WhenConsumed;PersistedRow",
      Superseding = true,
      SupersedingGroup = "group",
      SupersedingKey = "Key",
      Terminal = true,
      EndOfLife = "Archive",
      DigestExempt = true,
      Retired = true,
    };
    var set = new RuleSet(RuleSemantics.VERSION, RuleSemantics.ELIGIBILITY_SQL_VERSION,
      new Dictionary<string, string>(StringComparer.Ordinal) { [reference.TypeName] = reference.Hash() });
    var text = string.Join('\n',
      $"semantics={RuleSemantics.VERSION}",
      $"sql={RuleSemantics.ELIGIBILITY_SQL_VERSION}",
      $"digest_eligible={EligibilitySql.DIGEST_ELIGIBLE}",
      $"integrity_counted={EligibilitySql.INTEGRITY_COUNTED}",
      $"exempt_types={EligibilitySql.DIGEST_EXEMPT_TYPES}",
      $"canonical={reference.Canonical()}",
      $"type_hash={reference.Hash()}",
      $"fingerprint={set.Fingerprint()}");
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
  }

  /// <summary>One of the rules' files, embedded from <c>src/Whizbang.Rules</c> (the test project's csproj).</summary>
  private static async Task<string> _resourceAsync(string name) {
    await using var stream = typeof(RuleSemanticsGoldenTests).Assembly.GetManifestResourceStream($"WhizbangRules/{name}")!;
    using var reader = new StreamReader(stream);
    return await reader.ReadToEndAsync();
  }
}
