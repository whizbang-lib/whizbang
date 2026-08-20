using System;
using System.Collections.Generic;
using System.Linq;

namespace Whizbang.Core.Minting;

/// <summary>
/// Default <see cref="ICompositeFactory"/>: stable grouping (groups in first-occurrence order,
/// constituents in input order) followed by sequential chunking inside each group. A chunk closes
/// when it reaches <see cref="CompositeMintRequest{TConstituent}.MaxConstituentsPerComposite"/>,
/// when its accumulated <see cref="CompositeMintRequest{TConstituent}.ConstituentSizeBytes"/>
/// crosses <see cref="CompositeMintRequest{TConstituent}.MaxBytesPerComposite"/> (so one chunk may
/// exceed the budget by exactly the constituent that crossed it — a constituent larger than the
/// whole budget still plans, alone), or when the group ends. Stateless and thread-safe.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#minting</docs>
/// <tests>tests/Whizbang.Core.Tests/Minting/CompositeFactoryTests.cs</tests>
public sealed class CompositeFactory : ICompositeFactory {
  /// <inheritdoc />
  public IReadOnlyList<CompositeEnvelopePlan<TConstituent>> Create<TConstituent>(
      CompositeMintRequest<TConstituent> request) {
    ArgumentNullException.ThrowIfNull(request);
    if (request.MaxConstituentsPerComposite is { } maxCountBound && maxCountBound < 1) {
      throw new ArgumentOutOfRangeException(nameof(request),
        $"MaxConstituentsPerComposite must be positive (got {maxCountBound}).");
    }
    if (request.MaxBytesPerComposite is { } maxBytesBound) {
      if (maxBytesBound < 1) {
        throw new ArgumentOutOfRangeException(nameof(request),
          $"MaxBytesPerComposite must be positive (got {maxBytesBound}).");
      }
      if (request.ConstituentSizeBytes is null) {
        // A byte budget with no size accounting would be silently inert — every constituent would
        // measure zero and the budget would never flush. That defeats the budget's whole purpose
        // (broker message-size limits, serialization memory), so it is a producer bug, not a default.
        throw new InvalidOperationException(
          "MaxBytesPerComposite requires ConstituentSizeBytes — a byte budget without size accounting never flushes.");
      }
    }

    if (request.Constituents.Count == 0) {
      return [];
    }

    var maxCount = request.MaxConstituentsPerComposite ?? int.MaxValue;
    var maxBytes = request.MaxBytesPerComposite;
    var sizeOf = request.ConstituentSizeBytes;

    // Stable grouping: groups in first-occurrence order, constituents in input order — plan order
    // is deterministic and mirrors how each producer historically grouped (GroupBy for stamped
    // destinations; identical to consecutive-run splitting for (stream, version)-ordered input).
    var plans = new List<CompositeEnvelopePlan<TConstituent>>();
    foreach (var group in request.Constituents.GroupBy(request.GroupKey.KeyFor)) {
      var chunk = new List<TConstituent>();
      var chunkBytes = 0L;
      foreach (var constituent in group) {
        chunk.Add(constituent);
        if (sizeOf is not null) {
          chunkBytes += sizeOf(constituent);
        }
        // Boundary AFTER adding: a chunk may exceed the byte budget by exactly the constituent
        // that crossed it, and a single constituent larger than the whole budget still plans,
        // alone — the budget bounds grouping, it never drops.
        if (chunk.Count >= maxCount || (maxBytes is not null && chunkBytes >= maxBytes)) {
          plans.Add(_buildPlan(request, group.Key, chunk));
          chunk = [];
          chunkBytes = 0;
        }
      }
      if (chunk.Count > 0) {
        plans.Add(_buildPlan(request, group.Key, chunk));
      }
    }
    return plans;
  }

  private static CompositeEnvelopePlan<TConstituent> _buildPlan<TConstituent>(
      CompositeMintRequest<TConstituent> request,
      string? groupKey,
      List<TConstituent> chunk) {
    var composite = request.BuildComposite(new CompositeConstituentBatch<TConstituent>(groupKey, chunk))
      ?? throw new InvalidOperationException(
        $"BuildComposite returned null for group '{groupKey}' — plans are machine-built, so a null composite is a producer bug.");
    return new CompositeEnvelopePlan<TConstituent> {
      GroupKey = groupKey,
      Constituents = chunk,
      Composite = composite,
    };
  }
}
