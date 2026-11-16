// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// The name of a framework background mechanism, as its database statements are attributed: the SQL comment
/// <c>/* whizbang:&lt;name&gt; */</c> that leads each statement, and the <c>mechanism</c> tag on
/// <c>whizbang.db.statements</c>, <c>whizbang.db.duration</c> and <c>whizbang.db.rows</c>.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed: the constructor is private, so every name is one of the instances below. That keeps the metric
/// tag a bounded set, and keeps the number of distinct statement texts the driver prepares to one per statement per
/// mechanism that runs it, rather than one per caller.
/// </para>
/// <para>
/// <see cref="Untagged"/> is never opened as a scope. It is what a framework command reports when no mechanism scope
/// is open around it, so a gap in attribution shows on the dashboard instead of disappearing.
/// </para>
/// </remarks>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/DbMechanismTests.cs</tests>
public sealed class DbMechanism {
  private DbMechanism(string name) {
    Name = name;
    SqlComment = $"/* whizbang:{name} */ ";
  }

  /// <summary>The mechanism's name: the metric tag value and the word in the SQL comment.</summary>
  public string Name { get; }

  /// <summary>The comment prepended to each statement the mechanism runs, with its trailing space.</summary>
  public string SqlComment { get; }

  /// <inheritdoc />
  public override string ToString() => Name;

  /// <summary>A framework command that ran with no mechanism scope open: an attribution gap.</summary>
  public static readonly DbMechanism Untagged = new("untagged");

  /// <summary>Claiming work for this instance.</summary>
  public static readonly DbMechanism Claim = new("claim");

  /// <summary>The instance heartbeat.</summary>
  public static readonly DbMechanism Heartbeat = new("heartbeat");

  /// <summary>Applying events to perspectives.</summary>
  public static readonly DbMechanism Perspective = new("perspective");

  /// <summary>Flushing completions and failures back to the work tables.</summary>
  public static readonly DbMechanism CompletionFlush = new("completion_flush");

  /// <summary>Renewing the leases on claimed work.</summary>
  public static readonly DbMechanism Lease = new("lease");

  /// <summary>Receiving, dispatching and handling inbox messages.</summary>
  public static readonly DbMechanism Inbox = new("inbox");

  /// <summary>Publishing and draining the outbox.</summary>
  public static readonly DbMechanism Outbox = new("outbox");

  /// <summary>The maintenance cycle and the janitors that run beside it.</summary>
  public static readonly DbMechanism Maintenance = new("maintenance");

  /// <summary>Shipping coalesced messages.</summary>
  public static readonly DbMechanism Coalesce = new("coalesce");

  /// <summary>Publishing and receiving stream-integrity checkpoints.</summary>
  public static readonly DbMechanism IntegrityCheckpoint = new("integrity_checkpoint");

  /// <summary>The stream-integrity audit and its scheduled sweeps (manifest and digest comparisons).</summary>
  public static readonly DbMechanism IntegrityAudit = new("integrity_audit");

  /// <summary>Draining stream-integrity repairs.</summary>
  public static readonly DbMechanism IntegrityRepair = new("integrity_repair");

  /// <summary>Re-checking the stream-integrity ledger.</summary>
  public static readonly DbMechanism IntegrityRecheck = new("integrity_recheck");

  /// <summary>Answering stream-integrity redelivery and manifest requests.</summary>
  public static readonly DbMechanism IntegrityDelivery = new("integrity_delivery");

  /// <summary>Re-attributing stored events to their origin.</summary>
  public static readonly DbMechanism OriginReattribution = new("origin_reattribution");

  /// <summary>Expanding and backfilling subscriptions.</summary>
  public static readonly DbMechanism Subscription = new("subscription");

  /// <summary>The table-statistics and bloat probes.</summary>
  public static readonly DbMechanism TableStatistics = new("table_statistics");

  /// <summary>Firing scheduled messages.</summary>
  public static readonly DbMechanism Schedule = new("schedule");

  /// <summary>A standby instance watching for its turn.</summary>
  public static readonly DbMechanism Standby = new("standby");

  /// <summary>Reconciling the message type registry.</summary>
  public static readonly DbMechanism TypeRegistry = new("type_registry");

  /// <summary>Sampling notification debounce statistics.</summary>
  public static readonly DbMechanism NotifyStatistics = new("notify_statistics");

  /// <summary>Holding and electing roles and duties, and assigning partitions.</summary>
  public static readonly DbMechanism Role = new("role");

  /// <summary>The instance's lifecycle: watching peers come and go, and deregistering on shutdown.</summary>
  public static readonly DbMechanism Lifecycle = new("lifecycle");

  /// <summary>Recovering and draining dead letters.</summary>
  public static readonly DbMechanism DeadLetter = new("dead_letter");

  /// <summary>Running multi-stream commands.</summary>
  public static readonly DbMechanism MultiStream = new("multi_stream");

  /// <summary>Stamping the commit order of stored events.</summary>
  public static readonly DbMechanism CommitOrder = new("commit_order");

  /// <summary>The signal bus and its durable signals.</summary>
  public static readonly DbMechanism Signals = new("signals");

  /// <summary>Every mechanism, <see cref="Untagged"/> included: the complete set of tag values.</summary>
  public static IReadOnlyList<DbMechanism> All { get; } = [
    Untagged, Claim, Heartbeat, Perspective, CompletionFlush, Lease, Inbox, Outbox, Maintenance, Coalesce,
    IntegrityCheckpoint, IntegrityAudit, IntegrityRepair, IntegrityRecheck, IntegrityDelivery, OriginReattribution,
    Subscription, TableStatistics, Schedule, Standby, TypeRegistry, NotifyStatistics, Role, Lifecycle, DeadLetter,
    MultiStream, CommitOrder, Signals,
  ];
}
