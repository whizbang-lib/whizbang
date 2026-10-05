// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// Operator purge of durable streams: removes an explicit list of streams from this service's store, for streams
/// that should never have existed (events for an entity the origin service does not have, the leftovers of a
/// replay that minted streams with fresh ids).
/// </summary>
/// <remarks>
/// <para>
/// Everything keyed by the streams goes: events and their bodies, every perspective's row, perspective work,
/// cursors and snapshots, the outbox, the inbox and its deduplication entries, receptor bookkeeping, digests and
/// integrity rows. Each batch of streams is one transaction. A dry run counts the same rows and changes nothing.
/// Each committed batch writes an audit record (who, when, why, how many rows per table), and every purged stream
/// is marked purged for every perspective, so a later event on it is skipped rather than recreating a row.
/// </para>
/// <para>
/// Only one instance runs a given batch: the batch takes the existing <c>PublishOnceAsync</c> claim
/// (<c>wh_unique_emission_claims</c>) keyed by the purge id and the batch index, in the batch's own transaction.
/// A second instance running the same request finds the claim and skips the batch, and a crash rolls the claim
/// back with the batch, so a retry with the same <see cref="StreamPurgeRequest.PurgeId"/> resumes where it
/// stopped.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/purging-streams</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Operations/StreamPurgeTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperStreamPurgeTests.cs</tests>
public interface IStreamPurger {
  /// <summary>Purges (or, with <see cref="StreamPurgeRequest.DryRun"/>, counts) the requested streams.</summary>
  /// <param name="request">The streams, who asks, and why.</param>
  /// <param name="cancellationToken">Cancellation token. A batch that has committed stays committed.</param>
  /// <returns>Per batch, whether this instance ran it and the rows per table.</returns>
  Task<StreamPurgeReport> PurgeAsync(StreamPurgeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>An operator's request to purge streams.</summary>
/// <docs>operations/infrastructure/purging-streams</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamPurgeRequestTests.cs</tests>
public sealed record StreamPurgeRequest {
  /// <summary>The default number of streams per batch (one transaction each).</summary>
  public const int DEFAULT_BATCH_SIZE = 100;

  /// <summary>The streams to purge. Duplicates are ignored; the empty stream id is refused.</summary>
  public required IReadOnlyList<Guid> StreamIds { get; init; }

  /// <summary>Who asked, recorded in the audit.</summary>
  public required string RequestedBy { get; init; }

  /// <summary>Why, recorded in the audit.</summary>
  public required string Reason { get; init; }

  /// <summary>Count what would be removed and change nothing.</summary>
  public bool DryRun { get; init; }

  /// <summary>Streams per transaction.</summary>
  public int BatchSize { get; init; } = DEFAULT_BATCH_SIZE;

  /// <summary>
  /// Identifies the purge in the audit and in the per-batch claims. Reuse it to resume a purge that stopped:
  /// batches it already committed are skipped.
  /// </summary>
  public Guid PurgeId { get; init; } = ValueObjects.TrackedGuid.New().Value;

  /// <summary>
  /// The streams in batches: distinct, in a stable order, so the same request always yields the same batches
  /// (a resumed purge must claim the same batch under the same index).
  /// </summary>
  /// <exception cref="ArgumentException">A field is missing or out of range, or the empty stream id is listed.</exception>
  public IReadOnlyList<IReadOnlyList<Guid>> Batches() {
    if (string.IsNullOrWhiteSpace(RequestedBy)) {
      throw new ArgumentException("A purge must say who requested it.", nameof(RequestedBy));
    }
    if (string.IsNullOrWhiteSpace(Reason)) {
      throw new ArgumentException("A purge must say why.", nameof(Reason));
    }
    if (BatchSize < 1) {
      throw new ArgumentException("The batch size must be at least 1.", nameof(BatchSize));
    }
    if (StreamIds.Contains(Guid.Empty)) {
      throw new ArgumentException("The empty stream id cannot be purged: it would reach every streamless row.", nameof(StreamIds));
    }
    return [.. StreamIds.Distinct().Order().Chunk(BatchSize).Cast<IReadOnlyList<Guid>>()];
  }
}

/// <summary>What a purge did, or a dry run would do.</summary>
/// <param name="PurgeId">The purge's id.</param>
/// <param name="DryRun">Whether nothing was changed.</param>
/// <param name="Batches">Each batch, in order.</param>
/// <docs>operations/infrastructure/purging-streams</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamPurgeRequestTests.cs</tests>
public sealed record StreamPurgeReport(Guid PurgeId, bool DryRun, IReadOnlyList<StreamPurgeBatch> Batches) {
  /// <summary>Rows per table across the batches this instance ran.</summary>
  public IReadOnlyDictionary<string, long> Totals =>
    Batches.Where(b => b.Ran)
      .SelectMany(b => b.RowsByTable)
      .GroupBy(kv => kv.Key, StringComparer.Ordinal)
      .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal);

  /// <summary>
  /// A plain-text table of the totals, one line per table with rows, then the batches another instance or an
  /// earlier run had already claimed.
  /// </summary>
  public string Format() {
    var lines = new List<string> {
      $"{(DryRun ? "Dry run: would purge" : "Purged")} {Batches.Where(b => b.Ran).Sum(b => b.StreamIds.Count)} stream(s) in {Batches.Count(b => b.Ran)} batch(es), purge {PurgeId}"
    };
    foreach (var (table, rows) in Totals.Where(t => t.Value > 0).OrderBy(t => t.Key, StringComparer.Ordinal)) {
      lines.Add($"  {table,-32} {rows,12}");
    }
    var skipped = Batches.Count(b => !b.Ran);
    if (skipped > 0) {
      lines.Add($"Skipped {skipped} batch(es) already claimed by another instance or an earlier run of this purge");
    }
    return string.Join(Environment.NewLine, lines);
  }
}

/// <summary>One batch of a purge.</summary>
/// <param name="BatchIndex">The batch's position in the request.</param>
/// <param name="StreamIds">The streams in the batch.</param>
/// <param name="Ran">False when the batch was already claimed (another instance, or an earlier run of the purge).</param>
/// <param name="RowsByTable">Rows removed (or, in a dry run, that would be) per table; empty when not run.</param>
/// <docs>operations/infrastructure/purging-streams</docs>
public sealed record StreamPurgeBatch(
  int BatchIndex,
  IReadOnlyList<Guid> StreamIds,
  bool Ran,
  IReadOnlyDictionary<string, long> RowsByTable);
