// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The schema extension names the perspectives whose models are recursive, so a start on a database an earlier release
/// migrated can say which ones must be rebuilt (owner decision on #2112).
/// </summary>
/// <remarks>
/// Before #2112 the Entity Framework driver mapped a recursive model and kept only the first level of each recursive
/// member, so rows those releases wrote may be truncated. The generated list is what the startup notice reads; a
/// perspective is in it when its model reaches a type it is already inside, and out of it otherwise. Each entry is the
/// perspective's CLR type name (the name a rebuild takes) and its table.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
public class RecursivePerspectiveRebuildNoticeGenerationTests {
  private const string SOURCE = """
    using System;
    using System.Collections.Generic;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Noted : IEvent;

    public sealed record Node { public string Label { get; init; } = ""; public List<Node>? Children { get; init; } }

    public record TreeModel {
      [StreamId] public Guid Id { get; init; }
      public List<Node>? Skeleton { get; init; }
    }

    public record PlainModel {
      [StreamId] public Guid Id { get; init; }
      public string Name { get; init; } = "";
    }

    public class TreePerspective : IPerspectiveFor<TreeModel, Noted> {
      public TreeModel Apply(TreeModel currentData, Noted @event) => currentData;
    }

    public class SecondTreePerspective : IPerspectiveFor<TreeModel, Noted> {
      public TreeModel Apply(TreeModel currentData, Noted @event) => currentData;
    }

    public class PlainPerspective : IPerspectiveFor<PlainModel, Noted> {
      public PlainModel Apply(PlainModel currentData, Noted @event) => currentData;
    }

    [WhizbangDbContext]
    public class TestDbContext : DbContext {
      public TestDbContext(DbContextOptions<TestDbContext> options) : base(options) { }
    }
    """;

  [RequiresAssemblyFiles]
  private static async Task<string> _recursivePerspectivesAsync(string source) {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(source);
    var code = result.GeneratedSources.First(s => s.HintName.Contains("SchemaExtensions", StringComparison.Ordinal)).SourceText.ToString();
    var start = code.IndexOf("GetRecursivePerspectives()", StringComparison.Ordinal);
    await Assert.That(start).IsGreaterThanOrEqualTo(0).Because("the schema extension declares the list");
    return code[start..code.IndexOf("};", start, StringComparison.Ordinal)];
  }

  /// <summary>Every perspective over a recursive model is listed by its CLR type name and table; others are not.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task EveryPerspectiveOverARecursiveModel_IsListedWithItsTableAsync() {
    var list = await _recursivePerspectivesAsync(SOURCE);

    await Assert.That(list).Contains("(\"TestApp.TreePerspective\", \"wh_per_tree_model\"),");
    await Assert.That(list).Contains("(\"TestApp.SecondTreePerspective\", \"wh_per_tree_model\"),")
      .Because("each perspective over the table is named, since each is rebuilt by its own name");
    await Assert.That(list).DoesNotContain("PlainPerspective")
      .Because("a model with no recursion was never truncated");
  }

  /// <summary>A context with no recursive model lists nothing, so the notice has nothing to check.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AContextWithNoRecursiveModel_ListsNothingAsync() {
    var source = SOURCE
      .Replace("public List<Node>? Skeleton { get; init; }", "public string Title { get; init; } = \"\";", StringComparison.Ordinal);

    var list = await _recursivePerspectivesAsync(source);

    await Assert.That(list).DoesNotContain("(\"");
  }
}
