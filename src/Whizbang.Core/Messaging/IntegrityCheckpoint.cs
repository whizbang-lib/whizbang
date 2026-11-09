// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stream-integrity Phase B: the origin's periodic continuity checkpoint. "Between commit-sequence
/// watermark <see cref="FromCommitSequence"/> (exclusive) and <see cref="ToCommitSequence"/>
/// (inclusive), I emitted these per-(tenant, type) counts." Consumers count the events they have
/// persisted from this origin inside the same window — keyed by the per-event
/// <c>SourceCommitSequence</c> every live delivery already carries — and compare, for the types
/// they subscribe to. A deficit persisting past the NEXT checkpoint is a confirmed gap; a missing
/// checkpoint (3× interval) is a liveness alarm.
/// </summary>
/// <remarks>
/// <c>[Ephemeral]</c>: a checkpoint is a transient control signal — once every consumer has
/// consumed it, its body self-destructs on the standard consumption-gated path. The checkpoint
/// stream is the ORIGIN's service id (one homogeneous ephemeral stream per origin). Checkpoints
/// publish even when the window is empty — absence is the alarm, so silence must be abnormal.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/IntegrityCheckpointWorkerTests.cs</tests>
[PinnedId("7d2a9c4e-5b83-4f1a-9e67-0c8d3b2a1f54")]
[Ephemeral]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record IntegrityCheckpoint : IEvent, IControlPlaneMessage {
  /// <summary>The checkpoint stream — the origin's service id (one stream per origin).</summary>
  [StreamId]
  public required Guid CheckpointStreamId { get; init; }

  /// <summary>The origin service's stable id (matches every event's <c>SourceServiceId</c>).</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical service NAME — the directed-message Target a consumer uses to
  /// send a repair request back (<see cref="RequestRedeliveryCommand"/>).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>
  /// A topic THIS ORIGIN consumes — the address a consumer publishes directed integrity
  /// requests (manifest / redelivery / drill-down) to. Carried on the checkpoint because the
  /// requester cannot guess an origin-reachable topic in a domain-scoped topology; the origin
  /// is the only party that knows where it listens. Null from older origins — requesters fall
  /// back to their legacy behavior.
  /// </summary>
  public string? RequestTopic { get; init; }

  /// <summary>Exclusive window floor (the previous checkpoint's watermark).</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive window watermark (the highest STAMPED commit sequence at publish time).</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>Per-(tenant, type) emission counts inside the window. Empty = quiet window.</summary>
  public List<CheckpointBucket> Buckets { get; init; } = [];

  /// <summary>
  /// The domains the origin owns (<c>RoutingOptions.OwnedDomains</c>), trimmed and without a trailing dot. Consumers
  /// persist them as a domain to owner map, used to re-attribute pre-origin history to its author, to direct repair and
  /// backfill at the owner, and to ignore an origin's counts for types another service owns (#2008, #2032). Additive:
  /// an origin built before this field announces none, and an empty list gives up every earlier claim.
  /// </summary>
  /// <docs>resilience/stream-integrity#ownership-is-exchanged-on-the-checkpoint</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityCheckpointWireSerializationTests.cs:IntegrityCheckpoint_FromAnOlderOriginWithoutOwnedDomains_ReadsAsEmptyAsync</tests>
  public List<string> OwnedDomains {
    get => _ownedDomains;
    // A payload from an older origin has no such field, and the generated reader then hands null: read it as none.
    init => _ownedDomains = value ?? [];
  }

  private readonly List<string> _ownedDomains = [];
}

/// <summary>One (tenant, event type) emission count inside a checkpoint window.</summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record CheckpointBucket {
  /// <summary>Tenant scope (<c>t</c> key), or null for unscoped events.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>Events of this (tenant, type) emitted inside the window.</summary>
  public required int Count { get; init; }
}

/// <summary>
/// One advanced checkpoint window, as returned by
/// <see cref="IWorkCoordinator.AdvanceIntegrityCheckpointAsync"/>: the origin's previous watermark
/// (exclusive), the new watermark (inclusive), and the per-(tenant, type) counts between them.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record IntegrityCheckpointWindow {
  /// <summary>Exclusive window floor.</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive watermark.</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>Per-(tenant, type) counts. Empty for a quiet or baseline window.</summary>
  public IReadOnlyList<CheckpointBucket> Buckets { get; init; } = [];
}

/// <summary>
/// Stream-integrity Phase B: a CONFIRMED continuity gap — a per-(tenant, type) receipt deficit
/// against an origin's checkpoint window that persisted past the NEXT checkpoint (two-cycle
/// confirmation absorbs in-flight stragglers). This is the ops report; when the repair ladder is
/// at <see cref="IntegrityRepairMode.AutoRepairCapped"/> the consumer also sends a scoped
/// <see cref="RequestRedeliveryCommand"/> for exactly this window.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityCheckpointReceptorTests.cs</tests>
[PinnedId("a1e6d8c2-3f7b-4a95-8d21-6e4c9b0f7a38")]
[SystemControlTag(Tag = Tags.SystemTags.CONTROL, Properties = [])]
public sealed record IntegrityGapDetected : IEvent, IControlPlaneMessage {
  /// <summary>Report stream (one stream per report — reports are standalone ops facts).</summary>
  [StreamId]
  public required Guid ReportStreamId { get; init; }

  /// <summary>The origin the deficit is against.</summary>
  public required Guid OriginServiceId { get; init; }

  /// <summary>The origin's logical name (the repair Target).</summary>
  public required string OriginServiceName { get; init; }

