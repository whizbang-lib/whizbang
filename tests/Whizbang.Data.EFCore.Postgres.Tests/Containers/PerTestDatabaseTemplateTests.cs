// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Containers;

/// <summary>
/// A fixture fills a template once and gives each test a copy of it: a database of the test's own with the
/// template's tables, rows and statistics already in it (#1941).
/// </summary>
/// <remarks>
/// The containment fixture used to insert 200,000 rows for every test, which is load on a server every shard
/// shares and a statement long enough for the client to cancel. Copying a filled template moves that work to once
/// per class, and these tests pin what the copy has to carry for the fixture's planner assertions to still mean
/// what they say.
/// </remarks>
[Category("Integration")]
[Category("Shard2")]
public class PerTestDatabaseTemplateTests {
  private const string PREFIX = "tplcopy";

  private static async Task<long> _scalarAsync(string connectionString, string sql, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<PerTestDatabase> _filledTemplateAsync(CancellationToken cancellationToken) {
    var template = await PerTestDatabaseFactory.CreateAsync(PREFIX, cancellationToken: cancellationToken);
    // No pooling: a pooled session left idle in the template would refuse the copy (55006).
    var fill = new NpgsqlConnectionStringBuilder(PerTestDatabaseFactory.ForSetup(template.ConnectionString)) {
      Pooling = false,
    }.ConnectionString;
    await using var connection = new NpgsqlConnection(fill);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand("""
      CREATE TABLE seeded (id int PRIMARY KEY, doc jsonb NOT NULL);
      CREATE INDEX idx_seeded_doc ON seeded USING gin (doc);
      INSERT INTO seeded SELECT g, jsonb_build_object('n', g) FROM generate_series(1, 1000) g;
      ANALYZE seeded;
      """, connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return template;
  }

  [Test]
  [Timeout(120000)]
  public async Task ACopy_CarriesTheTemplatesRowsIndexesAndStatisticsAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var template = await _filledTemplateAsync(cancellationToken);
    try {
      var copy = await PerTestDatabaseFactory.CreateFromTemplateAsync(PREFIX, template.Name, cancellationToken);
      try {
        await Assert.That(copy.Name).IsNotEqualTo(template.Name);
        await Assert.That(await _scalarAsync(copy.ConnectionString, "SELECT count(*) FROM seeded", cancellationToken))
          .IsEqualTo(1000L);
        await Assert.That(await _scalarAsync(copy.ConnectionString,
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'idx_seeded_doc'", cancellationToken))
          .IsEqualTo(1L);
        await Assert.That(await _scalarAsync(copy.ConnectionString,
            "SELECT count(*) FROM pg_stats WHERE tablename = 'seeded'", cancellationToken))
          .IsGreaterThan(0L)
          .Because("the planner assertions a fixture makes over the copy rely on the statistics the template gathered");
      } finally {
        await PerTestDatabaseFactory.DropAsync(copy.Name, cancellationToken: cancellationToken);
      }
    } finally {
      await PerTestDatabaseFactory.DropAsync(template.Name, cancellationToken: cancellationToken);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task ACopy_IsItsOwnDatabase_SoAChangeToItLeavesTheTemplateAloneAsync(CancellationToken cancellationToken) {
    await SharedPostgresContainer.InitializeAsync(cancellationToken);
    var template = await _filledTemplateAsync(cancellationToken);
    try {
      var copy = await PerTestDatabaseFactory.CreateFromTemplateAsync(PREFIX, template.Name, cancellationToken);
      try {
        await _scalarAsync(copy.ConnectionString, "WITH gone AS (DELETE FROM seeded RETURNING 1) SELECT count(*) FROM gone",
          cancellationToken);

        var second = await PerTestDatabaseFactory.CreateFromTemplateAsync(PREFIX, template.Name, cancellationToken);
        try {
          await Assert.That(await _scalarAsync(second.ConnectionString, "SELECT count(*) FROM seeded", cancellationToken))
            .IsEqualTo(1000L)
            .Because("each test gets the template as it was filled, whatever an earlier test did to its copy");
        } finally {
          await PerTestDatabaseFactory.DropAsync(second.Name, cancellationToken: cancellationToken);
        }
      } finally {
        await PerTestDatabaseFactory.DropAsync(copy.Name, cancellationToken: cancellationToken);
      }
    } finally {
      await PerTestDatabaseFactory.DropAsync(template.Name, cancellationToken: cancellationToken);
    }
  }
}
