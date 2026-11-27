// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2626: the inventory of every read of a stored event body, derived by machine, so a body packed by #2625 never
/// leaves the database packed. Every SQL function whose latest definition reads <c>wh_event_body.event_data</c> or
/// <c>wh_event_archive.event_data</c>, and every driver source whose SQL names those tables, is found by what it does and
/// must be classified here with its reason. An export (a read whose body goes to another service, the transport, or code
/// outside the framework as raw JSON) must read through <c>wh_event_body_wire_form</c>. A new read fails until it is
/// classified, and a classification that matches nothing fails, so the list cannot go stale. The transport's publish
/// seam is checked independently: it reads the outbox, never a stored body.
/// </summary>
/// <docs>data/event-store#what-leaves-the-database</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/721_StoredBodiesLeaveInTheWireForm.sql</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public partial class PackedVectorExportInventoryTests {
  private const string WIRE_FORM = "wh_event_body_wire_form";

  private enum Use {
    /// <summary>Writes a stored body (the packing applies here).</summary>
    Write,

    /// <summary>Reads it in the database or the process, through typed readers or for bookkeeping: either form is read.</summary>
    InProcess,

    /// <summary>Copies it to another table of this database, which keeps the stored form.</summary>
    StorageCopy,

    /// <summary>Sends it out as raw JSON: must read through the wire form.</summary>
    Export,
  }

  [GeneratedRegex(@"--[^\r\n]*")]
  private static partial Regex LineComment();

  [GeneratedRegex(@"\bwh_event_(?:body|archive)\b")]
  private static partial Regex NamesABodyTable();

  /// <summary>Every SQL function whose latest definition reads a stored body, and why its form is right.</summary>
  private static readonly Dictionary<string, (Use Use, string Reason)> _functions = new(StringComparer.Ordinal) {
    ["_emit_event_store_chain"] = (Use.Write, "writes the stored body from the outbox's wire form, packing under the gate"),
    ["_emit_event_store_chain_for_inbox"] = (Use.Write, "writes the stored body from the inbox's wire form, packing under the gate"),
    ["wh_append_event"] = (Use.Write, "writes the stored body of a direct append, packing under the gate"),
    ["wh_backfill_event_bodies_batch"] = (Use.Write, "the 077 split, batched"),
    ["wh_restore_archived_stream"] = (Use.Write, "restores archived bodies into wh_event_body as they were stored"),
    ["fetch_events_by_ids"] = (Use.InProcess, "the perspective worker's read: deserialized through the generated readers"),
    ["get_stream_events_in_stream_order"] = (Use.InProcess, "the perspective drain: deserialized through the generated readers"),
    ["wh_superseding_history_rows"] = (Use.InProcess, "the superseding reconcile reads keys from metadata, in process"),
    ["wh_audit_body_apply"] = (Use.InProcess, "edits audit record bodies in place; an audit record has no vector paths"),
    ["wh_finalize_stream"] = (Use.InProcess, "sizes the body for the lifecycle ledger"),
    ["_wh_stream_lifecycle_settle"] = (Use.InProcess, "sizes the body for the lifecycle ledger"),
    ["wh_backfill_stream_lifecycle_batch"] = (Use.InProcess, "sizes the body for the lifecycle ledger"),
    ["_wh_record_ephemeral_blockers"] = (Use.InProcess, "reads whether a body exists, for ephemeral conversion"),
    ["wh_archive_stream"] = (Use.StorageCopy, "moves an archived stream's bodies to wh_event_archive in the stored form"),
    ["wh_truncate_stream_prefix"] = (Use.StorageCopy, "moves a truncated prefix's bodies to wh_event_archive in the stored form"),
    ["wh_trace_metadata"] = (Use.Export, "the trace store hands the payload to consumer code as a JsonElement"),
    [WIRE_FORM] = (Use.Export, "the wire form itself"),
  };

  /// <summary>Every driver source whose SQL names a stored-body table, with how many times, and why its form is right.</summary>
  private static readonly Dictionary<string, (int Count, Use Use, string Reason)> _sources = new(StringComparer.Ordinal) {
    ["src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs"] = (3, Use.Export,
      "the redelivery selection sends bodies to a peer (integrity repair) and to the audit rebuild, both onto the "
      + "transport, so it reads the wire form; its other read is the orphaned-lifecycle probe, which reads no body"),
    ["src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs"] = (5, Use.InProcess,
      "the Dapper event store's reads and writes: typed through the generated readers, and its append writes through wh_append_event"),
    ["src/Whizbang.Data.EFCore.Postgres/Configuration/WhizbangModelBuilderExtensions.cs"] = (1, Use.InProcess,
      "maps the table for the EF Core event store, whose reads are typed through the generated readers"),
    ["src/Whizbang.Data.Postgres/CoordinatorCatalogSql.cs"] = (1, Use.InProcess, "bookkeeping: whether a body exists"),
    ["src/Whizbang.Data.Postgres/EphemeralDestructionSql.cs"] = (2, Use.InProcess, "deletes reaped ephemeral bodies"),
    ["src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs"] = (1, Use.InProcess, "table sizes"),
    ["src/Whizbang.Data.Postgres/OperationalTableRegistry.cs"] = (2, Use.InProcess, "names the two tables' roles"),
    ["src/Whizbang.Data.Postgres/OrphanedLifecycleEventsSql.cs"] = (1, Use.InProcess, "reads metadata to reconcile lifecycle events"),
    ["src/Whizbang.Data.Postgres/PostgresFanoutChildRefiler.cs"] = (1, Use.InProcess,
      "deserializes typed only to read the stream id; the body is not passed on"),
    ["src/Whizbang.Data.Postgres/PostgresObjectArchiveStore.cs"] = (3, Use.StorageCopy,
      "the archive tier: uploads wh_event_archive in the stored form and restores it, read back only through the typed readers"),
    ["src/Whizbang.Data.Postgres/PostgresTableArchiveStore.cs"] = (1, Use.StorageCopy,
      "the archive tier's table reads, read back through the archive-aware event store, typed"),
    ["src/Whizbang.Data.Postgres/StreamPrefixTruncateSql.cs"] = (1, Use.StorageCopy, "reads the archived prefix for the archive tier"),
  };

  [Test]
  public async Task EverySqlFunctionThatReadsAStoredBody_IsClassified_AndEveryExportReadsTheWireFormAsync() {
    var found = _bodyReadingFunctions(_migrations());

    var unclassified = found.Keys.Where(f => !_functions.ContainsKey(f)).Order(StringComparer.Ordinal).ToList();
    var stale = _functions.Keys.Where(f => !found.ContainsKey(f)).Order(StringComparer.Ordinal).ToList();
    var unwired = found.Where(f => _functions.TryGetValue(f.Key, out var c) && c.Use == Use.Export && f.Key != WIRE_FORM
        && !f.Value.Contains(WIRE_FORM, StringComparison.Ordinal))
      .Select(f => f.Key).ToList();

    await Assert.That(unclassified).IsEmpty()
      .Because("each reads a stored body: classify it, and send any body that leaves through the wire form: " + string.Join(", ", unclassified));
    await Assert.That(stale).IsEmpty().Because("these no longer read a stored body; remove them: " + string.Join(", ", stale));
    await Assert.That(unwired).IsEmpty()
      .Because("these send a stored body out without the wire form, so a packed vector would leave packed: " + string.Join(", ", unwired));
  }

  [Test]
  public async Task EveryDriverSourceThatNamesAStoredBodyTable_IsClassified_AndEveryExportReadsTheWireFormAsync() {
    var root = _repositoryRoot();
    var found = _driverSources(root);

    var unclassified = found.Where(f => !_sources.TryGetValue(f.Key, out var c) || c.Count != f.Value)
      .Select(f => $"{f.Key} ({f.Value})").Order(StringComparer.Ordinal).ToList();
    var stale = _sources.Keys.Where(f => !found.ContainsKey(f)).Order(StringComparer.Ordinal).ToList();
    var unwired = _sources.Where(s => s.Value.Use == Use.Export
        && !File.ReadAllText(Path.Combine(root, s.Key)).Contains(WIRE_FORM, StringComparison.Ordinal))
      .Select(s => s.Key).ToList();

    await Assert.That(unclassified).IsEmpty()
      .Because("a source whose SQL names a stored-body table changed: classify each read again (the count is locked): "
        + string.Join(", ", unclassified));
    await Assert.That(stale).IsEmpty().Because("these no longer name a stored-body table; remove them: " + string.Join(", ", stale));
    await Assert.That(unwired).IsEmpty().Because("these export without the wire form: " + string.Join(", ", unwired));
  }

  [Test]
  public async Task TheTransportsPublishSeam_ReadsTheOutbox_NeverAStoredBodyAsync() {
    var lastWord = MigrationFunctionBodies.LastWord(_migrations());
    string[] seam = ["fetch_outbox_batch", "fetch_outbox_batch_in_stream_order", "wh_continue_outbox_streams",
      "wh_continue_outbox_streams_in_emission_order"];

    foreach (var function in seam) {
      await Assert.That(lastWord.ContainsKey(function)).IsTrue().Because($"precondition: {function} is defined");
      var body = lastWord[function].NormalizedBody;
      await Assert.That(body).Contains("wh_outbox");
      await Assert.That(NamesABodyTable().IsMatch(LineComment().Replace(body, ""))).IsFalse()
        .Because($"{function} hands the transport the outbox's wire form, never the stored body");
    }
  }

  [Test]
  public async Task TheGuard_FindsAFunctionByWhatItReads_AndIgnoresComments_AndOtherColumnsAsync() {
    var found = _bodyReadingFunctions([("001_a.sql", """
      CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_export(p UUID) RETURNS JSONB AS $$
        SELECT eb.event_data FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = p;
      $$ LANGUAGE sql;
      CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_metadata(p UUID) RETURNS JSONB AS $$
        SELECT eb.metadata FROM __SCHEMA__.wh_event_body eb WHERE eb.event_id = p;
      $$ LANGUAGE sql;
      CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_comment(p UUID) RETURNS JSONB AS $$
        -- SELECT event_data FROM wh_event_body
        SELECT o.event_data FROM __SCHEMA__.wh_outbox o WHERE o.message_id = p;
      $$ LANGUAGE sql;
      """)]);

    await Assert.That(found.Keys.ToList()).IsEquivalentTo(["wh_probe_export"]);
  }

  // ===== Helpers =====

  private static Dictionary<string, string> _bodyReadingFunctions(IEnumerable<(string Name, string Sql)> migrations) =>
    MigrationFunctionBodies.LastWord(migrations.Select(m => (m.Name, LineComment().Replace(m.Sql, ""))))
      .Where(f => NamesABodyTable().IsMatch(f.Value.NormalizedBody) && f.Value.NormalizedBody.Contains("event_data", StringComparison.Ordinal))
      .ToDictionary(f => f.Key, f => f.Value.NormalizedBody, StringComparer.Ordinal);

  /// <summary>Each driver source (not a migration, not a comment line) whose text names a stored-body table, with the count.</summary>
  private static Dictionary<string, int> _driverSources(string root) {
    var found = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) {
        continue;
      }
      var count = File.ReadLines(file)
        .Where(static l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith('*'))
        .Sum(l => NamesABodyTable().Count(l));
      if (count > 0) {
        found[relative] = count;
      }
    }
    return found;
  }

  // The up migrations only: a .down.sql restores an earlier word and is never the latest definition.
  private static IEnumerable<(string Name, string Sql)> _migrations() =>
    Directory.EnumerateFiles(Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations"), "*.sql")
      .Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal))
      .Order(StringComparer.Ordinal)
      .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

  /// <summary>The repository root, found by walking up to the directory holding <c>src</c> and <c>tests</c>.</summary>
  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null
        && !(Directory.Exists(Path.Combine(dir.FullName, "src"))
          && Directory.Exists(Path.Combine(dir.FullName, "tests")))) {
      dir = dir.Parent;
    }
    return dir?.FullName
      ?? throw new InvalidOperationException("could not find the repository root, so the guard cannot scan what it guards");
  }
}
