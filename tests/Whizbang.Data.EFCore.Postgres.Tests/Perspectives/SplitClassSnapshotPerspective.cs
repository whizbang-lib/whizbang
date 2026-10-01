using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A Split-storage perspective over a class model: one event sets the promoted fields, the other changes
/// only a document field. A class is stripped in place before the write, where a record is copied, which is
/// the shape a snapshot taken after the write used to lose the promoted fields from.
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitClassSnapshotRewindTests.cs</tests>
public class SplitClassSnapshotPerspective :
    IPerspectiveFor<SplitClassSnapshotModel, SplitClassSnapshotStatusSetEvent>,
    IPerspectiveFor<SplitClassSnapshotModel, SplitClassSnapshotNoteChangedEvent> {

  public SplitClassSnapshotModel Apply(SplitClassSnapshotModel currentData, SplitClassSnapshotStatusSetEvent @event) {
    currentData.Id = @event.StreamId;
    currentData.Status = @event.Status;
    currentData.Priority = @event.Priority;
    return currentData;
  }

  public SplitClassSnapshotModel Apply(SplitClassSnapshotModel currentData, SplitClassSnapshotNoteChangedEvent @event) {
    currentData.Note = @event.Note;
    return currentData;
  }
}

/// <summary>
/// Read model stored Split, as a class: <see cref="Status"/> and <see cref="Priority"/> live only in their
/// columns, <see cref="Note"/> only in the document.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Split)]
[SuppressIndexAdvisory("test fixture; the promoted fields are read back, never filtered on")]
public class SplitClassSnapshotModel {
  [StreamId]
  public Guid Id { get; set; }

  [PhysicalField]
  public string? Status { get; set; }

  [PhysicalField]
  public int Priority { get; set; }

  public string? Note { get; set; }
}

/// <summary>Sets the promoted fields.</summary>
public record SplitClassSnapshotStatusSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Status { get; init; }
  public required int Priority { get; init; }
}

/// <summary>Changes only the document field.</summary>
public record SplitClassSnapshotNoteChangedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Note { get; init; }
}
