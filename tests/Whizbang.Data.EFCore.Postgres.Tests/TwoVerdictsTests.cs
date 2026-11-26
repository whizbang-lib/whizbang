// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Versioning;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2553, owner decision 2: the W! migrations and the application's schema are judged apart, each with its own verdict
/// and reason (serve, roll back, stand down, or served as it is by its own override). W! comes first: when it stands
/// down nothing runs; an application stand-down never undoes or blocks the W! migrations, which run before the instance
/// stands down. The instance serves only when both sides can.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStartupAssessment.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/AssessStartupStep.cs</code-under-test>
/// <docs>operations/startup/rolling-upgrades#two-verdicts</docs>
[Category("Shard5")]
public class TwoVerdictsTests {
  private static readonly VersionSighting _me = new("1.0.0", "1.9.0", Guid.Parse("0199eeee-0000-0000-0000-000000000001"));

  private const string W = "380_X.sql";
  private const string APP = "perspective:wh_per_orders";

  /// <summary>One side's ledger and the override that applies to it, for a state the matrix names.</summary>
  private static (AppliedSchemaChange? Change, bool Override) _side(SchemaChangeKind side, string state) {
    var change = side == SchemaChangeKind.Platform
      ? new AppliedSchemaChange(W, side, "2.0.0", null)
      : new AppliedSchemaChange(APP, side, "1.0.0", "2.0.0");
    return state switch {
      "serve" => (null, false),
      "rollback" => (change with { Reversal = SchemaChangeReversal.Reversible }, false),
      "standdown" => (change, false),
      "overridden" => (change, true),
      _ => throw new ArgumentOutOfRangeException(nameof(state), state, "serve, rollback, standdown or overridden"),
    };
  }

  private static StartupAssessment _judge(string platform, string application) {
    var (w, overrideW) = _side(SchemaChangeKind.Platform, platform);
    var (app, overrideApp) = _side(SchemaChangeKind.Application, application);
    AppliedSchemaChange[] ledger = [.. new[] { w, app }.OfType<AppliedSchemaChange>(),
      new AppliedSchemaChange("100_Old.sql", SchemaChangeKind.Platform, "0.9.0", null)];
    return PostgresStartupAssessment.ComputeVerdict(_me, ledger, [],
      new SchemaGateOverrides(overrideW, overrideApp, AllowLossyPlatform: false, AllowLossyApplication: false));
  }

