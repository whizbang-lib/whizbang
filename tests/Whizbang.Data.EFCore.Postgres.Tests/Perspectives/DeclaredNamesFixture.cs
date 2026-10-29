// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Custom;

namespace Whizbang.Data.EFCore.Postgres.Tests;

// Perspectives in a context of their own whose members are declared under JSON names that differ from their C#
// names only in case, at depth and inside collections, so the schema pass, the model configuration and the
// stores are all the generated ones. See DeclaredNameSpellingIntegrationTests (#1874).

/// <summary>A point whose coordinates are declared in lower case, as an external drawing format names them.</summary>
public sealed record DeclaredPoint {
  /// <summary>Declared <c>x</c>; a release that ignored the attribute stored <c>X</c>.</summary>
  [JsonPropertyName("x")]
  public required double X { get; init; }

  /// <summary>Declared <c>y</c>.</summary>
  [JsonPropertyName("y")]
  public required double Y { get; init; }
}

/// <summary>A stroke: a color and the points it passes through.</summary>
public sealed record DeclaredStroke {
  /// <summary>Declared <c>color</c>.</summary>
  [JsonPropertyName("color")]
  public string Color { get; init; } = "";

  /// <summary>The points, a collection of records inside a collection.</summary>
  public List<DeclaredPoint> Points { get; init; } = [];
}

/// <summary>A picture the mapped path stores: every nested type has a constructor it can call.</summary>
public static class DeclaredPicture {
  /// <summary>The model.</summary>
  [PerspectiveTable("wh_per_declared_picture")]
  public class Model {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid Id { get; set; }

    /// <summary>Declared <c>title</c>.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>The strokes.</summary>
    public List<DeclaredStroke> Objects { get; set; } = [];

    /// <summary>A single nested object, which the mapped path stores as a nested document rather than a collection.</summary>
    public DeclaredPoint Origin { get; set; } = new() { X = 0, Y = 0 };
  }
}

/// <summary>The same picture stored opaquely: a read-only collection the mapped path cannot fill.</summary>
public static class DeclaredSketch {
  /// <summary>The model.</summary>
  [PerspectiveTable("wh_per_declared_sketch")]
  public class Model {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid Id { get; set; }

    /// <summary>Declared <c>title</c>.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>The strokes, read-only, which makes the model opaque.</summary>
    public IReadOnlyList<DeclaredStroke> Objects { get; set; } = [];
  }
}

/// <summary>The event both perspectives fold; the tests write rows directly.</summary>
/// <param name="Id">The stream.</param>
public record DeclaredPictureDrawn([property: StreamId] Guid Id) : IEvent;

/// <summary>Folds the mapped picture.</summary>
[WhizbangPerspective("declared-names")]
public class DeclaredPictureProjection : IPerspectiveFor<DeclaredPicture.Model, DeclaredPictureDrawn> {
  /// <inheritdoc/>
  public DeclaredPicture.Model Apply(DeclaredPicture.Model currentData, DeclaredPictureDrawn eventData) => currentData;
}

/// <summary>Folds the opaque sketch.</summary>
[WhizbangPerspective("declared-names")]
public class DeclaredSketchProjection : IPerspectiveFor<DeclaredSketch.Model, DeclaredPictureDrawn> {
  /// <inheritdoc/>
  public DeclaredSketch.Model Apply(DeclaredSketch.Model currentData, DeclaredPictureDrawn eventData) => currentData;
}

/// <summary>The context the declared-name tests start.</summary>
[WhizbangDbContext("declared-names", Schema = "public")]
public partial class DeclaredNamesDbContext(DbContextOptions<DeclaredNamesDbContext> options) : DbContext(options) {
}
