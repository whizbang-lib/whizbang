// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// The operator repair for composite children a receiver filed under the composite's stream (#1798): moves each to the
/// stream its payload's <c>[StreamId]</c> names and purges the rows keyed by a composite stream it leaves empty.
/// </summary>
/// <remarks>
/// <para>
/// A receiver stores a typed composite's children, never the composite. Before the fix every child was filed under the
/// composite's stream, so live apply folded it onto a row keyed by the composite (a ghost row nobody reads) and the row
/// its payload names never received it. The receiver now files each child under its own stream; this repairs what was
/// stored before. Run a dry run first, then the repair, then rebuild every perspective for
/// <see cref="FanoutChildRefileReport.StreamsToRebuild"/>: the moved events reach those rows only through a rebuild.
/// </para>
/// <para>
/// The integrity re-delivery repaired events the same way (its bundle named no stream, so each repaired event was filed
/// under the bundle envelope's id). A repaired event whose payload names its stream is moved there like any other
/// child; one whose payload names none, found on the stream its lineage hop names as its cause (the bundle's id), is
/// moved onto its own id, where its producer stored it and where live delivery stores every message whose hop names no
/// stream.
/// </para>
/// <para>
/// Safe to repeat: a child already on its own stream is left alone, so a second run finds nothing to do.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanoutChildRefileSqlTests.cs</tests>
public interface IFanoutChildRefiler {
  /// <summary>Finds the misfiled children and, unless <see cref="FanoutChildRefileRequest.DryRun"/>, moves them.</summary>
  /// <param name="request">Who asks, why, and whether to change anything.</param>
  /// <param name="cancellationToken">Cancels between batches; a batch already sent completes or rolls back whole.</param>
  /// <returns>What was (or, in a dry run, would be) moved and purged.</returns>
  Task<FanoutChildRefileReport> RefileAsync(FanoutChildRefileRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A request to refile misfiled composite children.</summary>
/// <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/FanoutChildRefileSqlTests.cs:Request_WithoutWhoOrWhyOrAPositiveBatch_IsRefusedAsync</tests>
public sealed record FanoutChildRefileRequest {
  /// <summary>The most events one transaction moves, unless one composite stream holds more (it is never split).</summary>
  public const int DEFAULT_BATCH_SIZE = 500;

  /// <summary>Who asked, recorded on the purge audit of the composite streams the repair empties.</summary>
  public required string RequestedBy { get; init; }

  /// <summary>Why, recorded with <see cref="RequestedBy"/>.</summary>
  public required string Reason { get; init; }

  /// <summary>Report what would change and change nothing.</summary>
  public bool DryRun { get; init; }

  /// <summary>The most events one transaction moves.</summary>
  public int BatchSize { get; init; } = DEFAULT_BATCH_SIZE;

  /// <summary>Identifies this run in the purge audit and the log.</summary>
  public Guid RepairId { get; init; } = ValueObjects.TrackedGuid.New().Value;

  /// <summary>Refuses a request that does not say who and why, or whose batch size is not positive.</summary>
  /// <exception cref="ArgumentException">The request is incomplete.</exception>
  public void Validate() {
    if (string.IsNullOrWhiteSpace(RequestedBy)) {
      throw new ArgumentException("A repair must say who requested it.", nameof(RequestedBy));
    }
    if (string.IsNullOrWhiteSpace(Reason)) {
      throw new ArgumentException("A repair must say why.", nameof(Reason));
    }
    if (BatchSize < 1) {
      throw new ArgumentException("The batch size must be at least 1.", nameof(BatchSize));
    }
  }
}

/// <summary>One stored event moved to the stream its payload names.</summary>
/// <param name="EventId">The event.</param>
/// <param name="FromStream">The stream it was filed under (the composite's).</param>
/// <param name="ToStream">The stream its payload names.</param>
/// <param name="ToVersion">Its version on that stream.</param>
public sealed record RefiledEvent(Guid EventId, Guid FromStream, Guid ToStream, long ToVersion);

/// <summary>What a refile run did, or in a dry run would do.</summary>
/// <param name="RepairId">The run.</param>
/// <param name="DryRun">Whether anything changed.</param>
/// <param name="Scanned">Fan-out children examined.</param>
/// <param name="Undecidable">Children whose stored type this service cannot read, so their stream cannot be known; left in place.</param>
/// <param name="Moves">The events moved.</param>
/// <param name="EmptiedStreams">Composite streams left with no events, whose rows were purged.</param>
/// <param name="PurgedRows">Rows the purge of <paramref name="EmptiedStreams"/> removed, by table.</param>
/// <docs>operations/infrastructure/purging-streams#refiling-composite-children</docs>
public sealed record FanoutChildRefileReport(
  Guid RepairId,
  bool DryRun,
  int Scanned,
  int Undecidable,
  IReadOnlyList<RefiledEvent> Moves,
  IReadOnlyList<Guid> EmptiedStreams,
  IReadOnlyDictionary<string, long> PurgedRows) {

  /// <summary>The streams that received events: rebuild every perspective for these, in place, to fold them.</summary>
  public IReadOnlyList<Guid> StreamsToRebuild => [.. Moves.Select(m => m.ToStream).Distinct().Order()];

  /// <summary>A summary an operator can read.</summary>
  /// <returns>One line per fact, then the purged tables.</returns>
  public string Format() {
    var lines = new List<string> {
      $"{(DryRun ? "Dry run: would move" : "Moved")} {Moves.Count} composite child(ren) of {Scanned} examined onto "
        + $"{StreamsToRebuild.Count} stream(s); {EmptiedStreams.Count} composite stream(s) emptied; {Undecidable} unreadable, left in place; repair {RepairId}",
    };
    foreach (var (table, rows) in PurgedRows.Where(t => t.Value > 0).OrderBy(t => t.Key, StringComparer.Ordinal)) {
      lines.Add($"  {table,-32} {rows,12}");
    }
    return string.Join(Environment.NewLine, lines);
  }
}