  [Test]
  [Arguments("serve", "serve", StartupVerdict.Serve, StartupVerdict.Serve, StartupVerdict.Serve, false, false, "")]
  [Arguments("serve", "rollback", StartupVerdict.Migrate, StartupVerdict.Serve, StartupVerdict.Migrate, false, false, APP)]
  [Arguments("serve", "standdown", StartupVerdict.StandDown, StartupVerdict.Serve, StartupVerdict.StandDown, false, true, "")]
  [Arguments("serve", "overridden", StartupVerdict.Serve, StartupVerdict.Serve, StartupVerdict.Current, false, true, "")]
  [Arguments("rollback", "serve", StartupVerdict.Migrate, StartupVerdict.Migrate, StartupVerdict.Serve, false, false, W)]
  [Arguments("rollback", "rollback", StartupVerdict.Migrate, StartupVerdict.Migrate, StartupVerdict.Migrate, false, false, W + "," + APP)]
  [Arguments("rollback", "standdown", StartupVerdict.StandDown, StartupVerdict.Migrate, StartupVerdict.StandDown, false, true, W)]
  [Arguments("rollback", "overridden", StartupVerdict.Migrate, StartupVerdict.Migrate, StartupVerdict.Current, false, true, W)]
  [Arguments("standdown", "serve", StartupVerdict.StandDown, StartupVerdict.StandDown, StartupVerdict.Serve, true, false, "")]
  [Arguments("standdown", "rollback", StartupVerdict.StandDown, StartupVerdict.StandDown, StartupVerdict.Migrate, true, false, "")]
  [Arguments("standdown", "standdown", StartupVerdict.StandDown, StartupVerdict.StandDown, StartupVerdict.StandDown, true, true, "")]
  [Arguments("standdown", "overridden", StartupVerdict.StandDown, StartupVerdict.StandDown, StartupVerdict.Current, true, true, "")]
  [Arguments("overridden", "serve", StartupVerdict.Current, StartupVerdict.Current, StartupVerdict.Serve, false, false, "")]
  [Arguments("overridden", "rollback", StartupVerdict.StandDown, StartupVerdict.Current, StartupVerdict.StandDown, true, true, "")]
  [Arguments("overridden", "standdown", StartupVerdict.StandDown, StartupVerdict.Current, StartupVerdict.StandDown, true, true, "")]
  [Arguments("overridden", "overridden", StartupVerdict.Current, StartupVerdict.Current, StartupVerdict.Current, false, true, "")]
  public async Task EveryCombinationOfTheTwoVerdicts_IsJudgedSideBySide_WithTheWMigrationsFirstAsync(
      string platform, string application, StartupVerdict expected, StartupVerdict platformVerdict, StartupVerdict applicationVerdict,
      bool appliesNothing, bool holdsApplication, string rolledBack) {
    var verdict = _judge(platform, application);

    await Assert.That(verdict.Verdict).IsEqualTo(expected).Because(verdict.Reason);
    await Assert.That(verdict.Platform?.Verdict).IsEqualTo(platformVerdict).Because(verdict.Reason);
    await Assert.That(verdict.Application?.Verdict).IsEqualTo(applicationVerdict).Because(verdict.Reason);
    await Assert.That(verdict.StandsDownWithoutMigrating).IsEqualTo(appliesNothing)
      .Because("only a W! stand-down (or W! served as it is) stops the W! migrations; an application stand-down never does");
    await Assert.That(verdict.HoldsApplication).IsEqualTo(holdsApplication);
    await Assert.That(string.Join(",", verdict.Rollback?.Changes.Select(c => c.Name) ?? [])).IsEqualTo(rolledBack)
      .Because("a rollback reverses only the sides this start runs");
    await Assert.That(verdict.Platform!.Side).IsEqualTo(SchemaChangeKind.Platform);
    await Assert.That(verdict.Application!.Side).IsEqualTo(SchemaChangeKind.Application);
  }

  [Test]
  public async Task EachSidesStandDown_NamesItsSide_AndItsChanges_AndTheInstancesReasonCarriesBothAsync() {
    var app = _judge("rollback", "standdown");
    var w = _judge("standdown", "serve");

    await Assert.That(app.Application!.Reason).Contains($"{APP} (applied by library 1.0.0, application 2.0.0)");
    await Assert.That(app.Application.Reason).DoesNotContain(W);
    await Assert.That(app.Platform!.Reason).Contains($"rolling back {W}");
    await Assert.That(app.Reason).Contains("application schema: since this instance's version");
    await Assert.That(app.Reason).Contains("W! migrations: since this instance's version");
    await Assert.That(app.Reason).EndsWith("standing down: never apply anything, hold the data plane, report not-ready-while-alive");
    await Assert.That(w.Reason).StartsWith("W! migrations: since this instance's version");
    await Assert.That(w.Reason).Contains($"{W} (applied by library 2.0.0, application unknown)");
    await Assert.That(w.Platform!.Name).IsEqualTo("W! migrations");
    await Assert.That(w.Application!.Name).IsEqualTo("application schema");
  }

  [Test]
  public async Task AnApplicationOverride_ServesTheApplicationAsItIs_WhileTheWMigrationsStillRunAsync() {
    var verdict = _judge("serve", "overridden");
    var refused = _judge("serve", "standdown");

    await Assert.That(verdict.OverriddenReason).IsEqualTo(refused.Reason)
      .Because("the same detail the stand-down error carries, recorded and logged loudly");
    await Assert.That(verdict.Application!.OverriddenReason).IsNotNull();
    await Assert.That(verdict.Reason).Contains(UpgradeOrder.APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY);
    await Assert.That(verdict.Platform!.OverriddenReason).IsNull();
  }

