// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
using Whizbang.Data.EFCore.Postgres.Tests.AbsorbedContracts;

namespace Whizbang.Data.EFCore.Postgres.Tests.AbsorbedContracts {
  /// <summary>An event of another service's contract namespace that the receiving service has no consumer for.</summary>
  public sealed record AbsorbedShipmentNoted([property: StreamId] Guid ShipmentId, string Note) : IEvent;
}

namespace Whizbang.Data.EFCore.Postgres.Tests {
  /// <summary>
  /// An event in an absorbed namespace reaches the event store even when nothing on the receiving service consumes it,
  /// so a perspective added later can rebuild from it (#1776).
  /// </summary>
  /// <remarks>
  /// The consumer worker decided "no local consumer" from its receptor registries alone, so such an event was stored
  /// settled with <see cref="InboxReceiveDiscard.NO_LOCAL_CONSUMER"/> and the claim never copied it into
  /// <c>wh_event_store</c>. Runs the real path: the transport consumer worker's receive gate, <c>store_inbox_messages</c>
  /// through the EF Core work coordinator, and the claim's inbox emit chain. The non-absorbed case is the control that
  /// keeps the assertion from passing because every event is copied.
  /// </remarks>
  /// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
  /// <code-under-test>src/Whizbang.Core/Workers/ReceiveConsumerGate.cs</code-under-test>
  /// <docs>fundamentals/dispatcher/routing#owned-and-subscribed</docs>
  [Category("Integration")]
  [NotInParallel("EFCorePostgresTests")]
  [Category("Shard3")]
  public class AbsorbedEventReachesEventStoreTests : EFCoreTestBase {
    private const string SERVICE = "absorb-svc";

    [Test]
    public async Task AnUnconsumedEventInAnAbsorbedNamespace_IsCopiedIntoTheEventStoreAsync() {
      var messageId = await _receiveAndClaimAsync(absorb: typeof(AbsorbedShipmentNoted).Namespace!);

      await Assert.That(await _scalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @id)", messageId))
        .IsTrue().Because("an absorbed event is persisted locally even with no consumer, so a later perspective can rebuild from it");
      await Assert.That(await _scalarAsync<bool>(
          "SELECT error IS NULL OR error NOT LIKE 'discarded on receive%' FROM wh_inbox_state WHERE message_id = @id", messageId))
        .IsTrue().Because("the row is stored live, not settled as having no local consumer");
    }

    [Test]
    public async Task AnUnconsumedEventOutsideTheAbsorbedNamespaces_IsNotCopiedAsync() {
      var messageId = await _receiveAndClaimAsync(absorb: "Some.Other.Contracts");

      await Assert.That(await _scalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM wh_event_store WHERE event_id = @id)", messageId))
        .IsFalse().Because("an event nothing here consumes, in a namespace not absorbed, is stored settled and never copied");
      await Assert.That(await _scalarAsync<string>("SELECT error FROM wh_inbox_state WHERE message_id = @id", messageId))
        .IsEqualTo("discarded on receive: " + InboxReceiveDiscard.NO_LOCAL_CONSUMER);
    }

    // ===== Helpers =====

    /// <summary>
    /// Delivers one event with no local consumer through the transport consumer worker of a service whose routing absorbs
    /// <paramref name="absorb"/>, then runs the claim the way the claim worker does. Returns the event's id.
    /// </summary>
    private async Task<Guid> _receiveAndClaimAsync(string absorb) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var routing = new RoutingOptions().AbsorbNamespaces(absorb);
      var noConsumer = new NoConsumerRegistry();
      var services = new ServiceCollection();
      services.TryAddWhizbangDefaults();
      services.AddScoped(_ => CreateDbContext());
      services.AddScoped<IWorkCoordinator>(sp =>
        new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
      services.AddSingleton<IEventTypeProvider>(new EventTypes(typeof(AbsorbedShipmentNoted)));
      services.AddWhizbangMessageSecurity(opts => opts.AllowAnonymous = true);
      // The shared discard policy, registered the way the worker pipeline registers it.
      services.AddSingleton<IMessageDiscardPolicy>(new MessageDiscardPolicy(
        noConsumer, NullLogger<MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("Whizbang.Tests.AbsorbedEventReachesEventStoreTests"), Options.Create(routing),
        new EventMarkerResolver(NullMessageTypeCatalog.Instance)));
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
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        routingOptions: Options.Create(routing),
        workChannelWriter: new WorkChannelWriter(),
        claimWorkerOptions: Options.Create(new ClaimWorkerOptions()),
        receptorRegistry: noConsumer,
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
      var envelopeType = EnvelopeTypeNameHelper.Format(
        typeof(AbsorbedShipmentNoted).FullName + ", " + typeof(AbsorbedShipmentNoted).Assembly.GetName().Name);

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

    /// <summary>Registers an instance and runs <c>claim_work</c> once, which copies claimed inbox events into the event store.</summary>
    private async Task _claimAsync() {
      var instanceId = (Guid)TrackedGuid.New();
      await using var connection = new NpgsqlConnection(ConnectionString);
      await connection.OpenAsync();
      await using (var register = connection.CreateCommand()) {
        register.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, @svc, 'absorb-host', 1, NOW(), NOW(), '{}'::jsonb)
          """;
        register.Parameters.AddWithValue("id", instanceId);
        register.Parameters.AddWithValue("svc", SERVICE);
        await register.ExecuteNonQueryAsync();
      }
      await using var claim = connection.CreateCommand();
      claim.CommandText = "SELECT * FROM claim_work(@inst, @svc, 'absorb-host', 100, 100, 100, 100)";
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

    private sealed class NoConsumerRegistry : IReceptorRegistryQuery {
      public bool HasReceptors(LifecycleStage stage, string messageType) => false;
      public bool HasInboxHandler(string messageType) => false;
      public bool HasAnyConsumer(string messageType) => false;
    }

    private sealed class EventTypes(params Type[] types) : IEventTypeProvider {
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
