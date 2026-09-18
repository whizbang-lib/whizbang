using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Migrations whose correctness depends on being one transaction must not carry a commit boundary.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SchemaCommandBoundary"/> applies a marker-free script as one command on one
/// connection, which PostgreSQL runs as a single implicit transaction. A marker splits the script
/// into separately committed pieces, which is exactly right for a data rewrite an index depends on,
/// and exactly wrong for a migration that moves data between tables and then drops the columns it
/// came from.
/// </para>
/// <para>
/// The hazard is not theoretical and it is not loud. Split at the wrong place, a migration can
/// commit a column drop while the functions that read those columns have not been replaced yet, or
/// commit a backfill that a later piece then fails to build on, leaving a half-migrated schema that
/// the next attempt starts from rather than retries cleanly. Neither shows up as a test failure
/// anywhere else, because every individual statement is valid.
/// </para>
/// <para>
/// The rule checked here is general, and general is what makes it useful: a script that takes an
/// explicit table lock or drops a column is relying on everything around it committing together,
/// whatever else it does. It catches the hazard by shape rather than by name, so a migration written
/// next year is covered without anyone remembering to add it.
/// </para>
/// <para>
/// A per-migration named guard is deliberately NOT added here. A list of names that a migration can
/// simply be absent from is a gate that passes when the thing it guards has gone, which is the
/// failure mode this suite keeps finding in other people's tests and should not introduce into its
/// own. The shape rule covers the same hazard without that hole.
/// </para>
/// </remarks>
[Category("Shard3")]
public partial class MigrationTransactionBoundaryGuardTests {
  /// <summary>
  /// The columns migration 162 drops from <c>wh_inbox</c>, moved to <c>wh_inbox_state</c>.
  /// </summary>
  private static readonly string[] MOVED_COLUMNS = [
    "instance_id", "lease_expiry", "attempts", "processed_at", "scheduled_for",
    "failure_reason", "error", "chain_emitted_at", "status", "partition_number",
  ];

  /// <summary>
  /// The functions the enumeration identified as touching <c>wh_inbox</c>'s moved columns, and so
  /// needing redirection to <c>wh_inbox_state</c>.
  /// </summary>
  private static readonly string[] ENUMERATED = [
    "_emit_event_store_chain_for_inbox", "claim_orphaned_inbox", "claim_work",
    "cleanup_completed_streams", "cleanup_stale_instances", "commit_handler_batch_bulk",
    "count_outstanding_work", "deregister_instance", "fetch_inbox_batch",
    "find_stuck_inbox_rows", "move_to_dead_letters", "notify_scheduled_retry_due",
    "perform_maintenance", "process_inbox_completions", "process_inbox_failures",
    "purge_orphan_inbox", "recompute_partition_numbers", "recover_dead_letter",
    "release_unprocessed_inbox", "release_unstarted_leases", "renew_leases",
    "store_inbox_messages",
  ];

  /// <summary>
  /// Enumerated functions that deliberately need no rewrite, with the reason. A name belongs here
  /// only when it was checked and found not to touch the moved columns at all.
  /// </summary>
  private static readonly Dictionary<string, string> NEEDS_NO_CHANGE = new(StringComparer.Ordinal) {
    ["commit_handler_batch_bulk"] =
      "matched the enumeration's text search only through a comment mentioning wh_inbox; it has no "
      + "statement against the table",
  };

  private const string CUTOVER_MIGRATION = "162_InboxWorkStateSideTable";

  /// <summary>
  /// C# lines that name <c>wh_inbox</c> beside a moved column and are nonetheless correct, with the
  /// reason. A line belongs here only when it was read and found not to build SQL over a moved
  /// column. Keyed by file and line content rather than line number, so it cannot rot into
  /// suppressing a different line when the file shifts.
  /// </summary>
  private static readonly (string File, string Contains, string Why)[] REVIEWED_AND_CORRECT = [
    ("DapperWorkCoordinator.cs", "InboxRowsRecomputed = get(\"wh_inbox\")",
      "a result-label lookup, not SQL: recompute_partition_numbers reports its per-table counts "
      + "under the label 'wh_inbox' and that label is unchanged"),
    ("WhizbangModelBuilderExtensions.cs", "entity.ToTable(\"wh_inbox\")",
      "the EF mapping for the message row, whose work-state properties are now explicitly Ignored "
      + "a few lines below"),
  ];

