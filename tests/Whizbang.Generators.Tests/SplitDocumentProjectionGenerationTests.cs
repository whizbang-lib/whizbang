// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2304: a query whose result is the projected document of a Split model (<c>Select(r =&gt; r.Data)</c>, the shape an
/// <c>IQueryable</c> composer returns) materializes only the document, which in Split mode no longer holds the promoted
/// fields. The registration generator emits, for each model with a Split field, the projection the query is rewritten to
/// (the document with every promoted column read beside it) and the copy that puts the columns into the document, by the
/// copy rules the hydrators follow: a <c>with</c> expression for a record, a new instance for a class with an
/// <c>init</c>-only promoted field, assignment in place otherwise.
/// </summary>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
[Category("SourceGenerators")]
public class SplitDocumentProjectionGenerationTests {
  private const string REGISTRATION_FILE = "EFCoreModelRegistration.g.cs";
  private const string REGISTRY = "Whizbang.Data.EFCore.Postgres.SplitDocumentRegistry";

  private const string SPLIT_RECORD_SOURCE = """
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

  private const string EXTRACTED_CLASS_SOURCE = """
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

  private static async Task<List<string>> _linesAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var file = result.GeneratedSources.FirstOrDefault(s => s.HintName == REGISTRATION_FILE);
    await Assert.That(file).IsNotNull();
    return [.. file!.SourceText.ToString().Split('\n').Select(l => l.Trim())];
  }

  [Test]
  public async Task SplitRecord_RegistersAProjectionThatReadsEveryPromotedColumn_AndACopyWithAWithExpressionAsync() {
    var lines = await _linesAsync(SPLIT_RECORD_SOURCE);

    await Assert.That(lines).Contains($"{REGISTRY}.Register<global::TestApp.TicketModel>(");
    await Assert.That(lines).Contains(
        $"r => {REGISTRY}.WithColumns(r.Data, new object?[] {{ "
        + "global::Microsoft.EntityFrameworkCore.EF.Property<string>(r, \"status\"), "
        + "global::Microsoft.EntityFrameworkCore.EF.Property<int?>(r, \"size\"), "
        + "global::Microsoft.EntityFrameworkCore.EF.Property<int>(r, \"priority\"), "
        + "global::Microsoft.EntityFrameworkCore.EF.Property<global::Pgvector.Vector?>(r, \"embedding\") }),")
      .Because("the query reads each promoted column the copy fills beside the document, in the order the copy reads them; "
        + "a computed field is never copied into, so its column is not read");
    await Assert.That(lines).Contains($"var row = new {REGISTRY}.Document<global::TestApp.TicketModel>(data);");
    await Assert.That(lines).Contains("var _status = (string)columns[0]!;");
    await Assert.That(lines).Contains("var _size = (int?)columns[1];");
    await Assert.That(lines).Contains("var _embedding = (global::Pgvector.Vector?)columns[3];");
    await Assert.That(lines).Contains(
        "row.Data = row.Data with { Status = _status, Size = _size ?? row.Data.Size, Priority = _priority, "
        + "Embedding = _embedding is not null ? _embedding.ToArray() : row.Data.Embedding };")
      .Because("the document is filled by the copy rules the hydrators follow");
    await Assert.That(lines).Contains("return row.Data;");
  }

  [Test]
  public async Task SplitClass_WithAnInitOnlyPromotedField_RegistersACopyIntoANewInstanceAsync() {
    var lines = await _linesAsync(SPLIT_CLASS_SOURCE);

    await Assert.That(lines.Count(l => l == $"{REGISTRY}.Register<global::TestApp.TicketModel>(")).IsEqualTo(1);
    await Assert.That(lines.Count(l => l ==
        "row.Data = new global::TestApp.TicketModel { Id = row.Data.Id, Status = _status, Size = _size ?? row.Data.Size, Note = row.Data.Note };"))
      .IsEqualTo(3).Because("the projection's copy and both hydrators copy a class with an init-only promoted field the same way");
  }

  [Test]
  public async Task ExtractedModel_RegistersNoProjection_ItsDocumentAlreadyHoldsTheFieldsAsync() {
    var lines = await _linesAsync(EXTRACTED_CLASS_SOURCE);

    await Assert.That(lines.Any(l => l.Contains(REGISTRY, StringComparison.Ordinal))).IsFalse()
      .Because("an Extracted document keeps every promoted field, so its projection needs no columns");
  }
}
