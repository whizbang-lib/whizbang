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
    await Assert.That(ColumnStorageSql.ForTable("t", null)).IsEmpty();
    await Assert.That(ColumnStorageSql.ForTable("t", new TableStorageInfo(null, null))).IsEmpty();
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

    await Assert.That(statements.Count).IsEqualTo(6);
    await Assert.That(statements[1]).Contains("SET (autovacuum_vacuum_scale_factor = 0.5);");
    await Assert.That(statements[5]).Contains("SET (toast.autovacuum_vacuum_scale_factor = 1E-05);")
      .Because("a scale factor is written in the invariant round-trip form, which PostgreSQL parses and stores as written.");
  }

  private static PhysicalFieldInfo _field() =>
    new("P", "p", "string", IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);
}
