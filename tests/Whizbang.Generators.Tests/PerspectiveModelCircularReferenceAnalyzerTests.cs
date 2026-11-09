// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// WHIZ201: a perspective model whose recursive types form a circular reference cannot be saved, and is an error
/// (owner decision on #2112).
/// </summary>
/// <remarks>
/// <para>
/// A recursion is a group of types in the model that reach each other through stored members. It is a tree, or a chain,
/// when it is entered in one direction only: all its members are collections (children, or alternating levels of two
/// types), or all are single references (a linked list's <c>Next</c>). It is circular when it holds both: the collection
/// is the containment direction, so a single reference inside the same recursion points an element back at its
/// container or an ancestor (<c>Node.Children</c> with <c>Node.Parent</c>, <c>Order.Lines</c> with <c>Line.Order</c>).
/// Writing such a graph whole has no end, so the document cannot be serialized.
/// </para>
/// <para>
/// A member left out of the document (<c>[JsonIgnore]</c>, <c>[NotMapped]</c>) is not part of it and is not judged.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz201</docs>
[Category("Analyzers")]
public class PerspectiveModelCircularReferenceAnalyzerTests {
  private static string _source(string members, string types) => $$"""
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    {{types}}

    public record Noted([property: StreamId] Guid Id) : IEvent;

    public class LedgerModel {
      [StreamId] public Guid Id { get; set; }
    {{members}}
    }

    public class LedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
      public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
    }
    """;

