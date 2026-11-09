// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Whether a perspective model's recursive types form a circular reference: a reference that lets an instance reach its
/// own container, so the document has no end and cannot be saved (WHIZ201, owner decision on #2112).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule.</strong> Take the types the document stores, starting at the model, and the stored members between
/// them (a property to its type, or a collection or array to its element; primitives and members left out of the
/// document with <c>[JsonIgnore]</c> or <c>[NotMapped]</c> are not edges). A <em>recursion</em> is a group of types that
/// reach each other through those members. A member is a <em>back-reference</em> when it is single-valued, inside a
/// recursion, and its type is the type that holds it or one of that type's containers on the path from the model (it
/// points back up the document). The model holds a circular reference when a recursion holds a back-reference and also
/// stores a collection: the collection is the containment direction, so the back-reference points an element at its
/// container or an ancestor (<c>Node.Children</c> with <c>Node.Parent</c>, <c>Order.Lines</c> with
/// <c>Line.Order</c>, or <c>Order.Lines</c>, <c>Line.Detail</c> and <c>Detail.Order</c>).
/// </para>
/// <para>
/// <strong>Not circular.</strong> A tree (<c>Node.Children</c>) and two types holding collections of each other
/// (alternating levels of one tree) store only collections; a linked list (<c>Link.Next</c>) stores only single
/// references. Each instance in them has one container, which is what makes them writable.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> A recursion of single references only that points back (<c>Next</c> with
/// <c>Prev</c>), a collection that points back (<c>Line.Orders</c>), a cycle through a member typed as an interface, an
/// abstract base or <c>object</c>, and an instance that puts a node among its own descendants in a model that is a
/// tree. None of these is truncated in silence: writing an object cycle fails at the write with the serializer's cycle
/// detection (<c>JsonException</c>), and nothing is stored.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz201</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveModelCircularReferenceAnalyzerTests.cs</tests>
public static class CircularReferenceDiscovery {
  /// <summary>Every back-reference in a recursion that also stores a collection, in the order the model declares them.</summary>
  /// <param name="modelType">The perspective's model type.</param>
  /// <returns>Each back-reference with its path, the type it points back to, and the path of a collection in its recursion.</returns>
  public static IReadOnlyList<CircularReference> Find(INamedTypeSymbol modelType) {
    var (paths, order, edges) = _storedGraph(modelType);

    var reach = _reachability(order, edges);
    bool together(INamedTypeSymbol a, INamedTypeSymbol b) =>
      reach[a].Contains(b) && reach[b].Contains(a);

    var found = new List<CircularReference>();
    foreach (var edge in edges) {
      if (edge.IsCollection || !together(edge.From, edge.To) || !_isUpTheDocument(paths[edge.To], paths[edge.From])) {
        continue;
      }
      var collection = edges.Find(e => e.IsCollection && together(e.From, edge.From) && together(e.To, edge.From));
      if (collection is not null) {
        found.Add(new CircularReference(edge.Property, edge.Path, edge.To, collection.Path));
      }
    }
    return found;
  }

  /// <summary>
  /// The types the document stores, each with its shortest path from the model, and the stored members between them.
  /// </summary>
  /// <remarks>
  /// Breadth first, so each type's path is its shortest from the model, and a back-reference is the member whose type
  /// sits on its own holder's path.
  /// </remarks>
  private static (Dictionary<INamedTypeSymbol, string> Paths, List<INamedTypeSymbol> Order, List<Edge> Edges) _storedGraph(
      INamedTypeSymbol modelType) {
    var paths = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default) { [modelType] = string.Empty };
    var order = new List<INamedTypeSymbol> { modelType };
    var edges = new List<Edge>();
    for (var i = 0; i < order.Count; i++) {
      var holder = order[i];
      foreach (var property in holder.GetMembers().OfType<IPropertySymbol>()) {
        if (_storedEdge(holder, property, paths[holder]) is not { } edge) {
          continue;
        }
        edges.Add(edge);
        if (!paths.ContainsKey(edge.To)) {
          paths.Add(edge.To, edge.Path);
          order.Add(edge.To);
        }
      }
    }
    return (paths, order, edges);
  }

  /// <summary>The stored member <paramref name="property"/> as an edge of the graph, or null when it stores no type.</summary>
  private static Edge? _storedEdge(INamedTypeSymbol holder, IPropertySymbol property, string holderPath) {
    var (target, isCollection) = PolymorphicModelDiscovery.IsAnalyzableProperty(property)
      ? MappedPathDiscovery.StoredTargetOf(property)
      : (null, false);
    if (target is null) {
      return null;
    }
    var path = holderPath.Length == 0 ? property.Name : $"{holderPath}.{property.Name}";
    return new Edge(holder, target, property, isCollection, path);
  }

  /// <summary>Whether the target's path is the holder's own or one of its containers' (a prefix of it).</summary>
  private static bool _isUpTheDocument(string targetPath, string holderPath) =>
    targetPath.Length == 0
    || holderPath == targetPath
    || holderPath.StartsWith(targetPath + ".", System.StringComparison.Ordinal);

  /// <summary>Every type each type reaches through one or more stored members.</summary>
  private static Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>> _reachability(
      List<INamedTypeSymbol> types, List<Edge> edges) {
    var reach = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
    foreach (var start in types) {
      var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
      var pending = new Stack<INamedTypeSymbol>();
      pending.Push(start);
      while (pending.Count > 0) {
        var current = pending.Pop();
        foreach (var edge in edges) {
          if (SymbolEqualityComparer.Default.Equals(edge.From, current) && seen.Add(edge.To)) {
            pending.Push(edge.To);
          }
        }
      }
      reach.Add(start, seen);
    }
    return reach;
  }

  private sealed record Edge(INamedTypeSymbol From, INamedTypeSymbol To, IPropertySymbol Property, bool IsCollection, string Path);
}

/// <summary>A back-reference that makes a perspective model circular.</summary>
/// <param name="BackReference">The single-valued member that points back up the document.</param>
/// <param name="Path">Its path from the model, such as <c>Orders.Lines.Order</c>.</param>
/// <param name="Target">The type it points back to.</param>
/// <param name="ContainerPath">The path of a collection in the same recursion, the containment it reverses.</param>
public sealed record CircularReference(IPropertySymbol BackReference, string Path, INamedTypeSymbol Target, string ContainerPath);
