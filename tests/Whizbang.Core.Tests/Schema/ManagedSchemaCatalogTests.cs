// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres.Schema;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The statements the reconcile drops each kind with, the ledger's spelling of a kind, the declaration surface every
/// contributor uses, and how the ledger reads in a terminal.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects</docs>
public class ManagedSchemaCatalogTests {
  [Test]
  [Arguments(ManagedObjectKind.Index, "DROP INDEX CONCURRENTLY IF EXISTS \"app\".\"idx_job_x\"")]
  [Arguments(ManagedObjectKind.Constraint, "ALTER TABLE \"app\".\"wh_per_job\" DROP CONSTRAINT IF EXISTS \"idx_job_x\"")]
  [Arguments(ManagedObjectKind.Trigger, "DROP TRIGGER IF EXISTS \"idx_job_x\" ON \"app\".\"wh_per_job\"")]
  [Arguments(ManagedObjectKind.Statistics, "DROP STATISTICS IF EXISTS \"app\".\"idx_job_x\"")]
  [Arguments(ManagedObjectKind.View, "DROP VIEW IF EXISTS \"app\".\"idx_job_x\"")]
  public async Task EachDroppableKind_HasItsStatementAsync(ManagedObjectKind kind, string expected) {
    var statement = ManagedSchemaCatalog.DropStatement("app", new PlannedDrop("wh_per_job", "idx_job_x", kind, "no longer declared"));

    await Assert.That(statement).IsEqualTo(expected);
  }

  [Test]
  [Arguments(ManagedObjectKind.Column)]
  [Arguments(ManagedObjectKind.Table)]
  [Arguments(ManagedObjectKind.Function)]
  public async Task AKindThatIsNeverDropped_HasNoStatementAsync(ManagedObjectKind kind) {
    await Assert.That(() => ManagedSchemaCatalog.DropStatement("app", new PlannedDrop("wh_per_job", "x", kind, "r")))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task EveryKind_ReadsBackAsItIsWrittenAsync() {
    foreach (var kind in Enum.GetValues<ManagedObjectKind>()) {
      await Assert.That(ManagedSchemaCatalog.ParseKind(ManagedSchemaCatalog.FormatKind(kind))).IsEqualTo(kind);
    }
    await Assert.That(ManagedSchemaCatalog.FormatKind(ManagedObjectKind.Statistics)).IsEqualTo("statistics");
  }

  [Test]
  [Arguments("nonsense")]
  [Arguments("99")]
  public async Task AnUnknownSpelling_ReadsAsAnIndexAsync(string spelling) {
    await Assert.That(ManagedSchemaCatalog.ParseKind(spelling)).IsEqualTo(ManagedObjectKind.Index);
  }

  [Test]
  public async Task EachDeclarationMethod_DeclaresItsKindAsync() {
    var set = new ManagedSchemaObjectSet()
      .Trigger("t", "a", "by").Function("t", "b", "by").Statistics("t", "c", "by").View("t", "d", "by").Column("t", "e", "by");

    await Assert.That(set.Objects.Select(o => o.Kind)).IsEquivalentTo([
      ManagedObjectKind.Trigger, ManagedObjectKind.Function, ManagedObjectKind.Statistics, ManagedObjectKind.View,
      ManagedObjectKind.Column,
    ]);
  }

  [Test]
  public async Task Declarations_FromAGeneratedList_KeepTheirKindsAsync() {
    var set = ManagedSchemaObjectSet.FromDeclarations([
      ("wh_per_job", "index", "ix_wh_per_job_status", "JobModel"),
      ("wh_per_job", "constraint", "ck_wh_per_job_code_len", "JobModel"),
    ]);

    await Assert.That(set.Objects.Select(o => $"{o.Name}:{o.Kind}:{o.DeclaredBy}")).IsEquivalentTo([
      "ix_wh_per_job_status:Index:JobModel", "ck_wh_per_job_code_len:Constraint:JobModel",
    ]);
  }

  [Test]
  public async Task KeepSchemaObject_CarriesTheNameAndTheReasonAsync() {
    var attribute = new KeepSchemaObjectAttribute("idx_job_legacy") { Reason = "the report reads it" };

    await Assert.That(attribute.Name).IsEqualTo("idx_job_legacy");
    await Assert.That(attribute.Reason).IsEqualTo("the report reads it");
  }

  [Test]
  public async Task TheLedger_ReadsWithBothPins_AndAPinWithoutAReasonOrSourceAsync() {
    var rows = new[] {
      new LedgerRow("wh_per_job", "idx_job_legacy", ManagedObjectKind.Index, "whizbang", "pending-retirement",
        CodePinned: true, CodePinSource: "code", CodePinReason: "the model keeps it",
        DbPinned: true, DbPinSource: "sql", DbPinReason: null, UndeclaredSince: null),
      new LedgerRow("wh_per_job", "customer_idx", ManagedObjectKind.Index, null, "active",
        CodePinned: false, CodePinSource: null, CodePinReason: null,
        DbPinned: true, DbPinSource: null, DbPinReason: null, UndeclaredSince: null),
    };

    var text = ManagedSchemaLedger.Format(rows);

    await Assert.That(text).Contains("pinned by code: the model keeps it; sql");
    await Assert.That(text).Contains("pinned by ?");
  }

  [Test]
  public async Task ADeclaredIndexAbsentButStandingUnderAnotherName_IsDeclaredUnderThatNameAsync() {
    var declared = new ManagedSchemaObjectSet()
      .Index("wh_per_job", "idx_job_tenant", "JobModel")
      .Index("wh_per_job", "idx_job_status", "JobModel")
      .Index("wh_per_job", "idx_job_owner", "JobModel")
      .Pin("wh_per_job", "idx_job_legacy", "kept", "JobModel [KeepSchemaObject]");
    var present = new HashSet<(string, string)> {
      ("wh_per_job", "idx_job_scope_t"), ("wh_per_job", "idx_job_status"), ("wh_per_job", "idx_job_status_old"),
    };

    var effective = declared.WithEquivalents([
      ("wh_per_job", "idx_job_tenant", "idx_job_scope_t"),
      ("wh_per_job", "idx_job_status", "idx_job_status_old"),
      ("wh_per_job", "idx_job_owner", "idx_job_owner_gone"),
    ], present);

    await Assert.That(effective.Objects.Select(o => o.Name)).IsEquivalentTo(["idx_job_scope_t", "idx_job_status", "idx_job_owner"])
      .Because("only an absent declaration with a present stand-in is replaced");
    await Assert.That(effective.Objects.Single(o => o.Name == "idx_job_scope_t").DeclaredBy).IsEqualTo("JobModel (as idx_job_tenant, the same definition)");
    await Assert.That(effective.Pins.Select(p => p.Name)).IsEquivalentTo(["idx_job_legacy"]);
  }
}
