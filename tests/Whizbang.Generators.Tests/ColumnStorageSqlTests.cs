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

  private static PhysicalFieldInfo _field() =>
    new("P", "p", "string", IsIndexed: false, IsUnique: false, MaxLength: null, IsVector: false,
      VectorDimensions: null, VectorDistanceMetric: null, VectorIndexType: null, VectorIndexLists: null);
}
