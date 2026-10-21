// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Workers;

/// <summary>
/// Retention windows the maintenance sweep reads from <c>wh_settings</c>, settable from configuration under
/// <c>Whizbang:Workers:Maintenance:Retention</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each property publishes one <c>wh_settings</c> row, written by the maintenance cycle before the sweep reads it,
/// so a change takes effect on the next cycle without a restart. Every property defaults to unset
/// (<see langword="null"/>): an unset property is never written, so the stored value stands, whether that is the
/// framework default or a value an operator set in SQL. A property that is set is written every cycle and wins
/// over a value set in SQL.
/// </para>
/// <para>Negative values are rejected at startup; zero is allowed and keeps nothing past the current cycle.</para>
/// </remarks>
/// <docs>operations/configuration/configuration-reference#database-side-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBridgeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/RetentionSettingsBindingTests.cs</tests>
public sealed class MaintenanceRetentionOptions {
  /// <summary>
  /// Days a message id stays in the deduplication ledger (<c>wh_message_deduplication</c>). Publishes
  /// <c>dedup_retention_days</c>; the database default is 30.
  /// </summary>
  /// <remarks>
  /// A redelivery older than this is no longer recognized as a duplicate. The ephemeral pointer prune never uses a
  /// horizon shorter than this window.
  /// </remarks>
  public int? DeduplicationDays { get; set; }

  /// <summary>
  /// Days an inbox message that was never processed is kept before the sweep purges it. Publishes
  /// <c>stuck_inbox_retention_days</c>; the database default is 7.
  /// </summary>
  public int? StuckInboxDays { get; set; }

  /// <summary>
  /// Days a recovered dead letter is kept after recovery before the sweep purges it. Publishes
  /// <c>dead_letter_retention_days</c>; the database default is 7. Skipped while debug retention is on.
  /// </summary>
  public int? RecoveredDeadLetterDays { get; set; }

  /// <summary>
  /// Hours an owner-less active-stream row must sit idle before the sweep purges it. Publishes
  /// <c>abandoned_stream_hours</c>; the database default is 1.
  /// </summary>
  /// <remarks>
  /// Rows whose owning instance is gone are purged at once regardless; this only governs the window in which an
  /// owner has been cleared and the next claim has not yet reassigned the stream.
  /// </remarks>
  public int? AbandonedStreamHours { get; set; }

  /// <summary>
  /// Hours an instance-eviction tombstone (<c>wh_instance_evictions</c>) is kept. Publishes
  /// <c>instance_eviction_retention_hours</c>; the database default is 24.
  /// </summary>
  /// <remarks>The tombstone only has to outlive the longest pause after which an evicted instance could resume.</remarks>
  public int? InstanceEvictionTombstoneHours { get; set; }

  /// <summary>
  /// Hours an unprocessed perspective event whose source event is missing is kept before the sweep removes it.
  /// Publishes <c>orphan_perspective_grace_hours</c>; the database default is 1.
  /// </summary>
  public int? OrphanPerspectiveEventGraceHours { get; set; }

  /// <summary>
  /// Days an idle collective-ordering head (<c>wh_collective_ordering_heads</c>) is kept. Publishes
  /// <c>collective_ordering_head_retention_days</c>; the database default is 7.
  /// </summary>
  public int? CollectiveOrderingHeadDays { get; set; }

  /// <summary>
  /// Days a framework log entry (<c>wh_log</c>) is kept. Publishes <c>log_retention_days</c>; the database default
  /// is 14.
  /// </summary>
  public int? LogDays { get; set; }

  /// <summary>
  /// Days a schedule run (<c>wh_schedule_runs</c>) is kept. Publishes <c>schedule_run_retention_days</c>; the
  /// database default is 30.
  /// </summary>
  /// <remarks>The newest run of every schedule is kept whatever its age, so a dormant schedule keeps its last outcome.</remarks>
  public int? ScheduleRunDays { get; set; }

  /// <summary>
  /// Days a stream-purge audit entry (<c>wh_stream_purge_audit</c>) is kept. Publishes
  /// <c>stream_purge_audit_retention_days</c>; the database default is 0, which keeps the audit forever.
  /// </summary>
  /// <remarks>The audit records what a purge destroyed, so bounding it is an operator's choice.</remarks>
  public int? StreamPurgeAuditDays { get; set; }

  /// <summary>
  /// The most entries the integrity ledger (<c>wh_integrity_ledger</c>) keeps, the most recently touched first.
  /// Publishes <c>integrity_ledger_max_entries</c>; the database default is 100000.
  /// </summary>
  public int? IntegrityLedgerMaxEntries { get; set; }
}
