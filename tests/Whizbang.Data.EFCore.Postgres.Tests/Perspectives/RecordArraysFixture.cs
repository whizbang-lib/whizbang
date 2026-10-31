// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// A perspective in a context of its own whose model holds arrays of records, top level and inside a collection
// element, so the schema pass, the model configuration and the read are all the generated ones (#1910).
// WHIZ200 says such a model is stored as one opaque document, which is exactly what these tests read back.
#pragma warning disable WHIZ200

/// <summary>A record held in an array.</summary>
public sealed record ArrayedTag(Guid TagId, string Label);

/// <summary>A collection element that itself holds an array of records.</summary>
public sealed record ArrayedGroup {
  public string Name { get; init; } = "";
  public ArrayedTag[] Tags { get; init; } = [];
}

/// <summary>A model whose collections are arrays of records.</summary>
public class RecordArrayDocument {
  [StreamId]
  public Guid Id { get; set; }

  public string Title { get; set; } = "";

  public ArrayedTag[] Tags { get; set; } = [];

  public ArrayedGroup[] Groups { get; set; } = [];

  public List<ArrayedGroup> Sections { get; set; } = [];
}

/// <summary>A model whose only arrays hold primitives, which the mapped path stores as values.</summary>
public class PrimitiveArrayDocument {
  [StreamId]
  public Guid Id { get; set; }

  public string[] Labels { get; set; } = [];

  public Guid[] Owners { get; set; } = [];
}

public record RecordArrayNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("record-arrays")]
public class RecordArrayProjection : IPerspectiveFor<RecordArrayDocument, RecordArrayNoted> {
  public RecordArrayDocument Apply(RecordArrayDocument currentData, RecordArrayNoted eventData) => currentData;
}

[WhizbangPerspective("record-arrays")]
public class PrimitiveArrayProjection : IPerspectiveFor<PrimitiveArrayDocument, RecordArrayNoted> {
  public PrimitiveArrayDocument Apply(PrimitiveArrayDocument currentData, RecordArrayNoted eventData) => currentData;
}

[WhizbangDbContext("record-arrays", Schema = "public")]
public partial class RecordArraysDbContext(DbContextOptions<RecordArraysDbContext> options) : DbContext(options) {
}
