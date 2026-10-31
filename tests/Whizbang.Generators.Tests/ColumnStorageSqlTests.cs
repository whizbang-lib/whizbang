// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;
using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;
using ColumnStorageSql = shared::Whizbang.Generators.Shared.Models.ColumnStorageSql;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;
using TableStorageInfo = shared::Whizbang.Generators.Shared.Models.TableStorageInfo;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The storage options a model declares reach both schema generators as statements that compare the catalog
/// first and alter only what differs. Whether PostgreSQL applies them, and that a second pass changes nothing,
/// is proven against a real database in JsonbColumnStorageIntegrationTests.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-storage</docs>
public class ColumnStorageSqlTests {
  private const string MODEL = """
    using System;
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    [PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]
    public record FilterModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField(Storage = ColumnStorage.Main, Compression = ColumnCompression.Pglz, MaxBytes = 1024)]
      public Dictionary<string, string[]> Filters { get; init; } = new();

      [PhysicalField(MaxBytes = 0, ColumnName = "plain")]
      public string? Plain { get; init; }
    }

    public class FilterPerspective : IPerspectiveFor<FilterModel, TestEvent> {
      public FilterModel Apply(FilterModel currentData, TestEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  public async Task ServiceRegistration_EmitsTheTableAndColumnOptionsAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("ALTER COLUMN data SET COMPRESSION lz4;");
    await Assert.That(sql).Contains("SET (toast_tuple_target = 512);");
    await Assert.That(sql).Contains("ALTER COLUMN filters SET STORAGE MAIN;");
    await Assert.That(sql).Contains("ALTER COLUMN filters SET COMPRESSION pglz;");
    await Assert.That(sql).Contains("CHECK (pg_column_size(filters) <= 1024) NOT VALID;");
    await Assert.That(sql).DoesNotContain("ALTER COLUMN plain SET")
      .Because("a field that declares nothing keeps the server's defaults.");
  }

  [Test]
  public async Task ATableDeclaringOnlyDefaults_GetsNoStatementsAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]",
      "[PerspectiveTableStorage(ToastTupleTarget = 0)]", StringComparison.Ordinal);
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).DoesNotContain("toast_tuple_target");
    await Assert.That(sql).DoesNotContain("ALTER COLUMN data SET");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SchemaGenerator_EmitsTheSameOptionsAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODEL);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains("ALTER COLUMN data SET COMPRESSION lz4;");
    await Assert.That(schema).Contains("ALTER COLUMN filters SET STORAGE MAIN;");
    await Assert.That(schema).Contains("CHECK (pg_column_size(filters) <= 1024) NOT VALID;");
  }

  [Test]
  [Arguments(1, "PLAIN", 'p')]
  [Arguments(2, "MAIN", 'm')]
  [Arguments(3, "EXTERNAL", 'e')]
  [Arguments(4, "EXTENDED", 'x')]
  public async Task Storage_IsComparedByItsCatalogCodeAsync(int value, string name, char code) {
    await Assert.That(ColumnStorageSql.StorageName(value)).IsEqualTo(name);
    await Assert.That(ColumnStorageSql.SetStorage("t", "c", name)).Contains($"attstorage <> '{code}'")
      .Because("pg_attribute stores the strategy as one letter, and EXTENDED's is not its initial.");
  }

  [Test]
  public async Task DefaultAndUnknownValues_DeclareNothingAsync() {
    await Assert.That(ColumnStorageSql.StorageName(0)).IsNull();
    await Assert.That(ColumnStorageSql.StorageName(null)).IsNull();
    await Assert.That(ColumnStorageSql.CompressionName(0)).IsNull();
    await Assert.That(ColumnStorageSql.CompressionName(9)).IsNull();
    // A table declaring nothing sets nothing; its only line is its storage marker, declaring nothing (#1747).
    await Assert.That(ColumnStorageSql.ForTable("t", null).Single()).IsEqualTo("-- @whizbang:table-storage t");
    await Assert.That(ColumnStorageSql.ForTable("t", new TableStorageInfo(null, null)).Single()).IsEqualTo("-- @whizbang:table-storage t");
    await Assert.That(ColumnStorageSql.ForColumn("t", "t", _field())).IsEmpty();
  }

  [Test]
  public async Task Compression_ToleratesAServerWithoutTheMethodAsync() {
    var sql = ColumnStorageSql.SetCompression("t", "data", "lz4");

    await Assert.That(sql).Contains("attcompression IS DISTINCT FROM 'l'");
    await Assert.That(sql).Contains("EXCEPTION WHEN feature_not_supported THEN");
  }

  [Test]
  public async Task SizeBudget_IsReplacedWhenTheDeclaredBudgetChangesAsync() {
    var sql = ColumnStorageSql.SizeBudget("t", "t", "c", 2048);

    await Assert.That(sql).Contains("NOT LIKE '%<= 2048)%'");
    await Assert.That(sql).Contains("DROP CONSTRAINT ck_t_c_size;");
    await Assert.That(sql).Contains("ADD CONSTRAINT ck_t_c_size CHECK (pg_column_size(c) <= 2048) NOT VALID;");
  }

  private const string AUTOVACUUM = """
    [PerspectiveTableStorage(AutovacuumVacuumThreshold = 500, AutovacuumVacuumScaleFactor = 0,
      AutovacuumInsertThreshold = 2000, AutovacuumInsertScaleFactor = 0.05,
      ToastAutovacuumVacuumThreshold = 300, ToastAutovacuumVacuumScaleFactor = 0.01)]
    """;

  [Test]
  public async Task StorageParameter_ComparesTheTableOrItsToastRelationAsync() {
    var table = ColumnStorageSql.SetStorageParameter("t", "autovacuum_vacuum_threshold", "500");
    var toast = ColumnStorageSql.SetStorageParameter("t", "toast.autovacuum_vacuum_threshold", "300");

    await Assert.That(table).Contains("'autovacuum_vacuum_threshold=500' = ANY (coalesce(reloptions, ARRAY[]::text[]))");
    await Assert.That(table).Contains("ALTER TABLE t SET (autovacuum_vacuum_threshold = 500);");
    await Assert.That(toast).Contains("JOIN pg_class toast ON toast.oid = c.reltoastrelid")
      .Because("PostgreSQL keeps a toast.* parameter on the TOAST relation, without the prefix.");
    await Assert.That(toast).Contains("'autovacuum_vacuum_threshold=300' = ANY (coalesce(toast.reloptions, ARRAY[]::text[]))");
    await Assert.That(toast).Contains("ALTER TABLE t SET (toast.autovacuum_vacuum_threshold = 300);");
  }

  private const string MARKER = "-- @whizbang:table-storage ";

  /// <summary>
  /// Issue #1584 and #1747: each autovacuum setting a model declares reaches both schema generators in the table's
  /// storage marker, which the reconcile reads at every start; neither generator sets it in the schema SQL, which runs
  /// only when the perspective's schema changes.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AutovacuumSettings_ReachBothSchemaGeneratorsAsync() {
    var (efSql, schema) = await _bothAsync(AUTOVACUUM);

    foreach (var sql in new[] { efSql, schema }) {
      await Assert.That(sql).Contains(MARKER + "wh_per_filter autovacuum_vacuum_threshold=500 autovacuum_vacuum_scale_factor=0 "
        + "autovacuum_vacuum_insert_threshold=2000 autovacuum_vacuum_insert_scale_factor=0.05 "
        + "toast.autovacuum_vacuum_threshold=300 toast.autovacuum_vacuum_scale_factor=0.01");
      await Assert.That(sql).DoesNotContain("wh_per_filter SET (autovacuum_")
        .Because("the reconcile applies them, so a start without a schema change still brings the table in line.");
    }
  }

  [Test]
  public async Task NegativeAutovacuumSettings_AreNotDeclaredAsync() {
    var (efSql, _) = await _bothAsync(
      "[PerspectiveTableStorage(AutovacuumVacuumThreshold = -1, AutovacuumVacuumScaleFactor = -1, AutovacuumInsertThreshold = -1, "
      + "AutovacuumInsertScaleFactor = -0.5, ToastAutovacuumVacuumThreshold = -5, ToastAutovacuumVacuumScaleFactor = -1)]");

    await Assert.That(efSql).Contains(MARKER + "wh_per_filter");
    await Assert.That(efSql).DoesNotContain(MARKER + "wh_per_filter ")
      .Because("a negative value means the server's, so nothing is declared and the reconcile resets what the table holds.");
  }

  /// <summary>The marker lists the governed parameters in a fixed order: the fillfactor, then the autovacuum settings.</summary>
  [Test]
  public async Task StorageMarker_ListsEachDeclaredParameterInOrderAsync() {
    var storage = new TableStorageInfo(null, null, AutovacuumVacuumThreshold: 1, AutovacuumVacuumScaleFactor: 0.5,
      AutovacuumInsertThreshold: 2, AutovacuumInsertScaleFactor: 0, ToastAutovacuumVacuumThreshold: 3,
      ToastAutovacuumVacuumScaleFactor: 1e-05, FillFactor: 90);

    var statements = ColumnStorageSql.ForTable("\"svc\".wh_per_t", storage).ToList();

    await Assert.That(statements.Count).IsEqualTo(1);
    await Assert.That(statements[0]).IsEqualTo(MARKER + "wh_per_t fillfactor=90 autovacuum_vacuum_threshold=1 "
      + "autovacuum_vacuum_scale_factor=0.5 autovacuum_vacuum_insert_threshold=2 autovacuum_vacuum_insert_scale_factor=0 "
      + "toast.autovacuum_vacuum_threshold=3 toast.autovacuum_vacuum_scale_factor=1E-05")
      .Because("the marker names the bare table, and a scale factor is written in the invariant round-trip form, which PostgreSQL stores as written.");
  }

  /// <summary>
  /// Issue #1592: a declared fillfactor reaches both schema generators in the marker; the reconcile sets it and queues
  /// the rewrite (JsonbColumnStorageIntegrationTests).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task FillFactor_IsDeclaredByBothSchemaGeneratorsAsync() {
    var (efSql, schema) = await _bothAsync("[PerspectiveTableStorage(FillFactor = 90)]");

    foreach (var sql in new[] { efSql, schema }) {
      await Assert.That(sql).Contains(MARKER + "wh_per_filter fillfactor=90");
      await Assert.That(sql).DoesNotContain("wh_per_filter SET (fillfactor");
    }
  }

  /// <summary>Issue #1747: a property removed from the attribute is left out of the marker, so the reconcile resets it.</summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ARemovedSetting_IsLeftOutOfBothMarkersAsync() {
    var (efSql, schema) = await _bothAsync("[PerspectiveTableStorage(FillFactor = -1, AutovacuumVacuumThreshold = 500)]");

    foreach (var sql in new[] { efSql, schema }) {
      await Assert.That(sql).Contains(MARKER + "wh_per_filter autovacuum_vacuum_threshold=500");
      await Assert.That(sql).DoesNotContain(MARKER + "wh_per_filter fillfactor");
    }
  }

  /// <summary>A model without the attribute still gets a marker, with nothing declared, so its table is governed too.</summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task ATableWithoutTheAttribute_GetsAMarkerDeclaringNothingAsync() {
    await Assert.That(ColumnStorageSql.ForTable("wh_per_t", null).Single()).IsEqualTo(MARKER + "wh_per_t");
  }

  /// <summary>
  /// The marker holds only the governed parameters: <c>toast_tuple_target</c> and compression keep their statements in the
  /// schema SQL and are never reset.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task ForTable_KeepsTheToastTargetStatement_OutsideTheMarkerAsync() {
    var statements = ColumnStorageSql.ForTable("t", new TableStorageInfo("lz4", 512, FillFactor: 80)).ToList();

    await Assert.That(statements.Count).IsEqualTo(3);
    await Assert.That(statements[0]).Contains("SET COMPRESSION lz4;");
    await Assert.That(statements[1]).Contains("SET (toast_tuple_target = 512);");
    await Assert.That(statements[2]).IsEqualTo(MARKER + "t fillfactor=80");
  }

  private static async Task<(string EfSql, string Schema)> _bothAsync(string attribute) {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]", attribute,
      StringComparison.Ordinal);
    var registration = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var efSql = registration.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    var schema = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source), "PerspectiveSchemas.g.sql.cs") ?? "";
    return (efSql, schema);
  }

  private static PhysicalFieldInfo _field() =>
    new("P", "p", "string", IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);
}
