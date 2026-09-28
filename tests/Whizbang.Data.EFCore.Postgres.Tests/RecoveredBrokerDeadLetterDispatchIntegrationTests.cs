using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A message that went to the broker's dead-letter queue, was imported into dead-letter custody and was
/// then recovered reaches its consumer (#934): a compile-time receptor, and a consumer registered at
/// runtime the way the saga watchdog tick router is.
/// </summary>
/// <remarks>
/// <para>
/// The import used to record the wire's envelope type name. Recovery re-emitted the row under that name,
/// and the inbox gate, asking whether anything consumes the envelope, found nothing and skipped the row
/// as RegistryChanged. Every recovered message was lost at the first step after recovery.
/// </para>
/// <para>
/// Real PostgreSQL for the import and the recovery; the real <see cref="InboxDispatchWorker"/> with the
/// production discard policy over the production registry query. Only the inbox channel is fed by hand:
/// the recovered row is read back and turned into a work item the way the inbox drain turns every row
/// into one, so the name the gate judges is the name recovery wrote.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Routing/MessageDiscardPolicy.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/WhizbangReceptorRegistryQueryAdapter.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard3")]
public class RecoveredBrokerDeadLetterDispatchIntegrationTests : EFCoreTestBase {

  /// <summary>A message with a compile-time receptor.</summary>
  public record RecoveredStaticEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>A message whose only consumer is registered at runtime.</summary>
  public record RecoveredRuntimeEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>The compile-time consumer, inline before the inbox commit so the commit signal orders it.</summary>
  [FireAt(LifecycleStage.PreInboxInline)]
  public sealed class RecoveredStaticReceptor : IReceptor<RecoveredStaticEvent> {
    public static TaskCompletionSource Reached { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask HandleAsync(RecoveredStaticEvent message, CancellationToken cancellationToken = default) {
      Reached.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// A consumer registered at runtime, as the saga tick router is. Generic so the source generator,
  /// which skips open generic types, does not also discover it as a compile-time receptor.
  /// </summary>
  private sealed class RuntimeConsumer<TMessage> : IReceptor<TMessage> where TMessage : IMessage {
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      Reached.TrySetResult();
      return ValueTask.CompletedTask;
    }
  }

  [Test]
  public async Task RecoveredBrokerDeadLetter_ReachesItsCompileTimeReceptorAsync() {
    RecoveredStaticReceptor.Reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var (provider, jsonOptions) = await _createServicesAsync();
    await using (provider) {
      var work = await _quarantineAndRecoverAsync(new RecoveredStaticEvent((Guid)TrackedGuid.New()), jsonOptions);

      await _dispatchAsync(provider, work);

      await Assert.That(RecoveredStaticReceptor.Reached.Task.IsCompleted).IsTrue()
        .Because($"a recovered message must reach its receptor; the inbox row was named '{work.MessageType}'");
    }
  }

  [Test]
  public async Task RecoveredBrokerDeadLetter_ReachesItsRuntimeRegisteredConsumerAsync() {
    var (provider, jsonOptions) = await _createServicesAsync();
    await using (provider) {
      var consumer = new RuntimeConsumer<RecoveredRuntimeEvent>();
      provider.GetRequiredService<IReceptorRegistry>().Register(consumer, LifecycleStage.PreInboxInline);
      var work = await _quarantineAndRecoverAsync(new RecoveredRuntimeEvent((Guid)TrackedGuid.New()), jsonOptions);

      await _dispatchAsync(provider, work);

      await Assert.That(consumer.Reached.Task.IsCompleted).IsTrue()
        .Because($"a consumer registered at runtime is still a consumer; the inbox row was named '{work.MessageType}'");
    }
  }

  /// <summary>
  /// Puts a message through dead-letter custody as the transport dead-letter drain does (the wire body
  /// verbatim, the wire's envelope type name), recovers it, and reads the recovered inbox row back as
  /// the inbox drain would hand it to dispatch.
  /// </summary>
  private async Task<InboxWork> _quarantineAndRecoverAsync<TEvent>(TEvent message, JsonSerializerOptions jsonOptions)
      where TEvent : IEvent {
    var messageId = (Guid)TrackedGuid.New();
    var envelopeTypeInfo = jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    var wireBody = JsonSerializer.Serialize(new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(message, jsonOptions),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.CreateVersion7(), ServiceName = "upstream-service", HostName = "upstream-host", ProcessId = 1,
        },
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a", UserId = "SYSTEM" }),
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
    }, envelopeTypeInfo);

