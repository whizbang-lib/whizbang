// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The reconcile plan: what Whizbang records, drops and keeps, given what the model declares, what the
/// database holds and what the ledger remembers. Whizbang drops only what it created and no longer declares,
/// never a foreign or pinned object, and drops nothing on the first start with the ledger.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
public class ManagedSchemaPlannerTests {
  private const string TABLE = "wh_per_job";
  private static readonly DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

  private static ManagedSchemaObjectSet _declared(Action<ManagedSchemaObjectSet>? declare = null) {
    var set = new ManagedSchemaObjectSet();
    set.Index(TABLE, "idx_job_status", "JobModel.Status [Indexed]");
    declare?.Invoke(set);
    return set;
  }

  private static LiveSchemaObject _index(string name, string? comment = null) =>
    new(TABLE, name, ManagedObjectKind.Index, $"CREATE INDEX {name} ON public.{TABLE} USING btree (x)", comment);

  private static LedgerRow _row(string name, string? owner = ManagedOwners.WHIZBANG, string status = ManagedStatuses.ACTIVE,
      bool dbPinned = false, string? dbPinSource = null, ManagedObjectKind kind = ManagedObjectKind.Index,
      bool codePinned = false, string? codePinSource = null) =>
    new(TABLE, name, kind, owner, status, codePinned, codePinSource, CodePinReason: null,
      dbPinned, dbPinSource, DbPinReason: null, UndeclaredSince: null);

  private static ReconcileSettings _settings(ReconcileMode mode = ReconcileMode.Apply,
      IReadOnlyCollection<ManagedObjectKind>? keepKinds = null, IReadOnlyList<string>? pins = null,
      IReadOnlySet<string>? fleetDeclared = null, bool fleetUnknown = false) =>
    new(mode, keepKinds ?? [], pins ?? [], fleetUnknown ? null : fleetDeclared ?? new HashSet<string>());

