using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Perspectives in a context of their own whose filter values live in promoted jsonb columns, so the schema
// pass, the model configuration, the registries and the hydrators are all the generated ones. See
// PhysicalJsonbContainmentIntegrationTests and JsonbColumnStorageIntegrationTests (#1019, #1020, #1023).

/// <summary>An element of a list of objects.</summary>
public sealed record JsonbLabel(string Key, string Value);

/// <summary>An object held in a column.</summary>
public sealed class JsonbPlace {
  public string City { get; set; } = "";
  public int Zone { get; set; }
}

/// <summary>A model keeping its filter values in jsonb columns as well as in its document.</summary>
public static class JsonbExtractedItem {
  [PerspectiveStorage(FieldStorageMode.Extracted)]
  [PerspectiveTableStorage(DataCompression = ColumnCompression.Lz4, ToastTupleTarget = 512)]
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    [PhysicalField(Storage = ColumnStorage.Main, MaxBytes = 4096)]
    [Indexed(IndexKinds.Containment)]
    public Dictionary<string, string[]> GridFilter { get; set; } = [];

    [PhysicalField]
    [Indexed(IndexKinds.Containment)]
    public List<JsonbLabel> Labels { get; set; } = [];

    [PhysicalField]
    [Indexed(IndexKinds.Containment)]
    public List<string> Tags { get; set; } = [];

    [PhysicalField]
    [Indexed(IndexKinds.Containment)]
    public JsonbPlace? Place { get; set; }

    [PhysicalField]
    public DateTime Stamp { get; set; }
  }
}

/// <summary>The same filter values on a model that keeps them in the columns only.</summary>
public static class JsonbSplitItem {
  [PerspectiveStorage(FieldStorageMode.Split)]
  public record Model {
    [StreamId]
    public Guid Id { get; init; }

    public string Title { get; init; } = "";

    [PhysicalField]
    [Indexed(IndexKinds.Containment)]
    public Dictionary<string, string[]> GridFilter { get; init; } = [];

    [PhysicalField]
    public List<JsonbLabel>? Labels { get; init; }
  }
}

public record JsonbItemNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("jsonb-columns")]
public class JsonbExtractedItemProjection : IPerspectiveFor<JsonbExtractedItem.Model, JsonbItemNoted> {
  public JsonbExtractedItem.Model Apply(JsonbExtractedItem.Model currentData, JsonbItemNoted eventData) => currentData;
}

[WhizbangPerspective("jsonb-columns")]
public class JsonbSplitItemProjection : IPerspectiveFor<JsonbSplitItem.Model, JsonbItemNoted> {
  public JsonbSplitItem.Model Apply(JsonbSplitItem.Model currentData, JsonbItemNoted eventData) => currentData;
}

[WhizbangDbContext("jsonb-columns", Schema = "public")]
public partial class JsonbColumnsDbContext(DbContextOptions<JsonbColumnsDbContext> options) : DbContext(options) {
}
