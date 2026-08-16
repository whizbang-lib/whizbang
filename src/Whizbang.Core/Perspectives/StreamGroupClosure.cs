namespace Whizbang.Core.Perspectives;

/// <summary>
/// The stream-group eviction closure: given the origin evictions a cycle's sweeps journaled,
/// expand through the group graph — honoring each membership's Announce/Follow/Bridge dials — to
/// the set of sibling rows that must leave with them. Pure set arithmetic, no I/O; cyclic bridged
/// graphs converge because each (model, row) enters the result at most once.
/// </summary>
/// <remarks>
/// The load-bearing distinction is <b>own-origin vs received</b>: a seed announces to every
/// membership with Announce on; a RECEIVED eviction re-announces through a member's other
/// memberships only where Bridge is on. Seeds themselves are never in the result — they are
/// already destroyed; the result is the cascade set only.
/// </remarks>
/// <docs>proposals/pre-destruction-seam</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/StreamGroupClosureTests.cs</tests>
public static class StreamGroupClosure {
  /// <summary>
  /// Computes the cascade set for the given origin evictions.
  /// </summary>
  /// <param name="seeds">The (model, row id) pairs the sweeps destroyed this cycle.</param>
  /// <param name="memberships">Each participating model's memberships (typically from
  /// <see cref="PerspectiveStreamGroupRegistry"/>).</param>
  /// <returns>The (model, row id) pairs the cascade must evict — seeds excluded.</returns>
  public static IReadOnlyList<(Type Model, Guid RowId)> Compute(
      IReadOnlyList<(Type Model, Guid RowId)> seeds,
      IReadOnlyDictionary<Type, IReadOnlyList<StreamGroupMembership>> memberships) {
    ArgumentNullException.ThrowIfNull(seeds);
    ArgumentNullException.ThrowIfNull(memberships);

    // key → members, precomputed once.
    var groups = new Dictionary<string, List<(Type Model, StreamGroupMembership Membership)>>(StringComparer.Ordinal);
    foreach (var (model, list) in memberships) {
      foreach (var membership in list) {
        if (!groups.TryGetValue(membership.Key, out var members)) {
          groups[membership.Key] = members = [];
        }
        members.Add((model, membership));
      }
    }

    var seedSet = new HashSet<(Type, Guid)>(seeds);
    var received = new HashSet<(Type, Guid)>();
    // Worklist entries: (model, row, isOrigin). Origins announce via Announce; received re-announce via Bridge.
    var worklist = new Queue<(Type Model, Guid RowId, bool IsOrigin)>(
      seeds.Select(s => (s.Model, s.RowId, true)));

    while (worklist.Count > 0) {
      var (model, rowId, isOrigin) = worklist.Dequeue();
      if (!memberships.TryGetValue(model, out var own)) {
        continue;
      }
      foreach (var membership in own) {
        var announces = isOrigin ? membership.Announce : membership.Bridge;
        if (!announces || !groups.TryGetValue(membership.Key, out var members)) {
          continue;
        }
        foreach (var (sibling, siblingMembership) in members) {
          if (sibling == model || !siblingMembership.Follow) {
            continue;
          }
          var entry = (sibling, rowId);
          if (seedSet.Contains(entry) || !received.Add(entry)) {
            continue; // already destroyed, or already in the cascade — fixpoint convergence.
          }
          worklist.Enqueue((sibling, rowId, false));
        }
      }
    }

    return [.. received.Select(r => (r.Item1, r.Item2))];
  }
}
