// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Shared.Models;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// <c>wh_ensure_index</c> creates a declared index only when the table has no index with the same
/// definition, whatever that index is called.
/// </summary>
/// <remarks>
/// <para>
/// <c>CREATE INDEX IF NOT EXISTS</c> compares names and nothing else. A perspective table that
/// already carried an index over <c>scope -&gt;&gt; 't'</c> from a path an earlier release took,
/// under that path's own naming, was given a second one with the identical definition by the
/// schema pass, and both were then maintained on every write. Each case here runs the real
/// migration against a real table and reads the catalog back, because the whole question is what
/// PostgreSQL considers the same index.
/// </para>
/// <para>
/// The statements come from <see cref="PerspectiveIndexSql.Ensure"/>, which is what the generator
/// emits, so the function and the call shape are tested together.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/174_EnsureIndex.sql</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class EnsureIndexFunctionTests {
  private const string TABLE = "wh_per_ensure_probe";
  private const string QUALIFIED = "\"public\"." + TABLE;
  private const string SCHEMA = "\"public\"";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("ensure_index");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;

    await using var db = await _openAsync();
    await _execAsync(db, $"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY,
        data jsonb NOT NULL,
        metadata jsonb NOT NULL,
        scope jsonb NOT NULL,
        code text
      );
      """);

    // The real migration text: a copy here would pass whether or not the shipped one works. 000
    // first, because 174 opens by clearing earlier overloads with the helper 000 defines.
    foreach (var migration in new[] { "000_MigrationTracking.sql", "174_EnsureIndex.sql" }) {
      await _execAsync(db, (await File.ReadAllTextAsync(_migrationPath(migration)))
        .Replace("__SCHEMA__", SCHEMA, StringComparison.Ordinal));
    }
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private async Task<NpgsqlConnection> _openAsync() {
    var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    return db;
  }

  private static async Task _execAsync(NpgsqlConnection db, string sql) {
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>Runs one declared index the way the schema pass does and returns what the function did.</summary>
  private async Task<string> _ensureAsync(string createStatement, NpgsqlConnection? on = null) {
    var db = on ?? await _openAsync();
    try {
      await using var command = new NpgsqlCommand(PerspectiveIndexSql.Ensure(createStatement, SCHEMA), db);
      return (string)(await command.ExecuteScalarAsync())!;
    } finally {
      if (on is null) {
        await db.DisposeAsync();
      }
    }
  }

  /// <summary>The names of the table's indexes whose definition contains <paramref name="fragment"/>.</summary>
  private async Task<List<string>> _indexesOverAsync(string fragment) {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand(
      "SELECT indexname FROM pg_indexes WHERE tablename = $1 AND indexdef LIKE '%' || $2 || '%' ORDER BY indexname", db);
    command.Parameters.AddWithValue(TABLE);
    command.Parameters.AddWithValue(fragment);
    var names = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  // ========================================
  // One index for one definition
  // ========================================

  /// <summary>
  /// The tenant expression requested through both paths, the one an earlier release used and the
  /// schema pass, yields one index.
  /// </summary>
  /// <remarks>
  /// The first statement is exactly what the removed apply-time ensurer ran, under its naming; the
  /// second is what the generator emits today. The first is left as it is, because renaming or
  /// dropping an operator's index is the operator's decision.
  /// </remarks>
  [Test]
  public async Task TheSameExpressionThroughBothPathsYieldsOneIndexAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"CREATE INDEX IF NOT EXISTS idx_{TABLE}_scope_t ON {TABLE} ((scope->>'t'))");
    }

    var outcome = await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_ensure_probe_scope_tenant ON {QUALIFIED} ((scope->>'t'));");

    await Assert.That(outcome).IsEqualTo($"equivalent:idx_{TABLE}_scope_t");
    await Assert.That(await _indexesOverAsync("(scope ->> 't'::text)")).IsEquivalentTo([$"idx_{TABLE}_scope_t"]);
  }

  /// <summary>
  /// A declared field index and the earlier path's index over the same extraction are one index,
  /// even though one was written with spaces around the operator and one without.
  /// </summary>
  [Test]
  public async Task ADeclaredFieldIndexMatchesAnEquivalentWrittenDifferentlyAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"CREATE INDEX idx_{TABLE}_data_status ON {TABLE} ((data->>'Status'))");
    }

    var statement = JsonIndexSql.CreateStatements(
      new JsonIndexInfo("Status", "Status", JsonIndexCast.None, Ordered: true, Substring: false, CaseInsensitive: false),
      QUALIFIED, "ensure_probe").Single();
    var outcome = await _ensureAsync(statement);

    await Assert.That(outcome).IsEqualTo($"equivalent:idx_{TABLE}_data_status");
    await Assert.That(await _indexesOverAsync("(data ->> 'Status'::text)")).Count().IsEqualTo(1);
  }

  /// <summary>Two declarations of one expression within the schema itself also yield one index.</summary>
  [Test]
  public async Task TwoDeclarationsOfOneExpressionYieldOneIndexAsync() {
    var first = await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_ensure_probe_scope_tenant ON {QUALIFIED} ((scope->>'t'));");
    var second = await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_ensure_probe_tenant ON {QUALIFIED} ((scope ->> 't'));");

    await Assert.That(first).IsEqualTo("created");
    await Assert.That(second).IsEqualTo("equivalent:idx_ensure_probe_scope_tenant");
    await Assert.That(await _indexesOverAsync("(scope ->> 't'::text)")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// The skip is reported, as a warning the server logs and the client can read, naming both
  /// indexes, because a declared index quietly not being built reads as a defect later.
  /// </summary>
  [Test]
  public async Task ASkipIsReportedAsync() {
    var notices = new List<string>();
    await using var db = await _openAsync();
    db.Notice += (_, e) => notices.Add($"{e.Notice.Severity}: {e.Notice.MessageText}");
    await _execAsync(db, $"CREATE INDEX legacy_tenant ON {TABLE} ((scope->>'t'))");

    await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_ensure_probe_scope_tenant ON {QUALIFIED} ((scope->>'t'));", db);

    await Assert.That(notices).Count().IsEqualTo(1);
    await Assert.That(notices[0]).StartsWith("WARNING");
    await Assert.That(notices[0]).Contains("idx_ensure_probe_scope_tenant");
    await Assert.That(notices[0]).Contains("legacy_tenant");
  }

  // ========================================
  // A different definition is a different index
  // ========================================

  /// <summary>
  /// Anything that changes what the index answers makes it a different index, which is created:
  /// uniqueness, a predicate, the access method, a cast.
  /// </summary>
  [Test]
  [Arguments("CREATE INDEX legacy ON wh_per_ensure_probe (code)", "CREATE UNIQUE INDEX IF NOT EXISTS idx_probe_code ON \"public\".wh_per_ensure_probe (code)")]
  [Arguments("CREATE INDEX legacy ON wh_per_ensure_probe (code)", "CREATE INDEX IF NOT EXISTS idx_probe_code ON \"public\".wh_per_ensure_probe (code) WHERE code IS NOT NULL")]
  [Arguments("CREATE INDEX legacy ON wh_per_ensure_probe USING gin (data jsonb_path_ops)", "CREATE INDEX IF NOT EXISTS idx_probe_data ON \"public\".wh_per_ensure_probe USING gin (data)")]
  [Arguments("CREATE INDEX legacy ON wh_per_ensure_probe ((data ->> 'Rank'))", "CREATE INDEX IF NOT EXISTS idx_probe_rank ON \"public\".wh_per_ensure_probe (((data ->> 'Rank')::integer))")]
  public async Task ADifferentDefinitionIsCreatedAsync(string existing, string declared) {
    await using (var db = await _openAsync()) {
      await _execAsync(db, existing);
    }

    await Assert.That(await _ensureAsync(declared)).IsEqualTo("created");
  }

  /// <summary>An index already present under its own name is left alone, without comparing anything.</summary>
  [Test]
  public async Task AnIndexPresentUnderItsNameIsLeftAloneAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"CREATE INDEX idx_probe_code ON {TABLE} (code)");
    }

    await Assert.That(await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_probe_code ON {QUALIFIED} (code)")).IsEqualTo("exists");
  }

  /// <summary>A quoted index name is compared as written, not folded to lower case.</summary>
  [Test]
  public async Task AQuotedNameIsMatchedExactlyAsync() {
    await using (var db = await _openAsync()) {
      await _execAsync(db, $"CREATE INDEX \"IX_Probe_Code\" ON {TABLE} (code)");
    }

    await Assert.That(await _ensureAsync($"CREATE INDEX IF NOT EXISTS \"IX_Probe_Code\" ON {QUALIFIED} (code)")).IsEqualTo("exists");
  }

  /// <summary>
  /// A table with no index of the same method cannot hold an equivalent, so the index is created
  /// without building a probe.
  /// </summary>
  [Test]
  public async Task NoIndexOfTheSameMethodMeansNothingToCompareAsync() {
    await Assert.That(await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_probe_scope ON {QUALIFIED} USING gin (scope)")).IsEqualTo("created");
    await Assert.That(await _indexesOverAsync("gin (scope)")).IsEquivalentTo(["idx_probe_scope"]);
  }

  /// <summary>
  /// A statement this function does not recognize is run as it was given, so a shape it cannot
  /// compare behaves exactly as it did before the comparison existed.
  /// </summary>
  [Test]
  public async Task AnUnrecognizedStatementRunsAsGivenAsync() {
    await using var db = await _openAsync();
    await using var command = new NpgsqlCommand(
      $"SELECT {SCHEMA}.wh_ensure_index('CREATE INDEX idx_probe_plain ON {TABLE} (code)')", db);

    await Assert.That((string)(await command.ExecuteScalarAsync())!).IsEqualTo("executed");
    await Assert.That(await _indexesOverAsync("(code)")).IsEquivalentTo(["idx_probe_plain"]);
  }

  /// <summary>
  /// A role that may not create temporary tables still gets its index, without the comparison,
  /// rather than a failed schema pass.
  /// </summary>
  /// <remarks>
  /// The comparison builds the candidate on an empty temporary copy of the table, and some
  /// hardened servers revoke the temporary-table privilege. Failing the pass there would turn a
  /// duplicate-avoidance step into a startup outage, which is the wrong way round.
  /// </remarks>
  [Test]
  public async Task WithoutTheTemporaryTablePrivilegeTheIndexIsStillCreatedAsync() {
    var role = $"wh_no_temp_{Guid.NewGuid():N}"[..24];
    await using var db = await _openAsync();
    await _execAsync(db, $"""
      CREATE ROLE {role};
      REVOKE TEMPORARY ON DATABASE "{_databaseName}" FROM PUBLIC;
      GRANT USAGE, CREATE ON SCHEMA public TO {role};
      ALTER TABLE {TABLE} OWNER TO {role};
      """);
    try {
      await _execAsync(db, $"SET ROLE {role}");
      var outcome = await _ensureAsync($"CREATE INDEX IF NOT EXISTS idx_probe_code ON {QUALIFIED} (code)", db);
      await _execAsync(db, "RESET ROLE");

      await Assert.That(outcome).IsEqualTo("created");
    } finally {
      await _execAsync(db, $"RESET ROLE; ALTER TABLE {TABLE} OWNER TO CURRENT_USER; DROP OWNED BY {role}; DROP ROLE {role}");
    }
  }

  private static string _migrationPath(string fileName) => Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "Whizbang.Data.Postgres", "Migrations", fileName);
}
