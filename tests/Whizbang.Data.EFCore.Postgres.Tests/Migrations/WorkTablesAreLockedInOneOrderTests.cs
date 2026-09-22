using System.Text;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Every SQL function that row-locks more than one work table must take them in one order.
/// </summary>
/// <remarks>
/// <para>
/// Two transactions that lock the same two tables in opposite orders deadlock the moment their row
/// sets overlap, and nothing about the code says they will. The order is spread across a hundred and
/// sixty migrations, a function's definition is replaced by whichever migration defines it last, and
/// the server reports only the victim: a deadlock on a <c>wh_outbox</c> row lock names neither the
/// pair of tables nor the pair of functions that produced it. Two inversions existed when this guard
/// was written and one of them was a function inverting against itself.
/// </para>
/// <para>
/// The order is <see cref="CANONICAL"/>: work flows in at the outbox, crosses to the inbox, acquires
/// its state row, becomes perspective work, and touches the stream lease last. The tables are locked
/// in the order work moves through them, which is the order most functions already used, so the rule
/// is a description of the design rather than a constraint imposed on it.
/// </para>
/// <para>
/// <strong>The orders are derived from the shipped migration text, never listed here.</strong> A
/// hardcoded table of who locks what is a gate that passes after the thing it guards has moved on.
/// Reading <see cref="PostgresMigrationProvider"/> also means the guard sees what actually ships as
/// an embedded resource, not what happens to be on disk.
/// </para>
/// <para>
/// Two details of the derivation decide whether it is right, and a narrower scan gets the answer
/// wrong in both directions. <c>UPDATE</c> and <c>DELETE</c> are not the only statements that take a
/// row lock: <c>INSERT ... ON CONFLICT</c> locks the row it conflicts with, and
/// <c>SELECT ... FOR UPDATE</c> locks unless it <c>SKIP LOCKED</c>s or <c>NOWAIT</c>s, neither of
/// which ever waits and so neither of which can be half of a deadlock. And plpgsql branches are
/// mutually exclusive: flattening <c>renew_leases</c>' <c>CASE</c> over the work category invents an
/// order that no single call takes and reports two inversions that do not exist.
/// <see cref="MutuallyExclusiveBranchesAreNotComparedAsync"/> and
/// <see cref="AnInsertOnConflictCountsAsARowLockAsync"/> hold both of those in place, and
/// <see cref="TheDerivationFindsTheMultiTableFunctionsAsync"/> refuses to let an empty parse make
/// the rule pass vacuously.
/// </para>
/// </remarks>
[Category("Shard3")]
public partial class WorkTablesAreLockedInOneOrderTests {
  /// <summary>
  /// The schema the migration text is rendered against, which the patterns below are built from.
  /// </summary>
  /// <remarks>
  /// <see cref="PostgresMigrationProvider.GetMigrations"/> does not hand back the file on disk: it
  /// substitutes the schema placeholder and applies the shared constants first. Patterns written
  /// against the placeholder therefore match nothing, every function parses as empty, and the rule
  /// passes on a set with no members -- a permanently green test that proves nothing, which is what
  /// <see cref="TheDerivationFindsTheMultiTableFunctionsAsync"/> caught here. Naming the schema
  /// explicitly keeps the guard reading the shipped resource while the patterns stay exact.
  /// </remarks>
  private const string SCHEMA = "wh_lock_order_check";

  /// <summary>The order the work tables are locked in, which is the order work moves through them.</summary>
  private static readonly string[] CANONICAL = [
    "wh_outbox",
    "wh_inbox",
    "wh_inbox_state",
    "wh_perspective_events",
    "wh_active_streams",
  ];

  /// <summary>
  /// Every function that can lock two of <see cref="CANONICAL"/>, and in which orders it can.
  /// </summary>
  /// <remarks>
  /// Keyed by function name; the value holds every ordered pair <c>(a, b)</c> for which some single
  /// execution of that function locks <c>a</c> and then later locks <c>b</c>. A function that can
  /// reach both <c>(a, b)</c> and <c>(b, a)</c> deadlocks against itself and fails the rule on its
  /// own, which is how <c>perform_maintenance</c> was found.
  /// </remarks>
  private static Dictionary<string, HashSet<(string First, string Then)>> _reachableOrders() {
    var byFunction = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);

