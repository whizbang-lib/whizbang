// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.EFCore.Postgres.Tests.OutboxStageSends;
using Whizbang.Data.EFCore.Postgres.Tests.OutboxStageSends.Owned.Commands;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1379 on the outbox side: a row leased to this instance sits in the real
/// <c>wh_outbox</c> of a real PostgreSQL database, the REAL <see cref="OutboxDrainWorker"/> fetches it
/// through <c>fetch_outbox_batch</c>, a receptor fires at a PreOutbox or PostOutbox stage, and it sends
/// a command the service owns, handled here by a receptor that returns no result. The command must be
/// handled exactly once, in-process, and never written to the outbox. Before the fix SendAsync
/// answered Accepted and dropped the command from every stage alike.
/// </summary>
/// <remarks>
/// <para>
/// Owned domains are configured the way a deployed service configures them. With them set, the
/// receptor invoker skips PreOutbox for owned commands and for events the service does not own, so
/// the trigger is an owned event; and it skips PreOutbox for an envelope dispatched with the
/// local-dispatch flag, so the seeded envelope carries an outbox-only mode.
/// </para>
/// <para>
/// The trigger row is seeded by SQL and leased to the worker's instance, the way the claim leaves a
/// row for the drainer: <c>fetch_outbox_batch</c> returns only rows whose <c>instance_id</c> is the
/// caller's and whose lease has not expired. Only the transport is faked: the publish strategy
/// reports success. Each stage runs through both publish paths, one row at a time and bulk.
/// Detached stages run on a fresh task and scope, so every assertion waits on the owned command's own
/// completion signal and on the trigger row's completion, never on time.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class OutboxStageOwnedCommandSendIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.OutboxStageSends.Owned";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(LifecycleStage.PreOutboxInline, false)]
  [Arguments(LifecycleStage.PreOutboxInline, true)]
  [Arguments(LifecycleStage.PreOutboxDetached, false)]
  [Arguments(LifecycleStage.PreOutboxDetached, true)]
  [Arguments(LifecycleStage.PostOutboxInline, false)]
  [Arguments(LifecycleStage.PostOutboxInline, true)]
  [Arguments(LifecycleStage.PostOutboxDetached, false)]
  [Arguments(LifecycleStage.PostOutboxDetached, true)]
  public async Task ReceptorAtOutboxStage_SendsOwnedVoidCommand_HandledOnceAndNeverInTheOutboxAsync(
      LifecycleStage stage, bool bulkPublish) {
    var trigger = OutboxStageTriggers.For(stage);
    var id = OutboxStageTriggers.IdOf(trigger);
    var handled = OutboxStageSendProbe.Expect(id);

    var (serviceProvider, jsonOptions) = _createServices();
    var instance = serviceProvider.GetRequiredService<IServiceInstanceProvider>();
    var triggerMessageId = await _seedLeasedTriggerRowAsync(trigger, id, instance, jsonOptions);

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new SignalingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var publish = new SucceedingPublishStrategy(bulkPublish);
    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 };
    // The registry query and the runtime registry are resolved the way the host resolves them, so
    // the worker's stage gate reads the generated discovery of the [FireAt] hook, not a stub.
    var worker = new OutboxDrainWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: instance,
      drainChannel: drainChannel,
      completionChannel: completion,
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(options),
      jsonOptions: jsonOptions,
      logger: NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: publish,
      lifecycleMessageDeserializer: serviceProvider.GetRequiredService<ILifecycleMessageDeserializer>(),
      receptorRegistry: serviceProvider.GetRequiredService<IReceptorRegistryQuery>(),
      runtimeReceptorRegistry: serviceProvider.GetRequiredService<IReceptorRegistry>(),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: OutboxDrainWorker.CreateDefaultGovernor(options),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()));

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await drainChannel.WriteAsync(id, cts.Token);

      // The owned command's receptor completing is the transition under test; a send the
      // dispatcher dropped or threw on never completes it (the hook reports a throw as a fault)
      await handled.Task.WaitAsync(_signalTimeout);
      // The trigger row's completion: every inline stage for it has run by now
      await completion.WaitForAsync(triggerMessageId).WaitAsync(_signalTimeout);

      await Assert.That(OutboxStageSendProbe.CountFor(id)).IsEqualTo(1)
        .Because("the owned command must be handled exactly once, in-process");

      await using var dbContext = CreateDbContext();
      var commandType = TypeNameFormatter.AssemblyQualifiedName(typeof(OutboxStageOwnedCommand));
      var outboxCopies = await dbContext.Outbox.CountAsync(m => m.MessageType == commandType);
      await Assert.That(outboxCopies).IsEqualTo(0)
        .Because("a command the service owns and handles never goes to the outbox");
      await Assert.That(publish.PublishedIds).Contains(triggerMessageId)
        .Because("the trigger row itself must have gone through the worker's publish path");
      await Assert.That(failure.All).IsEmpty()
        .Because("no stage hook may fault the trigger row");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  /// <summary>
  /// Writes the trigger row into <c>wh_outbox</c> leased to this instance, plus the instance's
  /// <c>wh_service_instances</c> row. The envelope carries a transport destination (stage hooks never
  /// fire for event-store-only rows) and an outbox-only dispatch mode.
  /// </summary>
  private async Task<Guid> _seedLeasedTriggerRowAsync(
      object trigger, Guid streamId, IServiceInstanceProvider instance, JsonSerializerOptions jsonOptions) {
    var messageId = (Guid)TrackedGuid.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonSerializer.SerializeToElement(trigger, jsonOptions.GetTypeInfo(trigger.GetType())),
      Hops = [new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceInstance = instance.ToInfo(),
        CorrelationId = CorrelationId.New(),
        CausationId = MessageId.New(),
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-outbox-stage-sends", UserId = "user-outbox-stage-sends" })
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local }
    };
    var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var messageType = TypeNameFormatter.AssemblyQualifiedName(trigger.GetType());

    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();

    await using (var insertInstance = connection.CreateCommand()) {
      insertInstance.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, @service, @host, @pid, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
        """;
      insertInstance.Parameters.AddWithValue("id", instance.InstanceId);
      insertInstance.Parameters.AddWithValue("service", instance.ServiceName);
      insertInstance.Parameters.AddWithValue("host", instance.HostName);
      insertInstance.Parameters.AddWithValue("pid", instance.ProcessId);
      await insertInstance.ExecuteNonQueryAsync();
    }

    await using (var insertRow = connection.CreateCommand()) {
      insertRow.CommandText = """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope, status, attempts,
           created_at, stream_id, partition_number, instance_id, lease_expiry, is_event)
        VALUES (@msg, 'outbox-stage-sends-topic', @type, @envelopeType, @data, '{}'::jsonb, NULL,
                1, 0, NOW(), @stream, 0, @inst, NOW() + INTERVAL '5 minutes', true)
        """;
      insertRow.Parameters.AddWithValue("msg", messageId);
      insertRow.Parameters.AddWithValue("type", messageType);
      insertRow.Parameters.AddWithValue("envelopeType", EnvelopeTypeNameHelper.Format(messageType));
      insertRow.Parameters.AddWithValue("data", NpgsqlDbType.Jsonb, envelopeJson);
      insertRow.Parameters.AddWithValue("stream", streamId);
      insertRow.Parameters.AddWithValue("inst", instance.InstanceId);
      await insertRow.ExecuteNonQueryAsync();
    }

    return messageId;
  }

  private (ServiceProvider Provider, JsonSerializerOptions JsonOptions) _createServices() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());
    services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));

    // The real strategy against the real database: were the owned command routed to the outbox, the
    // row would land in wh_outbox and the count below would see it
    services.AddScoped<IWorkCoordinatorStrategy>(sp =>
      new ScopedWorkCoordinatorStrategy(
        coordinator: sp.GetRequiredService<IWorkCoordinator>(),
        instanceProvider: sp.GetRequiredService<IServiceInstanceProvider>(),
        workChannelWriter: null,
        options: new WorkCoordinatorOptions { LeaseSeconds = 30, AbandonStaleInstanceThresholdSeconds = 300, PartitionCount = 4 },
        logger: sp.GetRequiredService<ILogger<ScopedWorkCoordinatorStrategy>>(),
        inboxChannelWriter: new InboxChannelWriter()));

    services.AddReceptors();
    services.AddWhizbangDispatcher();
    services.AddWhizbangLifecycleMessageDeserializer();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    return (services.BuildServiceProvider(), jsonOptions);
  }

  // --- fakes (transport edge and worker channels only) ---

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  /// <summary>Signals per message id when the worker enqueues its completion.</summary>
  private sealed class SignalingCompletionChannel : IOutboxCompletionChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completed = new();

    public Task WaitForAsync(Guid messageId) => _signal(messageId).Task;

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      _signal(outboxMessageId).TrySetResult();
      return ValueTask.CompletedTask;
    }

    private TaskCompletionSource _signal(Guid messageId) =>
      _completed.GetOrAdd(messageId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  private sealed class RecordingFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Reports every publish as successful; optionally takes the bulk path.</summary>
  private sealed class SucceedingPublishStrategy(bool supportsBulkPublish) : IMessagePublishStrategy {
    public ConcurrentBag<Guid> PublishedIds { get; } = [];
    public bool SupportsBulkPublish => supportsBulkPublish;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      Task.FromResult(_succeed(work));

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessagePublishResult>>([.. workItems.Select(_succeed)]);

    private MessagePublishResult _succeed(OutboxWork work) {
      PublishedIds.Add(work.MessageId);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      };
    }
  }
}
