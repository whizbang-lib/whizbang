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
using Whizbang.Core.Attributes;
using Whizbang.Core.Configuration;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2206: on the Dapper driver, as on EF Core, the history a type stored before it was declared [Ephemeral] is
/// reclassified by the registered maintenance step, not by startup.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#making-an-existing-type-ephemeral</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/EphemeralHistoryReclassifyStep.cs</code-under-test>
public class DapperEphemeralHistoryReclassifyTests : PostgresTestBase {
  private const string TYPE = "Contracts.Sessions.DapperSessionPinged, Contracts";

  [Test]
  public async Task HistoryStoredBeforeTheDeclaration_IsReclassifiedByTheRegisteredStepAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var stream = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    _ = await conn.ExecuteAsync(
      "SELECT wh_append_event(@id, @stream, @stream, 'Probe', @type, '{}'::jsonb, @meta::jsonb, NULL, NOW(), 0)",
      new { id, stream, type = TYPE, meta = $$"""{"MessageId":"{{id}}","Hops":[]}""" });
    _ = await conn.ExecuteAsync("SELECT stamp_pending_commit_sequences(1000)");
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    services.AddSingleton(Options.Create(new EphemeralOptions { ReconcileHistoricalOnStartup = true }));
    services.AddSingleton<IMessageTypeCatalog>(new Catalog(new MessageTypeCatalogEntry(typeof(object), TYPE, "event", null) {
      Ephemeral = new EphemeralInfo(default, default),
    }));
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<EphemeralHistoryReclassifyStep>().ToList();
    await steps.Single().RunAsync(scope.ServiceProvider, CancellationToken.None);

    var flags = await conn.ExecuteScalarAsync<int>("SELECT flags FROM wh_event_store WHERE event_id = @id", new { id });
    await Assert.That(flags & 8).IsEqualTo(8).Because("the registered step reclassified the history in the maintenance cycle");
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