  /// <summary>Tenant scope of the deficit bucket (null = unscoped).</summary>
  public string? TenantScope { get; init; }

  /// <summary>Event type of the deficit bucket.</summary>
  public required string EventType { get; init; }

  /// <summary>Exclusive window floor.</summary>
  public required long FromCommitSequence { get; init; }

  /// <summary>Inclusive window watermark.</summary>
  public required long ToCommitSequence { get; init; }

  /// <summary>The origin's emission count for the bucket.</summary>
  public required int ExpectedCount { get; init; }

  /// <summary>This consumer's persisted receipt count for the bucket.</summary>
  public required int ActualCount { get; init; }

  /// <summary>True when a scoped repair request was sent (ladder at AutoRepairCapped).</summary>
  public bool AutoRepairRequested { get; init; }
}

/// <summary>
/// The repair ladder position for confirmed gaps (stream-integrity Phase B).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public enum IntegrityRepairMode {
  /// <summary>Report confirmed gaps only (default): detect and report, let an operator decide. Every
  /// report still states exactly what auto-repair would have done, so this is also the dry run.</summary>
  ReportOnly = 0,

  /// <summary>
  /// Report AND repair (opt-in): a scoped re-delivery request / local rebuild per confirmed gap.
  /// </summary>
  /// <remarks>
  /// Bounded in two dimensions, and both are required. Per occurrence,
  /// <c>MaxAutoRepairRequestsPerAudit</c> / <c>MaxAutoRepairRequestsPerCheckpoint</c> cap one
  /// batch (with the paced repair drain on, <c>MaxAutoRepairRequestsPerAudit</c> caps the new
  /// buckets one comparison adds to the drain's ledger, and <c>MaxRepairDrainBacklog</c> caps the
  /// ledger's total). Across occurrences, the repair ledger's <c>RepairRequestBackoffSeconds</c> and
  /// <c>MaxRepairAttemptsPerBucket</c> cap how often the SAME bucket may be re-requested.
  /// <para>
  /// The second is what actually prevents a storm; a per-batch cap alone only sets its rate.
  /// Checkpoints fire on <c>CheckpointIntervalSeconds</c>, and a gap whose events are genuinely
  /// gone never heals, so it is re-confirmed and re-requested on every checkpoint for as long as
  /// the service runs. This documentation previously claimed the per-batch caps alone made a storm
  /// impossible. They do not, and the checkpoint rung consulted only those until it was corrected.
  /// </para>
  /// </remarks>
  AutoRepairCapped = 1,
}

/// <summary>
/// Stream-integrity Phase A: one digest bucket — the order-independent identity hash of a
/// (tenant, type, stream)'s events. Two-lane 64-bit XOR of <c>hashtextextended(event_id, seed)</c>
/// with seeds 0/1: 128-bit-equivalent collision resistance, self-inverse (deletions need no
/// bookkeeping), arrival-order independent (origins fold in commit order, consumers in receive
/// order — same digest). A1c: maintained INCREMENTALLY in <c>wh_stream_digests</c> by the write
/// paths (emit-chain folds, close/reclassify subtraction) and read via
/// <see cref="IWorkCoordinator.GetStreamDigestsAsync"/>; the on-demand recompute
/// (<see cref="IWorkCoordinator.ComputeStreamDigestsAsync"/>) remains the trust-but-verify sweep.
/// At <see cref="ManifestLevel.Types"/> the same record carries a per-(tenant, type) roll-up with
/// <see cref="StreamId"/> = <see cref="Guid.Empty"/> — valid because stream buckets partition the
/// type's events, so XOR-ing them equals folding every event of the type.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record StreamDigest {
  /// <summary>Tenant scope (<c>t</c> key), or null for unscoped events.</summary>
  public string? TenantScope { get; init; }

  /// <summary>Stored event type name.</summary>
  public required string EventType { get; init; }

  /// <summary>The stream, or <see cref="Guid.Empty"/> for a type-level roll-up.</summary>
  public required Guid StreamId { get; init; }

  /// <summary>XOR lane 0 (seed 0).</summary>
  public required long DigestLo { get; init; }

  /// <summary>XOR lane 1 (seed 1).</summary>
  public required long DigestHi { get; init; }

  /// <summary>Events folded into the digest.</summary>
  public required int EventCount { get; init; }

  /// <summary>
  /// #2103: XOR of the first 64 bits (big-endian) of every folded event id, or null when not computed (an older peer,
  /// or a stored bucket not yet backfilled). With <see cref="IdHi"/>, names the one event a bucket is missing.
  /// </summary>
  /// <docs>resilience/stream-integrity#one-missing-event-is-named</docs>
  public long? IdLo { get; init; }

  /// <summary>#2103: XOR of the last 64 bits of every folded event id, or null when not computed.</summary>
  /// <docs>resilience/stream-integrity#one-missing-event-is-named</docs>
  public long? IdHi { get; init; }

  /// <summary>When the bucket last changed (table reads only; null on recomputed rows). Both
  /// audit sides skip comparing buckets updated inside the settle window — the incremental
  /// equivalent of the recompute's created-at settle filter.</summary>
  public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// Stream-integrity A1c: the outcome of one <see cref="IWorkCoordinator.VerifyDigestTableAsync"/>
