using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A Split-storage perspective whose events each touch one field: one sets the promoted fields, the
/// other changes only a document field. Applying the second after the first is what shows whether the
/// promoted values survive a reload, because the document never holds them.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitPhysicalFieldReloadTests.cs</tests>
public class SplitReloadPerspective :
    IPerspectiveFor<SplitReloadModel, SplitReloadStatusSetEvent>,
    IPerspectiveFor<SplitReloadModel, SplitReloadNoteChangedEvent> {

  public SplitReloadModel Apply(SplitReloadModel currentData, SplitReloadStatusSetEvent @event) =>
    currentData with { Id = @event.StreamId, Status = @event.Status, Priority = @event.Priority };

  public SplitReloadModel Apply(SplitReloadModel currentData, SplitReloadNoteChangedEvent @event) =>
    currentData with { Note = @event.Note };
}

/// <summary>
/// Read model stored Split: <see cref="Status"/> and <see cref="Priority"/> live only in their columns,
/// <see cref="Note"/> only in the document.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Split)]
[SuppressIndexAdvisory("test fixture; the promoted fields are read back, never filtered on")]
public record SplitReloadModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Status { get; set; }

  [PhysicalField]
  public int Priority { get; set; }

  public string? Note { get; init; }
}

/// <summary>Sets the promoted fields.</summary>
public record SplitReloadStatusSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Status { get; init; }
  public required int Priority { get; init; }
}

/// <summary>Changes only the document field.</summary>
public record SplitReloadNoteChangedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Note { get; init; }
}
