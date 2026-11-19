// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A perspective model holding a recursive type is configured as one opaque document, so every level of it round-trips
/// (#2112).
/// </summary>
/// <remarks>
/// Mapped as a complex property, a recursive type keeps only its first level: Entity Framework stores and reads one
/// level of the self-reference and drops the rest without an error. The generated configuration is what decides the
/// form, so this asserts it directly for each collection shape and for mutual recursion.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("SourceGenerators")]
public class RecursivePerspectiveModelGenerationTests {
  private const string GENERATED_FILE = "WhizbangModelBuilderExtensions.g.cs";
  private const string OPAQUE = "entity.Property(e => e.Data).HasColumnName(\"data\").HasColumnType(\"jsonb\")";
  private const string MAPPED = "d.ToJson(\"data\");";

  private static string _source(string types, string members) => $$"""
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    {{types}}

    public class TreeModel {
      [StreamId] public Guid Id { get; set; }
    {{members}}
    }

    public record TreeNoted([property: StreamId] Guid Id) : IEvent;

    public class TreePerspective : IPerspectiveFor<TreeModel, TreeNoted> {
      public TreeModel Apply(TreeModel currentData, TreeNoted eventData) => currentData;
    }
    """;

  [RequiresAssemblyFiles]
  private static string _generated(string source) {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, GENERATED_FILE)!;
  }

  /// <summary>A node holding a collection of itself, in each shape, is stored opaquely.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("List<Node>?")]
  [Arguments("IList<Node>?")]
  [Arguments("ImmutableList<Node>?")]
  [Arguments("Node[]?")]
  public async Task ACollectionOfItself_IsConfiguredOpaqueAsync(string childrenType) {
    var generated = _generated(_source(
      $"public sealed record Node {{ public string Label {{ get; init; }} = \"\"; public {childrenType} Children {{ get; init; }} }}",
      "  public List<Node>? Skeleton { get; set; }"));

    await Assert.That(generated).Contains(OPAQUE)
      .Because("mapped, the tree would keep its first level and lose every level below it");
    await Assert.That(generated).DoesNotContain(MAPPED);
  }

  /// <summary>Mutual recursion (a branch holds leaves, a leaf holds branches) is stored opaquely too.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MutualRecursion_IsConfiguredOpaqueAsync() {
    var generated = _generated(_source("""
      public sealed class Branch { public string Label { get; set; } = ""; public List<Leaf>? Leaves { get; set; } }
      public sealed class Leaf { public string Label { get; set; } = ""; public List<Branch>? Branches { get; set; } }
      """, "  public List<Branch>? Roots { get; set; }"));

    await Assert.That(generated).Contains(OPAQUE);
  }

  /// <summary>A type reached twice without recursion stays mapped and keeps its indexing.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ATypeReachedTwice_StaysMappedAsync() {
    var generated = _generated(_source(
      "public sealed class Address { public string City { get; set; } = \"\"; }",
      "  public Address? Billing { get; set; }\n  public List<Address>? Others { get; set; }"));

    await Assert.That(generated).Contains(MAPPED);
    await Assert.That(generated).DoesNotContain(OPAQUE);
  }
}