/// pass — the trust-but-verify reconcile of the incrementally-maintained digest table against a
/// full recompute. Any non-zero drift means an unaccounted write path touched audited rows; the
/// pass HEALS the table (update/remove/add to match the recompute) and the caller alarms.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record DigestVerificationResult {
  /// <summary>Settled buckets the pass checked.</summary>
  public required int BucketsChecked { get; init; }

  /// <summary>Buckets whose digest/count disagreed with the recompute (healed in place).</summary>
  public required int DriftUpdated { get; init; }

  /// <summary>Phantom buckets with no backing events (removed).</summary>
  public required int DriftRemoved { get; init; }

  /// <summary>Buckets the table was missing entirely (added).</summary>
  public required int DriftAdded { get; init; }

  /// <summary>Total drifted buckets — zero means the incremental maintenance is provably clean.</summary>
  public int TotalDrift => DriftUpdated + DriftRemoved + DriftAdded;
}

/// <summary>
/// #80-D: result of the sweep's SEAL backstop — each closed digest epoch recomputed from the
/// store, compared bucket-for-bucket, and refolded on drift. Manifest answers trust seals
/// without re-verifying, so this pass is the one place a bad seal gets caught; non-zero
/// <paramref name="EpochsDrifted"/> means an unaccounted write path touched sealed history.
/// </summary>
/// <param name="EpochsChecked">Closed epochs the pass compared (unsettled-arrival epochs skip).</param>
/// <param name="EpochsDrifted">Epochs whose stored folds disagreed with the recompute (refolded).</param>
/// <docs>resilience/stream-integrity</docs>
public sealed record EpochVerificationResult(int EpochsChecked, int EpochsDrifted);

/// <summary>
/// Stream-integrity Phase S: one row of the consumed-type registry — when an event type joined
/// this service's consumed set, and where its backfill stands.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed record ConsumedTypeRegistration {
  /// <summary>Stored event type name (catalog wire name).</summary>
  public required string EventType { get; init; }

  /// <summary>The expansion's backfill lifecycle position.</summary>
  public required ConsumedTypeBackfillStatus Status { get; init; }
}

/// <summary>
/// Stream-integrity Phase S: the backfill lifecycle of a consumed-type registration.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public enum ConsumedTypeBackfillStatus {
  /// <summary>Registered on FIRST boot — nothing existed to miss, no backfill.</summary>
  Baseline = 0,

  /// <summary>Expansion detected, backfill not yet requested — the audit surface when backfill is disabled.</summary>
  Pending = 1,

  /// <summary>The broadcast re-delivery request was sent (completion graduates via the audit phases).</summary>
  Requested = 2,
}

/// <summary>
/// Stream-integrity tuning. The out-of-the-box posture is REPORT-AND-DECIDE: checkpoints, gap
/// detection, backfill, and the deep audit are ON, and repair runs at
/// <see cref="IntegrityRepairMode.ReportOnly"/> — confirmed gaps are reported (each report states
/// exactly what auto-repair would have done) and nothing is mutated unasked. Self-healing is the
/// opt-in <see cref="IntegrityRepairMode.AutoRepairCapped"/>, where every rung is hard-capped so a
/// mass divergence reports loudly instead of storming.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
public sealed class StreamIntegrityOptions {
  /// <summary>Publish periodic continuity checkpoints (default true).</summary>
  public bool CheckpointsEnabled { get; set; } = true;

  /// <summary>Checkpoint cadence in seconds (default 60).</summary>
  public int CheckpointIntervalSeconds { get; set; } = 60;

  /// <summary>Verify received counts against other origins' checkpoints (default true).</summary>
  public bool GapDetectionEnabled { get; set; } = true;

  /// <summary>What to do with a CONFIRMED gap (default <see cref="IntegrityRepairMode.ReportOnly"/>:
  /// detect and report, let an operator decide; <see cref="IntegrityRepairMode.AutoRepairCapped"/> is the
  /// opt-in to self-healing with storm caps). A default that mutates data unasked is not a default a
  /// consumer can trust out of the box; repair re-delivery has looped under a misconfigured consumer.</summary>
  public IntegrityRepairMode RepairMode { get; set; } = IntegrityRepairMode.ReportOnly;

  /// <summary>Storm cap: at most this many auto-repair requests per received checkpoint (default 10).</summary>
  public int MaxAutoRepairRequestsPerCheckpoint { get; set; } = 10;

  /// <summary>Wire topic repair requests publish to AND bundles return on. Null (default) = this service's own command
  /// inbox (#2078), or, for a service that consumes none, the first destination it consumes.</summary>
  public string? RepairTopic { get; set; }

  /// <summary>
  /// How long a directed integrity control message (a manifest request or answer, a redelivery request or answer, a
  /// drill-down or page follow) lives at the broker, in minutes (default 60, #2078). They are regenerated every
  /// integrity cycle, so a stale one is dropped by the broker rather than accumulated on a stopped service's
  /// subscription.
  /// </summary>
  /// <docs>resilience/stream-integrity#directed-integrity-traffic</docs>
  public int ControlMessageTimeToLiveMinutes { get; set; } = 60;

  /// <summary>
  /// Phase S: when the consumed-type set GROWS (a later boot adds event types), broadcast a
  /// state-only re-delivery request for the new types' history (default true). Disabling still
  /// RECORDS the expansion as Pending — the audit reports "pending backfill", not divergence.
  /// </summary>
  public bool BackfillOnSubscriptionGrowth { get; set; } = true;

  /// <summary>
  /// Events one backfill window asks an owner for (default 1,000, #2092). A grown subscription is backfilled in
  /// windows by the owner's commit sequence, one event type and owner at a time: the next window is requested only once
  /// the previous one is answered. The owner clamps it to its own <c>MaxEventsPerRequest</c>. Must be at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SubscriptionBackfillOptionsTests.cs:Defaults_PaceTheBackfillSoItCannotFloodAsync</tests>
  public int BackfillWindowSize { get; set; } = 1_000;

