// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>A bucket key as the comparisons key a digest: tenant (null when unscoped), type and stream.</summary>
/// <param name="TenantScope">The tenant scope.</param>
/// <param name="EventType">The stored type name.</param>
/// <param name="StreamId">The stream, or the empty stream for a type bucket.</param>
internal readonly record struct IntegrityBucket(string? TenantScope, string EventType, Guid StreamId);

/// <summary>An origin's answer with this side's exclusions removed, and the buckets an exclusion leaves undecided.</summary>
/// <param name="Message">The answer, each digest less what this side excluded.</param>
/// <param name="Pending">Buckets holding a mark whose place at the origin is not known yet: compared, never repaired.</param>
internal sealed record IntegrityExclusionAdjusted(IntegrityManifest Message, IReadOnlySet<IntegrityBucket> Pending);

/// <summary>
/// The comparison side of integrity exclusions (#2588): the one entry point every comparison of an origin's answer goes
/// through, so no comparison path has to remember it (<c>IntegrityExclusionAdjustmentGuardTests</c> fails one that does
/// not). This side excluded an event from its own folds through the eligibility rule; the origin still counts it, so the
/// same event is removed from the origin's answer here, by its digest lanes and count (an event's lanes depend on its id
/// only), and the two sides agree without the origin knowing anything of it.
/// </summary>
/// <remarks>
/// <para>
/// A windowed answer has removed from it only the marks the origin holds inside its window; a full-history answer all of
/// them. A mark not yet placed at the origin (pending) cannot be removed from a window, so its bucket is undecided until
/// the origin's position answer settles it: compared, never repaired. A bucket left with nothing is dropped from the
/// answer, as an origin that holds nothing there answers.
/// </para>
/// <para>
/// Cost: one indexed read (<c>wh_integrity_exclusion_adjustment</c>), which for an origin with no marks is one probe
/// that returns nothing; with marks, a range over that origin's marks of the compared types.
/// </para>
/// </remarks>
/// <docs>resilience/integrity-exclusions#cross-service-agreement</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityExclusionAgreementTests.cs</tests>
internal static class IntegrityExclusionAdjustment {
  private static readonly IReadOnlySet<IntegrityBucket> _none = new HashSet<IntegrityBucket>();

  /// <summary>Removes this side's exclusions from an origin's manifest answer.</summary>
  /// <param name="services">The comparison's services; without a store connection nothing is adjusted.</param>
  /// <param name="message">The origin's answer, already narrowed to what this side compares.</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The adjusted answer and the undecided buckets.</returns>
  internal static async Task<IntegrityExclusionAdjusted> ApplyAsync(
      IServiceProvider services, IntegrityManifest message, CancellationToken cancellationToken) {
    var database = services.GetService<PostgresArchiveConnection>();
    if (database is null || message.Digests.Count == 0) {
      return new IntegrityExclusionAdjusted(message, _none);
    }
    var byStream = message.Level == ManifestLevel.Streams;
    long? since = message.ComputedThrough is null ? null : message.SinceSequence ?? 0;
    List<string> types = [.. message.Digests.Select(d => d.EventType).Distinct(StringComparer.Ordinal)];
    List<Guid>? streams = byStream ? [.. message.Digests.Select(d => d.StreamId).Distinct()] : null;
    var rows = await new IntegrityExclusionStore(database)
      .AdjustmentAsync(message.OriginServiceId, types, streams, since, message.ComputedThrough, byStream, cancellationToken)
      .ConfigureAwait(false);
    if (rows.Count == 0) {
      return new IntegrityExclusionAdjusted(message, _none);
    }
    var removals = rows.ToDictionary(r => _key(r.Digest));
    var pending = rows.Where(r => r.Pending > 0).Select(r => _key(r.Digest)).ToHashSet();
    var digests = new List<StreamDigest>(message.Digests.Count);
    foreach (var origin in message.Digests) {
      if (!removals.TryGetValue(_key(origin), out var removal) || removal.Digest.EventCount == 0) {
        digests.Add(origin);
        continue;
      }
      var adjusted = Subtract(origin, removal.Digest);
      if (adjusted.EventCount > 0) {
        digests.Add(adjusted);
      }
    }
    return new IntegrityExclusionAdjusted(message with { Digests = digests }, pending);
  }

  /// <summary>
  /// Per (tenant, type), the events this side excluded that an origin's checkpoint counts in <c>(from, to]</c> of its
  /// commit sequences, plus the pending marks (their place unknown, they may be among them). The checkpoint adds them
  /// to what this side counted, as it adds the events held without a position.
  /// </summary>
  /// <param name="services">The comparison's services; without a store connection nothing is added.</param>
  /// <param name="originServiceId">The origin.</param>
  /// <param name="fromCommitSequence">The checkpoint range's start (exclusive).</param>
  /// <param name="toCommitSequence">The checkpoint range's end (inclusive).</param>
  /// <param name="cancellationToken">Cancellation.</param>
  /// <returns>The counts to add, keyed by (tenant, type).</returns>
  internal static async Task<IReadOnlyDictionary<(string? TenantScope, string EventType), int>> CheckpointCountsAsync(
      IServiceProvider services, Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken) {
    var database = services.GetService<PostgresArchiveConnection>();
    if (database is null) {
      return new Dictionary<(string?, string), int>();
    }
    var rows = await new IntegrityExclusionStore(database)
      .AdjustmentAsync(originServiceId, null, null, fromCommitSequence + 1, toCommitSequence + 1, byStream: false, cancellationToken)
      .ConfigureAwait(false);
    return rows.ToDictionary(r => (r.Digest.TenantScope, r.Digest.EventType), r => r.Digest.EventCount + r.Pending);
  }

  /// <summary>An origin's digest less what this side excluded: XOR is self-inverse, so the removal's lanes come out.</summary>
  /// <param name="origin">The origin's digest.</param>
  /// <param name="removal">The excluded events' lanes and count.</param>
  /// <returns>The adjusted digest.</returns>
  internal static StreamDigest Subtract(StreamDigest origin, StreamDigest removal) => origin with {
    DigestLo = origin.DigestLo ^ removal.DigestLo,
    DigestHi = origin.DigestHi ^ removal.DigestHi,
    EventCount = origin.EventCount - removal.EventCount,
    IdLo = origin.IdLo is long lo ? lo ^ (removal.IdLo ?? 0) : null,
    IdHi = origin.IdHi is long hi ? hi ^ (removal.IdHi ?? 0) : null,
  };

  private static IntegrityBucket _key(StreamDigest digest) => new(digest.TenantScope, digest.EventType, digest.StreamId);
}
