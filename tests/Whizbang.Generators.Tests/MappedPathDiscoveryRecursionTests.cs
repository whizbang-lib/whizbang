// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.CodeAnalysis;
using TUnit.Assertions.Extensions;
using Whizbang.Generators.Shared.Models;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A perspective model whose graph reaches a type it is already inside (a tree node holding its children) takes the
/// opaque form (#2112).
/// </summary>
/// <remarks>
/// <para>
/// Entity Framework maps a recursive complex type one level deep and stops: the first level of a self-reference is
/// mapped and everything below it is dropped on write and read back as null, with no error at model build, save or
/// query (<c>CollectionMemberShapeProbeTests.TheMappedPathMapsARecursiveTypeOneLevelDeepAsync</c> measures it). The
/// discovery used to skip a type it had already seen, which stopped the walk from looping and also meant a recursive
/// type was never judged at all.
/// </para>
/// <para>
/// Recursion is a type that reaches itself along one path, not a type reached twice: a model holding two members of
/// the same type is not recursive and keeps its mapped form and its indexing.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
public class MappedPathDiscoveryRecursionTests {
  private static INamedTypeSymbol _type(string types, string metadataName = "TestApp.Model") {
    var compilation = GeneratorTestHelper.CreateCompilation($$"""
      using System;
      using System.Collections.Generic;
      using System.Collections.Immutable;

      namespace TestApp;

      {{types}}
      """);
    return compilation.GetTypeByMetadataName(metadataName)!;
  }

  private static string _tree(string childrenType) =>
    $"public sealed record Node {{ public string Label {{ get; init; }} = \"\"; public {childrenType} Children {{ get; init; }} }}\n"
    + "public class Model { public List<Node>? Skeleton { get; init; } }";

  /// <summary>
  /// The case in the issue: a node holding a collection of itself, in every collection shape the mapped path otherwise
  /// keeps.
  /// </summary>
  [Test]
  [Arguments("List<Node>?")]
  [Arguments("IList<Node>?")]
  [Arguments("ImmutableList<Node>?")]
  [Arguments("Node[]?")]
  public async Task ACollectionOfItself_IsNamedByItsPathAsync(string childrenType) {
    var member = MappedPathDiscovery.UnmappableMember(_type(_tree(childrenType)));

    await Assert.That(member).IsEqualTo("Skeleton.Children")
      .Because("the mapped path stores only the first level of a recursive type and drops the rest in silence");
  }

  /// <summary>A model that stores a recursive type is stored opaquely, so every level round-trips.</summary>
  [Test]
  public async Task ARecursiveModel_MustBeStoredOpaquelyAsync() {
    await Assert.That(MappedPathDiscovery.MustStoreOpaquely(_type(_tree("List<Node>?")))).IsTrue();
  }

  /// <summary>Recursion through a single nested object, not a collection, is recursion too.</summary>
  [Test]
  public async Task ANestedObjectOfItself_IsNamedAsync() {
    var model = _type("""
      public sealed class Link { public string Label { get; set; } = ""; public Link? Next { get; set; } }
      public class Model { public Link? Head { get; set; } }
      """);

    await Assert.That(MappedPathDiscovery.UnmappableMember(model)).IsEqualTo("Head.Next");
  }

  /// <summary>Mutual recursion (A holds B, B holds A) is named where the path returns to a type it is inside.</summary>
  [Test]
  public async Task MutualRecursion_IsNamedWhereThePathReturnsAsync() {
    var model = _type("""
      public sealed class Branch { public string Label { get; set; } = ""; public List<Leaf>? Leaves { get; set; } }
      public sealed class Leaf { public string Label { get; set; } = ""; public List<Branch>? Branches { get; set; } }
      public class Model { public List<Branch>? Roots { get; set; } }
      """);

    await Assert.That(MappedPathDiscovery.UnmappableMember(model)).IsEqualTo("Roots.Leaves.Branches");
  }

  /// <summary>A model holding a collection of the model type itself is recursive at the top.</summary>
  [Test]
  public async Task AModelHoldingItself_IsNamedAsync() {
    var model = _type("public class Model { public string Name { get; set; } = \"\"; public List<Model>? Children { get; set; } }");

    await Assert.That(MappedPathDiscovery.UnmappableMember(model)).IsEqualTo("Children");
  }

  /// <summary>Recursion below an ordinary nested object is found and named by its full path.</summary>
  [Test]
  public async Task RecursionBelowANestedObject_IsNamedByItsFullPathAsync() {
    var model = _type(_tree("List<Node>?").Replace(
      "public class Model { public List<Node>? Skeleton { get; init; } }",
      "public sealed class Holder { public List<Node>? Skeleton { get; init; } }\npublic class Model { public Holder Holder { get; init; } = new(); }",
      StringComparison.Ordinal));

    await Assert.That(MappedPathDiscovery.UnmappableMember(model)).IsEqualTo("Holder.Skeleton.Children");
  }