  [RequiresAssemblyFiles]
  private static async Task<List<Diagnostic>> _whiz201Async(string source) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelCircularReferenceAnalyzer>(compilation);
    return [.. diagnostics.Where(d => d.Id == "WHIZ201")];
  }

  private static string _locatedAt(Diagnostic diagnostic) =>
    diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan);

  // ========================================
  // Not circular
  // ========================================

  /// <summary>A tree: a node holding a collection of itself. Every element has one container.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ATree_IsNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public Node? Tree { get; set; }", """
      public sealed class Node { public string Label { get; set; } = ""; public List<Node>? Children { get; set; } }
      """));

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>A linked list: a single forward reference to the next of its kind.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ALinkedListNext_IsNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public Link? Head { get; set; }", """
      public sealed class Link { public string Label { get; set; } = ""; public dynamic? Extra { get; set; } public Link? Next { get; set; } }
      """));

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>A tree whose children are an array of itself is a tree too.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ATreeOfArrays_IsNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public Node? Tree { get; set; }", """
      public sealed class Node { public string Label { get; set; } = ""; public Node[]? Children { get; set; } }
      """));

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>Two types holding collections of each other: alternating levels of one tree.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task MutualCollections_AreNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public List<Branch>? Roots { get; set; }", """
      public sealed class Branch { public string Label { get; set; } = ""; public List<Leaf>? Leaves { get; set; } }
      public sealed class Leaf { public string Label { get; set; } = ""; public List<Branch>? Branches { get; set; } }
      """));

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>A back-reference left out of the document is not part of it.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABackReferenceTheDocumentLeavesOut_IsNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public Node? Tree { get; set; }", """
      public sealed class Node {
        public List<Node>? Children { get; set; }
        [JsonIgnore] public Node? Parent { get; set; }
      }
      """));

    await Assert.That(reported).IsEmpty();
  }

  /// <summary>
  /// A collection and a single reference to a type outside any recursion are ordinary members, whatever their number.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ACollectionAndAReferenceToTheSameNonRecursiveType_AreNotReportedAsync() {
    var reported = await _whiz201Async(_source("  public List<Tag> Tags { get; set; } = [];\n  public Tag? Primary { get; set; }", """
      public sealed record Tag(Guid TagId, string Label);
      """));

    await Assert.That(reported).IsEmpty();
  }

  // ========================================
  // Circular
  // ========================================

  /// <summary>A parent back-pointer: a node holds its children and a reference to its parent.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AParentBackPointer_IsAnErrorAtTheBackReferenceAsync() {
    var reported = await _whiz201Async(_source("  public Node? Tree { get; set; }", """
      public sealed class Node {
        public List<Node>? Children { get; set; }
        public Node? Parent { get; set; }
      }
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Parent")
      .Because("reported at the back-reference, the member to change");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture)).IsEqualTo(
      "Property 'Tree.Parent' of perspective model 'LedgerModel' refers back to 'Node', which holds it through the "
      + "collection 'Tree.Children', so an instance can reach its own container and the document cannot be saved. "
      + "Leave the reference out of the stored model ([JsonIgnore]) and restore it after reading, or store the "
      + "container's identifier instead.");
  }

  /// <summary>A mutual back-reference: an order holds its lines and each line refers to its order.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AMutualBackReference_IsAnErrorAsync() {
    var reported = await _whiz201Async(_source("  public List<Order> Orders { get; set; } = [];", """
      public sealed class Order { public List<Line> Lines { get; set; } = []; }
      public sealed class Line { public string Sku { get; set; } = ""; public Order? Order { get; set; } }
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Order");
    var message = reported[0].GetMessage(CultureInfo.InvariantCulture);
    await Assert.That(message).Contains("Property 'Orders.Lines.Order' of perspective model 'LedgerModel' refers back to 'Order'");
    await Assert.That(message).Contains("through the collection 'Orders.Lines'");
  }

  /// <summary>A back-reference to the model itself, the root every element is held by.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ABackReferenceToTheModel_IsAnErrorAsync() {
    var reported = await _whiz201Async(_source("  public List<Line> Lines { get; set; } = [];", """
      public sealed class Line { public string Sku { get; set; } = ""; public LedgerModel? Owner { get; set; } }
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture))
      .Contains("Property 'Lines.Owner' of perspective model 'LedgerModel' refers back to 'LedgerModel', which holds it through the collection 'Lines'");
  }

  /// <summary>A deep back-reference: the reference back to the order sits two levels under the line.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ADeepBackReference_IsAnErrorAsync() {
    var reported = await _whiz201Async(_source("  public List<Order> Orders { get; set; } = [];", """
      public sealed record Tag(Guid TagId, string Label);
      public sealed class Order { public List<Tag> Tags { get; set; } = []; public List<Line> Lines { get; set; } = []; }
      public sealed class Line { public Detail Detail { get; set; } = new(); }
      public sealed class Detail { public string Note { get; set; } = ""; public Order? Order { get; set; } }
      """));

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("Order");
    await Assert.That(reported[0].GetMessage(CultureInfo.InvariantCulture))
      .Contains("Property 'Orders.Lines.Detail.Order' of perspective model 'LedgerModel' refers back to 'Order'");
  }

  /// <summary>
  /// A circular reference is reported whatever driver the project uses: neither can write a graph that has no end.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelTwoPerspectivesProject_IsReportedOnceAsync() {
    var source = _source("  public Node? Tree { get; set; }", """
      public sealed class Node { public List<Node>? Children { get; set; } public Node? Parent { get; set; } }
      """) + """

      public class SecondLedgerProjection : IPerspectiveFor<LedgerModel, Noted> {
        public LedgerModel Apply(LedgerModel currentData, Noted eventData) => currentData;
      }
      """;

    await Assert.That(await _whiz201Async(source)).Count().IsEqualTo(1);
  }

  /// <summary>
  /// A model from a referenced assembly has no source here, so the error is reported at the perspective that projects
  /// it.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AModelFromAReferencedAssembly_IsReportedAtThePerspectiveAsync() {
    var library = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
      using System.Collections.Generic;
      namespace Library;
      public sealed class Node { public List<Node>? Children { get; set; } public Node? Parent { get; set; } }
      public class SharedModel { public Node? Tree { get; set; } }
      """).WithAssemblyName("Library").WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    await using var image = new MemoryStream();
    await Assert.That(library.Emit(image).Success).IsTrue().Because("the model assembly has to compile to be referenced");
    var consumer = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;
      namespace TestApp;
      public record Noted([property: StreamId] Guid Id) : IEvent;
      public class SharedProjection : IPerspectiveFor<Library.SharedModel, Noted> {
        public Library.SharedModel Apply(Library.SharedModel currentData, Noted eventData) => currentData;
      }
      """).AddReferences(MetadataReference.CreateFromImage(image.ToArray()));

    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<PerspectiveModelCircularReferenceAnalyzer>(consumer);
    var reported = diagnostics.Where(d => d.Id == "WHIZ201").ToList();

    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(_locatedAt(reported[0])).IsEqualTo("SharedProjection");
  }

  /// <summary>Only classes and structs project a model; an interface extending a perspective interface does not.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task AnInterfaceExtendingAPerspectiveInterface_IsNotAPerspectiveAsync() {
    var source = _source("  public string Name { get; set; } = \"\";", """
      public sealed class Node { public List<Node>? Children { get; set; } public Node? Parent { get; set; } }
      public sealed class CircularModel { public Node? Tree { get; set; } }
      public interface ICircularProjection : IPerspectiveFor<CircularModel, Noted> { }
      """);

    await Assert.That(await _whiz201Async(source)).IsEmpty();
  }
}
