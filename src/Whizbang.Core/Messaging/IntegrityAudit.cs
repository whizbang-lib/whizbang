using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity A1c: the granularity of a manifest exchange. The scheduled audit starts at
/// <see cref="Types"/> — O(types) wire cost — and drills down to <see cref="Streams"/> only for
/// the (capped) types whose roll-ups disagree.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public enum ManifestLevel {
  /// <summary>Per-(tenant, type, stream) digest rows — the drill-down + repair granularity.</summary>
  Streams = 0,

  /// <summary>Per-(tenant, type) roll-ups (<see cref="StreamDigest.StreamId"/> = empty) — the
  /// cheap first pass of the hierarchical audit.</summary>
  Types = 1,
}

/// <summary>
/// Stream-integrity Phase A: asks an origin for its identity manifest — the digests of its OWN
/// emissions for the requester's subscribed types. Sent DIRECTED at the origin (envelope Target);
/// the origin answers with <see cref="IntegrityManifest"/> events targeted back at
/// <see cref="RequesterService"/> on <see cref="Topic"/>. A1c: <see cref="Level"/> picks the
/// granularity (the worker asks for <see cref="ManifestLevel.Types"/> first);
/// <see cref="UseRecompute"/> forces the full-sweep recompute instead of the maintained table —
/// the trust-but-verify cycle that also covers busy buckets settle-skipped on table-driven cycles.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
[PinnedId("c9f4b1e7-2a6d-4c38-9b05-7e1f8d3a6c92")]
public sealed record RequestIntegrityManifest : ICommand, IControlPlaneMessage {
  /// <summary>The auditing consumer's logical name — becomes the manifests' Target.</summary>
  public required string RequesterService { get; init; }

  /// <summary>Wire topic the manifests publish to.</summary>
  public required string Topic { get; init; }

  /// <summary>Types to manifest (the requester's subscribed set); null = all.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }

  /// <summary>Requested granularity (default <see cref="ManifestLevel.Streams"/> — wire-compatible
  /// with pre-A1c requests).</summary>
  public ManifestLevel Level { get; init; }

  /// <summary>True = answer from a full recompute over the event store (the sweep cycle);
  /// false (default) = answer from the maintained digest table.</summary>
  public bool UseRecompute { get; init; }

  /// <summary>
  /// #80-B: true = a NEGOTIATED-SCOPE ask — the origin answers only the window
  /// <c>[<see cref="SinceSequence"/>, <see cref="UntilSequence"/>)</c> (epoch-served) and stamps
  /// the watermark fields on the manifest. False (default) = the legacy full-history ask,
  /// byte-identical behavior — old peers never see a windowed answer they did not ask for.
  /// </summary>
  public bool Windowed { get; init; }

  /// <summary>Inclusive window start — the asker's current verified watermark (default 0 =
  /// nothing verified yet). Only read when <see cref="Windowed"/>.</summary>
  public long SinceSequence { get; init; }

  /// <summary>Exclusive window end; null = through the origin's settled max. Only read when
  /// <see cref="Windowed"/>.</summary>
  public long? UntilSequence { get; init; }

  /// <summary>The asker's page bound for stream-level answers (null = the origin's
  /// <c>MaxDigestsPerManifest</c>). The ASKER'S memory is the constraint the page protects.</summary>
  public int? MaxDigests { get; init; }

  /// <summary>Stream-level resume cursor from the previous answer's
  /// <see cref="IntegrityManifest.ResumeAfterStreamId"/> (null = the window's first page).</summary>
  public Guid? ResumeAfterStreamId { get; init; }
}

/// <summary>
/// Stream-integrity Phase A: one chunk of an origin's identity manifest — digest rows of its own
/// emissions. <c>[Ephemeral]</c>: superseded control data, reaped once consumed. Comparison is
/// per-bucket independent, so chunks need no assembly protocol — a lost chunk's buckets simply
/// re-audit next cycle.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
[PinnedId("e2b7d9f4-6c15-4a83-b270-9d4e1c8f5a36")]
[Ephemeral]
public sealed record IntegrityManifest : IEvent, IControlPlaneMessage {
  /// <summary>The manifest stream — the origin's service id.</summary>
  [StreamId]
  public required Guid ManifestStreamId { get; init; }

  /// <summary>The origin's stable id (what consumers key received rows on).</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical name (the repair Target).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Digest rows for this chunk.</summary>
  public List<StreamDigest> Digests { get; init; } = [];

  /// <summary>Granularity of <see cref="Digests"/> (default <see cref="ManifestLevel.Streams"/>).</summary>
  public ManifestLevel Level { get; init; }

  /// <summary>True when the digests came from a full recompute (the sweep cycle) — the consumer
  /// then compares against its OWN recompute; false = table-driven, compared against the
  /// consumer's table with settle-skipping. Drill-down requests inherit this flag.</summary>
  public bool Recomputed { get; init; }

  /// <summary>#80-B: echo of the ask's window start. Null = a legacy unwindowed answer.</summary>
  public long? SinceSequence { get; init; }

  /// <summary>
  /// #80-B: the watermark — the exclusive end sequence this answer actually covered, capped at
  /// the origin's settled max. The asker's next window starts here; it advances its seal to it
  /// ONLY when the window answered completely (<see cref="ResumeAfterStreamId"/> null). Null = a
  /// legacy unwindowed answer, or the engine could not window — no coverage claim is made.
  /// </summary>
  public long? ComputedThrough { get; init; }

  /// <summary>#80-B: stream-level paging cursor — non-null means the window is NOT complete; ask
  /// again from this stream id and do not advance any seal. Stamped on every chunk of the answer
  /// (chunks share one window).</summary>
  public Guid? ResumeAfterStreamId { get; init; }

