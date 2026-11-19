// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2116: on the Dapper driver too, an event stored under an origin that does not own its type is re-attributed to its
/// owner by the registered maintenance step, with its origin sequence cleared and its digest moved to the owner's bucket.
/// </summary>
/// <docs>resilience/stream-integrity#an-origin-that-does-not-own-the-type</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/MisattributedOriginCorrectionStep.cs</code-under-test>
public class DapperMisattributedOriginCorrectionTests : PostgresTestBase {
  private const string ORDERS_TYPE = "App.Contracts.Orders.Placed, App.Contracts";

  [Test]
  public async Task AnEventUnderAnOriginThatDoesNotOwnItsType_IsReattributedByTheRegisteredStepAsync() {
    var owner = (Guid)TrackedGuid.New();
    var relay = (Guid)TrackedGuid.New();
    var stream = (Guid)TrackedGuid.New();
    var eventId = (Guid)TrackedGuid.New();
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    _ = await conn.ExecuteAsync("SELECT * FROM wh_integrity_record_domain_claims(@owner, 'orders-svc', NULL, ARRAY['app.contracts.orders'])", new { owner });
    _ = await conn.ExecuteAsync("""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, commit_sequence, created_at,
         origin_service_id, origin_commit_sequence)
      VALUES (@eventId, @stream, @stream, 'Thing', @type, jsonb_build_object('t', 'tenant-a'), 1, nextval('wh_commit_seq'),
              NOW(), @relay, 7);
      SELECT _wh_fold_stream_digests(ARRAY[@relay], ARRAY['tenant-a'], ARRAY[@type], ARRAY[@stream], ARRAY[@eventId], 'fold', NOW());
      """, new { eventId, stream, type = ORDERS_TYPE, relay });
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<MisattributedOriginCorrectionStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);

    var (origin, sequence) = await conn.QuerySingleAsync<(Guid Origin, long? Sequence)>(
      "SELECT origin_service_id, origin_commit_sequence FROM wh_event_store WHERE event_id = @eventId", new { eventId });
    await Assert.That(origin).IsEqualTo(owner);
    await Assert.That(sequence).IsNull();
    var lanes = (await conn.QueryAsync<Guid>(
      "SELECT origin_service_id FROM wh_stream_digests WHERE stream_id = @stream AND event_count > 0", new { stream })).ToList();
    await Assert.That(lanes).IsEquivalentTo([owner]).Because("the digest left the relay's bucket for the owner's");
  }
}
