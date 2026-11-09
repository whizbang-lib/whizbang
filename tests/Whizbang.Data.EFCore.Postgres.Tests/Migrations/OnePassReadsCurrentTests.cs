// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2194: a database one complete schema pass has built reads as current on the next start. The stored-form rewrite
/// runs before the pass creates the perspective tables, so on a new database it finds no table to settle; the pass then
/// settles the tables it created, and the next start's Assess reads Current instead of running a pass it does not need.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class OnePassReadsCurrentTests {
  [Test]
  [Timeout(240000)]
  public async Task ADatabaseOnePassBuilt_ReadsAsCurrentOnTheNextStartAsync(CancellationToken ct) {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("efonepass", cancellationToken: ct);
    try {
      var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Timezone = "UTC" }.ConnectionString;
      var (dataSource, options) = EFCoreTestBase.CreateContextOptions(connectionString);
      await using (dataSource) {
        await using (var first = new WorkCoordinationDbContext(options)) {
          // The premise: the model declares conversions on tables only the pass creates, so the rewrite that runs
          // before the pass finds none of them. Without one, this test would prove nothing.
          var declared = CanonicalTemporalRewrite.ForModel(first.Model, PerspectiveDocumentSerialization.Options, "public").Length
            + DeclaredKeyShapes.ForModel(first.Model, PerspectiveDocumentSerialization.Options).Length;
          await Assert.That(declared).IsGreaterThan(0);

          await first.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: ct);
        }

        await using var next = new WorkCoordinationDbContext(options);
        await Assert.That(await next.IsWhizbangSchemaCurrentAsync(ct)).IsTrue()
          .Because("one complete pass built every table and settled every conversion on them; a second pass owes nothing");
      }
    } finally {
      NpgsqlConnection.ClearAllPools();
      await PerTestDatabaseFactory.DropAsync(database.Name);
    }
  }
}
