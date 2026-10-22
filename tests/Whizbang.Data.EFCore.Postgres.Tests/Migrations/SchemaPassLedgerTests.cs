// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Which step rows a pass beginning now marks pending, and which steps it runs: a pass of this build part way
/// through is continued, anything else starts afresh and keeps what a crashed pass owed.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaPassLedger.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
[Category("Unit")]
[Category("Shard1")]
public class SchemaPassLedgerTests {
  private const string THIS_BUILD = "this-build";
  private const string OTHER_BUILD = "other-build";

  private static Dictionary<string, (string Fingerprint, short Status)> _rows(
      params (string Step, string Fingerprint, short Status)[] rows) =>
    rows.ToDictionary(r => r.Step, r => (r.Fingerprint, r.Status), StringComparer.Ordinal);

  [Test]
  public async Task AFreshLedger_MarksAndRunsWhatThePassNeedsAsync() {
    var plan = SchemaPassLedger.Plan(_rows(), ["core-infrastructure", "constraints"], THIS_BUILD);

    await Assert.That(plan.ToMark).IsEquivalentTo(["core-infrastructure", "constraints"]);
    await Assert.That(plan.ToRun).IsEquivalentTo(["core-infrastructure", "constraints"]);
  }

  /// <summary>
  /// The case that makes a retry resume rather than restart: the core tables committed in the failed attempt, so
  /// marking them pending again would apply them a second time.
  /// </summary>
  [Test]
  public async Task APassOfThisBuildPartWayThrough_IsContinued_AndNothingIsMarkedAgainAsync() {
    var rows = _rows(
      ("core-infrastructure", THIS_BUILD, SchemaPassLedger.APPLIED),
      ("constraints", THIS_BUILD, SchemaPassLedger.PENDING),
      ("registries", THIS_BUILD, SchemaPassLedger.FAILED));

    var plan = SchemaPassLedger.Plan(rows, ["core-infrastructure", "constraints", "registries"], THIS_BUILD);

    await Assert.That(plan.ToMark).IsEmpty();
    await Assert.That(plan.ToRun).IsEquivalentTo(["constraints", "registries"]);
  }

  /// <summary>A finished pass of this build does not stop the next one from running what it needs.</summary>
  [Test]
  public async Task AFinishedPassOfThisBuild_IsNotContinued_SoTheNeededStepsRunAgainAsync() {
    var rows = _rows(
      ("core-infrastructure", THIS_BUILD, SchemaPassLedger.APPLIED),
      ("constraints", THIS_BUILD, SchemaPassLedger.APPLIED));

    var plan = SchemaPassLedger.Plan(rows, ["core-infrastructure"], THIS_BUILD);

    await Assert.That(plan.ToMark).IsEquivalentTo(["core-infrastructure"]);
    await Assert.That(plan.ToRun).IsEquivalentTo(["core-infrastructure"]);
  }

  /// <summary>
  /// A pass a different build left behind is not continued, because the steps it applied applied that build's SQL;
  /// what it still owed is kept, so a crash never loses a step.
  /// </summary>
  [Test]
  public async Task AnotherBuildsUnfinishedPass_StartsAfresh_AndKeepsWhatItOwedAsync() {
    var rows = _rows(
      ("core-infrastructure", OTHER_BUILD, SchemaPassLedger.APPLIED),
      ("registries", OTHER_BUILD, SchemaPassLedger.PENDING));

    var plan = SchemaPassLedger.Plan(rows, ["core-infrastructure"], THIS_BUILD);

    await Assert.That(plan.ToMark).IsEquivalentTo(["core-infrastructure", "registries"]);
    await Assert.That(plan.ToRun).IsEquivalentTo(["core-infrastructure", "registries"]);
  }

  [Test]
  public async Task PendingAndFailedAreOutstanding_AppliedIsNotAsync() {
    await Assert.That(SchemaPassLedger.IsOutstanding(SchemaPassLedger.PENDING)).IsTrue();
    await Assert.That(SchemaPassLedger.IsOutstanding(SchemaPassLedger.FAILED)).IsTrue();
    await Assert.That(SchemaPassLedger.IsOutstanding(SchemaPassLedger.APPLIED)).IsFalse();
  }

  [Test]
  public async Task AStepKeyNamesTheContextAndTheStepAsync() {
    await Assert.That(SchemaPassLedger.StepKey("OrdersDbContext", "constraints")).IsEqualTo("step:OrdersDbContext:constraints");
  }

  [Test]
  public async Task TheFingerprintIsStable_AndTellsListsThatConcatenateAlikeApartAsync() {
    var once = SchemaPassLedger.Fingerprint(["1.0.0", "ab", "c"]);

    await Assert.That(SchemaPassLedger.Fingerprint(["1.0.0", "ab", "c"])).IsEqualTo(once);
    await Assert.That(SchemaPassLedger.Fingerprint(["1.0.0", "a", "bc"])).IsNotEqualTo(once);
    await Assert.That(once).Matches("^[0-9a-f]{64}$");
  }

  [Test]
  public async Task ThePendingHashIsNoFilesHashAsync() {
    await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(SchemaPassLedger.PENDING_HASH, "^[0-9a-f]{64}$")).IsFalse()
      .Because("a file marked to run again must match no file, so every reader of the ledger sees it as changed");
  }

  [Test]
  public async Task NullArgumentsAreRefusedAsync() {
    await Assert.That(() => SchemaPassLedger.Fingerprint(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => SchemaPassLedger.Plan(null!, [], THIS_BUILD)).Throws<ArgumentNullException>();
    await Assert.That(() => SchemaPassLedger.Plan(_rows(), null!, THIS_BUILD)).Throws<ArgumentNullException>();
  }
}
