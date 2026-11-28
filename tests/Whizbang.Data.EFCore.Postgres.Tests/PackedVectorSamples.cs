// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;
using Whizbang.Core.Attributes;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>A labeled embedding, nested in an event: its vector has a fixed path.</summary>
/// <param name="Label">The label.</param>
/// <param name="Embedding">Its embedding.</param>
public sealed record PackedVectorChunk(string Label, float[] Embedding);

/// <summary>
/// A vector event for the stored-body packing tests (#2625): the generated message JSON context registers its vector
/// paths (<c>Embedding</c> and <c>Lead.Embedding</c>); <c>Centroid</c> is opted out and <c>Weights</c> are doubles, so
/// neither is ever packed.
/// </summary>
public sealed record PackedVectorSampleEvent : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid PassageId { get; init; }

  /// <summary>Text beside the vectors, never touched by packing.</summary>
  public string Text { get; init; } = "";

  /// <summary>A top-level vector: packed.</summary>
  public float[] Embedding { get; init; } = [];

  /// <summary>A nested object's vector: packed, by its segmented path.</summary>
  public PackedVectorChunk Lead { get; init; } = new("", []);

  /// <summary>Opted out: stays JSON numbers.</summary>
  [StoredAsJsonArray]
  public float[] Centroid { get; init; } = [];

  /// <summary>Doubles: never packed, they do not fit binary32.</summary>
  public double[] Weights { get; init; } = [];
}

/// <summary>An event with no vector at all: packing never reads it.</summary>
public sealed record PackedVectorOrdinaryEvent : IEvent {
  /// <summary>The stream.</summary>
  [StreamId]
  public Guid PassageId { get; init; }

  /// <summary>Its one value.</summary>
  public int Amount { get; init; }
}
