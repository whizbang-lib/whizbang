using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated schema for a table with substring indexes creates the trigram extension once, in a
/// block, and applies it through the optional-extension reader.
/// </summary>
/// <remarks>
/// The generator emits the perspective schema twice over: once per table for the hash-tracked pass,
/// and once as one script for the fallback the pass takes when the tracking tables cannot be read.
/// Both carry the trigram indexes, so both need the extension, and each needs it exactly once. A
/// trigram index emitted outside a block is the worse half of the same defect: the index statement
/// then reaches a server with no <c>gin_trgm_ops</c> operator class and fails the pass with nothing
/// to skip.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public class OptionalExtensionWiringTests {
  private const string BEGIN = "-- @whizbang:optional-extension pg_trgm";
  private const string END = "-- @whizbang:optional-extension-end";

  private const string SUBSTRING_MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Catalogued([property: StreamId] Guid Id) : IEvent;

    public record CatalogModel {
      [StreamId]
      public Guid CatalogId { get; init; }
      [Indexed(IndexKinds.Substring)]
      public string Title { get; init; } = string.Empty;
      [Indexed(IndexKinds.Substring)]
      public string Summary { get; init; } = string.Empty;
      [Indexed]
      public string Code { get; init; } = string.Empty;
    }

    public class CatalogPerspective : IPerspectiveFor<CatalogModel, Catalogued> {
      public CatalogModel Apply(CatalogModel currentData, Catalogued eventData) => currentData;
    }

    [WhizbangDbContext]
    public class CatalogDbContext : DbContext {
      public CatalogDbContext(DbContextOptions<CatalogDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>Every block, as its lines, in the order the generator wrote them.</summary>
  private static List<List<string>> _blocks(string output) {
    var blocks = new List<List<string>>();
    List<string>? current = null;
    foreach (var raw in output.Split('\n')) {
      var line = raw.Trim();
      if (line == BEGIN) {
        current = [];
        continue;
      }
      if (line == END) {
        blocks.Add(current ?? []);
        current = null;
        continue;
      }
      current?.Add(line);
    }
    return blocks;
  }

  /// <summary>Each block creates the extension once and holds every index that needs it.</summary>
  [Test]
  public async Task EachBlockCreatesTheExtensionOnceAsync() {
    var blocks = _blocks(await _generatedAsync(SUBSTRING_MODEL));

    await Assert.That(blocks).IsNotEmpty()
      .Because("a table with substring indexes is a table whose schema carries a block");
    foreach (var block in blocks) {
      await Assert.That(block.Count(l => l.StartsWith("CREATE EXTENSION", StringComparison.Ordinal))).IsEqualTo(1)
        .Because("one refused extension is one skipped block and one warning, not one per index");
      await Assert.That(block.Count(l => l.Contains("gin_trgm_ops", StringComparison.Ordinal))).IsEqualTo(2)
        .Because("both substring indexes share the block, which is what makes one extension enough");
    }
  }

  /// <summary>
  /// No trigram index is emitted outside a block, in any of the scripts the generator writes.
  /// </summary>
  /// <remarks>
  /// The hash-tracked pass and its fallback are separate scripts built by separate methods, so the
  /// extension has to be moved in both. Left outside a block, a trigram index reaches a server with
  /// no <c>gin_trgm_ops</c> operator class as an ordinary statement and fails the pass, which is the
  /// failure the block exists to turn into a warning.
  /// </remarks>
  [Test]
  public async Task NoTrigramIndexIsEmittedOutsideABlockAsync() {
    var output = await _generatedAsync(SUBSTRING_MODEL);

    var stray = new List<string>();
    var inside = false;
    foreach (var raw in output.Split('\n')) {
      var line = raw.Trim();
      if (line == BEGIN) {
        inside = true;
      } else if (line == END) {
        inside = false;
      } else if (!inside && line.Contains("gin_trgm_ops", StringComparison.Ordinal)) {
        stray.Add(line);
      }
    }

    await Assert.That(stray).IsEmpty()
      .Because("an index that needs the extension outside the block that creates it either fails "
        + "the pass on a server that refuses the extension, or is never created at all");
  }

  /// <summary>The initializer applies a script carrying the block through the reader that can skip it.</summary>
  [Test]
  public async Task TheInitializerAppliesOptionalExtensionBlocksThroughTheReaderAsync() {
    var output = await _generatedAsync(SUBSTRING_MODEL);

    await Assert.That(output).Contains("Whizbang.Data.Postgres.OptionalExtensionBlocks.HasBlocks(", StringComparison.Ordinal);
    await Assert.That(output).Contains("Whizbang.Data.Postgres.OptionalExtensionBlocks.ApplyAsync(", StringComparison.Ordinal)
      .Because("applied as one batch, a refused extension aborts the whole perspective pass; the reader "
        + "skips the block and lets the pass complete");
  }

  /// <summary>
  /// No script carries both a commit boundary and a block, because the reader that skips a block
  /// applies its script on one connection.
  /// </summary>
  /// <remarks>
  /// The two mechanisms answer different problems and the initializer picks one per script: a block
  /// is applied on the initializer's own connection under a savepoint, and a boundary needs each
  /// piece on a connection of its own so a rewrite commits before an index is built over it. A
  /// script carrying both would take the block path and lose the boundary, and a rewrite followed by
  /// an index in one transaction cannot ever succeed, on any retry. Nothing emits a boundary into
  /// perspective SQL today; this is what keeps that true.
  /// </remarks>
  [Test]
  public async Task NoScriptCarriesBothABoundaryAndABlockAsync() {
    var output = await _generatedAsync(SUBSTRING_MODEL);

    await Assert.That(output).Contains(BEGIN, StringComparison.Ordinal);
    await Assert.That(output).DoesNotContain("-- @whizbang:commit-boundary", StringComparison.Ordinal)
      .Because("the block path applies its script on one connection, so a boundary in the same "
        + "script would be dropped and the rewrite it separates could never commit before its index");
  }
}
