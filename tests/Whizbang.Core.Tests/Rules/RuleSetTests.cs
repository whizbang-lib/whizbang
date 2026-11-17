// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Core.Rules;

namespace Whizbang.Core.Tests.Rules;

/// <summary>
/// #2362: the pair comparison. Only the types both services name are compared (owner decision Q3), both versions must
/// match, and a peer that sent no rules is a mismatch, so a rolling deploy halts a mixed pair.
/// </summary>
[Category("Rules")]
public class RuleSetTests {
  private const string SHARED = "Contracts.OrderPlaced";
  private const string OTHER = "Contracts.Unrelated";

  private static RuleSet _set(params (string Type, string Hash)[] types) => _set(1, 1, types);

  private static RuleSet _set(int semantics, int sql, params (string Type, string Hash)[] types) =>
    new(semantics, sql, types.ToDictionary(t => t.Type, t => t.Hash, StringComparer.Ordinal));

  [Test]
  public async Task CompareWith_TheSameRules_AgreesAsync() {
    var verdict = _set((SHARED, "a")).CompareWith(_set((SHARED, "a")));

    await Assert.That(verdict.IsAgreed).IsTrue();
    await Assert.That(verdict).IsEqualTo(RulePairVerdict.Agrees);
  }

  [Test]
  public async Task CompareWith_NoPeerRules_IsHaltedForNoFingerprintAsync() {
    var verdict = _set((SHARED, "a")).CompareWith(null);

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.NoFingerprint);
    await Assert.That(verdict.IsAgreed).IsFalse();
  }

  [Test]
  public async Task CompareWith_ADifferentSemanticsVersion_IsHaltedAsync() {
    var verdict = _set(1, 1, (SHARED, "a")).CompareWith(_set(2, 1, (SHARED, "a")));

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.SemanticsVersion);
  }

  [Test]
  public async Task CompareWith_ADifferentEligibilitySqlVersion_IsHaltedAsync() {
    var verdict = _set(1, 1, (SHARED, "a")).CompareWith(_set(1, 2, (SHARED, "a")));

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.EligibilitySqlVersion);
  }

  [Test]
  public async Task CompareWith_ASharedTypeWithDifferentRules_IsHalted_NamingOnlyThatTypeAsync() {
    var verdict = _set((SHARED, "a"), ("Contracts.B", "b"), ("Contracts.A", "x"))
      .CompareWith(_set((SHARED, "changed"), ("Contracts.B", "b"), ("Contracts.A", "y")));

    await Assert.That(verdict.Reason).IsEqualTo(RulePairHaltReason.TypeRules);
    await Assert.That(verdict.DifferingTypes).IsEquivalentTo(["Contracts.A", SHARED]);
    await Assert.That(verdict.DifferingTypes[0]).IsEqualTo("Contracts.A").Because("named in ordinal order");
  }

  /// <summary>Q3: a type only one side names is not shared, so whatever its rules, the pair agrees.</summary>
  [Test]
  public async Task CompareWith_ADifferenceOnlyInATypeNotShared_AgreesAsync() {
    var verdict = _set((SHARED, "a"), (OTHER, "mine")).CompareWith(_set((SHARED, "a"), ("Contracts.TheirsOnly", "theirs")));

    await Assert.That(verdict.IsAgreed).IsTrue();
  }

  [Test]
  public async Task Fingerprint_IgnoresBuildOrder_AndChangesWithAnyPartAsync() {
    var a = _set((SHARED, "a"), (OTHER, "b"));
    var reordered = _set((OTHER, "b"), (SHARED, "a"));

    await Assert.That(reordered.Fingerprint()).IsEqualTo(a.Fingerprint());
    await Assert.That(a.Fingerprint().Length).IsEqualTo(16);
    string[] others = [
      _set(2, 1, (SHARED, "a"), (OTHER, "b")).Fingerprint(),
      _set(1, 2, (SHARED, "a"), (OTHER, "b")).Fingerprint(),
      _set((SHARED, "a"), (OTHER, "c")).Fingerprint(),
      _set((SHARED, "a")).Fingerprint(),
    ];
    await Assert.That(others.Append(a.Fingerprint()).Distinct().Count()).IsEqualTo(others.Length + 1);
  }
}
