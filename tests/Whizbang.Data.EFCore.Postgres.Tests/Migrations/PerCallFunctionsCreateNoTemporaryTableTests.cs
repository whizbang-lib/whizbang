// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// No framework function creates a temporary table, except the few named here that run at startup, once, or on an
/// operator's request (#2181).
/// </summary>
/// <remarks>
/// <para>
/// The framework does not own the session it runs in. A pooled connection is reset (<c>DISCARD ALL</c>) each time the
/// pool hands it out, which drops every temporary table, so a function that makes one makes it again on almost every
/// call: catalog rows written and deleted, a cache invalidation for every other session, fresh plans, and new
/// statement-statistics entries. #2179 found it in the bundle answer and #2181 in the three redelivery reconciles.
/// Each was found by measuring a deployed service, one at a time; this guard finds the next one when it is written.
/// </para>
/// <para>
/// The definitions are read from <see cref="PostgresMigrationProvider"/> (the embedded resources that ship), and each
/// function is judged by its last definition only, since that is the one a database runs. Comments and string
/// literals are blanked first, so a comment that explains the rule is not read as a breach of it.
/// <see cref="TheDerivationFindsTheExemptFunctionsAsync"/> refuses to let an empty parse pass.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations</code-under-test>
/// <docs>resilience/stream-integrity#per-bundle-work-table</docs>
[Category("Migrations")]
[Category("Shard4")]
public partial class PerCallFunctionsCreateNoTemporaryTableTests {
  private const string SCHEMA = "wh_temp_table_check";

  /// <summary>The functions that may create one, each with the reason a per-call cost does not arise.</summary>
  private static readonly Dictionary<string, string> _exempt = new(StringComparer.Ordinal) {
    ["register_message_associations"] = "runs once per startup, from the associations step",
    ["wh_normalize_aggregate_type_batch"] = "a one-time data migration (063), gated on its format version",
    ["wh_remove_rewind_lifecycle_events_batch"] = "a one-time data migration (210) that works through its batches once",
    ["wh_refile_events"] = "an operator's refile, run on request",
  };

  [Test]
  public async Task NoFunctionCreatesATemporaryTable_OutsideTheExemptOnesAsync() {
    var offenders = _lastDefinitions()
      .Where(f => TemporaryTable().IsMatch(f.Value) && !_exempt.ContainsKey(f.Key))
      .Select(f => f.Key)
      .Order(StringComparer.Ordinal)
      .ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("a pooled connection is reset before each use, so a temporary table is created again on almost every call; "
        + "hold the working set in arrays, a CTE or one statement");
  }

  [Test]
  public async Task TheDerivationFindsTheExemptFunctionsAsync() {
    var definitions = _lastDefinitions();
    var found = _exempt.Keys.Where(name => definitions.TryGetValue(name, out var body) && TemporaryTable().IsMatch(body)).ToList();

    await Assert.That(found).IsEquivalentTo(_exempt.Keys)
      .Because("each exemption must still be needed, and a parse that finds nothing would let the rule pass vacuously");
  }

  /// <summary>Each function's last definition, comments and string literals blanked.</summary>
  private static Dictionary<string, string> _lastDefinitions() {
    var last = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var migration in new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, SCHEMA).GetMigrations()) {
      var starts = FunctionStart().Matches(migration.Sql).ToList();
      for (var i = 0; i < starts.Count; i++) {
        var from = starts[i].Index;
        var to = i + 1 < starts.Count ? starts[i + 1].Index : migration.Sql.Length;
        last[starts[i].Groups[1].Value] = _blankCommentsAndLiterals(migration.Sql[from..to]);
      }
    }
    return last;
  }

  private static string _blankCommentsAndLiterals(string text) {
    var result = new StringBuilder(text.Length);
    var i = 0;
    while (i < text.Length) {
      if (i + 1 < text.Length && text[i] == '-' && text[i + 1] == '-') {
        var end = text.IndexOf('\n', i);
        end = end < 0 ? text.Length : end;
        result.Append(' ', end - i);
        i = end;
      } else if (text[i] == '\'') {
        var end = text.IndexOf('\'', i + 1);
        while (end >= 0 && end + 1 < text.Length && text[end + 1] == '\'') {
          end = text.IndexOf('\'', end + 2);
        }
        end = end < 0 ? text.Length : end + 1;
        result.Append(' ', end - i);
        i = end;
      } else {
        result.Append(text[i]);
        i++;
      }
    }
    return result.ToString();
  }

  [GeneratedRegex(@"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+wh_temp_table_check\.([a-z_0-9]+)\s*\(", RegexOptions.IgnoreCase, 5000)]
  private static partial Regex FunctionStart();

  [GeneratedRegex(@"\bCREATE\s+(?:LOCAL\s+|GLOBAL\s+)?TEMP(?:ORARY)?\s+TABLE\b", RegexOptions.IgnoreCase, 5000)]
  private static partial Regex TemporaryTable();
}
