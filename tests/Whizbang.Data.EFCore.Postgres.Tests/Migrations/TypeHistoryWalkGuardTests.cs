// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2205: a change to the stored history of one event type goes through the per-type history walker, which reads the
/// type's own events in commit order through the type's index. A walk of the store by event id (345's design, whose cost
/// was the store's size per type) fails this guard by shape: every type-scoped history function's latest definition is
/// read, and none may compare event ids as a range, which is what walking the primary key looks like.
/// </summary>
/// <remarks>
/// A type-scoped history function is one named <c>*_history_*</c> or one that reads the walker's cursor table
/// (<c>wh_type_history_walks</c>), so a new operation on the walker is covered without being listed here.
/// </remarks>
/// <docs>fundamentals/events/superseding-events#history-stored-before-the-declaration</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/374_HistoryWalksReadTheirOwnType.sql</code-under-test>
[Category("Migrations")]
[Category("Shard3")]
public partial class TypeHistoryWalkGuardTests {
  // An event id compared as a range: (es.event_id > cursor), (event_id <= through), a walk of the primary key.
  [GeneratedRegex(@"\bevent_id\s*(>|<)=?\s*", RegexOptions.IgnoreCase)]
  private static partial Regex EventIdRange();

  [GeneratedRegex(@"_history_|wh_type_history_walks", RegexOptions.IgnoreCase)]
  private static partial Regex TypeScopedHistory();

  [Test]
  public async Task NoTypeScopedHistoryFunction_WalksTheStoreByEventIdAsync() {
    var latest = _historyFunctions(_migrations(_migrationsDirectory()));

    await Assert.That(latest.Keys).Contains("wh_type_history_begin")
      .Because("precondition: the walker's window is among the functions read");
    await Assert.That(latest.Keys).Contains("wh_superseding_history_apply")
      .Because("precondition: the superseding reconcile is among the functions read");

    var offenders = latest.Where(f => EventIdRange().IsMatch(f.Value)).Select(f => f.Key).Order(StringComparer.Ordinal).ToList();

    await Assert.That(offenders).IsEmpty()
      .Because("these type-scoped history functions compare event ids as a range, which walks the whole store by its "
        + "primary key; read the type's events through wh_type_history_begin's window instead: " + string.Join(", ", offenders));
  }

  [Test]
  public async Task TheGuard_FlagsAWalkByEventId_AndPassesAWalkByTheTypesCommitOrderAsync() {
    var flagged = _historyFunctions([
      ("001_a.sql", """
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_history_window(p_after UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id > p_after ORDER BY es.event_id LIMIT 10;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_reads_the_cursor() RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_type_history_walks h, __SCHEMA__.wh_event_store es WHERE es.event_id<=h.last_id;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_history_by_type(p_type TEXT, p_after BIGINT) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_event_store es
          WHERE es.event_type = p_type AND es.commit_sequence > p_after AND es.event_id = ANY(ARRAY[]::uuid[])
          ORDER BY es.commit_sequence LIMIT 10;
        END;
        $$ LANGUAGE plpgsql;
        CREATE OR REPLACE FUNCTION __SCHEMA__.wh_probe_unrelated(p_after UUID) RETURNS void AS $$
        BEGIN
          PERFORM 1 FROM __SCHEMA__.wh_event_store es WHERE es.event_id > p_after;
        END;
        $$ LANGUAGE plpgsql;
        """),
    ]);

    await Assert.That(flagged.Keys.Order(StringComparer.Ordinal).ToList())
      .IsEquivalentTo(["wh_probe_history_by_type", "wh_probe_history_window", "wh_probe_reads_the_cursor"])
      .Because("a function named *_history_* or reading the walker's cursor is type-scoped; an unrelated one is not");
    await Assert.That(EventIdRange().IsMatch(flagged["wh_probe_history_window"])).IsTrue();
    await Assert.That(EventIdRange().IsMatch(flagged["wh_probe_reads_the_cursor"])).IsTrue();
    await Assert.That(EventIdRange().IsMatch(flagged["wh_probe_history_by_type"])).IsFalse()
      .Because("a range of the type's commit sequences, and event ids looked up by key, are not a walk of the store");
  }

  private static Dictionary<string, string> _historyFunctions(IEnumerable<(string Name, string Sql)> migrations) =>
    MigrationFunctionBodies.LastWord(migrations)
      .Where(f => TypeScopedHistory().IsMatch(f.Key) || TypeScopedHistory().IsMatch(f.Value.NormalizedBody))
      .ToDictionary(f => f.Key, f => f.Value.NormalizedBody, StringComparer.Ordinal);

  private static IEnumerable<(string Name, string Sql)> _migrations(string directory) =>
    Directory.EnumerateFiles(directory, "*.sql").Order(StringComparer.Ordinal)
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