  /// <summary>
  /// The backfill's pace, in events requested per minute across every type (default 6,000, #2092). Each window spends
  /// its full size from a token bucket that refills at this rate and holds at most one minute's worth (or one window,
  /// whichever is larger), so a store with millions of events is backfilled over hours instead of in one burst. Must be
  /// at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SubscriptionBackfillOptionsTests.cs:Defaults_PaceTheBackfillSoItCannotFloodAsync</tests>
  public int BackfillEventsPerMinute { get; set; } = 6_000;

  /// <summary>
  /// Backfill windows awaiting their answer at once, across every type and owner (default 2, #2092). Must be at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SubscriptionBackfillOptionsTests.cs:Defaults_PaceTheBackfillSoItCannotFloodAsync</tests>
  public int BackfillMaxConcurrentWindows { get; set; } = 2;

  /// <summary>
  /// Times one backfill window is requested without an answer before the backfill of that type stops (default 3,
  /// #2092). A window waits <see cref="RepairAnswerTimeoutMinutes"/> for its answer. An owner on a release that predates
  /// windowed backfill answers the request the legacy way, with no window answer, so after this many attempts the
  /// type's backfill ends as answered by a legacy owner and the integrity audit reports anything still missing. Must be
  /// at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#paced-backfill</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/SubscriptionBackfillOptionsTests.cs:Defaults_PaceTheBackfillSoItCannotFloodAsync</tests>
  public int BackfillMaxWindowAttempts { get; set; } = 3;

  /// <summary>Phase A/L: run the scheduled deep audit (default true).</summary>
  public bool AuditEnabled { get; set; } = true;

  /// <summary>Phase A/L: audit cadence in minutes (default 1440 — daily).</summary>
  public int AuditIntervalMinutes { get; set; } = 1440;

  /// <summary>
  /// Run the FIRST deep audit shortly after startup (default true) instead of waiting a full
  /// interval — historical divergence (a consumer that drifted before this boot) heals minutes
  /// after a deploy, not a day later. The startup audit fires after a 30-second floor plus a
  /// random splay of up to <see cref="StartupAuditMaxJitterSeconds"/>, so a fleet deploy's
  /// audits de-synchronize instead of storming; A1c's type-level exchange keeps each one at
  /// O(types) wire cost. False restores interval-first scheduling.
  /// </summary>
  public bool AuditOnStartup { get; set; } = true;

  /// <summary>Max random splay (seconds, default 300) added to the startup audit's 30-second
  /// floor — the deploy de-synchronizer.</summary>
  public int StartupAuditMaxJitterSeconds { get; set; } = 300;

  /// <summary>Phase A: both sides fold only events older than this (minutes, default 60) — an
  /// in-flight delivery must never read as divergence.</summary>
  public int AuditSettleWindowMinutes { get; set; } = 60;

  /// <summary>Phase A: digest rows per manifest chunk (default 500 — bounded payloads).</summary>
  public int MaxDigestsPerManifest { get; set; } = 500;

  /// <summary>
  /// Phase A: storm cap on the stream-scoped repairs one manifest comparison may start (default 25).
  /// A comparison is one received manifest chunk, so a window followed across several pages, or
  /// several origins audited in one cycle, each get their own allowance.
  /// </summary>
  /// <remarks>
  /// What the cap counts depends on who dispatches the repairs:
  /// <list type="bullet">
  ///   <item><description><b>Paced drain on</b> (<see cref="RepairDrainEnabled"/>, the default, with
  ///   <see cref="IntegrityRepairMode.AutoRepairCapped"/>): the cap bounds how many NEW deficit buckets
  ///   one comparison may add to the repair ledger the drain dispatches from. A bucket the ledger
  ///   already tracks is refreshed without counting, because it adds nothing to the backlog.
  ///   <see cref="MaxRepairDrainBacklog"/> bounds the ledger's total across comparisons. A deficit
  ///   over either cap is deferred, not dropped: it stays divergent, so a later audit re-detects it
  ///   and admits it once there is room. Deferrals are logged and counted
  ///   (<c>whizbang.stream_integrity.repairs_deferred</c>).</description></item>
  ///   <item><description><b>Paced drain off</b> (the burst path): the cap bounds the repair requests
  ///   the comparison itself sends.</description></item>
  /// </list>
  /// </remarks>
  /// <docs>resilience/stream-integrity#repair-caps</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestDrainAdmissionTests.cs</tests>
  public int MaxAutoRepairRequestsPerAudit { get; set; } = 25;

  /// <summary>
  /// Upper bound on the paced repair drain's backlog: the per-stream repair-ledger rows the drain
  /// may claim, across every origin (default 10,000).
  /// </summary>
  /// <remarks>
  /// <para>
  /// With the drain on, a manifest comparison records each admitted deficit bucket as a ledger row
  /// and the drain dispatches those rows at <see cref="RepairDrainRatePerSecond"/>. Without a total
  /// bound the ledger grew with the number of divergent streams and accumulated across audits, for
  /// example after an upgrade that left a large history unstamped. Once the backlog reaches this
  /// bound, comparisons admit no new deficit buckets; buckets already tracked keep draining, and
  /// the deferred ones are re-detected by a later audit once the drain has made room.
  /// </para>
  /// <para>
  /// The bound is checked when a comparison admits buckets, so concurrent comparisons on several
  /// replicas may each add up to <see cref="MaxAutoRepairRequestsPerAudit"/> past it before they
  /// see each other's rows. At the default drain rate of 5 rows per second, 10,000 rows is a little
  /// over half an hour of dispatch. Must be at least 1; host start fails otherwise. Applies only
  /// while the drain is on and <see cref="RepairMode"/> is
  /// <see cref="IntegrityRepairMode.AutoRepairCapped"/>.
  /// </para>
  /// </remarks>
  /// <docs>resilience/stream-integrity#repair-caps</docs>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestDrainAdmissionTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  public int MaxRepairDrainBacklog { get; set; } = 10_000;

