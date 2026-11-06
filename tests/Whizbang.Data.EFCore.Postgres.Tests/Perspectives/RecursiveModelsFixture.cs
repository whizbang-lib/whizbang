// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Perspectives in a context of their own whose models hold recursive types (a tree node holding its children, and a
// branch and a leaf holding each other), so the schema pass, the model configuration and the read are all the
// generated ones (#2112). WHIZ200 says each such model is stored as one opaque document, which is what these tests
// read back at every depth.
#pragma warning disable WHIZ200

/// <summary>A tree node whose children are a <c>List</c> of itself.</summary>
public sealed record ListTreeNode {
  public string Label { get; init; } = "";
  public List<ListTreeNode>? Children { get; init; }
}

/// <summary>A tree node whose children are an <c>IList</c> of itself.</summary>
public sealed record InterfaceTreeNode {
  public string Label { get; init; } = "";
  public IList<InterfaceTreeNode>? Children { get; init; }
}

/// <summary>A tree node whose children are an array of itself.</summary>
public sealed record ArrayTreeNode {
  public string Label { get; init; } = "";
  public ArrayTreeNode[]? Children { get; init; }
}

/// <summary>One half of a mutual recursion: a branch holds leaves.</summary>
public sealed class MutualBranch {
  public string Label { get; set; } = "";
  public List<MutualLeaf>? Leaves { get; set; }
}

/// <summary>The other half: a leaf holds branches.</summary>
public sealed class MutualLeaf {
  public string Label { get; set; } = "";
  public List<MutualBranch>? Branches { get; set; }
}

/// <summary>A model holding a tree of <see cref="ListTreeNode"/>.</summary>
public class ListTreeDocument {
  [StreamId] public Guid Id { get; set; }
  public List<ListTreeNode>? Skeleton { get; set; }
}

/// <summary>A model holding a tree of <see cref="InterfaceTreeNode"/>.</summary>
public class InterfaceTreeDocument {
  [StreamId] public Guid Id { get; set; }
  public IList<InterfaceTreeNode>? Skeleton { get; set; }
}

/// <summary>A model holding a tree of <see cref="ArrayTreeNode"/>.</summary>
public class ArrayTreeDocument {
  [StreamId] public Guid Id { get; set; }
  public List<ArrayTreeNode>? Skeleton { get; set; }
}

/// <summary>A model holding a mutually recursive tree.</summary>
public class MutualTreeDocument {
  [StreamId] public Guid Id { get; set; }
  public List<MutualBranch>? Roots { get; set; }
}

public record RecursiveTreeNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("recursive-models")]
public class ListTreeProjection : IPerspectiveFor<ListTreeDocument, RecursiveTreeNoted> {
  public ListTreeDocument Apply(ListTreeDocument currentData, RecursiveTreeNoted eventData) => currentData;
}

[WhizbangPerspective("recursive-models")]
public class InterfaceTreeProjection : IPerspectiveFor<InterfaceTreeDocument, RecursiveTreeNoted> {
  public InterfaceTreeDocument Apply(InterfaceTreeDocument currentData, RecursiveTreeNoted eventData) => currentData;
}

[WhizbangPerspective("recursive-models")]
public class ArrayTreeProjection : IPerspectiveFor<ArrayTreeDocument, RecursiveTreeNoted> {
  public ArrayTreeDocument Apply(ArrayTreeDocument currentData, RecursiveTreeNoted eventData) => currentData;
}

[WhizbangPerspective("recursive-models")]
public class MutualTreeProjection : IPerspectiveFor<MutualTreeDocument, RecursiveTreeNoted> {
  public MutualTreeDocument Apply(MutualTreeDocument currentData, RecursiveTreeNoted eventData) => currentData;
}

[WhizbangDbContext("recursive-models", Schema = "public")]
public partial class RecursiveModelsDbContext(DbContextOptions<RecursiveModelsDbContext> options) : DbContext(options) {
}
