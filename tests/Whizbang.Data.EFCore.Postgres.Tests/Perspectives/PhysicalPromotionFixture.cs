using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// A perspective in a context of its own whose fields are promoted to physical columns, so the schema pass
// that builds and promotes them is the shipped one. See PhysicalFieldPromotionTests and
// PhysicalColumnFillMaintenanceStepTests (#1009).

/// <summary>A model whose indexed fields are promoted to columns.</summary>
public static class PromotedItem {
  public class Model {
    [StreamId]
    public Guid Id { get; set; }

    /// <summary>Ordered and searched: its search index shares a name with its document search index.</summary>
    [PhysicalField]
    [Indexed]
    [Indexed(IndexKinds.Search)]
    public string? Name { get; set; }

    /// <summary>Matched by substring, folding case.</summary>
    [PhysicalField]
    [Indexed(IndexKinds.Substring, caseInsensitive: true)]
    public string? Code { get; set; }

    /// <summary>A number, whose document index was over a cast.</summary>
    [PhysicalField]
    [Indexed]
    public int Rank { get; set; }
  }
}

public record PromotedItemNoted([property: StreamId] Guid Id) : IEvent;

[WhizbangPerspective("physical-promotion")]
public class PromotedItemProjection : IPerspectiveFor<PromotedItem.Model, PromotedItemNoted> {
  public PromotedItem.Model Apply(PromotedItem.Model currentData, PromotedItemNoted eventData) => currentData;
}

[WhizbangDbContext("physical-promotion", Schema = "public")]
public partial class PhysicalPromotionDbContext(DbContextOptions<PhysicalPromotionDbContext> options) : DbContext(options) {
}