  /// <summary>
  /// #80-B cursor-following: pages of a windowed stream-level answer the consumer follows per
  /// (origin, window) burst (default 8). Without following, a lane wider than one page only ever
  /// audited its first <see cref="MaxDigestsPerManifest"/> streams — the rest of the window was
  /// never compared, never repaired, and the seal never certified it. The cap bounds a paging
  /// storm; whatever it leaves unfollowed re-audits from the seal next cycle.
  /// </summary>
  public int MaxManifestPagesPerAudit { get; set; } = 8;

  /// <summary>
  /// The widest window, in commit sequences, an origin answers for one windowed audit request (default 500,000, #2107).
  /// An audit asks from the consumer's seal to the origin's settled head; after a seal reset, on a new consumer, or
  /// while a divergence holds the seal back, that span is most of the origin's history, and the stream-level drill-down
  /// over it read every event in it. The origin answers at most this many sequences and marks the answer
  /// <see cref="IntegrityManifest.WindowCapped"/>; the consumer seals a clean capped window and immediately asks for the
  /// next one from where it stopped, so history is walked in bounded slices instead of skipped or read at once.
  /// </summary>
  /// <remarks>
  /// The default is five of the default 100,000-sequence digest epochs (<c>integrity_epoch_width</c>): a window that
  /// starts on an epoch boundary answers its type-level roll-up from sealed epochs, and the widest live fringe is two
  /// epochs. At the 20 or so events an 8 KB page holds in a typical event store, the worst case for a drill-down into a
  /// type dense enough to fill every page of the window is about 25,000 pages, against the hundreds of thousands an
  /// uncapped window read on a large store. Lower it when drill-downs on a large store are still slow; raise it when a
  /// store with little history needs fewer round trips. Applied by the origin, so a consumer always follows the
  /// origin's setting. Must be at least 1; host start fails otherwise.
  /// </remarks>
  /// <docs>resilience/stream-integrity#windowed-manifests</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs:RequestReceptor_AnOpenEndedAsk_ReadsOneCappedWindow_AndSaysItWasCappedAsync</tests>
  public int MaxAuditWindowSequences { get; set; } = 500_000;

  /// <summary>
  /// The windows one audit pass walks against one origin, the first included (default 5, #2107). After each capped
  /// window (<see cref="MaxAuditWindowSequences"/>) the consumer asks for the next one from where it stopped, until the
  /// walk reaches the origin's settled head or this many windows have been asked for. Every window counts, verified or
  /// not: a window that does not verify is not sealed, so the seal stays at the first such window and the next audit
  /// starts again from there, but the windows after it are still compared, and their deficits recorded and repaired,
  /// in the same audit.
  /// </summary>
  /// <remarks>
  /// The trade-off is read load per audit against how much later history is checked behind a difference that has not
  /// healed. Each window is one bounded type-level answer, plus drill-downs for the types that disagree; five windows at
  /// the default cap compare 2.5 million sequences of history per audit, which catches a consumer up on a large store in
  /// a few daily audits without turning one audit into a scan of the whole store. Raise it to check more history behind
  /// a stuck difference or to catch up faster; lower it to bound an audit's load more tightly. Counted by a window
  /// number the request carries and the origin echoes, so the bound holds without any state on the consumer. Must be at
  /// least 1; host start fails otherwise.
  /// </remarks>
  /// <docs>resilience/stream-integrity#windowed-manifests</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/IntegrityManifestReceptorTests.cs:ManifestReceptor_TheLastWindowTheBudgetAllows_SealsButAsksForNoMoreAsync</tests>
  public int MaxAuditWindowsPerAudit { get; set; } = 5;

  /// <summary>
  /// #2115: when the service is idle, re-check ledger rows whose report has gone stale, oldest first, instead of waiting
  /// for the daily audit to compare their buckets again (default true). A row clears only when its bucket is compared
  /// again, and the routine audit compares a bucket at most once a day; rows written under an older compare rule (an
  /// upgrade that changed the rule) otherwise linger for days, and with automatic repair on, the paced drain turns them
  /// into repair requests. A re-check asks the owner for a stream-level answer over the row's window, compares it, and
  /// removes the rows that now match or are undecided, exactly as the audit would; it never asks for a repair. Runs on
  /// the maintainer instance only, under the housekeeping idle gate.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/LedgerRecheckWorkerTests.cs</tests>
  public bool LedgerRecheckEnabled { get; set; } = true;

  /// <summary>
  /// #2115: seconds between idle ledger re-check ticks (default 60). Each tick asks the idle gate first, so a busy
  /// service spends nothing but that question. Must be at least 1; host start fails otherwise.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  public int LedgerRecheckIntervalSeconds { get; set; } = 60;

  /// <summary>
  /// #2115: the most ledger rows one idle re-check tick claims (default 500). Rows that share an origin, type and window
  /// are asked in one request, so a tick sends at most this many requests and usually far fewer. At the default
  /// interval that is up to 30,000 rows an hour, which clears an upgrade's worth of stale rows within a few idle hours
  /// without turning one tick into a burst of answers. Must be at least 1; host start fails otherwise.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  public int LedgerRecheckRowsPerTick { get; set; } = 500;