  [Test]
  public async Task FirstStart_RecordsEveryObject_AndDropsNothingAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin"), _index("customer_report_idx") };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger: [], _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(_status(plan, "idx_job_status")).IsEqualTo($"{ManagedOwners.WHIZBANG}/{ManagedStatuses.ACTIVE}");
    await Assert.That(_status(plan, "idx_job_data_gin")).IsEqualTo($"{ManagedOwners.WHIZBANG}/{ManagedStatuses.PENDING_RETIREMENT}")
      .Because("Whizbang's naming for this table, and no longer declared: retired on the next start");
    await Assert.That(_status(plan, "customer_report_idx")).IsEqualTo($"{ManagedOwners.FOREIGN}/{ManagedStatuses.ACTIVE}")
      .Because("not Whizbang-shaped, so someone else made it");
  }

  [Test]
  public async Task LaterStart_AnUndeclaredWhizbangObject_IsDroppedAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops.Select(d => d.Name)).IsEquivalentTo(["idx_job_data_gin"]);
  }

  [Test]
  [Arguments(ManagedObjectKind.Trigger)]
  [Arguments(ManagedObjectKind.Function)]
  public async Task APhysicalFieldMoveSyncObject_IsRecordedAsActive_AndNeverDroppedAsync(ManagedObjectKind kind) {
    var sync = new LiveSchemaObject(TABLE, "wh_mv_0123456789abcdef0123_a", kind, "CREATE TRIGGER ...", Comment: null);
    var ledger = new[] { _row("idx_job_status"), _row(sync.Name, kind: kind) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), [_index("idx_job_status"), sync], ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty()
      .Because("the move that armed it drops it when it settles; the reconcile never races that");
    await Assert.That(_status(plan, sync.Name)).IsEqualTo($"{ManagedOwners.WHIZBANG}/{ManagedStatuses.ACTIVE}");
    await Assert.That(plan.Records.Single(r => r.Name == sync.Name).DeclaredBy).IsEqualTo("physical-field move");
  }

  [Test]
  public async Task OnATableWithoutThePerspectivePrefix_WhizbangsNamingIsTheTablesOwnNameAsync() {
    var live = new[] { new LiveSchemaObject("orders", "idx_orders_status", ManagedObjectKind.Index, "x", null) };

    var plan = ManagedSchemaPlanner.Plan(new ManagedSchemaObjectSet(), live, ledger: [], _settings(), _now);

    await Assert.That(plan.Records.Single().Owner).IsEqualTo(ManagedOwners.WHIZBANG);
  }

  [Test]
  public async Task ATriggerWhizbangDoesNotNameOrDeclare_IsForeignAsync() {
    var trigger = new LiveSchemaObject(TABLE, "audit_job_changes", ManagedObjectKind.Trigger, "CREATE TRIGGER ...", null);

    var plan = ManagedSchemaPlanner.Plan(_declared(), [_index("idx_job_status"), trigger], ledger: [], _settings(), _now);

    await Assert.That(_status(plan, trigger.Name)).IsEqualTo($"{ManagedOwners.FOREIGN}/{ManagedStatuses.ACTIVE}");
  }

  [Test]
  public async Task AGoneObjectTheLedgerNeverClassified_IsRetiredAsForeignAsync() {
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_vanished", owner: null) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), [_index("idx_job_status")], ledger, _settings(), _now);

    await Assert.That(_status(plan, "idx_job_vanished")).IsEqualTo($"{ManagedOwners.FOREIGN}/{ManagedStatuses.RETIRED}");
  }

  [Test]
  public async Task ACodePinWithoutAReason_IsExplainedByWhoDeclaredItAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy") };

    var plan = ManagedSchemaPlanner.Plan(
      _declared(set => set.Pin(TABLE, "idx_job_legacy", reason: null, "JobModel [KeepSchemaObject]")), live, ledger: [], _settings(), _now);

    await Assert.That(plan.Records.Single(r => r.Name == "idx_job_legacy").CodePinReason).IsEqualTo("JobModel [KeepSchemaObject]");
  }

  [Test]
  public async Task APinCommentWithoutAReason_PinsWithNoReasonAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy", comment: "whizbang:pin") };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger: [], _settings(), _now);

    var record = plan.Records.Single(r => r.Name == "idx_job_legacy");
    await Assert.That(record.AddsDbPin).IsTrue();
    await Assert.That(record.NewDbPinReason).IsNull();
  }

  [Test]
  public async Task AForeignObject_IsNeverDroppedAsync() {
    var live = new[] { _index("idx_job_status"), _index("customer_report_idx") };
    var ledger = new[] { _row("idx_job_status"), _row("customer_report_idx", owner: ManagedOwners.FOREIGN) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
  }

  [Test]
  [Arguments("sql")]
  [Arguments("cli")]
  [Arguments("db-comment")]
  public async Task ADatabasePin_KeepsTheObject_WithItsSourceAsync(string source) {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_legacy", dbPinned: true, dbPinSource: source) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Kept.Single(k => k.Name == "idx_job_legacy").Reason).Contains(source);
  }

  [Test]
  public async Task APinFromCode_IsRecordedAndKeptAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_legacy") };
    var declared = _declared(s => s.Pin(TABLE, "idx_job_legacy", "reporting reads it", "JobModel [KeepSchemaObject]"));

    var plan = ManagedSchemaPlanner.Plan(declared, live, ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
    var record = plan.Records.Single(r => r.Name == "idx_job_legacy");
    await Assert.That(record.CodePinned).IsTrue();
    await Assert.That(record.CodePinSource).IsEqualTo(PinSources.CODE);
    await Assert.That(record.CodePinReason).IsEqualTo("reporting reads it");
  }

  [Test]
  public async Task RemovingACodePin_ReleasesIt_ButLeavesTheDatabasePinAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy") };
    var ledger = new[] {
      _row("idx_job_status"),
      _row("idx_job_legacy", dbPinned: true, dbPinSource: PinSources.SQL, codePinned: true, codePinSource: PinSources.CODE),
    };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    var record = plan.Records.Single(r => r.Name == "idx_job_legacy");
    await Assert.That(record.CodePinned).IsFalse().Because("the [KeepSchemaObject] declaration is gone");
    await Assert.That(record.AddsDbPin).IsFalse().Because("the database pin is the DBA's and is never rewritten");
    await Assert.That(plan.Drops).IsEmpty().Because("the database pin still holds it");
  }

  [Test]
  public async Task RemovingACodePin_WithNoDatabasePin_LetsTheObjectBeDroppedAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy") };
    var ledger = new[] {
      _row("idx_job_status"),
      _row("idx_job_legacy", status: ManagedStatuses.PENDING_RETIREMENT, codePinned: true, codePinSource: PinSources.CODE),
    };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops.Select(d => d.Name)).IsEquivalentTo(["idx_job_legacy"]);
  }

  [Test]
  public async Task APinFromConfiguration_MatchesByGlobAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_code_legacy") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_code_legacy") };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(pins: ["wh_per_*:idx_*_legacy"]), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Records.Single(r => r.Name == "idx_job_code_legacy").CodePinSource).IsEqualTo(PinSources.CONFIG);
  }

  [Test]
  public async Task APinComment_OnTheObject_PinsItAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_legacy", comment: "whizbang:pin the reporting job reads it") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_legacy") };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
    var record = plan.Records.Single(r => r.Name == "idx_job_legacy");
    await Assert.That(record.AddsDbPin).IsTrue();
    await Assert.That(record.NewDbPinReason).IsEqualTo("the reporting job reads it");
  }

  [Test]
  [Arguments(ReconcileMode.AddOnly)]
  [Arguments(ReconcileMode.ReportOnly)]
  public async Task AModeThatDoesNotDrop_KeepsWhatApplyWouldDropAsync(ReconcileMode mode) {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(mode), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Kept.Single(k => k.Name == "idx_job_data_gin").Reason).Contains(mode.ToString());
  }

  [Test]
  public async Task AKindSetToKeep_IsNotDroppedAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(keepKinds: [ManagedObjectKind.Index]), _now);

    await Assert.That(plan.Drops).IsEmpty();
  }

  [Test]
  public async Task WhileARunningInstanceStillDeclaresIt_ADropWaitsAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger,
      _settings(fleetDeclared: new HashSet<string> { $"{TABLE}:idx_job_data_gin" }), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Kept.Single(k => k.Name == "idx_job_data_gin").Reason).Contains("still declared by a running instance");
  }

  [Test]
  public async Task WhatOtherInstancesDeclare_DoesNotHoldBackOtherDropsAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger,
      _settings(fleetDeclared: new HashSet<string> { $"{TABLE}:idx_job_something_else" }), _now);

    await Assert.That(plan.Drops.Select(d => d.Name)).IsEquivalentTo(["idx_job_data_gin"]);
  }

  [Test]
  public async Task WhileAnInstanceThatHasNotReportedIsRunning_NothingIsDroppedAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_data_gin", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(fleetUnknown: true), _now);

    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Kept.Single(k => k.Name == "idx_job_data_gin").Reason).Contains("has not reported");
  }

  [Test]
  [Arguments(ManagedObjectKind.Column)]
  [Arguments(ManagedObjectKind.Table)]
  [Arguments(ManagedObjectKind.Policy)]
  public async Task AKindThatHoldsDataOrSecurity_IsNeverDroppedAutomaticallyAsync(ManagedObjectKind kind) {
    var live = new[] { _index("idx_job_status"), new LiveSchemaObject(TABLE, "idx_job_old", kind, "x", null) };
    var ledger = new[] { _row("idx_job_status"), _row("idx_job_old", status: ManagedStatuses.PENDING_RETIREMENT, kind: kind) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    await Assert.That(plan.Drops).IsEmpty();
  }

  [Test]
  public async Task ADeclaredObjectTheDatabaseLacks_IsReportedMissingAsync() {
    var plan = ManagedSchemaPlanner.Plan(_declared(), live: [], ledger: [_row("idx_job_status")], _settings(), _now);

    await Assert.That(plan.Missing.Select(m => m.Name)).IsEquivalentTo(["idx_job_status"]);
  }

  [Test]
  public async Task AnObjectDeclaredAgain_IsActiveAgainAsync() {
    var live = new[] { _index("idx_job_status") };
    var ledger = new[] { _row("idx_job_status", status: ManagedStatuses.PENDING_RETIREMENT) };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(), _now);

    var record = plan.Records.Single(r => r.Name == "idx_job_status");
    await Assert.That(record.Status).IsEqualTo(ManagedStatuses.ACTIVE);
    await Assert.That(record.UndeclaredSince).IsNull();
  }

  [Test]
  public async Task AnUndeclaredObject_RemembersWhenItWasFirstUndeclaredAsync() {
    var since = _now.AddDays(-2);
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };
    var ledger = new[] {
      _row("idx_job_status"),
      new LedgerRow(TABLE, "idx_job_data_gin", ManagedObjectKind.Index, ManagedOwners.WHIZBANG, ManagedStatuses.PENDING_RETIREMENT,
        CodePinned: false, CodePinSource: null, CodePinReason: null, DbPinned: false, DbPinSource: null, DbPinReason: null,
        UndeclaredSince: since),
    };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger, _settings(mode: ReconcileMode.AddOnly), _now);

    await Assert.That(plan.Records.Single(r => r.Name == "idx_job_data_gin").UndeclaredSince).IsEqualTo(since);
  }

  [Test]
  public async Task ModeOff_PlansNothingAsync() {
    var live = new[] { _index("idx_job_status"), _index("idx_job_data_gin") };

    var plan = ManagedSchemaPlanner.Plan(_declared(), live, ledger: [], _settings(ReconcileMode.Off), _now);

    await Assert.That(plan.Records).IsEmpty();
    await Assert.That(plan.Drops).IsEmpty();
    await Assert.That(plan.Missing).IsEmpty();
  }

  private static string _status(ReconcilePlan plan, string name) {
    var record = plan.Records.Single(r => r.Name == name);
    return $"{record.Owner}/{record.Status}";
  }
}
