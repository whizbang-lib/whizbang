// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>The opening balance of a Dapper-stored ledger's next period.</summary>
public sealed record DapperBooksOpened : OpeningBalance {
  /// <summary>The balance carried forward.</summary>
  public decimal Balance { get; init; }
}

/// <summary>
/// Closing the books (#1741) on the Dapper driver: the driver registers the closer, which stores the summary and the
/// opening balance as this driver's direct append stores them, seals the predecessor and links the successor to it. The SQL
/// itself is covered by the EF Core <c>CloseBooksSqlTests</c>; these lock this driver's side.
/// </summary>
/// <docs>fundamentals/events/closing-the-books</docs>
public class DapperCloseBooksTests : PostgresTestBase {
  [Test]
  public async Task AddWhizbangPostgres_RegistersTheCloser_WhichClosesTheBooks_AndTheSuccessorReadsBackLinkedAsync() {
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
    var ledger = (Guid)TrackedGuid.New();
    await store.AppendAsync(ledger, new DapperMixedStreamProbeNoted { StreamId = ledger });
    await store.AppendAsync(ledger, new DapperMixedStreamProbeNoted { StreamId = ledger });

    var result = await scope.ServiceProvider.GetRequiredService<IBookCloser>()
      .CloseBooksAsync(ledger, new DapperMixedStreamProbeNoted { StreamId = ledger }, new DapperBooksOpened { Balance = 12 });

    await Assert.That(result.Status).IsEqualTo(BookCloseStatus.Closed);
    await Assert.That(result.PredecessorFinalVersion).IsEqualTo(3L);
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var successor = result.SuccessorStreamId!.Value;
    var opening = (await conn.QueryAsync<(string Type, string Data, long Version)>(
      "SELECT es.aggregate_type, b.event_data::text, es.version::bigint FROM wh_event_store es JOIN wh_event_body b ON b.event_id = es.event_id WHERE es.stream_id = @successor",
      new { successor })).ToList();
    await Assert.That(opening).Count().IsEqualTo(1).Because("the opening balance is the successor's only event");
    await Assert.That(opening[0].Type).IsEqualTo(typeof(DapperBooksOpened).FullName);
    await Assert.That(opening[0].Data).Contains(ledger.ToString()).Because("the successor's opening event names its predecessor");
    await Assert.That(opening[0].Data).Contains("12");
    await Assert.That(await conn.ExecuteScalarAsync<string>("SELECT state FROM wh_stream_lifecycle WHERE stream_id = @ledger", new { ledger }))
      .IsEqualTo("Sealed");
    await Assert.That(async () => await store.AppendAsync(ledger, new DapperMixedStreamProbeNoted { StreamId = ledger }))
      .Throws<StreamSealedException>();
  }
}