  /// <summary>
  /// The same type reached twice along different paths is not recursion: routing it opaquely would take the model's
  /// indexing away for nothing.
  /// </summary>
  [Test]
  public async Task ATypeReachedTwice_IsNotRecursionAsync() {
    var model = _type("""
      public sealed class Address { public string City { get; set; } = ""; }
      public sealed class Party { public Address? Address { get; set; } }
      public class Model { public Address? Billing { get; set; } public Address? Shipping { get; set; } public List<Party>? Parties { get; set; } }
      """);

    await Assert.That(MappedPathDiscovery.UnmappableMember(model)).IsNull();
    await Assert.That(MappedPathDiscovery.MustStoreOpaquely(model)).IsFalse();
    await Assert.That(MappedPathDiscovery.RecursiveMembers(model)).IsEmpty();
  }

  /// <summary>Each property that returns the path to a type it is inside is listed, with its path and that type.</summary>
  [Test]
  public async Task RecursiveMembers_ListsEachPropertyThatClosesACycleAsync() {
    var model = _type("""
      public sealed class Branch { public string Label { get; set; } = ""; public List<Leaf>? Leaves { get; set; } }
      public sealed class Leaf { public string Label { get; set; } = ""; public List<Branch>? Branches { get; set; } }
      public sealed record Node { public Node[]? Children { get; init; } }
      public class Model { public List<Branch>? Roots { get; set; } public Node? Tree { get; set; } public string Name { get; set; } = ""; }
      """);

    var found = MappedPathDiscovery.RecursiveMembers(model);

    await Assert.That(found.Select(f => f.Path)).IsEquivalentTo(["Roots.Leaves.Branches", "Tree.Children"]);
    await Assert.That(found.Select(f => f.Property.Name)).IsEquivalentTo(["Branches", "Children"]);
    await Assert.That(found.Select(f => f.Recursive.Name)).IsEquivalentTo(["Branch", "Node"])
      .Because("the type named is the one the path returns to, which is what the author sees as recursive");
  }

  /// <summary>
  /// Members with nothing to walk into (a primitive, a dynamic member, an array of arrays, an array of records with no
  /// recursion) are not recursion.
  /// </summary>
  [Test]
  public async Task MembersWithNothingRecursiveBelowThem_AreNotListedAsync() {
    var model = _type("""
      public sealed record Tag(Guid TagId, string Label);
      public class Model { public string Name { get; set; } = ""; public dynamic? Extra { get; set; } public int[][] Grid { get; set; } = []; public Tag[] Tags { get; set; } = []; }
      """);

    await Assert.That(MappedPathDiscovery.RecursiveMembers(model)).IsEmpty();
  }

  /// <summary>Whether a type reaches a type it is inside anywhere below it, which decides the advice WHIZ200 gives.</summary>
  [Test]
  public async Task IsRecursive_AnswersForTheTypeAndWhatItHoldsAsync() {
    const string types = """
      public sealed record Node { public List<Node>? Children { get; init; } }
      public sealed record Holder { public List<Node>? Nodes { get; init; } }
      public sealed record Tag(Guid TagId, string Label);
      public class Model { }
      """;

    await Assert.That(MappedPathDiscovery.IsRecursive(_type(types, "TestApp.Node"))).IsTrue();
    await Assert.That(MappedPathDiscovery.IsRecursive(_type(types, "TestApp.Holder"))).IsTrue()
      .Because("a type holding a recursive type is stored opaquely in any collection shape too");
    await Assert.That(MappedPathDiscovery.IsRecursive(_type(types, "TestApp.Tag"))).IsFalse();
    await Assert.That(MappedPathDiscovery.IsRecursive(null)).IsFalse()
      .Because("a model the generator could not resolve to a named type has nothing to walk");
  }

  /// <summary>
  /// The reason an analyzer gives for an opaque model names the member and says why it is opaque: recursive, or not
  /// constructible, and nothing for a model that maps.
  /// </summary>
  [Test]
  public async Task DescribeUnmappable_SaysWhyTheMemberIsOpaqueAsync() {
    const string types = """
      public sealed record Node { public List<Node>? Children { get; init; } }
      public sealed record Tag(Guid TagId, string Label);
      public class Model { public List<Node>? Skeleton { get; set; } }
      public class Unconstructible { public IReadOnlyList<Tag> Tags { get; set; } = []; }
      public class Plain { public string Name { get; set; } = ""; }
      """;

    await Assert.That(MappedPathDiscovery.DescribeUnmappable(_type(types))).IsEqualTo(
      "holds 'Skeleton.Children', which is recursive (Entity Framework maps a self-reference only one level deep)");
    await Assert.That(MappedPathDiscovery.DescribeUnmappable(_type(types, "TestApp.Unconstructible"))).IsEqualTo(
      "holds 'Tags', which the mapped path cannot construct");
    await Assert.That(MappedPathDiscovery.DescribeUnmappable(_type(types, "TestApp.Plain"))).IsNull();
  }
}
