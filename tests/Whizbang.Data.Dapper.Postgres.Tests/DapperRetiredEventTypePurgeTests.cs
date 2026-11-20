// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2329 on the Dapper driver: the turnkey registration carries the retired-type purge step, and a type named retired is
/// purged from a stream it shares with a type that stays, through this driver's own append path.
/// </summary>
/// <docs>fundamentals/events/retired-event-types</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/RetiredEventTypePurgeStep.cs</code-under-test>
public class DapperRetiredEventTypePurgeTests : PostgresTestBase {
  [Test]
  public async Task ARetiredType_IsPurgedFromASharedStream_ByTheRegisteredStepAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stream = (Guid)TrackedGuid.New();
    await using (var writer = _provider(retired: null)) {
      await using var scope = writer.CreateAsyncScope();
      var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
      await store.AppendAsync(stream, new DapperEphemeralHistoryProbePinged { SessionId = stream });
      await store.AppendAsync(stream, new DapperEphemeralHistoryProbeCreated { SessionId = stream });
      await store.AppendAsync(stream, new DapperEphemeralHistoryProbePinged { SessionId = stream });
    }
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    var retired = TypeNameFormatter.FormatClrTypeName(typeof(DapperEphemeralHistoryProbeCreated));
    await using var provider = _provider(retired);
    await using var host = provider.CreateAsyncScope();

    var step = host.ServiceProvider.GetServices<IMaintenanceStep>().OfType<RetiredEventTypePurgeStep>().Single();
    await step.RunAsync(host.ServiceProvider, CancellationToken.None);

    await Assert.That(await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @stream AND event_type = @type",
      new { stream, type = TypeNameFormatter.Format(typeof(DapperEphemeralHistoryProbeCreated)) })).IsEqualTo(0L)
      .Because("the registered step purged the retired type by its name, without its class in the catalog");
    await Assert.That(await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM wh_event_store WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(2L).Because("the type that shares the stream stays");
  }

  private ServiceProvider _provider(string? retired) {
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    var options = new RetiredEventTypesOptions();
    if (retired is not null) {
      options.Names.Add(retired);
    }
    services.AddSingleton(Options.Create(options));
    return services.BuildServiceProvider();
  }
}
