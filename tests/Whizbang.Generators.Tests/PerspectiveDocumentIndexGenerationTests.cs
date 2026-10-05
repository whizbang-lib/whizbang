// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions.Extensions;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Which document indexes a perspective's schema builds, and that every index it builds goes
/// through the one statement that compares definitions before creating anything.
/// </summary>
/// <remarks>
/// <para>
/// A perspective used to get an index over each of its three documents whether or not anything
/// read them. The one over the model's document answers a whole-document match, which is what an
/// equality filter on a field without its own index compiles to, and it is built only when the
/// model says its queries match that way. The one over the metadata document answers queries almost
/// nobody writes, so it too is built only when the model asks for it.
/// </para>
/// <para>
/// The second half of this file is about duplicates. An index created by an earlier path under a
/// different name has the same definition as the one the schema declares, and
/// <c>CREATE INDEX IF NOT EXISTS</c> compares names only, so both were kept and both were written
/// on every change. Each declared index is therefore emitted through <c>wh_ensure_index</c>, which
/// compares the definition against the table's existing indexes first.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
public class PerspectiveDocumentIndexGenerationTests {
  private static string _source(string modelAttribute, string modelBase = "") => $$"""
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record LedgerPosted : IEvent;

    [PerspectiveQueries(MatchOnAnyField = true, MatchOnMetadata = true)]
    public record LedgerBase {
      public string Region { get; init; } = string.Empty;
    }

    {{modelAttribute}}
    public record LedgerModel{{modelBase}} {
      [StreamId]
      public Guid LedgerId { get; init; }

      [Indexed]
      public string Status { get; init; } = string.Empty;

      [Indexed(IndexKinds.Substring)]
      public string Memo { get; init; } = string.Empty;
    }

    public class LedgerPerspective : IPerspectiveFor<LedgerModel, LedgerPosted> {
      public LedgerModel Apply(LedgerModel currentData, LedgerPosted eventData) => currentData;
    }

    [WhizbangDbContext]
    public class LedgerDbContext : DbContext {
      public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _schemaAsync(string modelAttribute, string modelBase = "") {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(_source(modelAttribute, modelBase));
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal));
    return schema.SourceText.ToString();
  }

  /// <summary>
  /// The index lines of the hash-tracked entry for the ledger table, in the order emitted, which
  /// is what a reviewer reads and what the schema pass runs.
  /// </summary>
  private static List<string> _entryIndexLines(string schema) {
    var start = schema.IndexOf("(\"wh_per_ledger\", @\"", StringComparison.Ordinal);
    var end = schema.IndexOf("\")", start, StringComparison.Ordinal);
    return [.. schema[start..end].Split('\n')
      .Select(static l => l.Trim())
      .Where(static l => l.Contains("INDEX", StringComparison.Ordinal))];
  }

  // ========================================
  // Undeclared: neither document index
  // ========================================

  /// <summary>
  /// The exact index block for a model that declares nothing: neither the document index nor the
  /// metadata index is built, and every index but the trigram one is created through the comparing
  /// statement.
  /// </summary>
  /// <remarks>
  /// Asserted line for line so an index that appears or disappears is a visible change to this test
  /// rather than a quiet change to every consumer's schema.
  /// </remarks>
  [Test]
  public async Task Undeclared_EmitsTheDefaultIndexBlockAsync() {
    var lines = _entryIndexLines(await _schemaAsync(""));

    string[] expected = [
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_created_at ON \"\"testapp\"\".wh_per_ledger (created_at)$wbix$);",
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_updated_at ON \"\"testapp\"\".wh_per_ledger (updated_at)$wbix$);",
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_scope_gin ON \"\"testapp\"\".wh_per_ledger USING gin (scope)$wbix$);",
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_scope_tenant ON \"\"testapp\"\".wh_per_ledger ((scope->>'t'))$wbix$);",
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_status_json ON \"\"testapp\"\".wh_per_ledger ((data ->> 'Status'))$wbix$);",
      "CREATE INDEX IF NOT EXISTS idx_ledger_memo_trgm ON \"\"testapp\"\".wh_per_ledger USING gin ((data ->> 'Memo') gin_trgm_ops);",
    ];

    await Assert.That(lines).IsEquivalentTo(expected);
  }

  /// <summary>
  /// The model's document is not indexed as a whole unless the model asks: the whole-document index
  /// is the largest on the table and is rewritten on every change, so it is a decision, not a default.
  /// </summary>
  [Test]
  public async Task Undeclared_BuildsNoDocumentIndexInEitherScriptAsync() {
    var schema = await _schemaAsync("");

    await Assert.That(schema).DoesNotContain("_data_gin", StringComparison.Ordinal)
      .Because("an undeclared model no longer gets the whole-document index on a new database");
    await Assert.That(schema).DoesNotContain("gin (data)", StringComparison.Ordinal);
  }

  /// <summary>The metadata document is not indexed unless the model asks.</summary>
  [Test]
  public async Task Undeclared_BuildsNoMetadataIndexInEitherScriptAsync() {
    var schema = await _schemaAsync("");

    await Assert.That(schema).DoesNotContain("_metadata_gin", StringComparison.Ordinal)
      .Because("nothing the framework runs matches on metadata, so its index is written on every change and never read");
    await Assert.That(schema).DoesNotContain("gin (metadata)", StringComparison.Ordinal);
  }

  /// <summary>
  /// The fallback script, applied when the tracking tables cannot be read, makes the same
  /// decisions and goes through the same comparing statement as the hash-tracked one.
  /// </summary>
  [Test]
  public async Task Undeclared_TheFallbackScriptMatchesTheTrackedOneAsync() {
    var schema = await _schemaAsync("");
    var fallback = schema[..schema.IndexOf("(\"wh_per_ledger\", @\"", StringComparison.Ordinal)];

    await Assert.That(fallback).DoesNotContain("idx_ledger_data_gin", StringComparison.Ordinal);
    await Assert.That(fallback).Contains(
      "wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_scope_tenant ON \"\"testapp\"\".wh_per_ledger ((scope->>'t'))$wbix$);",
      StringComparison.Ordinal);
    await Assert.That(fallback).Contains(
      "wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_status_json ON \"\"testapp\"\".wh_per_ledger ((data ->> 'Status'))$wbix$);",
      StringComparison.Ordinal);
  }

  // ========================================
  // Declared
  // ========================================

  /// <summary>A model whose queries never match on any field does not get the document index.</summary>
  [Test]
  public async Task MatchOnAnyFieldFalse_OmitsTheDocumentIndexAsync() {
    var lines = _entryIndexLines(await _schemaAsync("[PerspectiveQueries(MatchOnAnyField = false)]"));

    await Assert.That(lines.Any(static l => l.Contains("_data_gin", StringComparison.Ordinal))).IsFalse();
    await Assert.That(lines.Any(static l => l.Contains("idx_ledger_status_json", StringComparison.Ordinal))).IsTrue()
      .Because("the declared field index is how a model that opted out still answers its filter");
    await Assert.That(lines.Any(static l => l.Contains("idx_ledger_scope_tenant", StringComparison.Ordinal))).IsTrue()
      .Because("the tenant index serves tenant isolation, which is not what this declaration is about");
  }

  /// <summary>Declaring the lookup is the one way to get the document index.</summary>
  [Test]
  public async Task MatchOnAnyFieldTrue_BuildsTheDocumentIndexAsync() {
    var lines = _entryIndexLines(await _schemaAsync("[PerspectiveQueries(MatchOnAnyField = true)]"));

    await Assert.That(lines).Contains(
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_data_gin ON \"\"testapp\"\".wh_per_ledger USING gin (data)$wbix$);");
    await Assert.That(lines.Any(static l => l.Contains("_metadata_gin", StringComparison.Ordinal))).IsFalse();
  }

  /// <summary>Matching on metadata is its own opt-in, and asking for it builds the metadata index.</summary>
  [Test]
  public async Task MatchOnMetadataTrue_BuildsTheMetadataIndexAsync() {
    var schema = await _schemaAsync("[PerspectiveQueries(MatchOnMetadata = true)]");
    var lines = _entryIndexLines(schema);

    await Assert.That(lines).Contains(
      "SELECT \"\"testapp\"\".wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_ledger_metadata_gin ON \"\"testapp\"\".wh_per_ledger USING gin (metadata)$wbix$);");
    await Assert.That(lines.Any(static l => l.Contains("_data_gin", StringComparison.Ordinal))).IsFalse()
      .Because("leaving MatchOnAnyField out means off, whatever else is declared");
  }

  /// <summary>A model inherits the declaration of the base it derives from.</summary>
  [Test]
  public async Task ADeclarationOnTheBaseModelAppliesAsync() {
    var lines = _entryIndexLines(await _schemaAsync("", " : LedgerBase"));

    await Assert.That(lines.Any(static l => l.Contains("idx_ledger_metadata_gin", StringComparison.Ordinal))).IsTrue();
    await Assert.That(lines.Any(static l => l.Contains("idx_ledger_data_gin", StringComparison.Ordinal))).IsTrue();
  }

  /// <summary>The nearest declaration wins over one on a base.</summary>
  [Test]
  public async Task TheModelsOwnDeclarationOverridesItsBaseAsync() {
    var lines = _entryIndexLines(await _schemaAsync("[PerspectiveQueries(MatchOnAnyField = false)]", " : LedgerBase"));

    await Assert.That(lines.Any(static l => l.Contains("_data_gin", StringComparison.Ordinal))).IsFalse();
    await Assert.That(lines.Any(static l => l.Contains("_metadata_gin", StringComparison.Ordinal))).IsFalse();
  }

  /// <summary>
  /// The registry that detects schema drift lists the indexes the table is meant to have, so it
  /// makes the same decisions the DDL does.
  /// </summary>
  [Test]
  public async Task TheRegistryListsOnlyTheDeclaredDocumentIndexesAsync() {
    var schema = await _schemaAsync("[PerspectiveQueries(MatchOnAnyField = false)]");
    var registry = schema[schema.IndexOf("PerspectiveRegistryJson =", StringComparison.Ordinal)..];
    registry = registry[..registry.IndexOf('\n')];

    await Assert.That(registry).DoesNotContain("_data_gin", StringComparison.Ordinal);
    await Assert.That(registry).DoesNotContain("_metadata_gin", StringComparison.Ordinal);
    await Assert.That(registry).Contains("idx_ledger_scope_gin", StringComparison.Ordinal);
  }

  /// <summary>
  /// A physical column's index and a composite index go through the same comparing statement,
  /// because "one path owns index creation" is only true if nothing is emitted around it.
  /// </summary>
  [Test]
  public async Task PhysicalAndCompositeIndexesAreEnsuredTooAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record TallyNoted : IEvent;

      [PerspectiveIndex(nameof(Owner), nameof(Kind))]
      public record TallyModel {
        [StreamId]
        public Guid TallyId { get; init; }

        [PhysicalField]
        [Indexed]
        public Guid Owner { get; init; }

        public string Kind { get; init; } = string.Empty;
      }

      public class TallyPerspective : IPerspectiveFor<TallyModel, TallyNoted> {
        public TallyModel Apply(TallyModel currentData, TallyNoted eventData) => currentData;
      }

      [WhizbangDbContext]
      public class TallyDbContext : DbContext {
        public TallyDbContext(DbContextOptions<TallyDbContext> options) : base(options) { }
      }
      """);
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    var bare = schema.Split('\n')
      .Select(static l => l.Trim())
      .Where(static l => l.StartsWith("CREATE INDEX", StringComparison.Ordinal) && l.Contains("wh_per_tally", StringComparison.Ordinal))
      .ToList();

    await Assert.That(bare).IsEmpty()
      .Because("a statement emitted around the comparing function is a second path, which is the defect this closes");
    await Assert.That(schema).Contains("wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_tally_owner ON", StringComparison.Ordinal);
    await Assert.That(schema).Contains("wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS idx_tally_owner_kind ON", StringComparison.Ordinal);
  }

  /// <summary>
  /// A long table name no longer pushes two of its standard indexes onto one truncated identifier.
  /// </summary>
  [Test]
  public async Task ALongTableNameKeepsEveryIndexNameDistinctAndWithinTheLimitAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync("""
      using System;
      using Microsoft.EntityFrameworkCore;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      using Whizbang.Data.EFCore.Custom;

      namespace TestApp;

      public record Touched : IEvent;

      [PerspectiveQueries(MatchOnAnyField = true)]
      public record OrganizationalReportingStructurePositionAssignmentHistoryModel {
        [StreamId]
        public Guid Id { get; init; }
      }

      public class HistoryPerspective : IPerspectiveFor<OrganizationalReportingStructurePositionAssignmentHistoryModel, Touched> {
        public OrganizationalReportingStructurePositionAssignmentHistoryModel Apply(
          OrganizationalReportingStructurePositionAssignmentHistoryModel currentData, Touched eventData) => currentData;
      }

      [WhizbangDbContext]
      public class HistoryDbContext : DbContext {
        public HistoryDbContext(DbContextOptions<HistoryDbContext> options) : base(options) { }
      }
      """);
    var schema = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();

    var names = schema.Split('\n')
      .Where(static l => l.Contains("wh_ensure_index($wbix$CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal))
      .Select(static l => l[(l.IndexOf("IF NOT EXISTS ", StringComparison.Ordinal) + "IF NOT EXISTS ".Length)..])
      .Select(static l => l[..l.IndexOf(' ', StringComparison.Ordinal)])
      .Distinct()
      .ToList();

    await Assert.That(names.Count).IsEqualTo(5)
      .Because("created_at, updated_at, data, scope and tenant are five indexes and need five names");
    await Assert.That(names.All(static n => n.Length <= 63)).IsTrue();
    await Assert.That(names.Select(static n => n[..Math.Min(63, n.Length)]).Distinct().Count()).IsEqualTo(5);
  }
}
