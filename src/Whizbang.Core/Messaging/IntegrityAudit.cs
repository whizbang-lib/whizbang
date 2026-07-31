using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity Phase A: asks an origin for its identity manifest — the digests of its OWN
/// emissions for the requester's subscribed types. Sent DIRECTED at the origin (envelope Target);
/// the origin answers with <see cref="IntegrityManifest"/> events targeted back at
/// <see cref="RequesterService"/> on <see cref="Topic"/>.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
[PinnedId("c9f4b1e7-2a6d-4c38-9b05-7e1f8d3a6c92")]
public sealed record RequestIntegrityManifest : ICommand {
  /// <summary>The auditing consumer's logical name — becomes the manifests' Target.</summary>
  public required string RequesterService { get; init; }

  /// <summary>Wire topic the manifests publish to.</summary>
  public required string Topic { get; init; }

  /// <summary>Types to manifest (the requester's subscribed set); null = all.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }
}

/// <summary>
/// Stream-integrity Phase A: one chunk of an origin's identity manifest — digest rows of its own
/// emissions. <c>[Ephemeral]</c>: superseded control data, reaped once consumed. Comparison is
/// per-bucket independent, so chunks need no assembly protocol — a lost chunk's buckets simply
/// re-audit next cycle.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs</tests>
[PinnedId("e2b7d9f4-6c15-4a83-b270-9d4e1c8f5a36")]
[Ephemeral]
public sealed record IntegrityManifest : IEvent {
  /// <summary>The manifest stream — the origin's service id.</summary>
  [StreamId]
  public required Guid ManifestStreamId { get; init; }

  /// <summary>The origin's stable id (what consumers key received rows on).</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical name (the repair Target).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Digest rows for this chunk.</summary>
  public List<StreamDigest> Digests { get; init; } = [];
}

/// <summary>
/// Stream-integrity Phase A: a CONFIRMED audit divergence — a (tenant, type, stream) bucket whose
/// consumer-side digest disagrees with the origin's manifest (missing events, or a differing
/// fold). The ops report; at <see cref="IntegrityRepairMode.AutoRepairCapped"/> a stream-scoped
/// re-delivery request also goes back to the origin.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
[PinnedId("f8a3c6e1-9d47-4b28-a5c1-3e7b0d9f2a64")]
public sealed record IntegrityDivergenceDetected : IEvent {
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
/// <docs>proposals/stream-integrity</docs>
[PinnedId("b5e8d2a7-4f91-4c36-8a05-6d3c9e1b7f48")]
public sealed record PerspectiveCoverageGapDetected : IEvent {
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
/// <docs>proposals/stream-integrity</docs>
public sealed record PerspectiveCoverageGap {
  /// <summary>The stream with unfolded history.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>The uncovered perspective.</summary>
  public required string PerspectiveName { get; init; }

  /// <summary>Settled events on the stream.</summary>
  public required int EventCount { get; init; }
}
