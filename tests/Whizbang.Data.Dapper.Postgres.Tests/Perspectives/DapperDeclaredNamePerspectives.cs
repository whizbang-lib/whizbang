// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

// A perspective whose members are declared under JSON names that differ from their C# names only in case, at depth
// and inside collections, so the generated Dapper schema and the registered document model are the shipped ones.
// See DapperDeclaredNameSpellingTests (#1874).

/// <summary>A point whose coordinates are declared in lower case.</summary>
public sealed record DapperDeclaredPoint {
  /// <summary>Declared <c>x</c>; a release that ignored the attribute stored <c>X</c>.</summary>
  [JsonPropertyName("x")]
  public required double X { get; init; }

  /// <summary>Declared <c>y</c>.</summary>
  [JsonPropertyName("y")]
  public required double Y { get; init; }
}

/// <summary>A stroke: a color and its points.</summary>
public sealed record DapperDeclaredStroke {
  /// <summary>Declared <c>color</c>.</summary>
  [JsonPropertyName("color")]
  public string Color { get; init; } = "";

  /// <summary>The points.</summary>
  public List<DapperDeclaredPoint> Points { get; init; } = [];
}

/// <summary>The picture.</summary>
[PerspectiveTable("wh_per_dapper_declared_picture")]
public record DapperDeclaredPicture {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid Id { get; init; }

  /// <summary>Declared <c>title</c>.</summary>
  [JsonPropertyName("title")]
  public string Title { get; init; } = "";

  /// <summary>The strokes.</summary>
  public List<DapperDeclaredStroke> Objects { get; init; } = [];
}

/// <summary>An event carrying points, which an earlier release stored under the C# names.</summary>
/// <param name="Id">The stream.</param>
/// <param name="Points">The points.</param>
public record DapperDeclaredPointsDrawn([property: StreamId] Guid Id, List<DapperDeclaredPoint> Points) : IEvent;

/// <summary>Folds the picture.</summary>
public class DapperDeclaredPictureProjection : IPerspectiveFor<DapperDeclaredPicture, DapperDeclaredPointsDrawn> {
  /// <inheritdoc/>
  public DapperDeclaredPicture Apply(DapperDeclaredPicture currentData, DapperDeclaredPointsDrawn eventData) => currentData;
}
