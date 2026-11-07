// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated message context provides metadata for an <c>IList&lt;T&gt;</c> and an <c>ImmutableList&lt;T&gt;</c>
/// property, as it does for <c>IReadOnlyList&lt;T&gt;</c> (#2112).
/// </summary>
/// <remarks>
/// <para>
/// The context is the only resolver on the AOT path, so a collection type it has no metadata for cannot be serialized
/// at all: writing a document holding one throws <c>NotSupportedException</c> ("JsonTypeInfo metadata for type
/// IList`1[...] was not provided"). A mapped perspective document never asked, because Entity Framework writes it
/// property by property; an opaque one is written whole by the serializer, which is how a recursive tree declared with
/// <c>IList</c> or <c>ImmutableList</c> children found the gap once it was stored opaquely.
/// </para>
/// <para>
/// The element type behind each is discovered too, so a type reached only through such a collection has metadata.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("SourceGenerators")]
public class MessageJsonContextInterfaceCollectionTests {
  private static string _source(string collection) => $$"""
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using Whizbang.Core;

    namespace TestApp;

    public record CatalogItem {
      public required string Name { get; init; }
    }

    public record CatalogEvent : IEvent {
      public required {{collection}}<CatalogItem> Items { get; init; }
    }
    """;

  [RequiresAssemblyFiles]
  private static async Task<string> _generatedAsync(string source) {
    var result = GeneratorTestHelper.RunGenerator<MessageJsonContextGenerator>(source);
    await Assert.That(result.Diagnostics).DoesNotContain(d => d.Severity == DiagnosticSeverity.Error);
    return GeneratorTestHelper.GetGeneratedSource(result, "MessageJsonContext.g.cs")!;
  }

  /// <summary>An <c>IList&lt;T&gt;</c> property gets its own factory, filled through a <c>List&lt;T&gt;</c>.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnIListProperty_GetsAFactoryAndATypeCheckAsync() {
    var code = await _generatedAsync(_source("IList"));

    await Assert.That(code).Contains("private JsonTypeInfo<global::System.Collections.Generic.IList<global::TestApp.CatalogItem>> CreateIList_TestApp_CatalogItem(");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Generic.IList<global::TestApp.CatalogItem>)) {");
    await Assert.That(code).Contains("JsonMetadataServices.CreateIEnumerableInfo<global::System.Collections.Generic.IList<global::TestApp.CatalogItem>, global::TestApp.CatalogItem>(");
    await Assert.That(code).Contains("Create_TestApp_CatalogItem")
      .Because("the element is discovered through the IList");
  }

  /// <summary>
  /// Every other collection interface the opaque path accepts gets its own factory too, filled through a
  /// <c>List&lt;T&gt;</c>, which implements each of them (#2114).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  [Arguments("ICollection")]
  [Arguments("IReadOnlyCollection")]
  [Arguments("IEnumerable")]
  public async Task ACollectionInterfaceProperty_GetsAFactoryAndATypeCheckAsync(string collection) {
    var code = await _generatedAsync(_source(collection));

    await Assert.That(code).Contains($"private JsonTypeInfo<global::System.Collections.Generic.{collection}<global::TestApp.CatalogItem>> Create{collection}_TestApp_CatalogItem(");
    await Assert.That(code).Contains($"if (type == typeof(global::System.Collections.Generic.{collection}<global::TestApp.CatalogItem>)) {{");
    await Assert.That(code).Contains($"JsonMetadataServices.CreateIEnumerableInfo<global::System.Collections.Generic.{collection}<global::TestApp.CatalogItem>, global::TestApp.CatalogItem>(");
    await Assert.That(code).Contains("Create_TestApp_CatalogItem");
  }

  /// <summary>
  /// An <c>ImmutableList&lt;T&gt;</c> property gets a factory that builds the list from the elements read, and its
  /// element type is discovered through it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnImmutableListProperty_GetsAFactoryAndItsElementIsDiscoveredAsync() {
    var code = await _generatedAsync(_source("ImmutableList"));

    await Assert.That(code).Contains("private JsonTypeInfo<global::System.Collections.Immutable.ImmutableList<global::TestApp.CatalogItem>> CreateImmutableList_TestApp_CatalogItem(");
    await Assert.That(code).Contains("if (type == typeof(global::System.Collections.Immutable.ImmutableList<global::TestApp.CatalogItem>)) {");
    await Assert.That(code).Contains("JsonMetadataServices.CreateImmutableEnumerableInfo<global::System.Collections.Immutable.ImmutableList<global::TestApp.CatalogItem>, global::TestApp.CatalogItem>(");
    await Assert.That(code).Contains("global::System.Collections.Immutable.ImmutableList.CreateRange");
    await Assert.That(code).Contains("Create_TestApp_CatalogItem")
      .Because("a type reached only through an ImmutableList needs metadata of its own");
  }

  /// <summary>The <c>IReadOnlyList&lt;T&gt;</c> output is unchanged by the generalization.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnIReadOnlyListProperty_KeepsItsFactoryNamesAsync() {
    var code = await _generatedAsync(_source("IReadOnlyList"));

    await Assert.That(code).Contains("private JsonTypeInfo<global::System.Collections.Generic.IReadOnlyList<global::TestApp.CatalogItem>>? _IReadOnlyList_TestApp_CatalogItem;");
    await Assert.That(code).Contains("CreateIReadOnlyList_TestApp_CatalogItem(options)");
  }

  /// <summary>An interface collection nested inside another collection is discovered, as an IReadOnlyList is.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnIListInsideAList_IsDiscoveredAsync() {
    var code = await _generatedAsync(_source("List").Replace(
      "required List<CatalogItem> Items",
      "required List<IList<CatalogItem>> Items",
      StringComparison.Ordinal));

    await Assert.That(code).Contains("CreateIList_TestApp_CatalogItem(");
  }
}