  /// <summary>
  /// #80-C: total chunks in this answer (0 = a legacy answer that never said). Chunks carry no
  /// assembly protocol, so a receiver can only certify a window it provably saw ALL of — the seal
  /// advances only on a single-chunk (<c>ChunkCount == 1</c>) clean window. Multi-chunk windows
  /// still compare and repair; they just never certify.
  /// </summary>
  public int ChunkCount { get; init; }

  /// <summary>
  /// #80-F: the origin's history generation — bumped whenever folded history legitimately
  /// mutates (a close-the-books truncation, a reclassification). A consumer seeing it change
  /// resets its seal and re-verifies instead of alarming on deliberate change; sealed-range
  /// divergence WITHOUT a generation change remains damage. Null = a pre-generation origin.
  /// </summary>
  public long? OriginGeneration { get; init; }
}

/// <summary>
/// #80-D: the occurrence a scheduled integrity-sweep fires at the configured idle-time cron. The
/// driver's built-in receptor reacts by running one full sweep
/// (<see cref="Whizbang.Core.Workers.IIntegritySweepRunner"/>) — the trust-but-verify pass that
/// catches exactly the state the epoch seals and audit seals assume is fine. A command (imperative
/// "sweep now"), riding the F2 occurrence mechanism like <c>ScheduledStreamClose</c>.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegritySweepSchedulingTests.cs</tests>
[PinnedId("a7d2e9c4-5b18-4f36-8e0a-1c9b7d4f2a85")]
public sealed record ScheduledIntegritySweep : ICommand;

/// <summary>
/// Stream-integrity #80-B: the result of a NEGOTIATED-SCOPE digest read — digests for a sequence
/// window <c>[since, until)</c> plus the two-dimensional resume cursor. The window is half-open on
/// purpose: epoch boundaries align exactly and <see cref="ComputedThrough"/> (the exclusive end
/// actually covered, capped at the settled max) IS the next ask's <c>since</c> — verified history
/// is never re-shipped.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/EpochWindowedDigestSqlTests.cs</tests>
public sealed record WindowedDigestResult {
  /// <summary>Digest rows for the covered window (and page, at stream level).</summary>
  public required IReadOnlyList<StreamDigest> Digests { get; init; }

  /// <summary>The exclusive end sequence this answer actually covered — the watermark. Always
  /// capped at the origin's settled max: an answer never claims coverage of what has not settled.</summary>
  public required long ComputedThrough { get; init; }

  /// <summary>Stream-level paging cursor: non-null = the window is NOT complete — ask again from
  /// this stream id and do not advance any seal; null = the window answered completely.</summary>
  public Guid? ResumeAfterStreamId { get; init; }
}

/// <summary>
/// Stream-integrity Phase A: a CONFIRMED audit divergence — a (tenant, type, stream) bucket whose
/// consumer-side digest disagrees with the origin's manifest (missing events, or a differing
/// fold). The ops report; at <see cref="IntegrityRepairMode.AutoRepairCapped"/> a stream-scoped
/// re-delivery request also goes back to the origin.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[PinnedId("f8a3c6e1-9d47-4b28-a5c1-3e7b0d9f2a64")]
public sealed record IntegrityDivergenceDetected : IEvent, IControlPlaneMessage {
  /// <summary>Report stream (one per report).</summary>
  [StreamId]
  public required Guid ReportStreamId { get; init; }

  /// <summary>The origin the divergence is against.</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical name.</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Tenant scope of the divergent bucket (null = unscoped).</summary>
  public string? TenantScope { get; init; }

  /// <summary>Event type of the divergent bucket.</summary>
  public required string EventType { get; init; }

  /// <summary>The audited stream.</summary>
  public required Guid AuditedStreamId { get; init; }

  /// <summary>The origin's event count for the bucket.</summary>
  public required int OriginCount { get; init; }

  /// <summary>This consumer's event count for the bucket (0 = the stream is missing entirely).</summary>
  public required int LocalCount { get; init; }

  /// <summary>True when a stream-scoped repair request was sent (ladder at AutoRepairCapped).</summary>
  public bool AutoRepairRequested { get; init; }
}

/// <summary>
/// Stream-integrity Phase L: a LOCAL coverage gap — a stream holds settled events a registered
/// perspective should fold, but that perspective has no cursor on the stream and no pending work
/// (typically: the perspective was born after the history). Repair is LOCAL (rebuild), never
/// cross-service.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[PinnedId("b5e8d2a7-4f91-4c36-8a05-6d3c9e1b7f48")]
public sealed record PerspectiveCoverageGapDetected : IEvent, IControlPlaneMessage {
  /// <summary>Report stream (one per report).</summary>
  [StreamId]
  public required Guid ReportStreamId { get; init; }

  /// <summary>The uncovered perspective.</summary>
  public required string PerspectiveName { get; init; }

  /// <summary>The stream with unfolded history.</summary>
  public required Guid GapStreamId { get; init; }

  /// <summary>Settled events on the stream the perspective should fold.</summary>
  public required int EventCount { get; init; }

  /// <summary>True when a local rebuild was dispatched (ladder at AutoRepairCapped).</summary>
  public bool AutoRebuildRequested { get; init; }
}

/// <summary>
/// Stream-integrity Phase L: one local coverage gap, as returned by
/// <see cref="IWorkCoordinator.GetPerspectiveCoverageGapsAsync"/>.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record PerspectiveCoverageGap {
  /// <summary>The stream with unfolded history.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>The uncovered perspective.</summary>
  public required string PerspectiveName { get; init; }

  /// <summary>Settled events on the stream.</summary>
  public required int EventCount { get; init; }
}
