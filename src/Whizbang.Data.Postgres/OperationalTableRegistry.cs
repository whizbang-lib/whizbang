// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>How a framework table is kept (#2354).</summary>
/// <docs>operations/infrastructure/operational-retention#every-table-is-classified</docs>
public enum OperationalTableClass {
  /// <summary>
  /// Kept: correctness depends on it. Bounded by lanes, types, epochs, streams, instances, configuration or work in
  /// flight, never by events or by time running.
  /// </summary>
  State = 0,

  /// <summary>Operational record of work done: kept for a resting period so it can be investigated, then removed.</summary>
  Rolling = 1,
}

/// <summary>One framework table and how it is kept (#2354).</summary>
/// <param name="Name">The table name, without the schema.</param>
/// <param name="Class">State or Rolling.</param>
/// <param name="Bound">What bounds it: for State, the key set it grows with; for Rolling, what removes its rows.</param>
/// <param name="Category">The retention category of a Rolling table; null for State.</param>
/// <param name="SweptHere">
/// True when the operational retention sweep (<c>wh_sweep_operational_retention</c>) removes its rows by
/// <see cref="Category"/>. False for a Rolling table another mechanism already bounds with a setting of its own, which
/// keeps that setting.
/// </param>
/// <docs>operations/infrastructure/operational-retention#every-table-is-classified</docs>
public sealed record OperationalTable(
  string Name, OperationalTableClass Class, string Bound, OperationalCategory? Category = null, bool SweptHere = false);

/// <summary>
/// Every framework table, classified as <see cref="OperationalTableClass.State"/> or
/// <see cref="OperationalTableClass.Rolling"/> (#2354).
/// </summary>
/// <remarks>
/// <para>
/// Cleanup and integrity keep records about their own work: ledgers, logs, attempt and delivery records, reports,
/// finished queue rows. Those that are never removed grow with time, so the bookkeeping itself adds growth as the system
/// scales. This registry is where each table says which kind it is, and the guard test
/// (<c>OperationalTableRegistryGuardTests</c>) fails the build when a table in the migrated schema is missing here, or
/// when an entry names a table the schema no longer has. A new operational table therefore cannot ship without a
/// retention rule.
/// </para>
/// <para>
/// Perspective tables (<see cref="PERSPECTIVE_PREFIX"/>) are application read models and State by definition; their
/// names are chosen by the application, so they are matched by prefix. Partitions are classified through their parent.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/operational-retention#every-table-is-classified</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalTableRegistryGuardTests.cs</tests>
public static class OperationalTableRegistry {
  /// <summary>The prefix of perspective tables, which are State: application read models, named by the application.</summary>
  public const string PERSPECTIVE_PREFIX = "wh_per_";

  private const string WORK_IN_FLIGHT_REMOVED_ON_COMPLETION = "work in flight: removed on completion";

  private static OperationalTable _state(string name, string bound) => new(name, OperationalTableClass.State, bound);

  private static OperationalTable _rolling(string name, OperationalCategory category, string bound, bool sweptHere = false) =>
    new(name, OperationalTableClass.Rolling, bound, category, sweptHere);