    // Migrations arrive in execution order, so a later definition of a function simply overwrites
    // an earlier one -- exactly what replay leaves behind on the server.
    foreach (var script in new PostgresMigrationProvider(
        typeof(PostgresMigrationProvider).Assembly, SCHEMA).GetMigrations()) {
      foreach (var (name, body) in _functionBodies(script.Sql)) {
        var statements = _lockingStatements(body);
        var pairs = new HashSet<(string, string)>();
        for (var i = 0; i < statements.Count; i++) {
          // Within one statement, text order is lock order.
          for (var a = 0; a < statements[i].Tables.Count; a++) {
            for (var b = a + 1; b < statements[i].Tables.Count; b++) {
              pairs.Add((statements[i].Tables[a], statements[i].Tables[b]));
            }
          }
          for (var j = i + 1; j < statements.Count; j++) {
            if (!_canBothRun(statements[i].Branch, statements[j].Branch)) {
              continue;
            }
            foreach (var first in statements[i].Tables) {
              foreach (var then in statements[j].Tables) {
                if (!string.Equals(first, then, StringComparison.Ordinal)) {
                  pairs.Add((first, then));
                }
              }
            }
          }
        }
        byFunction[name] = pairs;
      }
    }

    // Keep only the work tables this rule is about.
    var interesting = new Dictionary<string, HashSet<(string, string)>>(StringComparer.Ordinal);
    foreach (var (name, pairs) in byFunction) {
      var kept = pairs.Where(p => CANONICAL.Contains(p.Item1) && CANONICAL.Contains(p.Item2))
                      .ToHashSet();
      if (kept.Count > 0) {
        interesting[name] = kept;
      }
    }
    return interesting;
  }

  /// <summary>A statement that takes a row lock, with the branch arms that enclose it.</summary>
  private sealed record LockingStatement(IReadOnlyList<(int Construct, int Arm)> Branch, List<string> Tables);

  /// <summary>
  /// Whether one execution can reach both statements: they must agree on every branch they share.
  /// </summary>
  private static bool _canBothRun(
      IReadOnlyList<(int Construct, int Arm)> left,
      IReadOnlyList<(int Construct, int Arm)> right) {
    foreach (var (construct, arm) in left) {
      foreach (var (otherConstruct, otherArm) in right) {
        if (otherConstruct == construct && otherArm != arm) {
          return false;
        }
      }
    }
    return true;
  }

  /// <summary>Each function a migration defines, paired with the text of its definition.</summary>
  /// <remarks>
  /// A migration defines several functions one after another, so each definition runs to the start of
  /// the next one or to the end of the file. Only the name and the span are needed: the statements
  /// inside are found by <see cref="_lockingStatements"/>.
  /// </remarks>
  private static List<(string Name, string Body)> _functionBodies(string sql) {
    var bodies = new List<(string, string)>();
    var starts = FunctionStart().Matches(sql).ToList();
    for (var i = 0; i < starts.Count; i++) {
      var from = starts[i].Index;
      var to = i + 1 < starts.Count ? starts[i + 1].Index : sql.Length;
      bodies.Add((starts[i].Groups[1].Value, _blankCommentsAndLiterals(sql[from..to])));
    }
    return bodies;
  }

  /// <summary>
  /// Replaces line comments and single-quoted literals with spaces, so a keyword inside one of them
  /// is never read as code. Newlines survive, which keeps the text aligned with the file.
  /// </summary>
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
        var end = i + 1;
        while (end < text.Length) {
          if (text[end] == '\'') {
            if (end + 1 < text.Length && text[end + 1] == '\'') {
              end += 2;
              continue;
            }
            break;
          }
          end++;
        }
        end = Math.Min(end + 1, text.Length);
        for (var k = i; k < end; k++) {
          result.Append(text[k] == '\n' ? '\n' : ' ');
        }
        i = end;
      } else {
        result.Append(text[i]);
        i++;
      }
    }
    return result.ToString();
  }

  [GeneratedRegex(@"\G\b(CASE|WHEN|THEN|IF|ELSIF|ELSEIF|ELSE|END[ \t\r\n]+CASE|END[ \t\r\n]+IF|END)\b",
    RegexOptions.IgnoreCase)]
  private static partial Regex ControlFlow();

  /// <summary>A statement already opened by <c>EXIT</c> or <c>CONTINUE</c>, whose WHEN is a modifier.</summary>
  [GeneratedRegex(@"^\s*(EXIT|CONTINUE)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
  private static partial Regex LoopModifier();

  [GeneratedRegex(@"^[ \t]*CREATE OR REPLACE FUNCTION\s+" + SCHEMA + @"\.(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
  private static partial Regex FunctionStart();

  [GeneratedRegex(@"\s+")]
  private static partial Regex Whitespace();

  [GeneratedRegex(@"\bUPDATE\s+" + SCHEMA + @"\.(wh_\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex UpdateTarget();

  [GeneratedRegex(@"\bDELETE\s+FROM\s+" + SCHEMA + @"\.(wh_\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex DeleteTarget();

  [GeneratedRegex(@"\bINSERT\s+INTO\s+" + SCHEMA + @"\.(wh_\w+)(.*?)(?=\bINSERT\s+INTO\b|$)", RegexOptions.IgnoreCase)]
  private static partial Regex InsertTarget();

  [GeneratedRegex(@"\bON\s+CONFLICT\b", RegexOptions.IgnoreCase)]
  private static partial Regex OnConflict();

  [GeneratedRegex(@"\bFOR\s+UPDATE\b(?!\s+(?:OF\s+[\w,\s]+?\s+)?(?:SKIP\s+LOCKED|NOWAIT))", RegexOptions.IgnoreCase)]
  private static partial Regex WaitingForUpdate();

  [GeneratedRegex(@"\b(?:FROM|JOIN)\s+" + SCHEMA + @"\.(wh_\w+)", RegexOptions.IgnoreCase)]
  private static partial Regex FromOrJoinTarget();

  /// <summary>
  /// Walks a function body and returns every statement that takes a row lock, tagged with the
  /// branch arms enclosing it.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Statements are cut at semicolons outside parentheses. A <c>CASE</c> or <c>IF</c> opens a
  /// construct and each <c>WHEN</c>/<c>ELSIF</c>/<c>ELSE</c> advances its arm, so two statements in
  /// different arms of the same construct are never compared.
  /// </para>
  /// <para>
  /// A <c>CASE</c> <em>expression</em> is not a branch, and telling the two apart is not optional.
  /// A <c>CASE</c> statement closes with <c>END CASE</c>; an expression closes with a bare <c>END</c>
  /// that closes nothing here, so treating every <c>CASE</c> as a branch pushes a construct that is
  /// never popped and leaves every later statement carrying a stale arm.
  /// <c>perform_maintenance</c> is full of them: each task reports through
  /// <c>RETURN QUERY SELECT ..., CASE WHEN v_debug_mode THEN ... ELSE 'ok' END::TEXT</c>, and those
  /// sit at parenthesis depth zero, so depth cannot be the discriminator. Position can: a statement
  /// <c>CASE</c> begins a statement, so nothing has accumulated in the buffer when it is reached,
  /// while an expression <c>CASE</c> always follows something.
  /// </remarks>
  private static List<LockingStatement> _lockingStatements(string body) {
    var found = new List<LockingStatement>();
    var stack = new List<(int Construct, int Arm)>();
    var nextConstruct = 0;
    var depth = 0;
    var expressionCase = 0;
    var statement = new StringBuilder();
    var i = 0;

    void Flush() {
      var tables = _tablesLockedBy(statement.ToString());
      if (tables.Count > 0) {
        found.Add(new LockingStatement([.. stack], tables));
      }
      statement.Clear();
    }

    while (i < body.Length) {
      var c = body[i];
      if (c == '(') {
        depth++;
        statement.Append(c);
        i++;
        continue;
      }
      if (c == ')') {
        depth--;
        statement.Append(c);
        i++;
        continue;
      }
      if (c == ';' && depth <= 0) {
        Flush();
        i++;
        continue;
      }
      if (depth <= 0) {
        var keyword = ControlFlow().Match(body, i);
        // \G anchors the match at i; the index check says so in code rather than relying on it.
        if (keyword.Success && keyword.Index == i) {
          var word = Whitespace().Replace(keyword.Groups[1].Value.ToUpperInvariant(), " ");

          // Inside a CASE expression nothing is a branch: only its own nesting is tracked, and the
          // text is kept because the statement it belongs to is still being accumulated.
          if (expressionCase > 0) {
            if (word == "CASE") {
              expressionCase++;
            } else if (word == "END") {
              expressionCase--;
            }
            statement.Append(keyword.Groups[1].Value);
            i = keyword.Index + keyword.Length;
            continue;
          }
          // A CASE reached mid-statement is an expression, not a branch.
          if (word == "CASE" && statement.ToString().Trim().Length > 0) {
            expressionCase = 1;
            statement.Append(keyword.Groups[1].Value);
            i = keyword.Index + keyword.Length;
            continue;
          }
          // EXIT WHEN and CONTINUE WHEN are loop modifiers, not CASE arms. Reading one as an arm
          // splits a single arm in two, so statements on either side look mutually unreachable and
          // their pair is never derived -- the failure direction that HIDES an inversion rather
          // than inventing one. Sixteen of them exist in these migrations.
          if (word == "WHEN" && LoopModifier().IsMatch(statement.ToString())) {
            statement.Append(keyword.Groups[1].Value);
            i = keyword.Index + keyword.Length;
            continue;
          }

          Flush();
          switch (word) {
            case "CASE":
            case "IF":
              stack.Add((nextConstruct++, -1));
              break;
            case "WHEN":
            case "ELSIF":
            case "ELSEIF":
            case "ELSE":
              if (stack.Count > 0) {
                stack[^1] = (stack[^1].Construct, stack[^1].Arm + 1);
              }
              break;
            case "END CASE":
            case "END IF":
              if (stack.Count > 0) {
                stack.RemoveAt(stack.Count - 1);
              }
              break;
            default:
              // A bare END closes the function body or a nested BEGIN block; neither is an arm, and
              // a CASE expression's END was consumed above.
              break;
          }
          i = keyword.Index + keyword.Length;
          continue;
        }
      }
      statement.Append(c);
      i++;
    }
    Flush();
    return found;
  }

  /// <summary>Every table a single statement takes a row lock on, in the order it names them.</summary>
  private static List<string> _tablesLockedBy(string statement) {
    var oneLine = Whitespace().Replace(statement, " ");
    var tables = new List<string>();

    void Add(string table) {
      if (!tables.Contains(table, StringComparer.Ordinal)) {
        tables.Add(table);
      }
    }

    foreach (var m in UpdateTarget().Matches(oneLine).ToList()) {
      Add(m.Groups[1].Value);
    }
    foreach (var m in DeleteTarget().Matches(oneLine).ToList()) {
      Add(m.Groups[1].Value);
    }
    // INSERT ... ON CONFLICT locks the conflicting row. The ON CONFLICT can sit far below the
    // INSERT, so each INSERT is paired with the text up to the next one.
    foreach (var m in InsertTarget().Matches(oneLine).Where(m => OnConflict().IsMatch(m.Groups[2].Value))) {
      Add(m.Groups[1].Value);
    }
    // FOR UPDATE waits, and therefore deadlocks -- unless it SKIP LOCKEDs or NOWAITs, which do not.
    if (WaitingForUpdate().IsMatch(oneLine)) {
      foreach (var m in FromOrJoinTarget().Matches(oneLine).ToList()) {
        Add(m.Groups[1].Value);
      }
    }
    return tables;
  }

  /// <summary>
  /// No function locks two work tables in an order that disagrees with <see cref="CANONICAL"/>.
  /// </summary>
  /// <remarks>
  /// Asserting against a total order rather than merely "no two functions disagree" is deliberate. A
  /// pairwise check is satisfied by a cycle across three tables, which deadlocks exactly as readily,
  /// and it gives the author of a new function nothing to follow. The order below is the answer.
  /// </remarks>
  [Test]
  public async Task NoFunctionLocksTheWorkTablesOutOfCanonicalOrderAsync() {
    var rank = CANONICAL.Select((table, index) => (table, index))
                        .ToDictionary(x => x.table, x => x.index, StringComparer.Ordinal);

    var offenses = new List<string>();
    foreach (var (function, pairs) in _reachableOrders().OrderBy(x => x.Key, StringComparer.Ordinal)) {
      foreach (var (first, then) in pairs.OrderBy(p => p.First, StringComparer.Ordinal)
                                         .ThenBy(p => p.Then, StringComparer.Ordinal)) {
        if (rank[first] > rank[then]) {
          offenses.Add($"{function} can lock {first} before {then}");
        }
      }
    }

    await Assert.That(offenses).IsEmpty()
      .Because(
        "two transactions that take the same two tables in opposite orders deadlock as soon as "
        + "their row sets overlap, and the server names only the victim. Lock the work tables in "
        + "the order work moves through them: " + string.Join(" -> ", CANONICAL) + ". Offenses: "
        + string.Join("; ", offenses));
  }

  /// <summary>
  /// The derivation actually finds the functions, so the rule cannot pass on an empty parse.
  /// </summary>
  /// <remarks>
  /// Named rather than counted: a count drifts with every migration and gets bumped rather than
  /// investigated. These five are the functions whose lock order the rule exists to hold, and one of
  /// them is reached only through a <c>CASE</c>, so a parser that loses branches still lists it while
  /// a parser that loses functions does not.
  /// </remarks>
  [Test]
  public async Task TheDerivationFindsTheMultiTableFunctionsAsync() {
    var found = _reachableOrders();

    await Assert.That(found.Keys).Contains("perform_maintenance");
    await Assert.That(found.Keys).Contains("recompute_partition_numbers");
    await Assert.That(found.Keys).Contains("deregister_instance");
    await Assert.That(found.Keys).Contains("cleanup_stale_instances");
    await Assert.That(found.Keys).Contains("renew_leases");
    await Assert.That(found.Keys).Contains("store_inbox_messages");
  }

  /// <summary>
  /// Statements in different arms of one <c>CASE</c> are never treated as an order.
  /// </summary>
  /// <remarks>
  /// <c>renew_leases</c> is a <c>CASE</c> over the work category: one call renews outbox leases, or
  /// inbox leases, or perspective-event leases, never two of them. Flattening the arms invents the
  /// order wh_outbox then wh_inbox_state, which no call takes, and reports an inversion against
  /// <c>recompute_partition_numbers</c> that does not exist. A "simplified" parser that drops branch
  /// tracking fails here rather than quietly producing a phantom finding, which is the outcome that
  /// costs an afternoon.
  /// </remarks>
  [Test]
  public async Task MutuallyExclusiveBranchesAreNotComparedAsync() {
    var renewLeases = _reachableOrders()["renew_leases"];

    await Assert.That(renewLeases).Contains(("wh_outbox", "wh_active_streams"));
    await Assert.That(renewLeases).Contains(("wh_inbox_state", "wh_active_streams"));
    await Assert.That(renewLeases).Contains(("wh_perspective_events", "wh_active_streams"));

    await Assert.That(renewLeases).DoesNotContain(("wh_outbox", "wh_inbox_state"))
      .Because("the outbox and inbox arms of the CASE are mutually exclusive, so no call locks both");
    await Assert.That(renewLeases).DoesNotContain(("wh_inbox_state", "wh_perspective_events"))
      .Because("the inbox and perspective-event arms are mutually exclusive too");
  }

  /// <summary>
  /// An <c>INSERT ... ON CONFLICT</c> is counted, because it takes a row lock.
  /// </summary>
  /// <remarks>
  /// <c>store_inbox_messages</c> reaches <c>wh_active_streams</c> only through an upsert. A scan that
  /// looks for <c>UPDATE</c> and <c>DELETE</c> alone therefore misses it entirely, and with it every
  /// inversion that the claim path's upserts take part in.
  /// </remarks>
  [Test]
  public async Task AnInsertOnConflictCountsAsARowLockAsync() {
    var storeInboxMessages = _reachableOrders()["store_inbox_messages"];

    await Assert.That(storeInboxMessages).Contains(("wh_inbox", "wh_active_streams"))
      .Because("wh_active_streams is reached by INSERT ... ON CONFLICT, which locks the conflicting row");
  }
}
