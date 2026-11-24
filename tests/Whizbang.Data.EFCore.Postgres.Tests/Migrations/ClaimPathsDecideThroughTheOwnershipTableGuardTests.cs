// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2554 and #2555: with the service's switch on, every claim path decides through the one predicate,
/// <c>wh_i_own</c> (or its set form for a window of rows, <c>wh_partitions_i_own</c>, the same rule over the same table),
/// and nothing ranks instances itself. Each claim function's latest definition is read, and the guard fails when one
/// calls neither, or ranks anywhere but in the legacy branch the switch leaves:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a partition test against the rank is allowed only as the legacy arm of the one sanctioned form,
/// <c>CASE WHEN v_map THEN (the map's answer) ELSE wh_partition_in_share(p, rank, count, owners) END</c>, the
/// published map's answer with the switch off (#2558); a bare modulo against the rank is an offense anywhere;</item>
/// <item>ranking the live instances (<c>ROW_NUMBER() OVER (ORDER BY ... instance_id)</c>) is allowed only in a block
/// opened by <c>IF NOT v_map AND ... THEN</c>.</item>
/// </list>
/// Comments are removed before the check, so a comment that describes the rank never satisfies or trips it. The wake-up
/// is held to the map too: its latest definition must route by <c>wh_partition_ownership</c> in map mode.
/// </remarks>
/// <docs>fundamentals/work-coordinator/partition-assignment#ownership-by-map</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/566_ClaimsDecideThroughTheOwnershipTable.sql</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public partial class ClaimPathsDecideThroughTheOwnershipTableGuardTests {
  /// <summary>Every function that leases work to an instance, or admits it to a stream's events.</summary>
  private static readonly string[] _claimFunctions = [
    "claim_work",
    "claim_orphaned_outbox",
    "claim_orphaned_inbox",
    "claim_orphaned_perspective_events",
    "wh_claim_multi_stream_work",
    "get_stream_events",
    "wh_continue_outbox_streams",
    "wh_claim_due_schedules",
  ];

  [GeneratedRegex(@"--[^\n]*")]
  private static partial Regex LineComment();

  // The map arm is anything without a CASE or an ELSE of its own; the legacy arm is the map-aware rank test alone.
  [GeneratedRegex(@"CASE WHEN v_map THEN (?:(?!CASE |ELSE ).)*? ELSE __SCHEMA__\.wh_partition_in_share\([\w.]+, \w+, \w+, \w+\) END")]
  private static partial Regex SanctionedRankTest();

  [GeneratedRegex(@"__SCHEMA__\.(wh_i_own|wh_partitions_i_own)\(")]
  private static partial Regex ThePredicate();

  [GeneratedRegex(@"%\s*\(?\s*(p_active_instance_count|v_count|v_active_count|active_instance_count)\b")]
  private static partial Regex RankModulo();

  [GeneratedRegex(@"ROW_NUMBER\(\) OVER \(ORDER BY [\w.]*instance_id\)")]
  private static partial Regex InstanceRanking();

  [GeneratedRegex(@"IF NOT v_map AND [^;]*? THEN SELECT ranked\.instance_rank")]
  private static partial Regex SanctionedRanking();

  [Test]
  public async Task EveryClaimFunction_DecidesThroughWhIOwn_AndRanksOnlyInTheLegacyBranchAsync() {
    var latest = _latest(_migrations());

    var offenders = _offenders(latest);

    await Assert.That(offenders).IsEmpty()
      .Because("in map mode every claim decides through wh_i_own and nothing ranks itself: " + string.Join("; ", offenders));
  }

  [Test]
  public async Task TheWakeUp_RoutesByTheOwnershipTableInMapModeAsync() {
    var latest = _latest(_migrations());

    var wakeUp = latest["notify_instance_owners_with_payload"];

    await Assert.That(wakeUp).Contains("IF v_map THEN");
    await Assert.That(wakeUp).Contains("__SCHEMA__.wh_partition_ownership");
  }

  [Test]
  public async Task TheGuard_FlagsABypassAndAnUnguardedRank_AndPassesTheSanctionedFormsAsync() {
    var probe = _latest([
      ("001_a.sql", """
        CREATE OR REPLACE FUNCTION __SCHEMA__.claim_work(p UUID) RETURNS void AS $$
        DECLARE v_map BOOLEAN := TRUE;
        BEGIN
          -- the rank: ROW_NUMBER() OVER (ORDER BY live.instance_id) and x % v_count, in a comment only
          IF NOT v_map AND NOT v_assigned THEN
          SELECT ranked.instance_rank FROM (SELECT ROW_NUMBER() OVER (ORDER BY live.instance_id) FROM t live) ranked;
          END IF;
          PERFORM 1 WHERE CASE WHEN v_map THEN __SCHEMA__.wh_i_own(e.partition_number, p) IS NOT NULL ELSE __SCHEMA__.wh_partition_in_share(e.partition_number, v_rank, v_count, v_owners) END;
          PERFORM 1 WHERE CASE WHEN v_map THEN (e.partition_number % 10000) IN (SELECT m.p FROM __SCHEMA__.wh_partitions_i_own(p) m) ELSE __SCHEMA__.wh_partition_in_share(e.partition_number, v_rank, v_count, v_owners) END;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_outbox(p UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 WHERE (s.partition_number % p_active_instance_count) = p_instance_rank;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_claim_multi_stream_work(p UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 WHERE CASE WHEN v_map THEN __SCHEMA__.wh_i_own(rs.partition_number, p) IS NOT NULL ELSE (rs.partition_number % v_count) = v_rank END;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.claim_orphaned_inbox(p UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 WHERE __SCHEMA__.wh_i_own(i.partition_number, p) IS NOT NULL;
          SELECT ranked.instance_rank FROM (SELECT ROW_NUMBER() OVER (ORDER BY live.instance_id) FROM t live) ranked;
        END;
        $$ LANGUAGE plpgsql;
        """),
    ]);

    var offenders = _offenders(probe);

    await Assert.That(offenders.Any(o => o.StartsWith("claim_work", StringComparison.Ordinal))).IsFalse()
      .Because("the sanctioned forms and a comment are not offenses");
    await Assert.That(offenders).Contains("claim_orphaned_outbox: calls neither wh_i_own nor wh_partitions_i_own");
    await Assert.That(offenders).Contains("claim_orphaned_outbox: ranks by partition modulo outside the sanctioned form");
    await Assert.That(offenders).Contains("claim_orphaned_inbox: ranks the live instances outside IF NOT v_map");
    await Assert.That(offenders).Contains("claim_orphaned_perspective_events: has no definition");
    await Assert.That(offenders).Contains("wh_claim_multi_stream_work: ranks by partition modulo outside the sanctioned form")
      .Because("a legacy arm that ranks by the members' residue ignores the published map (#2558), whatever the switch says");
  }

  private static List<string> _offenders(IReadOnlyDictionary<string, string> latest) {
    var offenders = new List<string>();
    foreach (var name in _claimFunctions) {
      if (!latest.TryGetValue(name, out var body)) {
        offenders.Add($"{name}: has no definition");
        continue;
      }
      if (!ThePredicate().IsMatch(body)) {
        offenders.Add($"{name}: calls neither wh_i_own nor wh_partitions_i_own");
      }
      if (RankModulo().IsMatch(SanctionedRankTest().Replace(body, "/*sanctioned*/"))) {
        offenders.Add($"{name}: ranks by partition modulo outside the sanctioned form");
      }
      if (InstanceRanking().Count(body) > SanctionedRanking().Count(body)) {
        offenders.Add($"{name}: ranks the live instances outside IF NOT v_map");
      }
    }
    return offenders;
  }

  /// <summary>Each function's latest definition, comments removed before the whitespace is normalized.</summary>
  private static Dictionary<string, string> _latest(IEnumerable<(string Name, string Sql)> migrations) =>
    MigrationFunctionBodies.LastWord(migrations.Select(m => (m.Name, LineComment().Replace(m.Sql, ""))))
      .ToDictionary(f => f.Key, f => f.Value.NormalizedBody, StringComparer.Ordinal);

  private static IEnumerable<(string Name, string Sql)> _migrations() {
    var directory = Path.Combine(_repositoryRoot(), "src", "Whizbang.Data.Postgres", "Migrations");
    return Directory.EnumerateFiles(directory, "*.sql")
      .Where(f => !f.EndsWith(".down.sql", StringComparison.Ordinal))
      .Order(StringComparer.Ordinal)
      .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
  }

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
