using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// An enumeration promoted to a physical column is stored as its underlying number. Every generator that
/// describes the column has to agree on that: the schema DDL (both the core schema generator and the EF Core
/// service registration), the EF Core model (column type plus an explicit number conversion), and the runner's
/// physical-field registration (which carries the scalar type so a collective binds the same number). An
/// existing column created as text before this change is never altered: the schema pass warns and names the
/// migration instead.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class EnumPhysicalFieldGenerationTests {
  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public enum Stage { Draft, Open, Closed }
    public enum Tiny : byte { A, B }

    public record TicketEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record TicketModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public Stage Stage { get; init; }

      [PhysicalField]
      public Tiny? Size { get; init; }

      [PhysicalField(ColumnType = "jsonb")]
      public System.Collections.Generic.List<string>? Tags { get; init; }
    }

    public class TicketPerspective : IPerspectiveFor<TicketModel, TicketEvent> {
      public TicketModel Apply(TicketModel currentData, TicketEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SchemaGenerator_EnumColumn_IsTheUnderlyingIntegerTypeAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(MODEL);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains("stage INTEGER");
    await Assert.That(schema).Contains("size SMALLINT")
      .Because("A byte-backed enum widens to the smallest signed Postgres integer that holds it.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EFCoreModel_EnumShadowProperty_IsAnIntegerColumnWithANumberConversionAsync() {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(MODEL);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "WhizbangModelBuilderExtensions.g.cs") ?? "";

    var lines = generated.Split('\n').Select(l => l.Trim()).ToList();
    var stage = lines.IndexOf("entity.Property<TestApp.Stage>(\"stage\")");
    await Assert.That(stage).IsGreaterThan(-1);
    await Assert.That(lines[stage + 2]).IsEqualTo(".HasColumnType(\"integer\")");
    await Assert.That(lines[stage + 3]).IsEqualTo(".HasConversion<global::System.Int32>();")
      .Because("Without an explicit conversion a text column made EF store the enum's name; the column holds the number.");
    var size = lines.IndexOf("entity.Property<TestApp.Tiny?>(\"size\")");
    await Assert.That(size).IsGreaterThan(-1);
    await Assert.That(lines[size + 2]).IsEqualTo(".HasColumnType(\"smallint\")");
    await Assert.That(lines[size + 3]).IsEqualTo(".HasConversion<global::System.Int16>();");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ServiceRegistration_EnumColumn_IsAddedAsAnInteger_AndATextColumnIsFlaggedNotAlteredAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS stage INTEGER;");
    await Assert.That(sql).Contains("RAISE WARNING")
      .Because("A column created as text before enums were stored as numbers must be flagged, with the migration to run.");
    await Assert.That(sql).Contains("ALTER COLUMN stage TYPE INTEGER USING");
    var alterLines = sql.Split('\n').Where(l => l.Contains("ALTER COLUMN stage", StringComparison.Ordinal)).ToList();
    await Assert.That(alterLines).IsNotEmpty();
    await Assert.That(alterLines.All(l => l.Contains("RAISE WARNING", StringComparison.Ordinal))).IsTrue()
      .Because("The migration is only named in the warning; the schema pass never alters an existing column's type itself.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_EnumAndJsonbFields_RegisterTheirScalarAndColumnTypeAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(MODEL);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "TicketPerspectiveRunner.g.cs") ?? "";

    await Assert.That(runner).Contains(
      "\"Stage\", \"stage\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: false, scalarType: typeof(global::System.Int32));");
    await Assert.That(runner).Contains(
      "\"Size\", \"size\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: false, scalarType: typeof(global::System.Int16));");
    await Assert.That(runner).Contains(
      "\"Tags\", \"tags\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: false, columnType: \"jsonb\");");
  }
}