  /// <summary>
  /// #2115: a ledger row is re-checked once its last report is older than this many minutes (default 120), and the
  /// re-check stops when no row is that old. Twice the default <see cref="AuditSettleWindowMinutes"/>: a row reported
  /// within the last two hours was compared recently and settled history has not moved under it since, so re-checking
  /// it would only repeat that answer. A row a re-check finds still divergent is reported again and waits this long
  /// before its next re-check. Must be at least 1; host start fails otherwise.
  /// </summary>
  /// <docs>resilience/stream-integrity#idle-ledger-recheck</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs</tests>
  public int LedgerRecheckAfterMinutes { get; set; } = 120;

  /// <summary>
  /// Bulk-deficit escalation: when a TYPE-level windowed roll-up shows this service missing at
  /// least this many events of one (tenant, type) in the window (default 1000), the consumer asks
  /// the origin for ONE state-only range-bounded backfill of the whole type instead of drilling
  /// down stream-by-stream — a large deficit paged 500 streams at a time with 25 repairs per
  /// cycle takes days the bulk path covers in one request. Small deficits keep the precise
  /// drill-down. 0 or negative disables escalation.
  /// </summary>
  public int BulkBackfillThresholdEvents { get; set; } = 1000;

  /// <summary>Phase L: storm cap on local rebuilds dispatched per audit cycle (default 5).</summary>
  public int MaxAutoRebuildsPerAudit { get; set; } = 5;

  /// <summary>
  /// Phase L: hard cap on coverage-gap REPORTS per audit cycle (default 100) — and the bound on
  /// the gap query itself. A systematically-uncovered perspective can surface thousands of gaps
  /// in one cycle; an unbounded report loop flooded a live consumer's dispatcher at startup and
  /// crashlooped the pod. The remainder re-audits next cycle as repairs shrink it.
  /// </summary>
  public int MaxCoverageGapReportsPerAudit { get; set; } = 100;

  /// <summary>
  /// Hard cap on divergence REPORTS published per manifest comparison (default 100). The sibling
  /// of <see cref="MaxCoverageGapReportsPerAudit"/>, and it exists for the identical reason: each
  /// report is a durable outbox write, so an unbounded per-stream loop is thousands of sequential
  /// database round-trips inside one message handler. That starved the host's HTTP pipeline until
  /// the always-healthy liveness endpoint stopped answering, and the fleet entered a restart loop
  /// — audit, starve, get killed, restart, audit again.
  /// <para>
  /// A manifest carries up to <see cref="MaxDigestsPerManifest"/> buckets, so the cap must sit
  /// below it to bound the fan-out. Suppressed divergences are not lost: the ledger keeps them
  /// unhealed, and the next comparison re-offers whatever is still divergent, so a persistent
  /// problem still converges — it just stops trying to name every stream in one breath.
  /// </para>
  /// </summary>
  public int MaxDivergenceReportsPerManifest { get; set; } = 100;


  /// <summary>
  /// Hard cap on confirmed-gap REPORTS published per received checkpoint (default 100). The third
  /// member of the same family as <see cref="MaxCoverageGapReportsPerAudit"/> and
  /// <see cref="MaxDivergenceReportsPerManifest"/>: <see cref="MaxAutoRepairRequestsPerCheckpoint"/>
  /// already bounded the repairs in that loop, but the report published alongside them ran free.
  /// Pending gaps are keyed by (tenant, event type), so their number grows with the deployment, not
  /// with any batch size — and each report is a durable outbox write on the same thread that owes
  /// the liveness probe an answer.
  /// <para>
  /// Suppressed gaps are not lost: an unhealed deficit is re-detected on the next checkpoint, so
  /// the condition keeps surfacing until it is actually repaired.
  /// </para>
  /// </summary>
  public int MaxGapReportsPerCheckpoint { get; set; } = 100;

  /// <summary>
  /// A1c: every Nth audit cycle is a FULL SWEEP (default 7 — weekly at the daily default): the
  /// worker verifies + heals its own digest table against a full recompute
  /// (<see cref="IWorkCoordinator.VerifyDigestTableAsync"/>) and the manifest exchange runs on
  /// recomputed digests end to end — covering buckets whose steady traffic settle-skips them on
  /// table-driven cycles. 0 or negative disables sweeps (table-driven cycles only).
  /// #80-D: this counter is the FALLBACK cadence — once <see cref="FullSweepCron"/> is registered
  /// on the temporal engine, the counter stands down and the cron owns the sweep.
  /// </summary>
  public int FullSweepEveryNthAudit { get; set; } = 7;

  /// <summary>
  /// #80-D: cron for the full sweep on the temporal engine (default <c>"0 3 * * *"</c> — daily at
  /// 03:00 UTC), so the heaviest verification runs at a configured IDLE hour instead of wherever
  /// the every-Nth-cycle counter happens to land it. When the cron's minute field is <c>0</c>
  /// (the default), each service replaces it with a stable per-service splay minute so a fleet
  /// sharing one database server does not sweep in unison; an explicit non-zero minute is honored
  /// verbatim. Null or empty disables cron scheduling —
  /// <see cref="FullSweepEveryNthAudit"/> then remains the cadence, as it does on hosts without
  /// the temporal engine.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public string? FullSweepCron { get; set; } = "0 3 * * *";

