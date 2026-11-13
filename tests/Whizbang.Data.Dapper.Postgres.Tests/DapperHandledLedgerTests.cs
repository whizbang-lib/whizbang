// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2273: the handled ledger's lookup and sweep on the Dapper driver, with the contract the EF Core driver keeps
/// (<c>HandledLedgerSqlTests</c>). The ledger is written by the SQL completion on either driver, so the Dapper host must
/// sweep it, or it would grow without bound.
/// </summary>
/// <docs>resilience/stream-integrity#repaired-events-and-receptors</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.RetentionSweeps.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorRetentionSql.cs</code-under-test>
[Category("Integration")]
public class DapperHandledLedgerTests : PostgresTestBase {
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Deliberate: the interface is what the workers call through, so an unimplemented member reaches its default here as it does there.")]
  private IWorkCoordinator _coordinator() =>
    new DapperWorkCoordinator(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  public async Task Lookup_AndSweep_RunOnTheDapperDriverAsync() {
    var coordinator = _coordinator();
    await using var connection = new NpgsqlConnection(ConnectionString);
    var now = await connection.ExecuteScalarAsync<DateTime>("SELECT now()");
    var at = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    var old = Guid.CreateVersion7(at - TimeSpan.FromDays(10));
    var recent = Guid.CreateVersion7(at - TimeSpan.FromDays(1));
    var never = Guid.CreateVersion7(at - TimeSpan.FromHours(1));
    await connection.ExecuteAsync("INSERT INTO wh_receptor_handled (event_id) VALUES (@old), (@recent)", new { old, recent });

    var answer = await coordinator.LookupHandledEventsAsync([old, recent, never]);
    var removed = await coordinator.SweepHandledLedgerAsync(at, TimeSpan.FromDays(7));

    await Assert.That(answer.Handled).IsEquivalentTo([old, recent]);
    await Assert.That(answer.CoversFrom.HasValue).IsTrue();
    await Assert.That(removed).IsEqualTo(1L).Because("the record older than the window goes, as on EF Core");
    await Assert.That((await coordinator.LookupHandledEventsAsync([old, recent])).Handled).IsEquivalentTo([recent]);
    await Assert.That((await coordinator.LookupHandledEventsAsync([])).Handled).IsEmpty();
  }
}