  /// <summary>
  /// Statements whose presence means the script is relying on being one transaction.
  /// </summary>
  private static readonly string[] ATOMIC_ONLY_STATEMENTS = ["LOCK TABLE", "DROP COLUMN"];

  [Test]
  public async Task AScriptThatLocksOrDropsAColumnCarriesNoCommitBoundaryAsync() {
    var offenders = new List<string>();
    foreach (var migration in new PostgresMigrationProvider().GetMigrations()) {
      var relies = ATOMIC_ONLY_STATEMENTS.Any(
        s => migration.Sql.Contains(s, StringComparison.OrdinalIgnoreCase));
      if (relies && migration.Sql.Contains(SchemaCommandBoundary.MARKER, StringComparison.Ordinal)) {
        offenders.Add(migration.Name);
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("a script that takes an explicit table lock or drops a column depends on every "
        + "statement around it committing together. A commit boundary splits it into pieces that "
        + "commit separately, so a lock is released before the work it was protecting finishes and "
        + "a dropped column can be committed while the functions still reading it have not been "
        + "replaced. Move the dependent statements into one script instead of separating them");
  }

  /// <summary>
  /// Every enumerated function is redirected by the cutover migration, and every function the
  /// cutover migration redefines is in the enumeration.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This exists because the enumeration was right and the checklist against it drifted: one of the
  /// twenty-two, <c>perform_maintenance</c>, was never rewritten, and the full suite found it rather
  /// than any review. Comparing the two sets by hand worked once; this makes it not depend on
  /// anyone doing it again.
  /// </para>
  /// <para>
  /// Both directions are asserted, because a function in the migration that is not in the
  /// enumeration is just as wrong as the reverse, and is how a twenty-third function would hide: it
  /// would be quietly redirected without anyone deciding it should be.
  /// </para>
  /// </remarks>
  [Test]
  public async Task TheCutoverRedirectsExactlyTheEnumeratedFunctionsAsync() {
    var cutover = new PostgresMigrationProvider().GetMigrations()
      .FirstOrDefault(m => string.Equals(m.Name, CUTOVER_MIGRATION, StringComparison.Ordinal));

    await Assert.That(cutover).IsNotNull()
      .Because($"{CUTOVER_MIGRATION} carries the column drops and every redirection that has to "
        + "precede them. If it was renamed, rename it here rather than letting this guard pass "
        + "vacuously");

    var redefined = _functionsDefinedIn(cutover!.Sql);
    var expected = ENUMERATED.Where(f => !NEEDS_NO_CHANGE.ContainsKey(f)).ToHashSet(StringComparer.Ordinal);

    var missing = expected.Except(redefined, StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
    await Assert.That(missing).IsEmpty()
      .Because("these functions read a column the cutover drops and are not redirected by it, so "
        + "after the migration their effective definition references a column that no longer "
        + $"exists: {string.Join(", ", missing)}");

    var unexpected = redefined.Except(ENUMERATED, StringComparer.Ordinal)
      .OrderBy(f => f, StringComparer.Ordinal).ToList();
    await Assert.That(unexpected).IsEmpty()
      .Because("the cutover redefines these and the enumeration does not list them, so either the "
        + "enumeration is short or they are being redirected without that being a decision: "
        + $"{string.Join(", ", unexpected)}");
  }

  /// <summary>
  /// No function outside the enumeration reads a column the cutover drops.
  /// </summary>
  /// <remarks>
  /// The guard above compares two lists, so it catches a list drifting from the migration and not a
  /// list that was short to begin with. This one derives its answer from the migrations instead, so
  /// a twenty-third function added next year is caught without anyone updating anything. Comments
  /// are stripped first: the enumeration that produced <c>ENUMERATED</c> was itself wrong twice
  /// because a pattern matched, or stopped at, a word inside a comment.
  /// </remarks>
  [Test]
  public async Task NoUnenumeratedFunctionReadsADroppedColumnAsync() {
    var enumerated = ENUMERATED.ToHashSet(StringComparer.Ordinal);
    var offenders = new SortedSet<string>(StringComparer.Ordinal);

    // LATEST DEFINITION WINS, and modelling that is not a nicety. Migrations supersede each other,
    // so an older definition of a function that has since been rewritten is history rather than a
    // live read path. The first version of this guard flagged notify_instance_owners because its
    // 045 and 130 definitions scan wh_inbox; its current definition (141) does not reference the
    // table at all. A guard that fails on superseded text is a guard somebody disables.
    var latest = new Dictionary<string, (int Order, string Sql)>(StringComparer.Ordinal);
    foreach (var migration in new PostgresMigrationProvider().GetMigrations()) {
      if (string.Equals(migration.Name, CUTOVER_MIGRATION, StringComparison.Ordinal)) {
        continue;
      }
      var order = _migrationOrder(migration.Name);
      var sql = _stripLineComments(migration.Sql);
      foreach (var function in _functionsDefinedIn(sql)) {
        if (!latest.TryGetValue(function, out var held) || order > held.Order) {
          latest[function] = (order, sql);
        }
      }
    }

    foreach (var (function, (_, sql)) in latest) {
      if (enumerated.Contains(function)) {
        continue;
      }
      var body = _bodyOf(sql, function);
      if (body is null || !_referencesBareInbox(body)) {
        continue;
      }
      if (MOVED_COLUMNS.Any(c => body.Contains(c, StringComparison.Ordinal))) {
        offenders.Add(function);
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("these functions reference wh_inbox together with a column the cutover drops, and "
        + "they are not in the enumeration, so nothing redirects them and their first call after "
        + $"the migration raises undefined_column: {string.Join(", ", offenders)}");
  }

  /// <summary>
  /// The numeric prefix a migration sorts by, so "latest definition wins" is the real ordering
  /// rather than alphabetical (which would put 099 after 100).
  /// </summary>
  private static int _migrationOrder(string name) {
    var underscore = name.IndexOf('_', StringComparison.Ordinal);
    return underscore > 0 && int.TryParse(name[..underscore], out var n) ? n : int.MaxValue;
  }

  private static string _stripLineComments(string sql) =>
    string.Join("\n", sql.Split('\n').Select(line => {
      var i = line.IndexOf("--", StringComparison.Ordinal);
      return i < 0 ? line : line[..i];
    }));

  /// <summary>Function names a script defines, without the schema token.</summary>
  private static HashSet<string> _functionsDefinedIn(string sql) =>
    System.Text.RegularExpressions.Regex
      .Matches(sql, @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+[^\s.(]+\.([a-z_][a-z0-9_]*)\s*\(",
        RegexOptions.IgnoreCase)
      .Select(m => m.Groups[1].Value)
      .ToHashSet(StringComparer.Ordinal);

  /// <summary>The text of one function definition, from its CREATE to the terminating dollar quote.</summary>
  private static string? _bodyOf(string sql, string function) {
    var m = Regex.Match(
      sql,
      @"CREATE\s+OR\s+REPLACE\s+FUNCTION\s+[^\s.(]+\." + Regex.Escape(function)
        + @"\s*\(.*?\n\$\$",
      RegexOptions.IgnoreCase | RegexOptions.Singleline);
    return m.Success ? m.Value : null;
  }

  /// <summary>
  /// A reference to wh_inbox itself rather than to wh_inbox_state, which shares the prefix.
  /// </summary>
  private static bool _referencesBareInbox(string body) => BareInbox().IsMatch(body);

  /// <summary>wh_inbox itself, not wh_inbox_state, which shares the prefix.</summary>
  [GeneratedRegex(@"\bwh_inbox\b(?!_)")]
  private static partial Regex BareInbox();

  /// <summary>
  /// No C# source builds SQL that reads a column the cutover drops from <c>wh_inbox</c>.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This is the guard for the gap that cost the most. The function enumeration was derived from
  /// <c>pg_proc</c>, correctly and completely, and <c>pg_proc</c> is simply not where all of this
  /// system's SQL lives: the coordinators build statements as C# strings. That enumeration was
  /// complete for functions and complete for nothing else, and the question to have asked was
  /// "what reads these columns" rather than "which functions read these columns". A failing test
  /// found it, in <c>CountServiceBacklogAsync</c>, after the columns were already dropped.
  /// </para>
  /// <para>
  /// Derived rather than listed, so a fifteenth file added next year is covered without anyone
  /// updating anything. The same applies when the outbox and perspective-events tables are split:
  /// their C# exposure has to be enumerated from the start rather than discovered this way.
  /// </para>
  /// <para>
  /// Comment-only hits are REPORTED SEPARATELY rather than excluded, because C# comment stripping
  /// is not reliable enough to bet an outage on. A false positive costs a reader ten seconds; a
  /// false negative costs production an outage on first call. The assertion fails only on code
  /// lines, and the comment list is printed so a reader can see what was set aside and why.
  /// </para>
  /// </remarks>
  [Test]
  public async Task NoCSharpSourceReadsADroppedInboxColumnAsync() {
    var root = _repositoryRoot();
    var offenders = new SortedSet<string>(StringComparer.Ordinal);
    var commentOnly = new SortedSet<string>(StringComparer.Ordinal);

    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      var lines = await File.ReadAllLinesAsync(file);
      for (var i = 0; i < lines.Length; i++) {
        if (!BareInbox().IsMatch(lines[i])) {
          continue;
        }
        // A window rather than the line, because the table name and the column it selects are
        // routinely on different lines of the same string literal.
        var from = Math.Max(0, i - 12);
        var to = Math.Min(lines.Length, i + 25);
        var window = string.Join("\n", lines[from..to]);
        if (!MOVED_COLUMNS.Any(c => window.Contains(c, StringComparison.Ordinal))) {
          continue;
        }
        var trimmed = lines[i].TrimStart();
        var isComment = trimmed.StartsWith("//", StringComparison.Ordinal)
          || trimmed.StartsWith('*');
        var where = $"{Path.GetRelativePath(root, file)}:{i + 1}";
        if (isComment) {
          commentOnly.Add(where);
          continue;
        }
        // A line naming BOTH tables is a deliberate join across the split rather than a statement
        // that was missed, which is the shape every correct rewrite of a mixed predicate takes.
        if (lines[i].Contains("wh_inbox_state", StringComparison.Ordinal)) {
          continue;
        }
        if (REVIEWED_AND_CORRECT.Any(r =>
            file.EndsWith(r.File, StringComparison.Ordinal)
            && lines[i].Contains(r.Contains, StringComparison.Ordinal))) {
          continue;
        }
        offenders.Add($"{where}  {trimmed[..Math.Min(100, trimmed.Length)]}");
      }
    }

    await Assert.That(offenders).IsEmpty()
      .Because("these C# sources name wh_inbox near a column the cutover drops, so if any of them "
        + "builds SQL over that column it raises undefined_column on its first call in production. "
        + "Point the statement at wh_inbox_state, or if the line is prose that the comment check "
        + "misread, reword it so it does not read as code. Set aside as comments: "
        + $"{string.Join(", ", commentOnly)}. Code lines: {string.Join(" | ", offenders)}");
  }

  /// <summary>
  /// The repository root, found by walking up to the directory holding <c>src</c> and <c>tests</c>.
  /// </summary>
  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null
        && !(Directory.Exists(Path.Combine(dir.FullName, "src"))
          && Directory.Exists(Path.Combine(dir.FullName, "tests")))) {
      dir = dir.Parent;
    }
    return dir?.FullName
      ?? throw new InvalidOperationException(
        "could not find the repository root from the test binary location, so this guard cannot "
        + "scan the sources it exists to scan. That is a failure rather than a pass");
  }
}
