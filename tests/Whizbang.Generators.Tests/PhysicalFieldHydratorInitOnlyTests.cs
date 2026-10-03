using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The EF Core registration generator emits two hydrators per model with promoted fields, one for the
/// materialization interceptor and one for the change tracker. Each copies the promoted columns into the
/// model a row materialized. A record is copied with a <c>with</c> expression, which sets an <c>init</c>-only
/// property as well as a settable one, and a class is assigned in place, skipping any property it cannot
/// assign after construction. Assigning an <c>init</c>-only property is CS8852, so before issue #982 a record
/// model documented with <c>init</c> properties did not compile in any storage mode.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
[Category("SourceGenerators")]
public class PhysicalFieldHydratorInitOnlyTests {
  private const string REGISTRATION_FILE = "EFCoreModelRegistration.g.cs";

  private const string RECORD_SOURCE = """
    #nullable enable
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TicketEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public record TicketModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int? Size { get; init; }

      [PhysicalField]
      public int Priority { get; set; }

      [PhysicalField]
      public string Code => "fixed";

      [VectorField(3)]
      public float[]? Embedding { get; init; }
    }

    public class TicketPerspective : IPerspectiveFor<TicketModel, TicketEvent> {
      public TicketModel Apply(TicketModel currentData, TicketEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private const string CLASS_SOURCE = """
    #nullable enable
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TicketEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Extracted)]
    public class TicketModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int? Size { get; set; }

      [PhysicalField]
      public string Code => "fixed";

      [VectorField(3)]
      public float[]? Embedding { get; set; }
    }

    public class TicketPerspective : IPerspectiveFor<TicketModel, TicketEvent> {
      public TicketModel Apply(TicketModel currentData, TicketEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private const string SPLIT_CLASS_SOURCE = """
    #nullable enable
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TicketEvent : IEvent;

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class TicketModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int? Size { get; set; }

      [PhysicalField]
      public string Code => "fixed";

      public string? Note { get; set; }
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class UncopyableModel {
      public UncopyableModel(Guid id) { Id = id; }

      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int? Size { get; set; }
    }

    public class TicketPerspective : IPerspectiveFor<TicketModel, TicketEvent> {
      public TicketModel Apply(TicketModel currentData, TicketEvent @event) => currentData;
    }

    public class UncopyablePerspective : IPerspectiveFor<UncopyableModel, TicketEvent> {
      public UncopyableModel Apply(UncopyableModel currentData, TicketEvent @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _registrationAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var file = result.GeneratedSources.FirstOrDefault(s => s.HintName == REGISTRATION_FILE);
    await Assert.That(file).IsNotNull();
    return file!.SourceText.ToString();
  }

  /// <summary>The generated lines, trimmed, so an assertion names a whole statement.</summary>
  private static List<string> _lines(string generated) =>
    [.. generated.Split('\n').Select(l => l.Trim())];

  [Test]
  public async Task Record_ChangeTrackerHydrator_CopiesTheColumnsWithAWithExpressionAsync() {
    var lines = _lines(await _registrationAsync(RECORD_SOURCE));

    await Assert.That(lines).Contains("var _status = (string)entry.Property(\"status\").CurrentValue!;");
    await Assert.That(lines).Contains("var _size = (int?)entry.Property(\"size\").CurrentValue;");
    await Assert.That(lines).Contains("var _priority = (int)entry.Property(\"priority\").CurrentValue!;");
    await Assert.That(lines).Contains("var _embedding = (global::Pgvector.Vector?)entry.Property(\"embedding\").CurrentValue;");
    await Assert.That(lines).Contains(
        "row.Data = row.Data with { Status = _status, Size = _size ?? row.Data.Size, Priority = _priority, "
        + "Embedding = _embedding is not null ? _embedding.ToArray() : row.Data.Embedding };")
      .Because("a with expression sets an init-only property; a null column keeps the value the document holds");
  }

  [Test]
  public async Task Record_MaterializationHydrator_CopiesTheColumnsWithAWithExpressionAsync() {
    var lines = _lines(await _registrationAsync(RECORD_SOURCE));

    await Assert.That(lines).Contains("var _status = materializationData.GetPropertyValue<string>(\"status\");");
    await Assert.That(lines).Contains("var _size = materializationData.GetPropertyValue<int?>(\"size\");");
    await Assert.That(lines).Contains("var _embedding = materializationData.GetPropertyValue<global::Pgvector.Vector?>(\"embedding\");");
    await Assert.That(lines.Count(l => l.StartsWith("row.Data = row.Data with {", StringComparison.Ordinal))).IsEqualTo(2)
      .Because("both hydrators copy the record the same way");
  }

  [Test]
  public async Task Record_Hydrators_NeverAssignAPropertyOfTheModelAsync() {
    var generated = await _registrationAsync(RECORD_SOURCE);

    await Assert.That(generated).DoesNotContain("row.Data.Status =");
    await Assert.That(generated).DoesNotContain("row.Data.Priority =")
      .Because("a record is copied, even where a property is settable, so the copy is one construction");
    await Assert.That(generated).DoesNotContain("Code = _code")
      .Because("a property with no setter is computed, not stored, so there is nothing to copy into it");
    await Assert.That(generated).DoesNotContain("var _code =");
  }

  [Test]
  public async Task Class_Hydrators_AssignTheSettablePropertiesAndSkipTheRestAsync() {
    var generated = await _registrationAsync(CLASS_SOURCE);
    var lines = _lines(generated);

    await Assert.That(lines.Count(l => l == "row.Data.Size = _size ?? row.Data.Size;")).IsEqualTo(2);
    await Assert.That(lines.Count(l => l == "row.Data.Embedding = _embedding is not null ? _embedding.ToArray() : row.Data.Embedding;")).IsEqualTo(2);
    await Assert.That(generated).DoesNotContain("row.Data.Status =")
      .Because("an init-only property of a class cannot be assigned once the instance exists; its document holds it");
    await Assert.That(generated).DoesNotContain("var _status =");
    await Assert.That(generated).DoesNotContain("row.Data.Code =");
    await Assert.That(generated).DoesNotContain("row.Data = row.Data with")
      .Because("a class has no with expression");
  }

  [Test]
  public async Task SplitClass_WithAnInitOnlyPromotedField_IsHydratedThroughACopyAsync() {
    var lines = _lines(await _registrationAsync(SPLIT_CLASS_SOURCE));

    await Assert.That(lines.Count(l => l ==
      "row.Data = new global::TestApp.TicketModel { Id = row.Data.Id, Status = _status, Size = _size ?? row.Data.Size, Note = row.Data.Note };"))
      .IsEqualTo(2)
      .Because("issue #1002: a class sets an init-only property only while an instance is created, so both hydrators copy the model");
    await Assert.That(lines).Contains("var _status = (string)entry.Property(\"status\").CurrentValue!;");
    await Assert.That(lines).Contains("var _status = materializationData.GetPropertyValue<string>(\"status\");");
    await Assert.That(lines).DoesNotContain("row.Data.Status = _status;");
  }

  [Test]
  public async Task SplitClass_ThatCannotBeCopied_KeepsTheInPlaceCopyOfItsSettableFieldsAsync() {
    var lines = _lines(await _registrationAsync(SPLIT_CLASS_SOURCE));

    await Assert.That(lines.Count(l => l == "row.Data.Size = _size ?? row.Data.Size;")).IsEqualTo(2)
      .Because("the runner reports the model (WHIZ808); the hydrators still compile, assigning what a class can");
    await Assert.That(lines).DoesNotContain("row.Data = new global::TestApp.UncopyableModel {");
  }
}
