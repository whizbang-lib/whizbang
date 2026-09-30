using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Perspectives whose models declare their promoted fields <c>init</c>-only, the shape a record model is
/// documented with. The EF Core registration generated for this project copies each promoted column into the
/// model it materializes, so this file compiling at all is part of what the tests show: a copy that assigned
/// the property would not (CS8852).
/// </summary>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/InitOnlyPhysicalFieldHydrationTests.cs</tests>
public class InitOnlySplitPerspective : IPerspectiveFor<InitOnlySplitModel, InitOnlyFieldsSetEvent> {
  public InitOnlySplitModel Apply(InitOnlySplitModel currentData, InitOnlyFieldsSetEvent @event) =>
    currentData with { Id = @event.StreamId, Status = @event.Status, Priority = @event.Priority, Note = @event.Note };
}

/// <summary>The same fields, stored Extracted: in their columns and in the document.</summary>
public class InitOnlyExtractedPerspective : IPerspectiveFor<InitOnlyExtractedModel, InitOnlyFieldsSetEvent> {
  public InitOnlyExtractedModel Apply(InitOnlyExtractedModel currentData, InitOnlyFieldsSetEvent @event) =>
    currentData with { Id = @event.StreamId, Status = @event.Status, Priority = @event.Priority };
}

/// <summary>A class model stored Extracted, with one promoted field <c>init</c>-only and one settable.</summary>
public class InitOnlyExtractedClassPerspective : IPerspectiveFor<InitOnlyExtractedClassModel, InitOnlyFieldsSetEvent> {
  public InitOnlyExtractedClassModel Apply(InitOnlyExtractedClassModel currentData, InitOnlyFieldsSetEvent @event) =>
    new() { Id = @event.StreamId, Status = @event.Status, Priority = @event.Priority };
}

/// <summary>
/// Read model stored Split, every promoted field <c>init</c>-only: <see cref="Status"/> and <see cref="Priority"/>
/// live only in their columns, <see cref="Note"/> only in the document.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Split)]
[SuppressIndexAdvisory("test fixture; the promoted fields are read back, never filtered on")]
public record InitOnlySplitModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Status { get; init; }

  [PhysicalField]
  public int Priority { get; init; }

  public string? Note { get; init; }
}

/// <summary>Read model stored Extracted, every promoted field <c>init</c>-only.</summary>
[PerspectiveStorage(FieldStorageMode.Extracted)]
[SuppressIndexAdvisory("test fixture; the promoted fields are read back, never filtered on")]
public record InitOnlyExtractedModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public string? Status { get; init; }

  [PhysicalField]
  public int Priority { get; init; }
}

/// <summary>
/// A class stored Extracted: <see cref="Status"/> is <c>init</c>-only, which a class cannot copy into
/// an instance it already has, and <see cref="Priority"/> is settable.
/// </summary>
[PerspectiveStorage(FieldStorageMode.Extracted)]
[SuppressIndexAdvisory("test fixture; the promoted fields are read back, never filtered on")]
public class InitOnlyExtractedClassModel {
  [StreamId]
  public Guid Id { get; set; }

  [PhysicalField]
  public string? Status { get; init; }

  [PhysicalField]
  public int Priority { get; set; }
}

/// <summary>Sets every field of the models above.</summary>
public record InitOnlyFieldsSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Status { get; init; }
  public required int Priority { get; init; }
  public string? Note { get; init; }
}
