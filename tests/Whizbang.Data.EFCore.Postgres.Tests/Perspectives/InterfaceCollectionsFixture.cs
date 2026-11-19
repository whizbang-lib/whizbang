// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Perspectives in a context of their own whose models hold a collection interface of records the mapped path cannot
// fill (ICollection, IReadOnlyCollection, IEnumerable), so each is stored as one opaque document and written whole by
// the generated message context (#2114). Each model holds one such member, so one interface failing cannot hide
// another.

/// <summary>A record held in a collection interface.</summary>
public sealed record InterfacedTag(Guid TagId, string Label);

/// <summary>A model holding an <c>ICollection</c> of records.</summary>
public class CollectionInterfaceDocument {
  [StreamId] public Guid Id { get; set; }
  public ICollection<InterfacedTag> Tags { get; set; } = [];
}

/// <summary>A model holding an <c>IReadOnlyCollection</c> of records.</summary>
public class ReadOnlyCollectionInterfaceDocument {
  [StreamId] public Guid Id { get; set; }
  public IReadOnlyCollection<InterfacedTag> Tags { get; set; } = [];
}

/// <summary>A model holding an <c>IEnumerable</c> of records.</summary>
public class EnumerableInterfaceDocument {
  [StreamId] public Guid Id { get; set; }
  public IEnumerable<InterfacedTag> Tags { get; set; } = [];
}

public record InterfaceCollectionNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("interface-collections")]
public class CollectionInterfaceProjection : IPerspectiveFor<CollectionInterfaceDocument, InterfaceCollectionNoted> {
  public CollectionInterfaceDocument Apply(CollectionInterfaceDocument currentData, InterfaceCollectionNoted eventData) => currentData;
}

[WhizbangPerspective("interface-collections")]
public class ReadOnlyCollectionInterfaceProjection : IPerspectiveFor<ReadOnlyCollectionInterfaceDocument, InterfaceCollectionNoted> {
  public ReadOnlyCollectionInterfaceDocument Apply(ReadOnlyCollectionInterfaceDocument currentData, InterfaceCollectionNoted eventData) => currentData;
}

[WhizbangPerspective("interface-collections")]
public class EnumerableInterfaceProjection : IPerspectiveFor<EnumerableInterfaceDocument, InterfaceCollectionNoted> {
  public EnumerableInterfaceDocument Apply(EnumerableInterfaceDocument currentData, InterfaceCollectionNoted eventData) => currentData;
}

[WhizbangDbContext("interface-collections", Schema = "public")]
public partial class InterfaceCollectionsDbContext(DbContextOptions<InterfaceCollectionsDbContext> options) : DbContext(options) {
}
