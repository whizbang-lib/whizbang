// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Resilience;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.ConsumedContracts;

namespace Whizbang.Data.EFCore.Postgres.Tests.ConsumedContracts {
  /// <summary>An event the receiving service consumes with a receptor only.</summary>
  public sealed record ReceptorOnlyShipmentNoted([property: StreamId] Guid ShipmentId, string Note) : IEvent;

  /// <summary>An event a perspective of the receiving service folds.</summary>
  public sealed record FoldedShipmentNoted([property: StreamId] Guid ShipmentId) : IEvent;
}

namespace Whizbang.Data.EFCore.Postgres.Tests {
  /// <summary>
  /// A consumed event reaches the event store whether or not a perspective folds it (#2064). The receive path classified
  /// an event by the perspective event list, so a receptor-only event in a service with perspectives, and every event in a
  /// service with none (a transport payload arrives as JSON, so its own marker cannot say), was stored as a non-event and
  /// never copied into <c>wh_event_store</c>. Runs the real path: the transport consumer worker, <c>store_inbox_messages</c>
  /// through the EF Core work coordinator, and the claim's inbox emit chain.
  /// </summary>
  /// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
  /// <docs>extending/internals/receptor-registry-query#what-a-service-consumes</docs>
  [Category("Integration")]
  [NotInParallel("EFCorePostgresTests")]
  [Category("Shard3")]
  public class ConsumedEventReachesEventStoreTests : EFCoreTestBase {
    private const string SERVICE = "consumed-svc";

    [Test]
    public async Task WithAPerspectiveOnAnotherEvent_AReceptorOnlyEvent_IsCopiedIntoTheEventStoreAsync() {
      var messageId = await _receiveAndClaimAsync(perspectiveEvents: [typeof(FoldedShipmentNoted)]);

      await Assert.That(await _scalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @id)", messageId))
        .IsTrue().Because("the event is consumed here, so it is stored here, whether or not a perspective folds it");
    }

    [Test]
    public async Task WithNoPerspectives_AConsumedEvent_IsCopiedIntoTheEventStoreAsync() {
      var messageId = await _receiveAndClaimAsync(perspectiveEvents: null);

      await Assert.That(await _scalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @id)", messageId))
        .IsTrue().Because("a service with no perspectives still stores the events its receptors consume");
    }

    // ===== Helpers =====

    /// <summary>
    /// Delivers one <see cref="ReceptorOnlyShipmentNoted"/> (consumed by a receptor) through the transport consumer
    /// worker, then runs the claim the way the claim worker does. Returns the event's id.
    /// </summary>
    private async Task<Guid> _receiveAndClaimAsync(Type[]? perspectiveEvents) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var consumed = new ConsumesReceptorOnlyEvent();
      var services = new ServiceCollection();
      services.AddSingleton<IReceptorRegistryQuery>(consumed);
      services.TryAddWhizbangDefaults();
      services.AddScoped(_ => CreateDbContext());
      services.AddScoped<IWorkCoordinator>(sp =>
        new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
      if (perspectiveEvents is not null) {
        services.AddSingleton<IEventTypeProvider>(new EventTypes(perspectiveEvents));
      }
      services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
      await using var provider = services.BuildServiceProvider();

      var transport = new HandingTransport();
      var options = new TransportConsumerOptions();
      options.Destinations.Add(new TransportDestination("shipping-topic"));
      var worker = new TransportConsumerWorker(
        transport: transport,
        options: options,
        resilienceOptions: new SubscriptionResilienceOptions(),
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        jsonOptions: jsonOptions,
        orderedProcessor: new OrderedStreamProcessor(logger: NullLogger<OrderedStreamProcessor>.Instance, parallelizeStreams: false),
        metrics: null,
        logger: NullLogger<TransportConsumerWorker>.Instance,
        serviceInstanceProvider: new ServiceInstanceProvider((Guid)TrackedGuid.New(), SERVICE, "host", 1),
        routingOptions: Options.Create(new RoutingOptions()),
        workChannelWriter: new WorkChannelWriter(),
        claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
        receptorRegistry: consumed,
        runtimeReceptorRegistry: NullReceptorRegistry.Instance,
        ephemeralModeResolver: new EphemeralModeResolver(NullMessageTypeCatalog.Instance),
        eventMarkerResolver: new EventMarkerResolver(NullMessageTypeCatalog.Instance),
        controlClass: Options.Create(new ControlClassOptions()));

      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(TrackedGuid.New()),
        Payload = JsonElement.Parse("{\"note\":\"shipped\"}"),
        Hops = [new MessageHop {
          Type = HopType.Current,
          Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "upstream-svc", InstanceId = (Guid)TrackedGuid.New(), HostName = "host", ProcessId = 1,
          },
        }],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      };
      var envelopeType = EnvelopeTypeNameHelper.Format(_receptorOnlyType);

