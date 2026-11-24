// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2551: nothing orders a stream by id. An event or message id is time-ordered and never a stream position: a stream is
/// versioned in emission order when stored, in its origin's order when received, and read by version. Every store,
/// receive and replay function's latest definition is read, and none may order a stream by <c>message_id</c> or
/// <c>event_id</c>, break a commit-sequence tie by one, or compare two ids as positions.
/// </summary>
/// <remarks>
/// <para>
/// The functions read are found by what they do, so a new one is covered without being listed: every function that
/// inserts into <c>wh_event_store</c>, every refusal helper the store and receive chains call (<c>_wh_*_refusals</c>,
/// <c>_wh_refuse_*</c>, <c>_wh_raise_*</c>), and the store, dispatch and replay reads named in <see cref="_roster"/>.
/// </para>
/// <para>
/// An id that decides nothing about position (a lock order, an index-reachability probe) or the documented fallback for
/// an event that carries no position at all is allowed only by its exact text, with its reason, in
/// <see cref="_allowed"/>; an allowance that no longer matches fails, so the list cannot go stale.
/// </para>
/// </remarks>
/// <docs>fundamentals/event-store/version-ordering</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/580_StreamsAreVersionedInEmissionOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/581_ReceivedStreamsAreVersionedInReceiveOrder.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/582_StreamReadsFollowStreamOrder.sql</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public partial class StreamOrderNeverByIdGuardTests {
  // ORDER BY whose first key, after any leading stream id, is an id: a stream ordered by id.
  [GeneratedRegex(@"ORDER BY\s+(?:[\w.]*stream_id(?:\s+(?:ASC|DESC))?(?:\s+NULLS\s+(?:FIRST|LAST))?\s*,\s*)*[\w.]*\b(?:message_id|event_id)\b",
    RegexOptions.IgnoreCase)]
  private static partial Regex OrderedById();

  // A commit-sequence order whose tie (the unstamped rows) is broken by an id.
  [GeneratedRegex(@"commit_sequence(?:\s+(?:ASC|DESC))?(?:\s+NULLS\s+(?:FIRST|LAST))?\s*,\s*[\w.]*\b(?:event_id|message_id)\b",
    RegexOptions.IgnoreCase)]
  private static partial Regex TieBrokenById();

  // Two ids compared as positions.
  [GeneratedRegex(@"[\w.]*\b(?:event_id|message_id)\s*[<>]=?\s*[\w.]*(?:event_id|message_id)\b", RegexOptions.IgnoreCase)]
  private static partial Regex IdsComparedAsPositions();

  [GeneratedRegex(@"INSERT INTO __SCHEMA__\.wh_event_store\s*\(", RegexOptions.IgnoreCase)]
  private static partial Regex StoresEvents();

  [GeneratedRegex(@"^_wh_(?:\w+_refusals|refuse_\w+|raise_\w+)$")]
  private static partial Regex RefusalHelper();

  [GeneratedRegex(@"--[^\r\n]*")]
  private static partial Regex LineComment();

  /// <summary>The store, dispatch and replay reads that order a stream's rows without inserting them.</summary>
  private static readonly HashSet<string> _roster = new(StringComparer.Ordinal) {
    "store_outbox_messages", "store_inbox_messages", "fetch_inbox_batch", "fetch_inbox_batch_in_stream_order",
    "get_stream_events", "get_stream_events_in_stream_order", "wh_collective_sink_queue",
    "fetch_events_by_ids", "fetch_pending_perspective_events", "claim_and_fetch_pending_perspective_events",
    "complete_perspective_cursor_work", "wh_received_stream_order_window", "wh_resequence_stream",
  };

  /// <summary>
  /// Exact allowed texts, per function, with why each is the one documented fallback: an event that carries no origin
  /// position, placed by its id the way the replay places it (#2379; ReplayEventOrder in C#). Nothing else is allowed.
  /// </summary>
  private static readonly (string Function, string Text, string Reason)[] _allowed = [
    ("_emit_event_store_chain_for_inbox", "p.message_id > k.message_id",
      "the replay's rule for an event that carries no origin position (#2379): it goes before the first of its "
      + "origin's positioned events whose id sorts after its own; nothing positional exists for it"),
    ("_emit_event_store_chain_for_inbox", "se.event_id < applied.event_id",
      "the same fallback for the straggler: when either event carries no origin position, the replay places it by id"),
  ];

  [Test]
  public async Task NoStoreReceiveOrReplayFunction_OrdersAStreamById_InItsLatestDefinitionAsync() {
    var guarded = _guardedFunctions(_migrations(_migrationsDirectory()));

    foreach (var expected in new[] {
        "_emit_event_store_chain", "_emit_event_store_chain_for_inbox", "store_outbox_messages", "_wh_sealed_stream_refusals",
        "_wh_mixed_stream_refusals", "_wh_archived_stream_refusals", "get_stream_events", "fetch_inbox_batch" }) {
      await Assert.That(guarded.Keys).Contains(expected)
        .Because($"precondition: {expected} is among the functions read");
    }

    var findings = _findings(guarded);
    var offenders = findings.Where(f => !_isAllowed(f)).Select(f => $"{f.Function}: {f.Text}").ToList();
    var staleAllowances = _allowed
      .Where(a => !findings.Any(f => f.Function == a.Function && f.Text.Contains(a.Text, StringComparison.Ordinal)))
      .Select(a => $"{a.Function}: {a.Text}").ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("these order a stream by id; order by emission position, the origin's position or the version: "
        + string.Join(" | ", offenders));
    await Assert.That(staleAllowances).IsEmpty()
      .Because("these allowances match nothing any more; remove them: " + string.Join(" | ", staleAllowances));
  }

  [Test]
  public async Task TheGuard_FlagsEachWayOfOrderingAStreamById_AndPassesPositionalOrdersAsync() {
    var guarded = _guardedFunctions([
      ("001_a.sql", """
        CREATE OR REPLACE FUNCTION __SCHEMA__._wh_probe_chain(p_ids UUID[]) RETURNS void AS $$
        BEGIN
          -- ORDER BY o.message_id in a comment is not code
          INSERT INTO __SCHEMA__.wh_event_store (event_id, version)
          SELECT o.message_id, ROW_NUMBER() OVER (PARTITION BY o.stream_id ORDER BY o.message_id) FROM __SCHEMA__.wh_outbox o;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.get_stream_events(p_stream UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_event_store es ORDER BY es.stream_id, es.commit_sequence ASC NULLS LAST, es.event_id;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__._wh_probe_refusals(p_ids UUID[]) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM unnest(p_ids) k, unnest(p_ids) e WHERE k.event_id > e.event_id;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_resequence_stream(p_stream UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_event_store es WHERE es.stream_id = p_stream
          ORDER BY es.stream_id, es.version, es.event_id;
          INSERT INTO __SCHEMA__.wh_event_store (event_id, version)
          SELECT b.message_id, ROW_NUMBER() OVER (PARTITION BY b.stream_id ORDER BY b.position) FROM batch b;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_unrelated(p_after UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_dead_letters d ORDER BY d.message_id;
        END;
        $$ LANGUAGE plpgsql;
        """),
    ]);

    await Assert.That(guarded.Keys.Order(StringComparer.Ordinal).ToList())
      .IsEquivalentTo(["_wh_probe_chain", "_wh_probe_refusals", "get_stream_events", "wh_resequence_stream"])
      .Because("a function that stores events, a refusal helper and a rostered read are guarded; an unrelated one is not");
    var flagged = _findings(guarded).Select(f => f.Function).Distinct().Order(StringComparer.Ordinal).ToList();
    await Assert.That(flagged).IsEquivalentTo(["_wh_probe_chain", "_wh_probe_refusals", "get_stream_events"])
      .Because("a window ordered by id, a commit-sequence tie broken by id and two ids compared as positions are each "
        + "flagged; an order by version with an id after it, an order by emission position, and a comment are not");
  }

  private sealed record Finding(string Function, string Text);

  private static bool _isAllowed(Finding finding) =>
    _allowed.Any(a => a.Function == finding.Function && finding.Text.Contains(a.Text, StringComparison.Ordinal));

  private static List<Finding> _findings(Dictionary<string, string> guarded) {
    var findings = new List<Finding>();
    foreach (var (function, body) in guarded) {
      foreach (var pattern in new[] { OrderedById(), TieBrokenById(), IdsComparedAsPositions() }) {
        foreach (Match match in pattern.Matches(body)) {
          var start = Math.Max(0, match.Index - 40);
          var end = Math.Min(body.Length, match.Index + match.Length + 20);
          findings.Add(new Finding(function, body[start..end]));
        }
      }
    }
    return findings;
  }

  private static Dictionary<string, string> _guardedFunctions(IEnumerable<(string Name, string Sql)> migrations) =>
    MigrationFunctionBodies.LastWord(migrations.Select(m => (m.Name, LineComment().Replace(m.Sql, ""))))
      .Where(f => _roster.Contains(f.Key) || RefusalHelper().IsMatch(f.Key) || StoresEvents().IsMatch(f.Value.NormalizedBody))
      .ToDictionary(f => f.Key, f => f.Value.NormalizedBody, StringComparer.Ordinal);

  // The up migrations only: a .down.sql restores an earlier word and is never the latest definition.
  private static IEnumerable<(string Name, string Sql)> _migrations(string directory) =>
    Directory.EnumerateFiles(directory, "*.sql")
      .Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal))
      .Order(StringComparer.Ordinal)
      .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

  private static string _migrationsDirectory() =>
    Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations");

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