    await using var ctx = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, jsonOptions);
    _ = await coordinator.ImportBrokerDeadLetterAsync(new BrokerDeadLetterImport(
      MessageId: messageId,
      StreamId: null,
      MessageType: EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(typeof(TEvent))),
      Destination: "topic/subscription",
      EnvelopeJson: wireBody,
      BrokerReason: "PoisonQuarantine",
      BrokerDescription: "first enqueued past the age threshold",
      EnqueuedAt: DateTimeOffset.UtcNow.AddHours(-2),
      DeliveryCount: 1));

    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using (var recover = conn.CreateCommand()) {
      recover.CommandText = "SELECT recover_dead_letter(dead_letter_id) FROM wh_dead_letters WHERE source_id = @id AND source_table = 'broker'";
      recover.Parameters.AddWithValue("id", messageId);
      await Assert.That((bool?)await recover.ExecuteScalarAsync()).IsTrue();
    }
    await using var read = conn.CreateCommand();
    read.CommandText = "SELECT message_type, event_data::text FROM wh_inbox WHERE message_id = @id";
    read.Parameters.AddWithValue("id", messageId);
    await using var reader = await read.ExecuteReaderAsync();
    await Assert.That(await reader.ReadAsync()).IsTrue()
      .Because("recovery re-emits a broker dead letter through the inbox");
    var messageType = reader.GetString(0);
    var envelope = (IMessageEnvelope<JsonElement>)JsonSerializer.Deserialize(reader.GetString(1), envelopeTypeInfo)!;
    return new InboxWork {
      MessageId = messageId,
      Envelope = envelope,
      MessageType = messageType,
      StreamId = messageId,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
    };
  }

  /// <summary>Runs one work item through the real inbox dispatch worker and waits for its commit.</summary>
  private static async Task _dispatchAsync(ServiceProvider provider, InboxWork work) {
    var runtimeRegistry = provider.GetRequiredService<IReceptorRegistry>();
    var registryQuery = new WhizbangReceptorRegistryQueryAdapter(runtimeRegistry);
    var inbox = new FakeInboxChannelWriter();
    var handlerCommit = new FakeHandlerCommitChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;

    var worker = new InboxDispatchWorker(
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: provider.GetRequiredService<IServiceInstanceProvider>(),
      inboxChannelWriter: inbox,
      handlerCommitChannel: handlerCommit,
      failureChannel: new FakeFailureChannel(),
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: provider.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      receptorRegistry: registryQuery,
      discardPolicy: new MessageDiscardPolicy(
        registryQuery, NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("test"),
        Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: runtimeRegistry,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      leaseRegistry: new LeaseRegistry());

    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);
      // The row's commit is enqueued after its pre-commit stage, and also when the gate skips it, so
      // this signal says the dispatch has finished either way.
      await handlerCommit.First.Task.WaitAsync(TimeSpan.FromSeconds(30));
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  private async Task<(ServiceProvider Provider, JsonSerializerOptions JsonOptions)> _createServicesAsync() {
    await base.SetupAsync();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  private sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) { }
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  private sealed class FakeHandlerCommitChannel : IInboxHandlerCommitChannel {
    public TaskCompletionSource<HandlerCommitRequest> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      First.TrySetResult(request);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }
}
