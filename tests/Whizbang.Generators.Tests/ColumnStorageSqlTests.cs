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
    // A table declaring nothing sets nothing; its only statement is the reset of the governed parameters (#1747).
    await Assert.That(ColumnStorageSql.ForTable("t", null).Single()).Contains("RESET (");
    await Assert.That(ColumnStorageSql.ForTable("t", new TableStorageInfo(null, null)).Single()).Contains("RESET (");
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

  /// <summary>
  /// Issue #1584: each autovacuum setting a model declares reaches both schema generators as a statement that sets
  /// the table's storage parameter, and a TOAST setting is compared against the TOAST relation, where PostgreSQL
  /// keeps it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AutovacuumSettings_ReachBothSchemaGeneratorsAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]", AUTOVACUUM,
      StringComparison.Ordinal);
    var registration = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var efSql = registration.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    var schema = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source), "PerspectiveSchemas.g.sql.cs") ?? "";

    foreach (var sql in new[] { efSql, schema }) {
      await Assert.That(sql).Contains("SET (autovacuum_vacuum_threshold = 500);");
      await Assert.That(sql).Contains("SET (autovacuum_vacuum_scale_factor = 0);");
      await Assert.That(sql).Contains("SET (autovacuum_vacuum_insert_threshold = 2000);");
      await Assert.That(sql).Contains("SET (autovacuum_vacuum_insert_scale_factor = 0.05);");
      await Assert.That(sql).Contains("SET (toast.autovacuum_vacuum_threshold = 300);");
      await Assert.That(sql).Contains("SET (toast.autovacuum_vacuum_scale_factor = 0.01);");
    }
  }

  [Test]
  public async Task NegativeAutovacuumSettings_KeepTheServerDefaultsAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]",
      "[PerspectiveTableStorage(AutovacuumVacuumThreshold = -1, AutovacuumVacuumScaleFactor = -1, AutovacuumInsertThreshold = -1, "
      + "AutovacuumInsertScaleFactor = -0.5, ToastAutovacuumVacuumThreshold = -5, ToastAutovacuumVacuumScaleFactor = -1)]",
      StringComparison.Ordinal);
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).DoesNotContain("SET (autovacuum_");
    await Assert.That(sql).DoesNotContain("SET (toast.autovacuum_");
  }

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

  [Test]
  public async Task ForTable_EmitsEachDeclaredParameterInOrderAsync() {
    var storage = new TableStorageInfo(null, null, AutovacuumVacuumThreshold: 1, AutovacuumVacuumScaleFactor: 0.5,
      AutovacuumInsertThreshold: 2, AutovacuumInsertScaleFactor: 0, ToastAutovacuumVacuumThreshold: 3,
      ToastAutovacuumVacuumScaleFactor: 1e-05);

    var statements = ColumnStorageSql.ForTable("t", storage).ToList();

    await Assert.That(statements.Count).IsEqualTo(7);
    await Assert.That(statements[1]).Contains("SET (autovacuum_vacuum_scale_factor = 0.5);");
    await Assert.That(statements[5]).Contains("SET (toast.autovacuum_vacuum_scale_factor = 1E-05);")
      .Because("a scale factor is written in the invariant round-trip form, which PostgreSQL parses and stores as written.");
    await Assert.That(statements[6]).Contains("p.name IN ('fillfactor')")
      .Because("the undeclared fillfactor is reset after the declared parameters are set (#1747).");
  }

  /// <summary>
  /// Issue #1592: a declared fillfactor reaches both schema generators as a statement that sets it when the table
  /// does not already hold it, and queues a rewrite of the table, because a changed fillfactor applies only to the
  /// pages written from then on.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task FillFactor_ReachesBothSchemaGenerators_WithARewriteRequestAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]",
      "[PerspectiveTableStorage(FillFactor = 90)]", StringComparison.Ordinal);
    var registration = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var efSql = registration.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    var schema = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source), "PerspectiveSchemas.g.sql.cs") ?? "";

    // Both generators name the table from the model (#1719); the request names the table the statement altered.
    foreach (var (sql, table) in new[] { (efSql, "wh_per_filter"), (schema, "wh_per_filter") }) {
      await Assert.That(sql).Contains($"{table} SET (fillfactor = 90);");
      await Assert.That(sql).Contains($"wh_request_table_rewrite('{table}');")
        .Because("the rewrite request names the bare table, as the rewriter lists the tables it measures.");
    }
  }

  [Test]
  public async Task FillFactor_NotDeclared_IsResetNotSetAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]",
      "[PerspectiveTableStorage(FillFactor = -1, AutovacuumVacuumThreshold = 500)]", StringComparison.Ordinal);
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("SET (autovacuum_vacuum_threshold = 500);");
    await Assert.That(sql).DoesNotContain(".wh_per_filter SET (fillfactor")
      .Because("the embedded framework migrations set a fillfactor on their own tables only.");
    await Assert.That(sql).Contains("IF 'fillfactor' = ANY (v_reset) AND pg_relation_size(")
      .Because("an undeclared fillfactor is reset, and its rewrite request sits only in the branch that reset it (#1747).");
  }

  /// <summary>
  /// The rewrite is queued only inside the branch that changes the fillfactor, and only when the table has pages
  /// to rewrite: a restart that finds the value in place queues nothing, and neither does a table just created.
  /// </summary>
  [Test]
  public async Task SetFillFactor_QueuesARewriteOnlyWhenTheValueChangesOnATableWithPagesAsync() {
    var sql = ColumnStorageSql.SetFillFactor("\"svc\".wh_per_order", 85);

    await Assert.That(sql).Contains("'fillfactor=85' = ANY (coalesce(reloptions, ARRAY[]::text[]))");
    await Assert.That(sql).Contains("ALTER TABLE \"svc\".wh_per_order SET (fillfactor = 85);");
    await Assert.That(sql).Contains("IF pg_relation_size('\"svc\".wh_per_order'::regclass) > 0 THEN");
    await Assert.That(sql).Contains("PERFORM \"svc\".wh_request_table_rewrite('wh_per_order');")
      .Because("the request goes to the framework function in the table's own schema.");
    await Assert.That(sql.IndexOf("PERFORM", StringComparison.Ordinal))
      .IsGreaterThan(sql.IndexOf("ALTER TABLE", StringComparison.Ordinal))
      .Because("the request is inside the branch that found a different value, never before the comparison.");
  }

  [Test]
  public async Task SetFillFactor_OnAnUnqualifiedTable_CallsTheFunctionUnqualifiedAsync() {
    var sql = ColumnStorageSql.SetFillFactor("wh_per_order", 70);

    await Assert.That(sql).Contains("PERFORM wh_request_table_rewrite('wh_per_order');");
  }

  [Test]
  public async Task ForTable_EmitsTheFillFactorAfterTheToastTargetAsync() {
    var statements = ColumnStorageSql.ForTable("t", new TableStorageInfo(null, 512, FillFactor: 80)).ToList();

    await Assert.That(statements.Count).IsEqualTo(3);
    await Assert.That(statements[0]).Contains("SET (toast_tuple_target = 512);");
    await Assert.That(statements[1]).Contains("SET (fillfactor = 80);");
    await Assert.That(statements[2]).Contains("RESET (").And.DoesNotContain("'fillfactor'")
      .Because("the six undeclared autovacuum parameters are reset; the declared fillfactor is not (#1747).");
  }

  private const string GOVERNED_LIST =
    "'fillfactor', 'autovacuum_vacuum_threshold', 'autovacuum_vacuum_scale_factor', 'autovacuum_vacuum_insert_threshold', "
    + "'autovacuum_vacuum_insert_scale_factor', 'toast.autovacuum_vacuum_threshold', 'toast.autovacuum_vacuum_scale_factor'";

  /// <summary>
  /// Issue #1747 (WS-4 decision 3): a model that stops declaring an autovacuum or fillfactor setting gets it reset,
  /// so the table matches the code. Both schema generators emit the reset, naming every governed parameter the
  /// model no longer declares and none it still does.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ARemovedSetting_IsResetByBothSchemaGeneratorsAsync() {
    var source = MODEL.Replace(
      "[PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]",
      "[PerspectiveTableStorage(AutovacuumVacuumThreshold = 500, ToastAutovacuumVacuumScaleFactor = 0.01)]",
      StringComparison.Ordinal);
    var registration = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var efSql = registration.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    var schema = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source), "PerspectiveSchemas.g.sql.cs") ?? "";
    const string UNDECLARED = "'fillfactor', 'autovacuum_vacuum_scale_factor', 'autovacuum_vacuum_insert_threshold', "
      + "'autovacuum_vacuum_insert_scale_factor', 'toast.autovacuum_vacuum_threshold'";

    foreach (var sql in new[] { efSql, schema }) {
      await Assert.That(sql).Contains($"p.name IN ({UNDECLARED})")
        .Because("each governed parameter the model no longer declares is reset, and the two it declares are not.");
      await Assert.That(sql).Contains("wh_per_filter RESET (' || array_to_string(v_reset, ', ') || ')';");
      await Assert.That(sql).Contains("SET (autovacuum_vacuum_threshold = 500);")
        .Because("the declared settings are still set as before.");
    }
  }

  /// <summary>A model without the attribute declares none of the governed parameters, so every one is reset.</summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task ATableWithoutTheAttribute_ResetsEveryGovernedParameterAsync() {
    var statements = ColumnStorageSql.ForTable("t", null).ToList();

    await Assert.That(statements.Count).IsEqualTo(1);
    await Assert.That(statements[0]).Contains($"p.name IN ({GOVERNED_LIST})");
    await Assert.That(statements[0]).Contains("PERFORM wh_request_table_rewrite('t');")
      .Because("a fillfactor reset, like a fillfactor set, applies only to the pages written from then on.");
  }

  /// <summary>
  /// The governed parameters are the ones decision 3 names, autovacuum and fillfactor. <c>toast_tuple_target</c> and
  /// any parameter the attribute cannot set (an operator's <c>autovacuum_analyze_threshold</c>, say) are never reset.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task TheReset_NamesOnlyTheGovernedParametersAsync() {
    var reset = ColumnStorageSql.ForTable("t", null).Single();

    await Assert.That(reset).DoesNotContain("toast_tuple_target");
    await Assert.That(reset).DoesNotContain("autovacuum_analyze");
    await Assert.That(reset).DoesNotContain("autovacuum_enabled");
  }

  /// <summary>A model declaring every governed parameter has nothing to reset, so no reset is emitted.</summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task EveryGovernedParameterDeclared_EmitsNoResetAsync() {
    var storage = new TableStorageInfo(null, null, AutovacuumVacuumThreshold: 1, AutovacuumVacuumScaleFactor: 0.5,
      AutovacuumInsertThreshold: 2, AutovacuumInsertScaleFactor: 0, ToastAutovacuumVacuumThreshold: 3,
      ToastAutovacuumVacuumScaleFactor: 0.1, FillFactor: 90);

    var statements = ColumnStorageSql.ForTable("t", storage).ToList();

    await Assert.That(statements.Count).IsEqualTo(7);
    await Assert.That(statements.Any(s => s.Contains("RESET", StringComparison.Ordinal))).IsFalse();
  }

  /// <summary>
  /// The reset compares the catalog first: it alters the table only when it holds one of the named parameters, on the
  /// table or on its TOAST relation, and queues a rewrite only when the fillfactor was among them on a table with pages.
  /// </summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task ResetStorageParameters_AltersOnlyWhatTheCatalogHoldsAsync() {
    var sql = ColumnStorageSql.ResetStorageParameters("\"svc\".wh_per_order", ["fillfactor", "toast.autovacuum_vacuum_threshold"]);

    await Assert.That(sql).Contains("unnest(c.reloptions)");
    await Assert.That(sql).Contains("JOIN pg_class toast ON toast.oid = c.reltoastrelid")
      .Because("PostgreSQL keeps a toast.* parameter on the TOAST relation, without the prefix.");
    await Assert.That(sql).Contains("p.name IN ('fillfactor', 'toast.autovacuum_vacuum_threshold')");
    await Assert.That(sql).Contains("IF v_reset IS NOT NULL THEN");
    await Assert.That(sql).Contains("IF 'fillfactor' = ANY (v_reset) AND pg_relation_size('\"svc\".wh_per_order'::regclass) > 0 THEN");
    await Assert.That(sql).Contains("PERFORM \"svc\".wh_request_table_rewrite('wh_per_order');");
  }

  /// <summary>Without the fillfactor among the reset parameters, no rewrite is ever queued: an autovacuum reset changes no pages.</summary>
  /// <docs>fundamentals/perspectives/physical-fields#removed-storage-settings</docs>
  [Test]
  public async Task ResetStorageParameters_WithoutTheFillFactor_QueuesNoRewriteAsync() {
    var sql = ColumnStorageSql.ResetStorageParameters("t", ["autovacuum_vacuum_threshold"]);

    await Assert.That(sql).Contains("p.name IN ('autovacuum_vacuum_threshold')");
    await Assert.That(sql).DoesNotContain("wh_request_table_rewrite");
  }

  private static PhysicalFieldInfo _field() =>
    new("P", "p", "string", IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);
}