  /// <summary>Every framework table, in name order.</summary>
  public static IReadOnlyList<OperationalTable> Tables { get; } = [
    _state("wh_active_streams", "live streams; an owner gone or idle past abandoned_stream_hours is released"),
    _rolling("wh_advisory_ledger", OperationalCategory.Reports,
      "a finding no scan touched for the Reports retention is removed (#2359); one still detected is touched every scan", sweptHere: true),
    _state("wh_apply_fold_watermarks", "streams; removed with the stream"),
    _state("wh_apply_paths", "distinct apply-path signatures, bounded by the code's shape"),
    _state("wh_archive_locations", "configuration: one row per archive location"),
    _state("wh_archive_manifest", "archived streams; removed on restore or delete"),
    _state("wh_archive_object_deletions", "work in flight: blob deletions owed, removed once storage confirms"),
    _rolling("wh_archive_restore_requests", OperationalCategory.Queues,
      "Restored requests are removed after the Queues retention; Pending, Restoring and Failed are work and back-pressure, and stay",
      sweptHere: true),
    _state("wh_bootstrap_closure", "configuration: the migration bootstrap marker"),
    _rolling("wh_collective_apply_progress", OperationalCategory.Deliveries, "own retention: collective_apply_progress_retention_days (7)"),
    _state("wh_collective_ordering_heads", "ordering keys in use; idle keys go after collective_ordering_head_retention_days"),
    _state("wh_consumed_types", "consumed types"),
    _rolling("wh_dead_letter_summary", OperationalCategory.Reports,
      "a cluster past the Reports retention with no dead letter left is removed (#2359)", sweptHere: true),
    _rolling("wh_dead_letters", OperationalCategory.Deliveries,
      "own retention: recovered rows go after dead_letter_retention_days (7); unresolved rows are work"),
    _state("wh_definition_lineage", "type definitions"),
    _state("wh_digest_epoch_frontiers", "lanes"),
    _state("wh_digest_epoch_removed", "epochs per lane, tenant and type a cut removed events from (#2363)"),
    _state("wh_digest_epoch_seals", "sealed epochs: when each was sealed and last re-checked (#2353)"),
    _state("wh_digest_epochs", "sealed epochs per lane, tenant and type"),
    _rolling("wh_dlq_probe_campaigns", OperationalCategory.Reports,
      "a finished campaign past the Reports retention whose fingerprint has no dead letter left is removed (#2359); running ones are work",
      sweptHere: true),
    _state("wh_doorbell_queue", "work in flight: notifications rung then removed"),
    _rolling("wh_ephemeral_blockers", OperationalCategory.Reports, "replaced on every history walk; bounded by type pairs"),
    _state("wh_ephemeral_type_grace", "configuration: one row per ephemeral type"),
    _state("wh_event_archive", "archived event data; removed on restore or delete"),
    _state("wh_event_archive_cursors", "archived streams"),
    _state("wh_event_archive_rows", "archived perspective rows"),
    _state("wh_event_body", "event data; consumed ephemeral bodies are reaped"),
    _state("wh_event_destruction_hold", "holds on bodies about to be destroyed; removed once the body is gone"),
    _rolling("wh_event_metadata_staging", OperationalCategory.Queues, "own retention: metadata_staging_days (2), daily partitions dropped"),
    _state("wh_event_store", "event data"),
    _state("wh_fillfactor_rewrite_requests", "work in flight: rewrites owed, removed when done"),
    _state("wh_housekeeping_deferrals", "deferral budgets; removed when a pass completes"),
    _state("wh_inbox", WORK_IN_FLIGHT_REMOVED_ON_COMPLETION),
    _state("wh_inbox_state", "work in flight: removed with its inbox row"),
    _state("wh_index_equivalents", "configuration: declared index names"),
    _state("wh_index_statistics_pending", "work in flight: statistics owed, drained"),
    _state("wh_instance_capabilities", "instances"),
    _rolling("wh_instance_evictions", OperationalCategory.PairHistory, "own retention: instance_eviction_retention_hours (24)"),
    _rolling("wh_instance_exits", OperationalCategory.PairHistory, "own retention: instance_exit_retention_days (30)"),
    _state("wh_integrity_answer_progress",
      "work in flight: removed when its answer's last bundle commits, or a day after an answer's bundles stop coming (#2340)"),
    _state("wh_integrity_consumer_seals", "consumer services that report their seal (#2363)"),
    _state("wh_integrity_cuts_awaiting_seal", "streams whose cut the seal gate holds now: removed when the cut answers otherwise (#2365)"),
    _state("wh_integrity_deliveries", "work in flight: removed when handled or dropped"),
    _state("wh_integrity_domain_claims", "domain owners"),
    _state("wh_integrity_drill_cursors", "origins and windows: one resume point each, taken once"),
    _state("wh_integrity_known_origins", "origins"),
    _rolling("wh_integrity_ledger", OperationalCategory.RepairLedger,
      "own bound: a bucket is removed when it heals or is forgotten; the rest are open divergences, capped at integrity_ledger_max_entries"),
    _rolling("wh_integrity_ledger_tally", OperationalCategory.MaintenanceRuns, "own retention: two progress windows"),
    _state("wh_integrity_origin_correction", "one cursor per correction walk"),
    _rolling("wh_integrity_origin_rewrites", OperationalCategory.RewriteLog,
      "removed after the RewriteLog retention, oldest generation first, and capped at integrity_origin_rewrite_log_rows; the log start moves with it",
      sweptHere: true),
    _rolling("wh_integrity_pair_halts", OperationalCategory.PairHistory,
      "a halt that resumed longer ago than the PairHistory retention is removed (#2377); an open halt is the alert clock of a pair still halted, and stays",
      sweptHere: true),
    _state("wh_integrity_pending_gaps", "origins: replaced per origin"),
    _state("wh_integrity_reattribution", "one cursor per re-attribution walk"),
    _state("wh_integrity_seals", "origins"),
    _rolling("wh_lifecycle_completions", OperationalCategory.Deliveries, "own retention: LifecycleCompletionRetentionDays (7), daily partitions"),
    _rolling("wh_log", OperationalCategory.MaintenanceRuns, "own retention: log_retention_days (14), weekly partitions"),
    _state("wh_managed_object_declarations", "instances: removed when the instance is gone"),
    _state("wh_managed_objects", "managed database objects; retired ones are marked, not removed"),
    _state("wh_message_associations", "configuration: reconciled per service"),
    _rolling("wh_message_deduplication", OperationalCategory.Deliveries,
      "own retention: dedup_retention_days (30), which is also how long a redelivery is recognized"),
    _state("wh_message_type_registry", "configuration: message types"),
    _state("wh_metadata_history_conversion", "one row: the conversion's progress"),
    _state("wh_mixed_stream_allowances", "configuration: replaced at startup"),
    _rolling("wh_multi_stream_run_streams", OperationalCategory.Queues, "removed with its run"),
    _rolling("wh_multi_stream_runs", OperationalCategory.Queues, "own retention: MultiStreamCommandOptions.CompletedRunRetention (7 days)"),
    _state("wh_notify_state", "instances and notification keys"),
    _state("wh_offload_claims", "offloaded bodies; expire after MessageBodyOffloadOptions.PassiveExpiry"),
    _state("wh_outbox", WORK_IN_FLIGHT_REMOVED_ON_COMPLETION),
    _rolling("wh_perspective_applied", OperationalCategory.Deliveries, "own retention: one hour"),
    _state("wh_perspective_cursors", "streams and perspectives"),
    _state("wh_perspective_events", WORK_IN_FLIGHT_REMOVED_ON_COMPLETION),
    _state("wh_perspective_forms", "perspective tables"),
    _state("wh_perspective_registry", "configuration: perspectives"),
    _state("wh_perspective_row_hold", "holds on perspective rows; released or removed with the row"),
    _state("wh_perspective_snapshots", "streams and perspectives: the newest snapshots are kept"),
    _state("wh_physical_column_fills", "the model's physical fields"),
    _state("wh_published_partition_assignment", "one row"),
    _rolling("wh_receptor_handled", OperationalCategory.Deliveries, "own retention: StreamIntegrity:RepairReceptorLookback (7 days), daily partitions"),
    _state("wh_receptor_handled_coverage", "one row: where the handled ledger starts"),
    _rolling("wh_request_response", OperationalCategory.Queues,
      "own expiry: RequestResponseExpiryStep removes records past expires_at every maintenance cycle (#2359)"),
    _state("wh_role_assignments", "roles"),
    _state("wh_role_candidates", "instances"),
    _state("wh_role_pending_work", WORK_IN_FLIGHT_REMOVED_ON_COMPLETION),
    _rolling("wh_routes", OperationalCategory.Reports, "own retention: route_idle_days (365)"),
    _state("wh_row_eviction_journal", "work in flight: evictions awaiting their cascade, drained"),
    _rolling("wh_schedule_runs", OperationalCategory.MaintenanceRuns, "own retention: schedule_run_retention_days (30), newest run per schedule kept"),
    _state("wh_schedules", "configuration: schedules"),
    _state("wh_schema_migrations", "migrations"),
    _state("wh_schema_versions", "library releases applied"),
    _state("wh_sequences", "sequence names"),
    _state("wh_service_config", "one row"),
    _state("wh_service_instances", "live instances"),
    _state("wh_settings", "configuration: settings"),
    _rolling("wh_signal_cursors", OperationalCategory.Deliveries,
      "a cursor whose instance is no longer registered, unmoved for the Deliveries retention, is removed (#2360); a live pod's never is",
      sweptHere: true),
    _rolling("wh_signals", OperationalCategory.Deliveries, "own retention: SignalRetention (7 days) and the lowest live cursor"),
    _rolling("wh_stack_daily", OperationalCategory.Reports, "own retention: StackHistoryRetentionDays (90)"),
    _rolling("wh_stack_frames", OperationalCategory.Reports,
      "a frame past the Reports retention that no link names is removed (#2359)", sweptHere: true),
    _rolling("wh_stack_links", OperationalCategory.Reports, "removed with their stack (#2359)", sweptHere: true),
    _rolling("wh_stacks", OperationalCategory.Reports,
      "a stack past the Reports retention that no dead letter and no daily history row names is removed (#2359)", sweptHere: true),
    _state("wh_standby_requests", "one row: the standby handshake"),
    _state("wh_stored_form_migrations", "configuration: declared stored-form migrations"),
    _state("wh_stream_digests", "streams and types"),
    _state("wh_stream_lifecycle", "streams: floors and the lifecycle of each stream"),
    _state("wh_stream_purge_audit",
      "evidence of deliberate destruction, kept by default (#2360); stream_purge_audit_retention_days can still set a period"),
    _state("wh_stream_purge_markers", "purged streams and perspectives; cleared on resurrect"),
    _state("wh_stream_type_policies", "configuration: replaced each tick"),
    _state("wh_subscription_backfills",
      "owners and types: one paced backfill each; a finished row is the already-done marker, never removed (#2360)"),
    _state("wh_superseded_events", "work in flight: removed when reaped"),
    _state("wh_superseding_history", "retired and kept empty"),
    _rolling("wh_table_rewrite_history", OperationalCategory.MaintenanceRuns, "own retention: 90 days and 1,000 rows"),
    _state("wh_table_rewrite_measurements", "work in flight: cleared with the queued rewrite"),
    _rolling("wh_traces", OperationalCategory.Reports, "own retention: trace_retention_days (30), daily partitions"),
    _state("wh_type_definitions", "types and their versions"),
    _state("wh_type_history_walks", "types: one walk each"),
    _rolling("wh_unique_emission_claims", OperationalCategory.Deliveries, "own retention: a claim's expiry plus one day"),
    _state("wh_unsequenced_digest_changes",
      "work in flight: each change is deleted by the fold that takes it in, and the maintenance fold runs whoever asks (#2351)"),
    _state("wh_unsequenced_digest_totals", "origins, tenants and types: one row each (#2351)"),
  ];

  /// <summary>The entry for a table, or null when the table is not classified.</summary>
  /// <param name="name">The table name, without the schema.</param>
  /// <returns>The entry, a perspective table's State entry, or null.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/OperationalTableRegistryGuardTests.cs</tests>
  public static OperationalTable? Find(string name) {
    ArgumentNullException.ThrowIfNull(name);
    foreach (var table in Tables) {
      if (table.Name == name) {
        return table;
      }
    }
    return name.StartsWith(PERSPECTIVE_PREFIX, StringComparison.Ordinal)
      ? _state(name, "application read model (perspective table)")
      : null;
  }
}
