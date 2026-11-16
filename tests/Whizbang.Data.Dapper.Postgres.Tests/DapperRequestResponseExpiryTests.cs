// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2359: a request/response record past its own expiry is removed by the maintenance cycle. The store's expiry existed,
/// but nothing called it, so the table kept every record it was given.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RequestResponseExpiryStep.cs</code-under-test>
/// <docs>operations/infrastructure/operational-retention#what-the-sweep-removes</docs>
public class DapperRequestResponseExpiryTests : PostgresTestBase {
  [Test]
  public async Task AnExpiredRecord_IsRemovedByTheMaintenanceStep_AndALiveOneStaysAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await conn.ExecuteAsync("""
      INSERT INTO wh_request_response (request_id, correlation_id, request_type, request_data, status, created_at, expires_at)
      VALUES (@expired, @expiredCorrelation, 'Request', '{}'::jsonb, 'Pending', NOW() - INTERVAL '2 days', NOW() - INTERVAL '1 day'),
             (@live, @liveCorrelation, 'Request', '{}'::jsonb, 'Pending', NOW(), NOW() + INTERVAL '1 day')
      """, new {
      expired = TrackedGuid.New().Value,
      expiredCorrelation = TrackedGuid.New().Value,
      live = TrackedGuid.New().Value,
      liveCorrelation = TrackedGuid.New().Value,
    });
    var services = new ServiceCollection().AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), false, (string?)null, null);
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var step = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<RequestResponseExpiryStep>().Single();

    await step.RunAsync(scope.ServiceProvider, CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("request-response-expiry");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_request_response")).IsEqualTo(1L)
      .Because("the expired record goes and the live one stays");
  }

  /// <summary>A host with no request/response store has nothing to expire.</summary>
  [Test]
  public async Task WithoutAStore_TheStepDoesNothingAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await new RequestResponseExpiryStep().RunAsync(provider, CancellationToken.None);

    await Assert.That(() => new RequestResponseExpiryStep().RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }
}