  /// <summary>
  /// #80-D: cap on closed epochs the sweep's seal backstop recomputes per sweep (default 10000).
  /// Manifest answers trust sealed epochs without re-verifying, so the sweep is the one place a
  /// bad seal gets caught — but the recompute is O(events-in-epoch) each, and a very large store
  /// finishes verification across several nightly sweeps rather than stalling one.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public int MaxEpochVerificationsPerSweep { get; set; } = 10_000;

  /// <summary>
  /// A1c: storm cap on the DRILL-DOWN — how many mismatched types one type-level manifest may
  /// escalate to stream-level manifest requests (default 10). Remaining mismatches re-audit next
  /// cycle; a healthy system drills down for zero.
  /// </summary>
  public int MaxDrillDownTypesPerAudit { get; set; } = 10;

  /// <summary>
  /// Minutes an UNCHANGED divergence stays silent after being reported (default 60). The same
  /// unhealed bucket re-detected on every audit cycle is cadence, not news — without this
  /// cooldown a persistent divergence floods the outbox with an
  /// <see cref="IntegrityDivergenceDetected"/> per bucket per cycle (observed live: tens of
  /// thousands of rows in hours, saturating a shared database server). A changed signature
  /// (either side's digest moved) always reports immediately.
  /// </summary>
  public int DivergenceReportCooldownMinutes { get; set; } = 60;

  /// <summary>
  /// Base seconds between repair requests for one divergent bucket (default 300); each further
  /// attempt doubles the wait. An origin that stays silent — down, or unable to help — is asked
  /// less and less often instead of hammered on every audit cycle.
  /// </summary>
  public int RepairRequestBackoffSeconds { get; set; } = 300;

  /// <summary>
  /// Repair attempts per divergent bucket before the requester stops asking (default 8). Past the cap the bucket is
  /// no longer requested at all and counts in <c>repair_exhausted_buckets</c>: it needs operator eyes, not a request
  /// loop (#2029). The divergence still re-reports at the cooldown cadence. A change repair can act on resets the
  /// budget: this store's digest of the bucket moved, or its deficit grew (#2030). New events on the origin's side
  /// alone do not.
  /// </summary>
  /// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
  public int MaxRepairAttemptsPerBucket { get; set; } = 8;

  /// <summary>
  /// How long a requested bucket waits for its answer before it may be asked for again, in minutes (default 180).
  /// A request is answered by a redelivery bundle; the bucket is not requested while the answer is on the way, and a
  /// bundle that lands for it stops the asking until the next comparison decides (#2029). The answer can wait in the
  /// origin's integrity queue (up to <c>Whizbang:Housekeeping:MaxDeferral</c>, one hour by default, before a forced
  /// pass) and then in this service's inbox, so the default covers that with room to spare, and stays well inside the
  /// daily audit so a request that was lost is asked for again the same day. Must be at least 1.
  /// </summary>
  /// <docs>resilience/stream-integrity#repair-waits-for-its-answer</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs:RepairAnswerTimeout_DefaultsToThreeHoursAsync</tests>
  public int RepairAnswerTimeoutMinutes { get; set; } = 180;

  /// <summary>
  /// Run the paced repair drain (default true): a continuous worker that dispatches repair
  /// requests from the durable ledger at a steady rate instead of bursting a whole budget at
  /// each audit tick. Discovery (audits, checkpoints) records deficits; the drain sends.
  /// Requires a drain-capable coordinator; inert otherwise.
  /// </summary>
  /// <docs>proposals/paced-repair-drain</docs>
  public bool RepairDrainEnabled { get; set; } = true;

  /// <summary>
  /// Steady-state repair dispatch rate in ledger rows per second (default 5). The drain's token
  /// bucket refills at this rate (burst cap 2×), so request traffic is spread over the whole
  /// interval instead of leaving in one audit-tick burst that saturates the broker.
  /// </summary>
  /// <docs>proposals/paced-repair-drain</docs>
  public double RepairDrainRatePerSecond { get; set; } = 5;

  /// <summary>
  /// Maximum ledger rows claimed in one drain pass (default 50) — bounds a single pass's claim
  /// round trip and the burst a freshly-filled token bucket can emit.
  /// </summary>
  /// <docs>proposals/paced-repair-drain</docs>
  public int RepairDrainBatchSize { get; set; } = 50;

  /// <summary>
  /// The most repair-ledger buckets that may await an answer at once (default 500); 0 turns the cap off. While this many
  /// requested buckets have their answer on the way, the paced repair drain claims no new row, and it resumes as answers
  /// land or time out (#2184).
  /// </summary>
  /// <remarks>
  /// <para>
  /// An answer can land up to <see cref="RepairAnswerTimeoutMinutes"/> after its request, so the request rate alone
  /// does not bound the load answers put on this service's store: at the default 5 rows a second, three hours of
  /// requests is 54,000 buckets whose answers could arrive together. The cap bounds that to its own size. At the
  /// default rate, 500 answers is 100 seconds of requests: while answers land within that, the cap is never reached and
  /// the drain runs at <see cref="RepairDrainRatePerSecond"/>; when they lag, the drain is paced to the rate they land at.
  /// A drain holding at the cap is logged once as a warning when the hold starts and once when it ends, and counted
  /// (<c>whizbang.stream_integrity.repair_drain_holds</c>); <c>repair_answer_pending_buckets</c> shows the count.
  /// </para>
  /// <para>
  /// The count covers every origin, because every answer lands in this service's store. Must not be negative; host
  /// start fails otherwise.
  /// </para>
  /// </remarks>
  /// <docs>resilience/stream-integrity#outstanding-repair-answers-are-capped</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/RepairDrainWorkerTests.cs:DrainTick_AtTheOutstandingAnswerCap_ClaimsNothing_AndSaysSoOnceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamIntegrityOptionsValidatorTests.cs:Validate_ANegativeOutstandingAnswerCap_FailsNamingTheKeyAsync</tests>
  public int MaxOutstandingRepairAnswers { get; set; } = 500;

