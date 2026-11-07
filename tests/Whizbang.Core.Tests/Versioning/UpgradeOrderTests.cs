// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Versioning;

namespace Whizbang.Core.Tests.Versioning;

/// <summary>
/// #2022: the one rule that orders versions and decides whether a newer one is recorded or online. Startup's Assess, the
/// running instance's re-assessment, the newer instance's announcement, the standby handshake and the migration guard all
/// ask it; a second comparison anywhere else fails the coordination source guard.
/// </summary>
/// <remarks>
/// Both versions drive it (owner decisions on #2022): the library (W!) version by SemVer, and the application version
/// by SemVer or a host-supplied deploy number. Ordering is one-way and strict: only a strictly newer sighting is found,
/// so an equal or older one never stands an instance down. An unknown application version never orders, and two
/// application versions of different schemes do not order either.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Versioning/UpgradeOrder.cs</code-under-test>
[Category("Core")]
public class UpgradeOrderTests {
  private static readonly Guid _me = Guid.Parse("0199bbbb-0000-0000-0000-000000000001");
  private static readonly Guid _peer = Guid.Parse("0199bbbb-0000-0000-0000-000000000002");
  private static readonly Guid _otherPeer = Guid.Parse("0199bbbb-0000-0000-0000-000000000003");

  private static VersionSighting _own(string? library = "1.0.0", string? application = "1.0.0") => new(library, application, _me);

  // ── the library version ─────────────────────────────────────────────

