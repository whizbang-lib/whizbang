using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A promoted field holding an object, a list or a dictionary is a jsonb column, and every generator that
/// describes the column agrees: both schema generators create it as jsonb, the EF Core model reads and writes
/// it as one value under the persistence profile and keeps it out of the mapped document, and the runner
/// registers it as jsonb so the write paths bind it as one.
/// </summary>
/// <remarks>
/// Before this, such a field with no declared column type became a TEXT column holding the type's string
/// form, with no diagnostic, and a dictionary was refused by the mapped document altogether.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
public class PhysicalJsonbColumnGenerationTests {
  private const string MODEL = """
    using System;
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record GridLabel(string Key, string Label);
    public readonly record struct Grade(int Low, int High);
    public class Location { public string City { get; set; } = ""; }

    public record GridEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Extracted)]
    public class GridModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public Dictionary<string, string[]> GridFilter { get; set; } = new();

      [PhysicalField]
      public List<GridLabel>? Labels { get; set; }

      [PhysicalField]
      public string[] Tags { get; set; } = [];

      [PhysicalField]
      public Location? Location { get; set; }

      [PhysicalField]
      public Grade Grade { get; set; }

      [PhysicalField(ColumnType = "text")]
      public List<string>? Declared { get; set; }

      [PhysicalField]
      public DateTime Stamp { get; set; }

      [PhysicalField]
      public byte[]? Blob { get; set; }

      [PhysicalField]
      public float[]? Weights { get; set; }

      [PhysicalField]
      public TimeSpan Window { get; set; }
    }

    public class GridPerspective : IPerspectiveFor<GridModel, GridEvent> {
      public GridModel Apply(GridModel currentData, GridEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private const string CONVERTER = "global::Whizbang.Data.EFCore.Postgres.Perspectives.PerspectiveDocumentSerialization.ColumnConverterFor";

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("grid_filter")]
  [Arguments("labels")]
  [Arguments("tags")]
  [Arguments("location")]
  [Arguments("grade")]
  public async Task SchemaGenerator_NonScalarField_IsAJsonbColumnAsync(string column) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODEL);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains($"{column} jsonb", StringComparison.Ordinal);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SchemaGenerator_ScalarsAndDeclaredTypes_AreUnchangedAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODEL);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains("declared text", StringComparison.Ordinal)
      .Because("a declared column type is the author's decision and always wins.");
    await Assert.That(schema).Contains("weights REAL[]", StringComparison.Ordinal)
      .Because("an array of reals has a native column type of its own.");
    await Assert.That(schema).Contains("window TEXT", StringComparison.Ordinal)
      .Because("a framework value type is a scalar, not an object.");
  }

  // A jsonb column filtered by containment declares its GIN index portably; both drivers build it over the column.
  private static readonly string _containmentModel = MODEL.Replace(
    "public Dictionary<string, string[]> GridFilter",
    "[Indexed(IndexKinds.Containment)] public Dictionary<string, string[]> GridFilter",
    StringComparison.Ordinal);

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ServiceRegistration_ContainmentIndex_IsAGinJsonbPathOpsIndexOnTheColumnAsync() {
    await Assert.That(_containmentModel).IsNotEqualTo(MODEL).Because("the model must actually declare the index");
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_containmentModel);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("USING gin (grid_filter jsonb_path_ops)", StringComparison.Ordinal);
    await Assert.That(sql).Contains("grid_filter_gin", StringComparison.Ordinal);
  }

  [Test]
  public async Task SchemaGenerator_ContainmentIndex_IsAGinJsonbPathOpsIndexOnTheColumnAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(_containmentModel);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains("USING gin (grid_filter jsonb_path_ops)", StringComparison.Ordinal);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ServiceRegistration_NonScalarField_IsAddedAsJsonbAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS grid_filter jsonb;", StringComparison.Ordinal);
    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS labels jsonb;", StringComparison.Ordinal);
    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS stamp TIMESTAMPTZ;", StringComparison.Ordinal);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EFCoreModel_JsonbField_IsReadAndWrittenUnderThePersistenceProfileAsync() {
    var generated = _modelConfiguration();
    var lines = generated.Split('\n').Select(l => l.Trim()).ToList();

    var grid = lines.IndexOf("entity.Property<System.Collections.Generic.Dictionary<string, string[]>>(\"grid_filter\")");
    await Assert.That(grid).IsGreaterThan(-1);
    await Assert.That(lines[grid + 2]).IsEqualTo(".HasColumnType(\"jsonb\")");
    await Assert.That(lines[grid + 3])
      .IsEqualTo($".HasConversion({CONVERTER}<System.Collections.Generic.Dictionary<string, string[]>>());");

    var grade = lines.IndexOf("entity.Property<TestApp.Grade>(\"grade\")");
    await Assert.That(lines[grade + 3]).IsEqualTo($".HasConversion({CONVERTER}<TestApp.Grade>());")
      .Because("a struct is converted the same way as a class.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EFCoreModel_JsonbField_IsLeftOutOfTheMappedDocumentAsync() {
    var generated = _modelConfiguration();

    await Assert.That(generated).Contains("d.Ignore(\"GridFilter\");", StringComparison.Ordinal)
      .Because("the mapped document cannot hold a dictionary at all, and the column is where the value is read from.");
    await Assert.That(generated).Contains("d.Ignore(\"Labels\");", StringComparison.Ordinal);
    await Assert.That(generated).DoesNotContain("d.Ignore(\"Declared\");", StringComparison.Ordinal)
      .Because("only a jsonb column holds the value in the form the column converter reads.");
    await Assert.That(generated).DoesNotContain($"{CONVERTER}<System.Collections.Generic.List<string>?>", StringComparison.Ordinal);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EFCoreModel_DateTime_IsTheSameColumnTypeAsTheTableAsync() {
    var generated = _modelConfiguration();
    var lines = generated.Split('\n').Select(l => l.Trim()).ToList();

    var stamp = lines.IndexOf("entity.Property<System.DateTime>(\"stamp\")");
    await Assert.That(stamp).IsGreaterThan(-1);
    await Assert.That(lines[stamp + 2]).IsEqualTo(".HasColumnType(\"timestamptz\");")
      .Because("the table creates the column as timestamptz, and a model claiming timestamp rejects a UTC value on write.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_NonScalarField_RegistersAJsonbColumnAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(MODEL);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "GridPerspectiveRunner.g.cs") ?? "";

    await Assert.That(runner).Contains(
      "\"GridFilter\", \"grid_filter\", global::Whizbang.Core.Perspectives.FieldStorageMode.Extracted, isVector: false, columnType: \"jsonb\");");
    await Assert.That(runner).Contains(
      "\"Declared\", \"declared\", global::Whizbang.Core.Perspectives.FieldStorageMode.Extracted, isVector: false, columnType: \"text\");");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ExtractedModel_ReadsItsJsonbColumnsBackAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(MODEL);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "GridPerspectiveRunner.g.cs") ?? "";

    await Assert.That(runner).Contains("new global::Whizbang.Core.Perspectives.SplitPhysicalColumn(\"grid_filter\", false)")
      .Because("the mapped document no longer holds a jsonb field, so the model the next event is applied to reads it from its column.");
    await Assert.That(runner).DoesNotContain("SplitPhysicalColumn(\"stamp\"")
      .Because("a scalar is still in the mapped document of a model that is not Split.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ExtractedModelWithoutJsonbColumns_RegistersNoReadBackAsync() {
    // The same model with every jsonb field left in the document only.
    string[] jsonbFields = ["GridFilter", "Labels", "Tags", "Location", "Grade"];
    var lines = MODEL.Split('\n').ToList();
    for (var i = lines.Count - 2; i >= 0; i--) {
      if (lines[i].Trim() == "[PhysicalField]" && jsonbFields.Any(f => lines[i + 1].Contains($" {f} {{", StringComparison.Ordinal))) {
        lines.RemoveAt(i);
      }
    }
    var source = string.Join('\n', lines);
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "GridPerspectiveRunner.g.cs") ?? "";

    await Assert.That(runner).Contains("\"Stamp\", \"stamp\"");
    await Assert.That(runner).DoesNotContain("SplitPhysicalColumn(");
  }

  private static string _modelConfiguration() {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(MODEL);
    return GeneratorTestHelper.GetGeneratedSource(result, "WhizbangModelBuilderExtensions.g.cs") ?? "";
  }
}
