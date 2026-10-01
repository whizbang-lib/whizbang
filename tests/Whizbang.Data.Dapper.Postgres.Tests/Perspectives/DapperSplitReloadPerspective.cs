using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// A Split-storage perspective whose events each touch one field: one sets the promoted fields, the
/// other changes only a document field. Applying the second after the first is what shows whether the
/// promoted values survive a reload, because the document never holds them.
/// </summary>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperSplitPhysicalFieldReloadTests.cs</tests>
public class DapperSplitReloadPerspective :
    IPerspectiveFor<DapperSplitReloadModel, DapperSplitReloadStatusSetEvent>,
    IPerspectiveFor<DapperSplitReloadModel, DapperSplitReloadNoteChangedEvent> {

  public DapperSplitReloadModel Apply(DapperSplitReloadModel currentData, DapperSplitReloadStatusSetEvent @event) =>
    currentData with { Id = @event.StreamId, Status = @event.Status, Priority = @event.Priority, Tier = @event.Tier, PlacedAt = @event.PlacedAt };

  public DapperSplitReloadModel Apply(DapperSplitReloadModel currentData, DapperSplitReloadNoteChangedEvent @event) =>
    currentData with { Note = @event.Note };
}

/// <summary>A promoted enum, which the store writes as its underlying number.</summary>
public enum DapperSplitReloadTier { None, Gold }

/// <summary>
/// Read model stored Split: the promoted fields live only in their columns, <see cref="Note"/> only in
/// the document.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Split)]
public record DapperSplitReloadModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Status { get; init; }

  [PhysicalField]
  public int Priority { get; init; }

  [PhysicalField]
  public DapperSplitReloadTier? Tier { get; init; }

  [PhysicalField]
  public DateTimeOffset? PlacedAt { get; init; }

  public string? Note { get; init; }
}

/// <summary>Sets the promoted fields.</summary>
public record DapperSplitReloadStatusSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Status { get; init; }
  public required int Priority { get; init; }
  public required DapperSplitReloadTier Tier { get; init; }
  public required DateTimeOffset PlacedAt { get; init; }
}

/// <summary>Changes only the document field.</summary>
public record DapperSplitReloadNoteChangedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Note { get; init; }
}

[JsonSerializable(typeof(DapperSplitReloadModel))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperSplitReloadJsonContext : JsonSerializerContext;
