// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Three perspectives in a context of their own, one per way a model can answer "do my queries match
// on any field, and on metadata?", so the schema pass that builds them is the shipped one and the
// catalog can be read back after it. See DocumentIndexInitializationTests.

/// <summary>A model that declares nothing: the transitional default.</summary>
public static class DocumentIndexUndeclared {
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    [Indexed]
    public string Status { get; set; } = string.Empty;
  }
}

/// <summary>A model whose queries never match on an unindexed field, and that keeps one index it no longer declares.</summary>
public static class DocumentIndexOptedOut {
  [PerspectiveQueries(MatchOnAnyField = false)]
  [KeepSchemaObject("idx_document_index_opted_out_legacy", Reason = "the reporting job reads it")]
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    [Indexed]
    public string Status { get; set; } = string.Empty;
  }
}

/// <summary>A model whose queries match on any field and on metadata.</summary>
public static class DocumentIndexMetadata {
  [PerspectiveQueries(MatchOnAnyField = true, MatchOnMetadata = true)]
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    public string Region { get; set; } = string.Empty;
  }
}

public record DocumentIndexUndeclaredNoted([property: StreamId] Guid Id) : IEvent;

public record DocumentIndexOptedOutNoted([property: StreamId] Guid Id) : IEvent;

public record DocumentIndexMetadataNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("document-indexes")]
public class DocumentIndexUndeclaredProjection : IPerspectiveFor<DocumentIndexUndeclared.Model, DocumentIndexUndeclaredNoted> {
  public DocumentIndexUndeclared.Model Apply(DocumentIndexUndeclared.Model currentData, DocumentIndexUndeclaredNoted eventData) => currentData;
}

[WhizbangPerspective("document-indexes")]
public class DocumentIndexOptedOutProjection : IPerspectiveFor<DocumentIndexOptedOut.Model, DocumentIndexOptedOutNoted> {
  public DocumentIndexOptedOut.Model Apply(DocumentIndexOptedOut.Model currentData, DocumentIndexOptedOutNoted eventData) => currentData;
}

[WhizbangPerspective("document-indexes")]
public class DocumentIndexMetadataProjection : IPerspectiveFor<DocumentIndexMetadata.Model, DocumentIndexMetadataNoted> {
  public DocumentIndexMetadata.Model Apply(DocumentIndexMetadata.Model currentData, DocumentIndexMetadataNoted eventData) => currentData;
}

[WhizbangDbContext("document-indexes", Schema = "public")]
public partial class DocumentIndexesDbContext(DbContextOptions<DocumentIndexesDbContext> options) : DbContext(options) {
}
