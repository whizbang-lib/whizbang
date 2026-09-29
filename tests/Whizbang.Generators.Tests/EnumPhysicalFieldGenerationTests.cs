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
/// existing column created as text before this change is converted by a generated rewrite built from the enum's
/// members, which for a <c>[Flags]</c> enumeration also decodes combined names.
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
    [Flags] public enum Access { None = 0, Read = 1, Write = 2 }

    public record TicketEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record TicketModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public Stage Stage { get; init; }

      [PhysicalField]
      public Tiny? Size { get; init; }

      [PhysicalField]
      public Access? Access { get; init; }

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

  private const string WIDE_MODEL = """
    using System;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public enum Counted : uint { A, B }
    public enum Huge : ulong { A, B }

    public record MeterEvent : IEvent;

    public record MeterModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public Counted Counted { get; init; }

      [PhysicalField]
      public Huge? Huge { get; init; }
    }

    public class MeterPerspective : IPerspectiveFor<MeterModel, MeterEvent> {
      public MeterModel Apply(MeterModel currentData, MeterEvent @event) => currentData;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SchemaGenerator_UnsignedEnumColumns_WidenToTheNextSignedTypeOrNumericAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(WIDE_MODEL);
    var schema = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs") ?? "";

    await Assert.That(schema).Contains("counted BIGINT")
      .Because("A uint-backed enum does not fit a signed integer, so it widens to bigint.");
    await Assert.That(schema).Contains("huge DECIMAL")
      .Because("No Postgres integer holds every ulong, so a ulong-backed enum is stored as decimal (numeric).");
  }

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
  public async Task ServiceRegistration_EnumColumn_IsAddedAsAnInteger_AndATextColumnIsRewrittenByTheRewritePhaseAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    var sql = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    await Assert.That(sql).Contains("ADD COLUMN IF NOT EXISTS stage INTEGER;");
    await Assert.That(sql).Contains(
      "(\"enum-column:wh_per_ticket.stage\", global::Whizbang.Data.Postgres.EnumColumnRewriteSql.Build(\"testapp\", \"wh_per_ticket\", \"stage\", \"Stage\", \"INTEGER\", new (string Name, string Value)[] { (\"Draft\", \"0\"), (\"Open\", \"1\"), (\"Closed\", \"2\") }))")
      .Because("The generator writes the name-to-number mapping from the enum's own members, for the rewrite phase to apply.");
    await Assert.That(sql).Contains(
      "GetPhysicalColumnRewrites()")
      .Because("The initializer hands the physical-column rewrites to the same stored-format phase as the temporal ones.");
    await Assert.That(sql).Contains("\"SMALLINT\", new (string Name, string Value)[] { (\"A\", \"0\"), (\"B\", \"1\") }")
      .Because("A nullable byte-backed enum is converted to its smallint column the same way.");
    await Assert.That(sql).Contains(
      "(\"enum-column:wh_per_ticket.access\", global::Whizbang.Data.Postgres.EnumColumnRewriteSql.BuildFlags(\"testapp\", \"wh_per_ticket\", \"access\", \"Access\", \"INTEGER\", new (string Name, string Value)[] { (\"None\", \"0\"), (\"Read\", \"1\"), (\"Write\", \"2\") }))")
      .Because("A [Flags] enumeration's rewrite also decodes combined names (\"Read, Write\") into the bitwise OR.");
    await Assert.That(sql).DoesNotContain("EnumColumnRewriteSql.BuildFlags(\"testapp\", \"wh_per_ticket\", \"stage\"")
      .Because("Only an enumeration marked [Flags] gets the combined-name decoding.");
    await Assert.That(sql).DoesNotContain("enum-column:wh_per_ticket.tags")
      .Because("Only an enumeration's column is rewritten.");
    await Assert.That(sql).DoesNotContain("holds the enumeration")
      .Because("A text enum column is converted, not flagged for an operator.");
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
