// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.DispatchExecution.Upstream;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Perspective sync tracks the id the event store actually wrote (#1465). The real EF Core store,
/// behind the decorators the driver registers, on Postgres.
/// </summary>
/// <code-under-test>src/Whizbang.Core/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/SyncTrackingEventStoreDecorator.cs</code-under-test>
/// <docs>fundamentals/perspectives/perspective-sync</docs>
public class EventStoreSyncTrackingIntegrationTests : EFCoreTestBase {
  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task AppendingAMessage_TracksTheIdTheStoreWroteAsync(bool envelopeRegistered) {
    await using var provider = _provider();
    await using var scope = provider.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
    var tracker = scope.ServiceProvider.GetRequiredService<IScopedEventTracker>();
    var streamId = Guid.CreateVersion7();
    var evt = new DxEvent(streamId);
    if (envelopeRegistered) {
      // As a dispatch registers it for the receptor that is handling the message
      provider.GetRequiredService<IEnvelopeRegistry>().Register(new MessageEnvelope<DxEvent> {
        MessageId = MessageId.New(),
        Payload = evt,
        Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UnixEpoch }],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
      });
    }

    await store.AppendAsync(streamId, evt);

    var stored = await _storedEventIdsAsync(streamId);
    await Assert.That(stored).Count().IsEqualTo(1);
    var tracked = tracker.GetEmittedEvents().Where(e => e.StreamId == streamId).Select(e => e.EventId).ToList();
    await Assert.That(tracked).IsEquivalentTo(stored)
      .Because("a perspective sync waits on the tracked id, so it must be the id the store wrote, or the wait can only time out");
  }

  private ServiceProvider _provider() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IEventStore>(sp => new EFCoreEventStore<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    // As the Postgres driver does
    services.DecorateEventStoreWithSyncTracking();
    return services.BuildServiceProvider();
  }

  private async Task<List<Guid>> _storedEventIdsAsync(Guid streamId) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var select = connection.CreateCommand();
    select.CommandText = "SELECT event_id FROM wh_event_store WHERE stream_id = @s";
    select.Parameters.AddWithValue("s", streamId);
    var ids = new List<Guid>();
    await using var reader = await select.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      ids.Add(reader.GetGuid(0));
    }
    return ids;
  }
}
