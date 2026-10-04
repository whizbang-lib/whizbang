using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>
/// Perspectives whose filter values are promoted jsonb columns, one Extracted and one Split, driven through the
/// generated runner: one event sets the filter values, the other changes only a document field. Applying the
/// second after the first shows the jsonb columns are written as JSON and read back for the next event.
/// </summary>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperJsonbColumnTests.cs</tests>
public class DapperJsonbExtractedPerspective :
    IPerspectiveFor<DapperJsonbExtractedModel, DapperJsonbFiltersSetEvent>,
    IPerspectiveFor<DapperJsonbExtractedModel, DapperJsonbNoteChangedEvent> {

  public DapperJsonbExtractedModel Apply(DapperJsonbExtractedModel currentData, DapperJsonbFiltersSetEvent @event) =>
    currentData with { Id = @event.StreamId, Filters = @event.Filters, Labels = @event.Labels, Place = @event.Place, Owners = @event.Owners };

  public DapperJsonbExtractedModel Apply(DapperJsonbExtractedModel currentData, DapperJsonbNoteChangedEvent @event) =>
    currentData with { Note = @event.Note };
}

/// <inheritdoc cref="DapperJsonbExtractedPerspective"/>
public class DapperJsonbSplitPerspective :
    IPerspectiveFor<DapperJsonbSplitModel, DapperJsonbFiltersSetEvent>,
    IPerspectiveFor<DapperJsonbSplitModel, DapperJsonbNoteChangedEvent> {

  public DapperJsonbSplitModel Apply(DapperJsonbSplitModel currentData, DapperJsonbFiltersSetEvent @event) =>
    currentData with { Id = @event.StreamId, Filters = @event.Filters, Labels = @event.Labels, Place = @event.Place, Owners = @event.Owners };

  public DapperJsonbSplitModel Apply(DapperJsonbSplitModel currentData, DapperJsonbNoteChangedEvent @event) =>
    currentData with { Note = @event.Note };
}

/// <summary>An element of a list of objects.</summary>
public sealed record DapperJsonbLabel(string Key, string Value);

/// <summary>An object held in a column.</summary>
public sealed record DapperJsonbPlace(string City, int Zone);

/// <summary>Filter values in jsonb columns and in the document.</summary>
[PerspectiveStorage(FieldStorageMode.Extracted)]
public record DapperJsonbExtractedModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public Dictionary<string, string[]> Filters { get; init; } = [];

  [PhysicalField]
  public List<DapperJsonbLabel>? Labels { get; init; }

  [PhysicalField]
  public DapperJsonbPlace? Place { get; init; }

  [PhysicalField]
  public List<Guid> Owners { get; init; } = [];

  public string? Note { get; init; }
}

/// <summary>Filter values in jsonb columns only.</summary>
[PerspectiveStorage(FieldStorageMode.Split)]
public record DapperJsonbSplitModel {
  [StreamId]
  public Guid Id { get; init; }

  [PhysicalField]
  public Dictionary<string, string[]> Filters { get; init; } = [];

  [PhysicalField]
  public List<DapperJsonbLabel>? Labels { get; init; }

  [PhysicalField]
  public DapperJsonbPlace? Place { get; init; }

  [PhysicalField]
  public List<Guid> Owners { get; init; } = [];

  public string? Note { get; init; }
}

/// <summary>Sets the filter values.</summary>
public record DapperJsonbFiltersSetEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required Dictionary<string, string[]> Filters { get; init; }
  public required List<DapperJsonbLabel> Labels { get; init; }
  public required DapperJsonbPlace Place { get; init; }
  public required List<Guid> Owners { get; init; }
}

/// <summary>Changes only the document field.</summary>
public record DapperJsonbNoteChangedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
  public required string Note { get; init; }
}

[JsonSerializable(typeof(DapperJsonbExtractedModel))]
[JsonSerializable(typeof(DapperJsonbSplitModel))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(List<DapperJsonbLabel>))]
[JsonSerializable(typeof(DapperJsonbPlace))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperJsonbColumnJsonContext : JsonSerializerContext;