  [Test]
  public async Task TheWMigrationsServedAsTheyAre_CannotRunTheApplicationsRollback_AndSayWhichOverrideWouldAsync() {
    var verdict = _judge("overridden", "rollback");

    await Assert.That(verdict.Verdict).IsEqualTo(StartupVerdict.StandDown);
    await Assert.That(verdict.Application!.Reason).Contains(UpgradeOrder.APPLICATION_OVERRIDE_INCOMPATIBILITY_KEY);
    await Assert.That(verdict.Rollback).IsNull().Because("nothing is applied while the W! migrations are served as they are");
  }

  [Test]
  public async Task ARollback_ReversesOnlyTheSidesThatRun_EachWithItsOwnLossySettingAsync() {
    var lossyApp = new AppliedSchemaChange(APP, SchemaChangeKind.Application, "1.0.0", "2.0.0") { Reversal = SchemaChangeReversal.Lossy };
    var lossyW = new AppliedSchemaChange(W, SchemaChangeKind.Platform, "2.0.0", null) { Reversal = SchemaChangeReversal.Lossy };

    var appOnly = PostgresStartupAssessment.ComputeVerdict(_me, [lossyApp, lossyW], [],
      new SchemaGateOverrides(OverridePlatform: false, OverrideApplication: false, AllowLossyPlatform: false, AllowLossyApplication: true));

    await Assert.That(appOnly.Verdict).IsEqualTo(StartupVerdict.StandDown).Because(appOnly.Reason);
    await Assert.That(appOnly.Platform!.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("the W! side's lossy rollback is not allowed: the application's setting is its own");
    await Assert.That(appOnly.Application!.Verdict).IsEqualTo(StartupVerdict.Migrate);
    await Assert.That(appOnly.Platform.Reason).Contains(UpgradeOrder.ALLOW_LOSSY_ROLLBACK_KEY);

    var both = PostgresStartupAssessment.ComputeVerdict(_me, [lossyApp, lossyW], [],
      new SchemaGateOverrides(OverridePlatform: false, OverrideApplication: false, AllowLossyPlatform: true, AllowLossyApplication: true));
    await Assert.That(both.Rollback!.Reverses(SchemaChangeKind.Platform, out var platformLossy)).IsTrue();
    await Assert.That(platformLossy).IsTrue();
    await Assert.That(both.Rollback.Reverses(SchemaChangeKind.Application, out var applicationLossy)).IsTrue();
    await Assert.That(applicationLossy).IsTrue();

    var held = _judge("rollback", "standdown");
    await Assert.That(held.Rollback!.Reverses(SchemaChangeKind.Application, out _)).IsFalse()
      .Because("a held side is left as it is: the rollback never reverses it");
  }

  [Test]
  public async Task TheWholeInstancesRefusals_AreNotJudgedBySideAsync() {
    var unknown = PostgresStartupAssessment.ComputeVerdict(new VersionSighting(null, null), [], []);
    var newerOnline = PostgresStartupAssessment.ComputeVerdict(_me, [new AppliedSchemaChange(W, SchemaChangeKind.Platform, "1.0.0", null)],
      [new VersionSighting("1.0.0", "2.0.0", Guid.Parse("0199eeee-0000-0000-0000-000000000002"))]);

    await Assert.That(unknown.StandsDownWithoutMigrating).IsTrue();
    await Assert.That(unknown.Platform).IsNull();
    await Assert.That(newerOnline.StandsDownWithoutMigrating).IsTrue()
      .Because("the newer-version-online stand-down is cluster-wide and unchanged");
    await Assert.That(newerOnline.HoldsApplication).IsFalse();
  }
}
