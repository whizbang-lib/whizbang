using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Selection criteria for re-delivering persisted events from this (origin) service's event store
/// to the wire — stream-integrity R1. Every filter is optional and conjunctive; an empty request
/// selects everything up to <see cref="MaxEvents"/>. Selection is identity-preserving: results
/// carry original event ids, versions, and commit sequences, ordered by (stream, version) so a
/// per-stream bundle replays in append order.
/// </summary>
/// <remarks>
/// Two exclusions are built into the selection, not left to callers:
/// <list type="bullet">
/// <item><b>At-most-once schedule occurrences</b> (envelope metadata <c>deliveryGuarantee = 1</c>)
/// are never selected — re-delivery would violate the exact guarantee they were declared for.</item>
/// <item><b>Reaped ephemeral events</b> are excluded structurally: selection joins the event body,
/// and a reaped body is absent — an event whose body no longer exists cannot be re-delivered
/// (accepted ephemeral loss). An ephemeral event still inside its grace window has a body and IS
/// selectable.</item>
/// </list>
/// </remarks>
/// <docs>proposals/stream-integrity</docs>
public sealed record RedeliveryRequest {
  /// <summary>Tenant filter — matches the event scope's tenant (<c>scope-&gt;&gt;'t'</c>) exactly when set.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Event-type filter (stored <c>event_type</c> names); null = all types.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }

  /// <summary>Stream filter; null = all streams matching the other criteria.</summary>
  public IReadOnlyList<Guid>? StreamIds { get; init; }

  /// <summary>Exclusive commit-sequence floor; null = from the beginning.</summary>
  public long? FromCommitSequence { get; init; }

  /// <summary>Inclusive commit-sequence ceiling (a comparison watermark); null = to the head.</summary>
  public long? ToCommitSequence { get; init; }

  /// <summary>Hard cap on selected events per call — the storm-cap building block. Default 10,000.</summary>
  public int MaxEvents { get; init; } = 10_000;
}

/// <summary>
/// One selected event in original stored form — the unit the re-delivery pump bundles into
/// per-stream composites. <see cref="EventData"/>/<see cref="Metadata"/> are the raw stored JSON.
/// </summary>
/// <docs>proposals/stream-integrity</docs>
public sealed record RedeliveryEvent {
  /// <summary>Original event id — preserved so consumers converge via the event-id conflict skip.</summary>
  public required Guid EventId { get; init; }

  /// <summary>The stream this event belongs to.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>Per-stream version — bundle ordering key.</summary>
  public required long Version { get; init; }

  /// <summary>Origin commit sequence (null on rows stored before stamping).</summary>
  public long? CommitSequence { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>Raw stored event body JSON.</summary>
  public required string EventData { get; init; }

  /// <summary>Raw stored envelope metadata JSON (null when none was stored).</summary>
  public string? Metadata { get; init; }

  /// <summary>Raw stored scope JSON (null for unscoped events).</summary>
  public string? Scope { get; init; }

  /// <summary>Persisted <see cref="EventFlags"/> bits.</summary>
  public int Flags { get; init; }
}

/// <summary>
/// Stream-integrity R1b: asks an origin service to re-deliver persisted events matching the given
/// selection. A damaged consumer (or an operator) dispatches this command with the envelope's
/// <see cref="Whizbang.Core.Observability.IMessageEnvelope.Target"/> set to the origin service's
/// name — directed messaging routes it there without any domain-level ownership of a framework
/// command. The origin's built-in receptor (driver assembly) runs the selection and pumps the
/// results back as targeted <see cref="RedeliveryComposite"/> bundles on <see cref="Topic"/>,
/// directed at <see cref="RequesterService"/>.
/// </summary>
/// <remarks>
/// Selection fields mirror <see cref="RedeliveryRequest"/>; <see cref="MaxEvents"/> is a REQUEST,
/// clamped by the origin's configured <c>RedeliveryPumpOptions.MaxEventsPerRequest</c> — the origin
/// owns its own storm cap, a requester can never raise it.
/// </remarks>
/// <docs>proposals/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
[PinnedId("4f8e2b7a-9d31-4c6e-8b5f-2a1d0c9e7f43")]
public sealed record RequestRedeliveryCommand : ICommand {
  /// <summary>Optional tenant filter (scope <c>t</c> key), as in <see cref="RedeliveryRequest.TenantScope"/>.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Optional stored event-type-name filter; null = all types.</summary>
  public IReadOnlyList<string>? EventTypes { get; init; }

  /// <summary>Optional stream filter; null = all streams.</summary>
  public IReadOnlyList<Guid>? StreamIds { get; init; }

  /// <summary>Exclusive commit-sequence floor; null = from the beginning.</summary>
  public long? FromCommitSequence { get; init; }

  /// <summary>Inclusive commit-sequence ceiling; null = to the head.</summary>
  public long? ToCommitSequence { get; init; }

  /// <summary>Requested event cap; null = the origin's configured cap. Always clamped by the origin.</summary>
  public int? MaxEvents { get; init; }

  /// <summary>The requesting (damaged) service's logical name — becomes the re-delivery bundles' Target.</summary>
  public required string RequesterService { get; init; }

  /// <summary>Wire topic the bundles publish to (the same topic the original events published to).</summary>
  public required string Topic { get; init; }

  /// <summary>
  /// Phase S: true = the bundles deliver STATE-ONLY (event-stored and projected, trigger receptors
  /// skipped) — the backfill semantics. False (default) = repair semantics: the delivery a live
  /// subscriber missed, receptors and all.
  /// </summary>
  public bool StateOnly { get; init; }
}
