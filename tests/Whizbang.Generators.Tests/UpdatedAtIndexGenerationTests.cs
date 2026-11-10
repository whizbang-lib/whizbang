// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using JsonIndexDiscovery = shared::Whizbang.Generators.Shared.Models.JsonIndexDiscovery;
using SharedRowColumns = shared::Whizbang.Generators.Shared.Models.PerspectiveRowColumnNames;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The <c>updated_at</c> index is no longer built on every perspective table (#2087): a btree over a column every
/// update rewrites keeps the update from being HOT. Neither driver's schema builds it for a model that does not ask
/// for it, and a model that does asks the way it asks for any other index, with <c>[PerspectiveIndex]</c> naming the
/// row's system column through <see cref="Whizbang.Core.Perspectives.PerspectiveRowColumns"/>.
/// </summary>
/// <remarks>
/// The tables that declare row retention keep the index too, but that is decided from the perspective registry at
/// run time, not here, so it is covered by the reconcile's integration tests.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators.Shared/Models/JsonIndexDiscovery.cs</code-under-test>
[Category("SourceGenerators")]
public class UpdatedAtIndexGenerationTests {
  private static string _source(string modelAttribute) => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record LedgerPosted : IEvent;

    {{modelAttribute}}
    public record LedgerModel {
      [StreamId]
      public Guid LedgerId { get; init; }

      public string Status { get; init; } = string.Empty;

      public DateTimeOffset UpdatedAt { get; init; }
    }

    public class LedgerPerspective : IPerspectiveFor<LedgerModel, LedgerPosted> {
      public LedgerModel Apply(LedgerModel currentData, LedgerPosted eventData) => currentData;
    }

    [WhizbangDbContext]
    public class LedgerDbContext : DbContext {
      public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _efCoreSchemaAsync(string modelAttribute) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(modelAttribute));
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal));
    return schema.SourceText.ToString();
  }

  [RequiresAssemblyFiles()]
  private static string _dapperSchema(string modelAttribute) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(_source(modelAttribute));
    return GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveSchemas.g.sql.cs")!;
  }

  private const string EF_TABLE = "\"\"testapp\"\".wh_per_ledger";

  private static string _efEnsure(string ddl) => $"SELECT \"\"testapp\"\".wh_ensure_index($wbix${ddl}$wbix$);";

  // ========================================
  // EF Core: undeclared
  // ========================================

  /// <summary>A model that declares nothing gets no index on <c>updated_at</c>, in the tracked entry or the fallback.</summary>
  [Test]
  public async Task EFCore_AModelThatDeclaresNothing_GetsNoUpdatedAtIndexAsync() {
    var schema = await _efCoreSchemaAsync("");

    await Assert.That(schema).DoesNotContain("idx_ledger_updated_at", StringComparison.Ordinal)
      .Because("a btree on a column every update rewrites keeps every update from being HOT (#2087)");
    await Assert.That(schema).DoesNotContain("(updated_at)", StringComparison.Ordinal);
    await Assert.That(schema).Contains("idx_ledger_created_at", StringComparison.Ordinal)
      .Because("only the updated_at index changed; created_at is written once and does not block HOT");
  }

  // ========================================
  // EF Core: declared
  // ========================================

  /// <summary>Declared on its own, the index is the one the schema used to build, under the same name.</summary>
  [Test]
  public async Task EFCore_DeclaredOnItsOwn_BuildsTheIndexUnderItsFormerNameAsync() {
    var schema = await _efCoreSchemaAsync("[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]");

    await Assert.That(schema).Contains(
      _efEnsure($"CREATE INDEX IF NOT EXISTS idx_ledger_updated_at ON {EF_TABLE} (updated_at)"), StringComparison.Ordinal)
      .Because("the same name an existing table's index has, so the declaration keeps it rather than building a twin");
  }

  /// <summary>Declared after a model property, the system column is the composite's second element.</summary>
  [Test]
  public async Task EFCore_DeclaredInAComposite_FollowsTheModelPropertyAsync() {
    var schema = await _efCoreSchemaAsync("[PerspectiveIndex(nameof(Status), PerspectiveRowColumns.UpdatedAt)]");

    await Assert.That(schema).Contains(
      _efEnsure($"CREATE INDEX IF NOT EXISTS idx_ledger_status_updated_at ON {EF_TABLE} ((data ->> 'Status'), updated_at)"),
      StringComparison.Ordinal);
  }

  /// <summary>Declared with a predicate, the index is partial and named so.</summary>
  [Test]
  public async Task EFCore_DeclaredWithAPredicate_IsAPartialIndexAsync() {
    var schema = await _efCoreSchemaAsync(
      "[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt, Where = \"(data ->> 'Status') = 'open'\")]");

    await Assert.That(schema).Contains(
      _efEnsure($"CREATE INDEX IF NOT EXISTS idx_ledger_updated_at_partial ON {EF_TABLE} (updated_at) WHERE (data ->> 'Status') = 'open'"),
      StringComparison.Ordinal);
    await Assert.That(schema).DoesNotContain("idx_ledger_updated_at ON", StringComparison.Ordinal)
      .Because("the partial index is the one declared; the full one is not built beside it");
  }

  /// <summary><c>created_at</c> is a row column too, and composes the same way.</summary>
  [Test]
  public async Task EFCore_CreatedAt_ComposesLikeUpdatedAtAsync() {
    var schema = await _efCoreSchemaAsync("[PerspectiveIndex(nameof(Status), PerspectiveRowColumns.CreatedAt)]");

    await Assert.That(schema).Contains(
      _efEnsure($"CREATE INDEX IF NOT EXISTS idx_ledger_status_created_at ON {EF_TABLE} ((data ->> 'Status'), created_at)"),
      StringComparison.Ordinal);
  }

  /// <summary>
  /// A model's own property named <c>UpdatedAt</c> is a document field, not the row column: <c>nameof</c> names the
  /// property, and only the well-known name reaches the column.
  /// </summary>
  [Test]
  public async Task EFCore_AModelPropertyNamedUpdatedAt_IsIndexedInTheDocumentAsync() {
    var schema = await _efCoreSchemaAsync("[PerspectiveIndex(nameof(Status), nameof(UpdatedAt))]");

    await Assert.That(schema).Contains("idx_ledger_status_updatedat ON", StringComparison.Ordinal);
    await Assert.That(schema).DoesNotContain("(data ->> 'Status'), updated_at)", StringComparison.Ordinal)
      .Because("the model's property lives in the document; naming it must not silently index the row's column");
  }

  // ========================================
  // Dapper
  // ========================================

  /// <summary>The Dapper schema builds no <c>updated_at</c> index for a model that declares nothing either.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_AModelThatDeclaresNothing_GetsNoUpdatedAtIndexAsync() {
    var schema = _dapperSchema("");

    await Assert.That(schema).DoesNotContain("_updated_at ON", StringComparison.Ordinal)
      .Because("driver parity: neither driver builds the index unasked (#2087)");
    await Assert.That(schema).DoesNotContain("(updated_at)", StringComparison.Ordinal);
  }

  /// <summary>Declared on its own, the Dapper schema builds it under the name the EF Core driver gives it.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_DeclaredOnItsOwn_BuildsTheIndexAsync() {
    var schema = _dapperSchema("[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]");

    await Assert.That(schema).Contains(
      "CREATE INDEX IF NOT EXISTS idx_ledger_updated_at ON wh_per_ledger (updated_at);", StringComparison.Ordinal);
  }

  /// <summary>A composite and a partial declaration reach the Dapper schema as they reach the EF Core one.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_CompositeAndPartialDeclarations_AreBuiltAsync() {
    var schema = _dapperSchema(
      "[PerspectiveIndex(nameof(Status), PerspectiveRowColumns.UpdatedAt)]\n"
      + "[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt, Where = \"(data ->> 'Status') = 'open'\")]");

    await Assert.That(schema).Contains(
      "CREATE INDEX IF NOT EXISTS idx_ledger_status_updated_at ON wh_per_ledger ((data ->> 'Status'), updated_at);",
      StringComparison.Ordinal);
    await Assert.That(schema).Contains(
      "CREATE INDEX IF NOT EXISTS idx_ledger_updated_at_partial ON wh_per_ledger (updated_at) WHERE (data ->> 'Status') = 'open';",
      StringComparison.Ordinal);
  }

  /// <summary>The Dapper schema declares a declared index to the managed-object ledger, so the reconcile keeps it.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Dapper_ADeclaredIndex_IsAManagedObjectAsync() {
    var schema = _dapperSchema("[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]");

    await Assert.That(schema).Contains("(\"wh_per_ledger\", \"index\", \"idx_ledger_updated_at\", \"LedgerModel\")",
      StringComparison.Ordinal);
  }

  // ========================================
  // The well-known names
  // ========================================

  /// <summary>The generator reads the names the attribute's callers write: the same constants on both sides.</summary>
  [Test]
  public async Task TheWellKnownNames_AreTheOnesTheGeneratorsResolveAsync() {
    await Assert.That(SharedRowColumns.UPDATED_AT).IsEqualTo(Whizbang.Core.Perspectives.PerspectiveRowColumns.UpdatedAt);
    await Assert.That(SharedRowColumns.CREATED_AT).IsEqualTo(Whizbang.Core.Perspectives.PerspectiveRowColumns.CreatedAt);
  }

  /// <summary>Each well-known name resolves to its column, under the column's own name, and nothing else does.</summary>
  [Test]
  public async Task Discovery_ResolvesEachWellKnownNameToItsColumnAsync() {
    var compilation = GeneratorTestHelper.CreateCompilation("""
      using System;
      using Whizbang.Core.Perspectives;

      namespace Probe;

      [PerspectiveIndex(PerspectiveRowColumns.CreatedAt, PerspectiveRowColumns.UpdatedAt)]
      [PerspectiveIndex("$version")]
      public class ProbeModel {
        public string Kind { get; init; } = "";
      }
      """);
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
    await Assert.That(errors).IsEmpty();

    var found = JsonIndexDiscovery.CompositesFrom(compilation.GetTypeByMetadataName("Probe.ProbeModel")!);

    await Assert.That(found).Count().IsEqualTo(1)
      .Because("a name that is neither a property nor a known row column resolves to nothing, as before");
    await Assert.That(found[0].Elements.Select(e => e.Element)).IsEquivalentTo(["created_at", "updated_at"]);
    await Assert.That(found[0].Elements.Select(e => e.PropertyName)).IsEquivalentTo(["created_at", "updated_at"]);
  }
}
