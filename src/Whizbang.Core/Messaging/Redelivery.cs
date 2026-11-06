// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// <item><b>At-most-once schedule occurrences</b> (flagged <see cref="EventFlags.AtMostOnce"/> on the stored row)
/// are never selected — re-delivery would violate the exact guarantee they were declared for.</item>
/// <item><b>Reaped ephemeral events</b> are excluded structurally: selection joins the event body,
/// and a reaped body is absent — an event whose body no longer exists cannot be re-delivered
/// (accepted ephemeral loss). An ephemeral event still inside its grace window has a body and IS
/// selectable.</item>
/// </list>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
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

  /// <summary>
  /// Keyset continuation, with <see cref="AfterVersion"/>: only rows strictly after
  /// (stream, version) in the selection's (stream_id, version) order. Null = from the start.
  /// Lets the origin page a large repair in bounded slices instead of materializing the whole
  /// cap in memory at once.
  /// </summary>
  public Guid? AfterStreamId { get; init; }

  /// <summary>Keyset continuation partner of <see cref="AfterStreamId"/>.</summary>
  public long? AfterVersion { get; init; }

  /// <summary>
  /// The answering service's name. When set, only events that service authored are selected: no origin recorded, and
  /// a stored first hop that names this service, names no service, or is absent (#2032). An event received from
  /// another service, whether it carries that origin or only a first hop naming it, is refused by every receiver, so
  /// shipping it is pure cost. Null selects as before.
  /// </summary>
  /// <docs>resilience/stream-integrity#redelivery-ships-only-what-the-origin-wrote</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SelectRedeliveryEventsTests.cs:Select_AuthoredBy_ShipsOnlyEventsTheAnsweringServiceWroteAsync</tests>
  public string? AuthoredBy { get; init; }

  /// <summary>
  /// True selects in commit-sequence order instead of (stream, version) order, and only stamped rows (#2092): a
  /// windowed backfill walks one type's history by the owner's commit sequence, continuing from
  /// <see cref="FromCommitSequence"/>, so the last row of a window is exactly where the next one starts. The keyset
  /// continuation (<see cref="AfterStreamId"/>) does not apply in this order; page with
  /// <see cref="FromCommitSequence"/> instead.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/SelectRedeliveryEventsTests.cs:Select_ByCommitSequence_WalksStampedRowsInCommitOrderAsync</tests>
  public bool OrderByCommitSequence { get; init; }
}

/// <summary>
/// One selected event in original stored form — the unit the re-delivery pump bundles into
/// per-stream composites. <see cref="EventData"/>/<see cref="Metadata"/> are the raw stored JSON.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
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
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryRequestReceptorTests.cs</tests>
[PinnedId("4f8e2b7a-9d31-4c6e-8b5f-2a1d0c9e7f43")]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record RequestRedeliveryCommand : ICommand, IControlPlaneMessage, Audit.IBoundedCommandAudit {
  private static readonly string[] _idLists = [nameof(StreamIds)];

  /// <inheritdoc />
  /// <docs>fundamentals/security/audit-logging#bounded-command-audit</docs>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/CommandAuditListLengthsTests.cs:ARedeliveryRequest_IsAuditedWithItsStreamCountAsync</tests>
  public IReadOnlyCollection<string> ListsAuditedAsLengths() => _idLists;

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

  /// <summary>
  /// True for a request an operator made for an explicit list of streams (<see cref="IStreamRedeliveryRequester"/>),
  /// rather than one the integrity ledger drove. Report-only (<see cref="IntegrityRepairMode.ReportOnly"/>) opts a
  /// service out of AUTOMATIC repair, so an operator's request is served and its bundles are applied whatever the
  /// repair mode on either side; the bundles carry the flag as <see cref="Minting.RedeliveryComposite.OperatorRequested"/>.
  /// </summary>
  public bool OperatorRequested { get; init; }

  /// <summary>
  /// Set by a paced backfill (#2092): the request is one WINDOW of that backfill. The origin selects in commit-sequence
  /// order from <see cref="FromCommitSequence"/>, up to <see cref="MaxEvents"/> (clamped by its own cap), ships the
  /// events in as few bundles as its size bounds allow, all on this id's session, and then sends a
  /// <see cref="BackfillWindowAnswer"/> saying where the window stopped and whether more remains. Null (the default) is
  /// an ordinary request. An origin on a release that predates the field ignores it and answers the ordinary way.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryWindowAnswerTests.cs</tests>
  public Guid? BackfillId { get; init; }

  /// <summary>The window's number within its backfill (1 for the first), echoed on the answer so a late answer to an
  /// earlier window is recognized and ignored (#2092).</summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  public long BackfillWindow { get; init; }
}

/// <summary>
/// The origin's answer to one window of a paced backfill (#2092), sent to the requester after the window's bundles. It
/// says where the window stopped (<see cref="StoppedAtCommitSequence"/>) and whether more remains (<see cref="HasMore"/>):
/// an answer that stops at the origin's bound never truncates silently, and the requester asks for the next window from
/// exactly that point (#2098). Rides the backfill's session, after its bundles.
/// </summary>
/// <docs>resilience/stream-integrity#paced-backfill</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/RedeliveryWindowAnswerTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/BackfillWindowAnswerReceptorTests.cs</tests>
/// <remarks>
/// Not in the control class (no <c>SystemControlTag</c>): it travels as the window's bundles do, on the backfill's
/// session and in order behind them, and is not superseded by a later message.
/// </remarks>
[PinnedId("84fb00a5-2cc2-416b-88e2-132982c701f3")]
public sealed record BackfillWindowAnswer : ICommand, IControlPlaneMessage {
  /// <summary>The backfill this answers, from <see cref="RequestRedeliveryCommand.BackfillId"/>.</summary>
  public required Guid BackfillId { get; init; }

  /// <summary>The window this answers, from <see cref="RequestRedeliveryCommand.BackfillWindow"/>.</summary>
  public required long Window { get; init; }

  /// <summary>The answering origin's service id.</summary>
  public Guid OriginServiceId { get; init; }

  /// <summary>The answering origin's service name.</summary>
  public string? OriginServiceName { get; init; }

  /// <summary>Events the window selected (shipped or, for a type another service owns, passed over).</summary>
  public int EventCount { get; init; }

  /// <summary>The commit sequence of the first event the window selected; null when it selected none.</summary>
  public long? FirstCommitSequence { get; init; }

  /// <summary>
  /// The commit sequence of the last event the window selected: the next window starts after it. Null when the window
  /// selected nothing, in which case the requester's position does not move.
  /// </summary>
  public long? StoppedAtCommitSequence { get; init; }

  /// <summary>True when the window stopped at its bound, so more of the type's history may remain.</summary>
  public bool HasMore { get; init; }

  /// <summary>The highest commit sequence of the requested types in the origin's store when it answered, for the
  /// requester's estimate of what remains; null when unknown.</summary>
  public long? HeadCommitSequence { get; init; }
}