      using var cts = new CancellationTokenSource();
      await worker.StartAsync(cts.Token);
      try {
        await transport.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));
        await transport.HandAsync([new TransportMessage(envelope, envelopeType)]);
      } finally {
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
      }

      await _claimAsync();
      return envelope.MessageId.Value;
    }

    private static readonly string _receptorOnlyType =
      typeof(ReceptorOnlyShipmentNoted).FullName + ", " + typeof(ReceptorOnlyShipmentNoted).Assembly.GetName().Name;

    /// <summary>Registers an instance and runs <c>claim_work</c> once, which copies claimed inbox events into the event store.</summary>
    private async Task _claimAsync() {
      var instanceId = (Guid)TrackedGuid.New();
      await using var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync();
      await using (var register = connection.CreateCommand()) {
        register.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, @svc, 'consumed-host', 1, NOW(), NOW(), '{}'::jsonb)
          """;
        register.Parameters.AddWithValue("id", instanceId);
        register.Parameters.AddWithValue("svc", SERVICE);
        await register.ExecuteNonQueryAsync();
      }
      await using var claim = connection.CreateCommand();
      claim.CommandText = "SELECT * FROM claim_work(@inst, @svc, 'consumed-host', 100, 100, 100, 100)";
      claim.Parameters.AddWithValue("inst", instanceId);
      claim.Parameters.AddWithValue("svc", SERVICE);
      await using var reader = await claim.ExecuteReaderAsync();
      while (await reader.ReadAsync()) {
        // drain
      }
    }

    private async Task<T> _scalarAsync<T>(string sql, Guid id) {
      await using var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = sql;
      command.Parameters.AddWithValue(nameof(id), id);
      return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The consumer registry: a receptor consumes <see cref="ReceptorOnlyShipmentNoted"/>.</summary>
    private sealed class ConsumesReceptorOnlyEvent : IReceptorRegistryQuery {
      public bool HasReceptors(LifecycleStage stage, string messageType) => false;
      public bool HasInboxHandler(string messageType) => HasAnyConsumer(messageType);
      public bool HasAnyConsumer(string messageType) =>
        EventTypeMatchingHelper.NormalizeTypeName(messageType) == _receptorOnlyType;
      public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => [new ConsumedMessageInfo(_receptorOnlyType, IsEvent: true)];
    }

    private sealed class EventTypes(Type[] types) : IEventTypeProvider {
      public IReadOnlyList<Type> GetEventTypes() => types;
    }

    private sealed class HandingTransport : ITransport {
      private Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? _handler;
      private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
      public Task Subscribed => _subscribed.Task;
      public bool IsInitialized => true;
      public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe | TransportCapabilities.Reliable;
      public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
      public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null,
          ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
      public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
          TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) {
        _handler = batchHandler;
        _subscribed.TrySetResult();
        return Task.FromResult<ISubscription>(new Subscription());
      }
      public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination,
          CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull =>
        throw new NotSupportedException();
      public Task HandAsync(IReadOnlyList<TransportMessage> batch) => _handler!(batch, CancellationToken.None);

      private sealed class Subscription : ISubscription {
        public bool IsActive => true;
#pragma warning disable CS0067
        public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public void Dispose() { }
      }
    }
  }
}
