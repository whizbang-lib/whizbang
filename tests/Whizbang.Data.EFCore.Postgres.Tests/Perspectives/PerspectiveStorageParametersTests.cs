// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1747: the pure half of the storage-parameter reconcile. The generated schema declares each perspective
/// table's fillfactor and autovacuum settings in a marker line; the reconcile reads the markers, compares them with
/// the catalog and plans one <c>ALTER TABLE</c> per table that differs. Against a real database in
/// <see cref="JsonbColumnStorageIntegrationTests"/>.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
public class PerspectiveStorageParametersTests {
  private static TableStorageDeclaration _declared(string table, params (string Name, string Value)[] parameters) =>
    new(table, parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

  /// <summary>The generators write the marker the reconcile reads, with the bare table name and each declared value.</summary>
  [Test]
  public async Task TheGeneratorsMarker_IsTheOneTheReconcileReadsAsync() {
    var generatorMarker = ColumnStorageSql.STORAGE_MARKER;
    await Assert.That(generatorMarker).IsEqualTo(PerspectiveStorageParameters.MARKER);

    var sql = string.Join('\n', ColumnStorageSql.ForTable("\"svc\".wh_per_order",
      new TableStorageInfo(null, 512, AutovacuumVacuumThreshold: 500, ToastAutovacuumVacuumScaleFactor: 0.01, FillFactor: 90)));
    var declaration = PerspectiveStorageParameters.Declarations([sql]).Single();

    await Assert.That(declaration.Table).IsEqualTo("wh_per_order");
    await Assert.That(declaration.Parameters.Count).IsEqualTo(3);
    await Assert.That(declaration.Parameters["fillfactor"]).IsEqualTo("90");
    await Assert.That(declaration.Parameters["autovacuum_vacuum_threshold"]).IsEqualTo("500");
    await Assert.That(declaration.Parameters["toast.autovacuum_vacuum_scale_factor"]).IsEqualTo("0.01");
  }

  /// <summary>A table without the attribute still has a marker, so the reconcile governs it and resets what it holds.</summary>
  [Test]
  public async Task ATableWithoutTheAttribute_IsDeclaredWithNoParametersAsync() {
    var sql = string.Join('\n', ColumnStorageSql.ForTable("wh_per_plain", null));
    var declaration = PerspectiveStorageParameters.Declarations(["CREATE TABLE x ();\n" + sql]).Single();

    await Assert.That(declaration.Table).IsEqualTo("wh_per_plain");
    await Assert.That(declaration.Parameters).IsEmpty();
  }

  /// <summary>
  /// A token naming a parameter the attribute cannot set, or carrying a value that is not a number, is not read: the
  /// marker is generated, so such a token is not one the generators wrote, and it never reaches an ALTER TABLE.
  /// </summary>
  [Test]
  public async Task Declarations_IgnoreAnUngovernedOrMalformedTokenAsync() {
    var declaration = PerspectiveStorageParameters.Declarations([
      PerspectiveStorageParameters.MARKER + "wh_per_t autovacuum_enabled=off fillfactor=9);drop fillfactor autovacuum_vacuum_threshold=7"]).Single();

    await Assert.That(declaration.Parameters.Count).IsEqualTo(1);
    await Assert.That(declaration.Parameters["autovacuum_vacuum_threshold"]).IsEqualTo("7");
  }

  /// <summary>A marker whose table name is not a plain identifier is not read, so no such name reaches a statement.</summary>
  [Test]
  public async Task Declarations_IgnoreAMarkerWhoseTableIsNotAnIdentifierAsync() {
    await Assert.That(PerspectiveStorageParameters.Declarations([PerspectiveStorageParameters.MARKER + "wh_per_t';drop fillfactor=90"])).IsEmpty();
  }

  /// <summary>The override setting is a comma-separated list of bare table names; spaces and empty entries are ignored.</summary>
  [Test]
  public async Task ManualTables_AreReadFromACommaSeparatedListAsync() {
    await Assert.That(PerspectiveStorageParameters.ManualTables(" wh_per_a, ,wh_per_b ,")).IsEquivalentTo(["wh_per_a", "wh_per_b"]);
    await Assert.That(PerspectiveStorageParameters.ManualTables(null)).IsEmpty();
    await Assert.That(PerspectiveStorageParameters.ManualTables("")).IsEmpty();
  }

  /// <summary>
  /// The plan sets each declared value the catalog does not hold, resets each governed parameter the model does not
  /// declare but the table holds, and leaves a parameter the attribute cannot set.
  /// </summary>
  [Test]
  public async Task Plan_SetsWhatDiffers_AndResetsTheUndeclaredItHoldsAsync() {
    var change = PerspectiveStorageParameters.Plan(
      _declared("wh_per_t", ("fillfactor", "90"), ("autovacuum_vacuum_threshold", "500")),
      ["fillfactor=70", "autovacuum_vacuum_threshold=500", "autovacuum_vacuum_scale_factor=0.2",
       "toast.autovacuum_vacuum_threshold=300", "autovacuum_analyze_threshold=77"]);

    await Assert.That(change).IsNotNull();
    await Assert.That(change!.Set.Count).IsEqualTo(1);
    await Assert.That(change.Set["fillfactor"]).IsEqualTo("90");
    await Assert.That(change.Reset).IsEquivalentTo(["autovacuum_vacuum_scale_factor", "toast.autovacuum_vacuum_threshold"]);
    await Assert.That(change.TouchesFillFactor).IsTrue();
  }

  /// <summary>A table that already matches its declaration needs no change, so the reconcile issues no ALTER TABLE.</summary>
  [Test]
  public async Task Plan_IsNull_WhenNothingDiffersAsync() {
    var change = PerspectiveStorageParameters.Plan(
      _declared("wh_per_t", ("fillfactor", "90")), ["fillfactor=90", "autovacuum_analyze_threshold=77"]);

    await Assert.That(change).IsNull();
  }

  /// <summary>An autovacuum change alone touches no pages, so it never queues a rewrite.</summary>
  [Test]
  public async Task Plan_OfAnAutovacuumChange_DoesNotTouchTheFillFactorAsync() {
    var change = PerspectiveStorageParameters.Plan(_declared("wh_per_t"), ["autovacuum_vacuum_threshold=5"]);

    await Assert.That(change!.Reset).IsEquivalentTo(["autovacuum_vacuum_threshold"]);
    await Assert.That(change.Set).IsEmpty();
    await Assert.That(change.TouchesFillFactor).IsFalse();
  }

  /// <summary>A reset fillfactor touches it too: the pages already written stay packed to the old value.</summary>
  [Test]
  public async Task Plan_OfAFillFactorReset_TouchesTheFillFactorAsync() {
    var change = PerspectiveStorageParameters.Plan(_declared("wh_per_t"), ["fillfactor=70"]);

    await Assert.That(change!.TouchesFillFactor).IsTrue();
  }

  /// <summary>One statement per table: the settings and the resets together, so the table is locked once.</summary>
  [Test]
  public async Task AlterStatement_SetsAndResetsInOneStatementAsync() {
    var both = new TableStorageChange("wh_per_t",
      new Dictionary<string, string> { ["fillfactor"] = "90", ["toast.autovacuum_vacuum_threshold"] = "300" },
      ["autovacuum_vacuum_scale_factor"]);

    await Assert.That(PerspectiveStorageParameters.AlterStatement("\"svc\".\"wh_per_t\"", both))
      .IsEqualTo("ALTER TABLE \"svc\".\"wh_per_t\" SET (fillfactor = 90, toast.autovacuum_vacuum_threshold = 300), RESET (autovacuum_vacuum_scale_factor)");
    await Assert.That(PerspectiveStorageParameters.AlterStatement("t", new TableStorageChange("t", new Dictionary<string, string>(), ["fillfactor"])))
      .IsEqualTo("ALTER TABLE t RESET (fillfactor)");
    await Assert.That(PerspectiveStorageParameters.AlterStatement("t", new TableStorageChange("t", new Dictionary<string, string> { ["fillfactor"] = "80" }, [])))
      .IsEqualTo("ALTER TABLE t SET (fillfactor = 80)");
  }
}
