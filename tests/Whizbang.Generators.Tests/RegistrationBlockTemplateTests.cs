// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;
using TemplateUtilities = shared::Whizbang.Generators.Shared.Utilities.TemplateUtilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2382: a DbContext group's registration block comes from snippets in <c>EFCoreSnippets.cs</c>, one per registration
/// kind, as the perspective and infrastructure registrations already did, rather than from string concatenation in the
/// generator. The move changes no byte of the output: <c>Snapshots/EFCoreModelRegistration.snapshot.txt</c> holds the
/// registration file the generator emitted for a source that exercises every kind before the move, and the output must
/// still equal it.
/// </summary>
/// <remarks>
/// Regenerate the snapshot, when a change to the output is intended, with <c>WHIZBANG_UPDATE_SNAPSHOTS=1</c>; the diff
/// is the review.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/Snippets/EFCoreSnippets.cs</code-under-test>
[Category("SourceGenerators")]
public partial class RegistrationBlockTemplateTests {
  private const string SNAPSHOT_RESOURCE = "Whizbang.Generators.Tests.Snapshots.EFCoreModelRegistration.snapshot.txt";
  private const string SNIPPETS_NAMESPACE = "Whizbang.Data.EFCore.Postgres.Generators.Templates.Snippets";

  /// <summary>Every registration kind the block emits, each a region of its own.</summary>
  private static readonly string[] _registrationRegions = [
    "REGISTER_DBCONTEXT_GROUP_SNIPPET",
    "REGISTER_PHYSICAL_FIELD_SNIPPET",
    "REGISTER_SEARCH_FIELD_SNIPPET",
    "REGISTER_JSON_INDEX_SNIPPET",
    "REGISTER_MATERIALIZATION_HYDRATOR_SNIPPET",
    "REGISTER_CHANGE_TRACKER_HYDRATOR_SNIPPET",
    "REGISTER_SPLIT_DOCUMENT_SNIPPET",
    "REGISTER_STREAM_ID_SNIPPET",
    "REGISTER_DATA_COALESCER_SNIPPET",
    "REGISTER_MULTI_LENS_QUERY_SNIPPET",
    "REGISTER_MULTI_LENS_TABLE_NAME_SNIPPET",
    "REGISTER_MANAGED_OBJECT_MANIFEST_SNIPPET",
  ];

  /// <summary>One model per registration kind: promoted, searched and indexed fields, a vector, a Split record, a Split
  /// class copied into a new instance, a class assigned in place with a collection to coalesce, a stream id and a
  /// two-model lens query.</summary>
  private const string SOURCE = """
    #nullable enable
    using System;
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Lenses;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record TestEvent : IEvent;

    public record ItemModel {
      [StreamId]
      public Guid Id { get; init; }

      [PhysicalField]
      [Indexed]
      [Indexed(IndexKinds.Search)]
      public string? Name { get; init; }

      [PhysicalField]
      [Indexed(IndexKinds.Substring, caseInsensitive: true)]
      [Indexed(IndexKinds.Ordered, caseInsensitive: true)]
      public string? Code { get; init; }

      [PhysicalField]
      public int Rank { get; init; }

      [Indexed]
      public string Status { get; init; } = "";
    }

    public class ItemPerspective : IPerspectiveFor<ItemModel, TestEvent> {
      public ItemModel Apply(ItemModel currentData, TestEvent @event) => currentData;
    }

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

    public class TicketPerspective : IPerspectiveFor<TicketModel, TestEvent> {
      public TicketModel Apply(TicketModel currentData, TestEvent @event) => currentData;
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class CaseModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int? Size { get; set; }

      public string? Note { get; set; }
    }

    public class CasePerspective : IPerspectiveFor<CaseModel, TestEvent> {
      public CaseModel Apply(CaseModel currentData, TestEvent @event) => currentData;
    }

    public record LineItem {
      public string Sku { get; init; } = "";
    }

    public class OrderModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public decimal Total { get; set; }

      public List<LineItem> Lines { get; set; } = [];
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, TestEvent> {
      public OrderModel Apply(OrderModel currentData, TestEvent @event) => currentData;
    }

    public class OrderReport {
      public OrderReport(ILensQuery<OrderModel, ItemModel> query) { }
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [Test]
  public async Task EachRegistrationKind_HasASnippetOfItsOwnAsync() {
    var assembly = typeof(EFCoreServiceRegistrationGenerator).Assembly;

    var missing = _registrationRegions
      .Where(region => TemplateUtilities.ExtractSnippet(assembly, "EFCoreSnippets.cs", region, SNIPPETS_NAMESPACE)
        .StartsWith("// ERROR:", StringComparison.Ordinal))
      .ToList();

    await Assert.That(missing).IsEmpty()
      .Because("core principle 5 of the template system: emitted code comes from templates, one snippet per kind");
  }

  [Test]
  public async Task TheRegistrationFile_IsByteForByteTheSnapshotAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(SOURCE);
    var generated = _normalized(
      result.GeneratedSources.Single(s => s.HintName == "EFCoreModelRegistration.g.cs").SourceText.ToString());

    if (Environment.GetEnvironmentVariable("WHIZBANG_UPDATE_SNAPSHOTS") == "1") {
      await File.WriteAllTextAsync(_snapshotPath(), generated);
    }

    await Assert.That(generated).Contains("StreamIdRegistry.Register<global::TestApp.ItemModel>")
      .Because("the source must reach every kind, or an equal snapshot proves nothing about it");
    await Assert.That(generated).Contains("SplitDocumentRegistry.Register<global::TestApp.CaseModel>");
    await Assert.That(generated).Contains("ILensQuery<global::TestApp.OrderModel, global::TestApp.ItemModel>");
    await Assert.That(generated).Contains("var data = row.Data;");
    await Assert.That(generated).IsEqualTo(_snapshot())
      .Because("moving the block onto templates must not change the generated code (#2382)");
  }

  /// <summary>
  /// The two things that differ by where the generator runs and are not the block's doing: the line break AppendLine
  /// writes is the platform's (the snapshot is checked out with LF everywhere), and the header names the generator's
  /// version.
  /// </summary>
  private static string _normalized(string generated) =>
    _generatorVersion().Replace(generated.Replace("\r\n", "\n", StringComparison.Ordinal), "at generator v{GENERATOR_VERSION}");

  [System.Text.RegularExpressions.GeneratedRegex(@"at generator v\S+")]
  private static partial System.Text.RegularExpressions.Regex _generatorVersion();

  private static string _snapshot() {
    using var stream = typeof(RegistrationBlockTemplateTests).Assembly.GetManifestResourceStream(SNAPSHOT_RESOURCE)
      ?? throw new InvalidOperationException($"The snapshot resource {SNAPSHOT_RESOURCE} is missing");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  // Found from the test assembly's location, since the build maps source paths: walk up to the repository root.
  private static string _snapshotPath() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      var candidate = Path.Combine(dir, "tests", "Whizbang.Generators.Tests");
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core")) && Directory.Exists(candidate)) {
        return Path.Combine(candidate, "Snapshots", "EFCoreModelRegistration.snapshot.txt");
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the whizbang repository root from AppContext.BaseDirectory");
  }
}
