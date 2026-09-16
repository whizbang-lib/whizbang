using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A feature that nests its model under a static holder, as consumers commonly do.</summary>
public static class CollidingFirst {
  /// <summary>The first feature's model, named the way the second feature's is.</summary>
  public class Model {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid Id { get; set; }

    /// <summary>A label.</summary>
    public string Label { get; set; } = string.Empty;
  }
}

/// <summary>A second feature with a model of the same simple name.</summary>
public static class CollidingSecond {
  /// <summary>The second feature's model.</summary>
  public class Model {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid Id { get; set; }

    /// <summary>A count.</summary>
    public int Count { get; set; }
  }
}

/// <summary>An event the first feature applies.</summary>
public record CollidingFirstNoted([property: StreamId] Guid Id) : IEvent;

/// <summary>An event the second feature applies.</summary>
public record CollidingSecondNoted([property: StreamId] Guid Id) : IEvent;

/// <summary>Projects the first model.</summary>
[WhizbangPerspective("colliding-names")]
public class CollidingFirstProjection : IPerspectiveFor<CollidingFirst.Model, CollidingFirstNoted> {
  /// <inheritdoc />
  public CollidingFirst.Model Apply(CollidingFirst.Model currentData, CollidingFirstNoted eventData) => currentData;
}

/// <summary>Projects the second model.</summary>
[WhizbangPerspective("colliding-names")]
public class CollidingSecondProjection : IPerspectiveFor<CollidingSecond.Model, CollidingSecondNoted> {
  /// <inheritdoc />
  public CollidingSecond.Model Apply(CollidingSecond.Model currentData, CollidingSecondNoted eventData) => currentData;
}

/// <summary>The context that holds both colliding models and nothing else.</summary>
[WhizbangDbContext("colliding-names", Schema = "public")]
public partial class CollidingNamesDbContext(DbContextOptions<CollidingNamesDbContext> options) : DbContext(options) {
}
