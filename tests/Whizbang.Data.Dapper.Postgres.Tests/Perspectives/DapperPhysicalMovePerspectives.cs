using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

// Perspectives whose generated Dapper schema the move tests apply, so the statements that move their fields
// are the shipped ones. See DapperPhysicalFieldMoveTests (#1010, #1021, #1022).

/// <summary>An enumeration, stored as its number in the column and in the document.</summary>
public enum DapperMoveStage { Backlog = 0, Doing = 1, Done = 2 }

/// <summary>An element of a collection kept in a jsonb column.</summary>
public sealed record DapperMoveTag(string Label, int Weight);

/// <summary>
/// An Extracted model: the previous release kept these fields only in the document, this one promotes them.
/// Both releases read the document, so its moves fill the columns without syncing writes.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Extracted)]
public record DapperPromotedModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Name { get; init; }

  [PhysicalField]
  public int Rank { get; init; }

  [PhysicalField]
  public DapperMoveStage Lane { get; init; }

  [PhysicalField(ColumnType = "jsonb")]
  public List<DapperMoveTag>? Tags { get; init; }
}

/// <summary>A Split model: promoted fields of each kind, and fields an earlier release kept in columns.</summary>
[PerspectiveStorage(FieldStorageMode.Split)]
public record DapperSplitMovedModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Title { get; init; }

  [PhysicalField]
  public int Points { get; init; }

  [PhysicalField(ColumnType = "uuid[]")]
  public List<Guid>? Watchers { get; init; }

  /// <summary>Kept in the document: a demoted number.</summary>
  public int Score { get; init; }

  /// <summary>Kept in the document: a demoted array.</summary>
  public List<string>? Labels { get; init; }
}

public record DapperMoveNoted([property: StreamId] Guid Id) : IEvent;

public class DapperPromotedPerspective : IPerspectiveFor<DapperPromotedModel, DapperMoveNoted> {
  public DapperPromotedModel Apply(DapperPromotedModel currentData, DapperMoveNoted eventData) => currentData;
}

public class DapperSplitMovedPerspective : IPerspectiveFor<DapperSplitMovedModel, DapperMoveNoted> {
  public DapperSplitMovedModel Apply(DapperSplitMovedModel currentData, DapperMoveNoted eventData) => currentData;
}

[JsonSerializable(typeof(DapperSplitMovedModel))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperMoveJsonContext : JsonSerializerContext;