  [Test]
  [Arguments("1.0.0", "1.0.1")]
  [Arguments("1.0.0-rc.1", "1.0.0")]
  [Arguments("1.0.0-alpha.2", "1.0.0-alpha.10")]
  public async Task AStrictlyNewerLibraryVersion_IsFoundAsync(string mine, string theirs) {
    var found = UpgradeOrder.FindNewer(_own(mine), [new VersionSighting(theirs, "1.0.0", _peer)]);

    await Assert.That(found).IsNotNull().Because("SemVer precedence: numeric pre-release identifiers compare numerically");
    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Library);
    await Assert.That(found.NewerVersion).IsEqualTo(theirs);
    await Assert.That(found.OwnVersion).IsEqualTo(mine);
    await Assert.That(found.InstanceId).IsEqualTo(_peer);
  }

  [Test]
  [Arguments("1.0.1", "1.0.0")]
  [Arguments("1.0.0", "1.0.0")]
  [Arguments("1.0.0", "1.0.0+build.7")]
  [Arguments("1.0.0", "not-a-version")]
  [Arguments("1.0.0", null)]
  public async Task AnEqualOlderOrUnreadableLibraryVersion_IsNeverFoundAsync(string mine, string? theirs) {
    var found = UpgradeOrder.FindNewer(_own(mine), [new VersionSighting(theirs, "1.0.0", _peer)]);

    await Assert.That(found).IsNull()
      .Because("only a strictly newer version stands an instance down; equal does nothing and the unreadable cannot outrank");
  }

  // ── the application version ─────────────────────────────────────────

  [Test]
  [Arguments("1.0.0", "1.1.0")]
  [Arguments("deploy:41", "deploy:42")]
  [Arguments("2.0.0-alpha.2", "2.0.0-alpha.10")]
  public async Task AStrictlyNewerApplicationVersion_OnTheSameLibrary_IsFoundAsync(string mine, string theirs) {
    var found = UpgradeOrder.FindNewer(_own(application: mine), [new VersionSighting("1.0.0", theirs, _peer)]);

    await Assert.That(found).IsNotNull().Because("either version can be part of a rollout");
    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Application);
    await Assert.That(found.NewerVersion).IsEqualTo(theirs);
    await Assert.That(found.OwnVersion).IsEqualTo(mine);
  }

  [Test]
  [Arguments(null, "9.0.0")]
  [Arguments("unknown", "9.0.0")]
  [Arguments("1.0.0", null)]
  [Arguments("1.0.0", "unknown")]
  [Arguments("1.0.0", "a build from somewhere")]
  public async Task AnUnknownApplicationVersion_NeverOrders_EitherWayAsync(string? mine, string? theirs) {
    // An unknown version never stands another instance down, and is never stood down by the application version alone.
    var found = UpgradeOrder.FindNewer(_own(application: mine), [new VersionSighting("1.0.0", theirs, _peer)]);

    await Assert.That(found).IsNull();
  }

  [Test]
  [Arguments("1.0.0", "deploy:99")]
  [Arguments("deploy:1", "9.0.0")]
  public async Task ApplicationVersionsOfDifferentSchemes_DoNotOrderAsync(string mine, string theirs) {
    // Like with like: a SemVer version and a deploy number say nothing about each other, so the comparison is treated as
    // unknown. Guessing an order between them would stand a healthy instance down on a coin flip.
    var found = UpgradeOrder.FindNewer(_own(application: mine), [new VersionSighting("1.0.0", theirs, _peer)]);

    await Assert.That(found).IsNull();
  }

  // ── one-way, and both versions together ─────────────────────────────

  [Test]
  public async Task ANewerInstance_NeverFindsAnOlderOne_AndEqualVersionsFindNothingAsync() {
    var older = new VersionSighting("1.0.0", "1.0.0", _peer);
    var newer = new VersionSighting("2.0.0", "2.0.0", _me);

    await Assert.That(UpgradeOrder.FindNewer(newer, [older])).IsNull()
      .Because("one-way only: a newer instance never stands down for an older one");
    await Assert.That(UpgradeOrder.FindNewer(older with { InstanceId = _me }, [older])).IsNull()
      .Because("equal versions do nothing");
  }

  [Test]
  public async Task CrossedVersions_TheApplicationDecides_SoExactlyOneOfThePairStandsDownAsync() {
    // Owner decision (#2022): the newest application version has priority. A newer application on an older library
    // against the reverse: read separately each would stand down and take the fleet with it; read in this order, the
    // older application stands down. Whether the newer application may serve a schema its library did not create is
    // the rollback gate's question, not this order's.
    var newerApplication = new VersionSighting("1.0.0", "2.0.0", _me);
    var newerLibrary = new VersionSighting("2.0.0", "1.0.0", _peer);

    await Assert.That(UpgradeOrder.FindNewer(newerApplication, [newerLibrary])).IsNull();
    var found = UpgradeOrder.FindNewer(newerLibrary with { InstanceId = _otherPeer }, [newerApplication]);
    await Assert.That(found).IsNotNull();
    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Application);
  }

  [Test]
  [Arguments(null, "9.0.0")]
  [Arguments("1.0.0", "unknown")]
  [Arguments("1.0.0", "deploy:3")]
  public async Task WhenTheApplicationVersionsCannotBeOrdered_TheLibraryDecidesAsync(string? mine, string? theirs) {
    var found = UpgradeOrder.FindNewer(_own("1.0.0", mine), [new VersionSighting("2.0.0", theirs, _peer)]);

    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Library);
  }

  [Test]
  public async Task AnUnreadableLibraryVersion_LeavesTheApplicationVersionToDecideAsync() {
    var found = UpgradeOrder.FindNewer(_own(application: "1.0.0"), [new VersionSighting("garbage", "2.0.0", _peer)]);

    await Assert.That(found).IsNotNull();
    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Application);
  }

  [Test]
  public async Task TheInstancesOwnRow_IsNeverASightingAsync() {
    // An instance id that outlives a restart (or a row that recorded an earlier run) is not another instance.
    var found = UpgradeOrder.FindNewer(_own(), [new VersionSighting("9.0.0", "9.0.0", _me)]);

    await Assert.That(found).IsNull();
  }

  // ── which finding is reported ───────────────────────────────────────

  [Test]
  public async Task OfSeveralFindings_TheApplicationOne_TheNewest_AndAnOnlineInstanceOverTheLedger_IsReportedAsync() {
    VersionSighting[] seen = [
      new("1.2.0", "1.0.0", _peer),            // library only
      new("1.0.0", "5.0.0", null),             // the ledger
      new("1.0.0", "5.0.0", _otherPeer),       // the same application version, online
      new("1.0.0", "4.0.0", _peer),            // an older application finding
    ];

    var found = UpgradeOrder.FindNewer(_own(), seen);

    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Application);
    await Assert.That(found.NewerVersion).IsEqualTo("5.0.0");
    await Assert.That(found.InstanceId).IsEqualTo(_otherPeer)
      .Because("a newer instance online names the instance an operator can go and look at");
  }

  [Test]
  public async Task ALaterLibraryFinding_NeverDisplacesAnApplicationOne_AndTheLedgerNeverDisplacesAnEqualOnlineOneAsync() {
    var found = UpgradeOrder.FindNewer(_own(), [
      new VersionSighting("1.0.0", "2.0.0", _peer),     // application, online
      new VersionSighting("1.0.0", "2.0.0", null),      // the same application version, in the ledger
      new VersionSighting("9.0.0", "1.0.0", _otherPeer), // library only
    ]);

    await Assert.That(found!.Kind).IsEqualTo(VersionKind.Application);
    await Assert.That(found.InstanceId).IsEqualTo(_peer);
  }

  [Test]
  public async Task OfTwoLibraryFindings_TheNewestIsReportedAsync() {
    var found = UpgradeOrder.FindNewer(_own(), [new VersionSighting("1.5.0", "1.0.0", _peer), new VersionSighting("1.9.0", "1.0.0", _otherPeer),
      new VersionSighting("1.2.0", "1.0.0", null)]);

    await Assert.That(found!.NewerVersion).IsEqualTo("1.9.0");
  }

  [Test]
  public async Task OfTwoInstancesOnlineAtTheSameNewerVersion_TheLowerIdIsReported_WhicheverIsSeenFirstAsync() {
    var first = UpgradeOrder.FindNewer(_own(), [new VersionSighting("2.0.0", null, _otherPeer), new VersionSighting("2.0.0", null, _peer)]);
    var second = UpgradeOrder.FindNewer(_own(), [new VersionSighting("2.0.0", null, _peer), new VersionSighting("2.0.0", null, _otherPeer)]);

    await Assert.That(first!.InstanceId).IsEqualTo(_peer);
    await Assert.That(second!.InstanceId).IsEqualTo(_peer).Because("two new instances at once: the reason is the same either way");
  }

  [Test]
  public async Task OfTwoApplicationFindings_TheNewestIsReportedAsync() {
    var found = UpgradeOrder.FindNewer(_own(), [new VersionSighting("1.0.0", "1.5.0", _peer), new VersionSighting("1.0.0", "1.9.0", _otherPeer),
      new VersionSighting("1.0.0", "1.2.0", null)]);

    await Assert.That(found!.NewerVersion).IsEqualTo("1.9.0");
  }

  [Test]
  public async Task TheReason_NamesTheVersionsAndWhereTheNewerOneWasSeenAsync() {
    var online = UpgradeOrder.FindNewer(_own(), [new VersionSighting("1.0.0", "2.0.0", _peer)])!.Describe();
    var ledger = UpgradeOrder.FindNewer(_own(), [new VersionSighting("3.0.0", "1.0.0", null)])!.Describe();
    var onlineLibrary = UpgradeOrder.FindNewer(_own(), [new VersionSighting("4.0.0", "1.0.0", _peer)])!.Describe();
    var ledgerApplication = UpgradeOrder.FindNewer(_own(), [new VersionSighting("1.0.0", "5.0.0", null)])!.Describe();

    await Assert.That(online).IsEqualTo($"instance {_peer} is online with application version 2.0.0, newer than this instance's 1.0.0");
    await Assert.That(ledger).IsEqualTo("the ledger records library version 3.0.0, newer than this binary's 1.0.0");
    await Assert.That(onlineLibrary).IsEqualTo($"instance {_peer} is online with library version 4.0.0, newer than this binary's 1.0.0");
    await Assert.That(ledgerApplication).IsEqualTo("the ledger records application version 5.0.0, newer than this instance's 1.0.0");
  }

  // ── the announcement's side of the rule ─────────────────────────────

  [Test]
  public async Task OlderThan_IsTheSameRuleAskedFromTheNewerSideAsync() {
    var me = new VersionSighting("2.0.0", "1.0.0", _me);
    VersionSighting[] online = [
      new("1.0.0", "1.0.0", _peer),          // older library: stands down for me
      new("2.0.0", "1.0.0", _otherPeer),     // equal: nothing
      new("2.0.0", "1.0.0", _me),            // my own row
      new("3.0.0", "1.0.0", null),           // newer: never made to stand down by me
    ];

    var older = UpgradeOrder.OlderThan(me, online);

    await Assert.That(older.Select(o => o.InstanceId)).IsEquivalentTo([(Guid?)_peer]);
  }

  // ── the rollback gate ───────────────────────────────────────────────

  private static AppliedSchemaChange _platform(string name, string library, string? application = "1.0.0") =>
    new(name, SchemaChangeKind.Platform, library, application);

  private static AppliedSchemaChange _application(string name, string library, string? application) =>
    new(name, SchemaChangeKind.Application, library, application);

  [Test]
  public async Task NothingAppliedSinceThisVersion_AnApplicationRollbackHasNothingToFearAsync() {
    // An application rolled back from 2.0.0 to 1.9.0: every change was applied by 1.9.0 or before, so it serves.
    var since = UpgradeOrder.AppliedSince(_own("1.0.0", "1.9.0"), [
      _platform("150_Something.sql", "1.0.0", "1.2.0"),
      _application("perspective:wh_per_orders", "1.0.0", "1.9.0"),
      _application("perspective:wh_per_lines", "0.9.0", "1.0.0"),
    ]);

    await Assert.That(since).IsEmpty();
  }

  [Test]
  public async Task AnApplicationSchemaChange_AppliedByANewerApplication_IsFoundAsync() {
    var change = _application("perspective:wh_per_orders", "1.0.0", "2.0.0");

    var since = UpgradeOrder.AppliedSince(_own("1.0.0", "1.9.0"), [change]);

    await Assert.That(since).IsEquivalentTo([change]);
  }

  [Test]
  public async Task AnApplicationSchemaChange_AppliedByANewerLibrary_IsFound_WhateverTheApplicationVersionAsync() {
    // The library generates the application's tables: a newer library's DDL is not this binary's to serve.
    var change = _application("perspective:wh_per_orders", "2.0.0", "1.0.0");

    await Assert.That(UpgradeOrder.AppliedSince(_own("1.0.0", "3.0.0"), [change])).IsEquivalentTo([change]);
  }

  [Test]
  public async Task APlatformMigration_IsJudgedByTheLibraryVersionAloneAsync() {
    var newer = _platform("351_X.sql", "2.0.0", "1.0.0");
    var sameLibraryNewerApplication = _platform("150_Y.sql", "1.0.0", "9.0.0");

    var since = UpgradeOrder.AppliedSince(_own("1.0.0", "1.0.0"), [newer, sameLibraryNewerApplication]);

    await Assert.That(since).IsEquivalentTo([newer])
      .Because("a W! migration is the library's: a newer application on the same library applied nothing of its own");
  }

  [Test]
  [Arguments(null, null)]
  [Arguments("garbage", "unknown")]
  public async Task AChangeWithNoReadableApplier_NeverStandsAnInstanceDownAsync(string? library, string? application) {
    var since = UpgradeOrder.AppliedSince(_own(), [
      new AppliedSchemaChange("200_Z.sql", SchemaChangeKind.Platform, library, application),
      new AppliedSchemaChange("perspective:wh_per_z", SchemaChangeKind.Application, library, application),
    ]);

    await Assert.That(since).IsEmpty();
  }

  [Test]
  public async Task EqualVersions_FindNothingAppliedSinceAsync() {
    var since = UpgradeOrder.AppliedSince(_own("1.0.0", "1.0.0"), [
      _platform("351_X.sql", "1.0.0", "1.0.0"), _application("perspective:wh_per_x", "1.0.0", "1.0.0")]);

    await Assert.That(since).IsEmpty();
  }

  [Test]
  public async Task TheGatesError_NamesEachChange_WhoAppliedIt_AndThatItCannotBeReversedYetAsync() {
    var own = _own("1.0.0", "1.9.0");
    var text = UpgradeOrder.DescribeAppliedSince(own, [
      _application("perspective:wh_per_orders", "1.0.0", "2.0.0"),
      _platform("351_X.sql", "2.0.0", null),
    ]);

    await Assert.That(text).IsEqualTo(
      "since this instance's version (library 1.0.0, application 1.9.0), 2 schema change(s) were applied by a newer "
      + "version that this version did not make, and a migration cannot be reversed yet (down migrations: "
      + "whizbang-lib/whizbang#2122): perspective:wh_per_orders (applied by library 1.0.0, application 2.0.0), "
      + "351_X.sql (applied by library 2.0.0, application unknown)");
  }

  [Test]
  public async Task TheGatesError_NamesTheFirstFive_AndCountsTheRestAsync() {
    var changes = Enumerable.Range(1, 7).Select(i => _platform($"35{i}_X.sql", "2.0.0", null)).ToList();

    var text = UpgradeOrder.DescribeAppliedSince(_own(), changes);

    await Assert.That(text).Contains("7 schema change(s)");
    await Assert.That(text).Contains("355_X.sql (applied by library 2.0.0, application unknown), and 2 more");
    await Assert.That(text).DoesNotContain("356_X.sql");
  }

  [Test]
  public async Task TheGate_RefusesNullArgumentsAsync() {
    await Assert.That(() => UpgradeOrder.AppliedSince(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.AppliedSince(_own(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.DescribeAppliedSince(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.DescribeAppliedSince(_own(), null!)).Throws<ArgumentNullException>();
  }

  // ── the ordering primitives the other deciders share ────────────────

  [Test]
  public async Task ThePrimitives_OrderLibraryVersionsBySemVerPrecedence_AndReadNothingTheyCannotAsync() {
    await Assert.That(UpgradeOrder.IsReadable("1.0.0-alpha.10")).IsTrue();
    await Assert.That(UpgradeOrder.IsReadable("nope")).IsFalse();
    await Assert.That(UpgradeOrder.IsReadable(null)).IsFalse();
    await Assert.That(UpgradeOrder.CompareLibrary("1.0.0-alpha.10", "1.0.0-alpha.2")).IsEqualTo(1);
    await Assert.That(UpgradeOrder.CompareLibrary("1.0.0", "1.0.0+meta")).IsEqualTo(0);
    await Assert.That(UpgradeOrder.CompareLibrary("0.9.0", "1.0.0")).IsEqualTo(-1);
    await Assert.That(UpgradeOrder.CompareLibrary("nope", "1.0.0")).IsNull();
    await Assert.That(UpgradeOrder.CompareLibrary("1.0.0", null)).IsNull();
    await Assert.That(UpgradeOrder.CompareApplication("deploy:2", "deploy:10")).IsEqualTo(-1);
    await Assert.That(UpgradeOrder.CompareApplication("1.0.0", "deploy:10")).IsNull();
    await Assert.That(UpgradeOrder.NewestLibrary(["0.9.0", "garbage", null, "1.0.0-alpha.10", "1.0.0-alpha.2"])).IsEqualTo("1.0.0-alpha.10");
    await Assert.That(UpgradeOrder.NewestLibrary(["garbage", null])).IsNull();
  }

  [Test]
  public async Task FindNewer_RefusesNullArgumentsAsync() {
    await Assert.That(() => UpgradeOrder.FindNewer(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.FindNewer(_own(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.OlderThan(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.OlderThan(_own(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => UpgradeOrder.NewestLibrary(null!)).Throws<ArgumentNullException>();
  }
}