  /// <summary>
  /// How often the maintainer's integrity delivery drain runs, in seconds (default 15). Integrity deliveries (received
  /// checkpoints, manifest requests and chunks, redelivery requests, bundle reconciles) are recorded where they arrive
  /// and handled by the instance holding the maintainer duty on this cadence, under the housekeeping idle gate (#2001).
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int IntegrityDeliveryIntervalSeconds { get; set; } = 15;

  /// <summary>Integrity deliveries leased per round trip of the drain (default 50).</summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int IntegrityDeliveryBatchSize { get; set; } = 50;

  /// <summary>
  /// The most integrity deliveries one drain pass handles (default 500), so a pass admitted by the idle gate (or
  /// forced through after <c>Whizbang:Housekeeping:MaxDeferral</c>) is bounded; the rest waits for the next pass.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int MaxIntegrityDeliveriesPerPass { get; set; } = 500;

  /// <summary>
  /// How long a leased integrity delivery stays invisible to another drain, in seconds (default 300). It covers a
  /// maintainer-duty failover that briefly overlaps two drains; a delivery not completed or failed by then is due again.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int IntegrityDeliveryLeaseSeconds { get; set; } = 300;

  /// <summary>
  /// Attempts an integrity delivery gets before it is dropped with a warning naming its type and error (default 5).
  /// Control-plane traffic is dropped at its bound rather than dead-lettered: the next checkpoint, audit or repair
  /// cycle asks again.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int MaxIntegrityDeliveryAttempts { get; set; } = 5;

  /// <summary>
  /// The wait before retrying a failed integrity delivery, in seconds (default 30), doubled on each further attempt and
  /// capped at one hour.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-work-runs-on-the-maintainer</docs>
  public int IntegrityDeliveryRetryBackoffSeconds { get; set; } = 30;

  /// <summary>
  /// Advance the digest-epoch closure frontier on the maintenance cadence (default true).
  /// Epochs (migration 092) are what let manifest answers read immutable folds instead of
  /// re-aggregating live history; without closure the frontier never moves and every audit
  /// keeps paying the full-scan cost the epochs exist to end. Disable only on engines that
  /// serve no integrity manifests.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public bool EpochClosureEnabled { get; set; } = true;

  /// <summary>
  /// Max epochs closed per maintenance cycle across all lanes (default 64). Each closure is an
  /// O(events-in-epoch) recompute, so this bounds a cycle's closure work by the operator's cap
  /// rather than by backlog size — a long-unclosed store catches up over several cycles instead
  /// of stalling one. The settle window closure uses is <see cref="AuditSettleWindowMinutes"/>:
  /// closure and the audit MUST agree on what "settled" means, or a seal could disagree with a
  /// manifest folded over the same range.
  /// </summary>
  /// <docs>resilience/stream-integrity</docs>
  public int MaxEpochClosuresPerMaintenanceCycle { get; set; } = 64;

  /// <summary>
  /// Publish <see cref="IntegrityDivergenceDetected"/> / <see cref="IntegrityGapDetected"/> as
  /// durable events (default <c>false</c>).
  /// </summary>
  /// <remarks>
  /// <para>
  /// Stream integrity has two halves. REPAIR is a closed loop — a request goes to the origin, the
  /// origin answers, the divergence heals. REPORTING was an open loop: nothing in the framework
  /// consumes either report type, so every one published was a durable write that no code path
  /// ever read.
  /// </para>
  /// <para>
  /// The cost was not proportional to "some unread messages". Each report carries its own
  /// <c>ReportStreamId</c>, so it mints a NEW event stream — a stream row, an outbox row, an
  /// event-store pointer and body, and perspective work items, per sighting. With no consumer no
  /// cursor ever advances past them, so the consumption-gated reaper can never collect them
  /// either: not a backlog that drains, but unbounded permanent growth in the tables the work
  /// pump scans every poll.
  /// </para>
  /// <para>
  /// The state those reports described is what the durable ledger already holds — one row per
  /// divergent bucket, deduplicated, surviving restarts — and it is surfaced as gauges
  /// (<c>whizbang.integrity.unhealed_buckets</c> and friends) that go DOWN when things heal.
  /// A count of currently-broken things is what an operator can act on; a stream of past-tense
  /// notifications is not. Enable this only if you have a consumer that genuinely reacts.
  /// </para>
  /// </remarks>
  public bool PublishReportEvents { get; set; }

  /// <summary>
  /// Re-attribute this store's pre-origin history to the services that wrote it (default true; #2008). Runs on the
  /// maintainer under the housekeeping idle gate, and only when owned domains are registered: events of a type another
  /// service owns take that owner as origin and leave the local lane.
  /// </summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public bool ReattributionEnabled { get; set; } = true;

  /// <summary>Seconds between re-attribution passes (default 60). At least 1.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public int ReattributionIntervalSeconds { get; set; } = 60;

  /// <summary>Rows one re-attribution batch scans, by commit sequence (default 5000). At least 1.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public int ReattributionBatchSize { get; set; } = 5000;

  /// <summary>The most re-attribution batches one pass runs (default 20), so a pass admitted by the idle gate, or forced
  /// through, is bounded. At least 1.</summary>
  /// <docs>resilience/stream-integrity#re-attribution</docs>
  public int MaxReattributionBatchesPerPass { get; set; } = 20;

}
