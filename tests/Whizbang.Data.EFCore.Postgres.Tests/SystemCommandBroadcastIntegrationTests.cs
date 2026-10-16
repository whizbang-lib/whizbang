// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
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
using Whizbang.Core.Commands.System;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End to end for #1385: a framework system command sent with <c>IDispatcher.SendAsync</c> is
/// broadcast through the REAL outbox in a real PostgreSQL database, and the sending service runs its
/// handler exactly once. The handler is registered the way the framework's registrars register theirs,
/// one instance at <see cref="LifecycleStage.LocalImmediateInline"/> and at
/// <see cref="LifecycleStage.PreOutboxInline"/>. The send runs it in-process; the outbox row then
/// reaches the REAL <see cref="OutboxDrainWorker"/>, whose PreOutbox stage must recognize the
/// locally-dispatched copy and not run it again. What this proves that a component test cannot: the
/// local-dispatch flag survives serialization into <c>wh_outbox</c> and back.
/// </summary>
/// <remarks>
/// Only the transport is faked: the publish strategy reports success and records what it published,
/// which is the broadcast. The row the send wrote is leased to this instance the way the claim leaves
/// it for the drainer. Every assertion waits on the row's completion signal, never on time.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/ReceptorInvoker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class SystemCommandBroadcastIntegrationTests : EFCoreTestBase {
  private const string OWNED_DOMAIN = "Whizbang.Data.EFCore.Postgres.Tests.SomeOwnedDomain";
  private static readonly TimeSpan _signalTimeout = TimeSpan.FromSeconds(30);

  [Test]
  [Arguments(false, false)]
  [Arguments(false, true)]
  [Arguments(true, false)]
  [Arguments(true, true)]
  public async Task SendAsync_SystemCommandHandledLocally_IsBroadcastAndRunsOnceOnTheSenderAsync(bool withOwnedDomain, bool bulkPublish) {
    var probe = new CountingReceptor<ClearCacheCommand>();
    var (serviceProvider, jsonOptions) = _createServices(withOwnedDomain);
    var registry = serviceProvider.GetRequiredService<IReceptorRegistry>();
    registry.Register<ClearCacheCommand>(probe, LifecycleStage.LocalImmediateInline);
    registry.Register<ClearCacheCommand>(probe, LifecycleStage.PreOutboxInline);

    var cacheKey = "cache-" + Guid.CreateVersion7();
    // A security context on the envelope: the drain worker runs no stage for a row without one
    await serviceProvider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-system-broadcast")
      .SendAsync(new ClearCacheCommand(CacheKey: cacheKey));

    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("the sending service handles its own copy in-process before SendAsync returns");

    var published = await _drainTheSentRowAsync(serviceProvider, jsonOptions, bulkPublish);

    await Assert.That(published.Count).IsEqualTo(1)
      .Because("a system command is broadcast even when the sender handled it");
    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("the outbox copy is flagged as locally dispatched, so PreOutbox must not run the handler again");
  }

  [Test]
  [Arguments(false)]
  [Arguments(true)]
  public async Task SendAsync_SystemCommandWithOnlyAPreOutboxHandler_RunsItAtPreOutboxOnceAsync(bool bulkPublish) {
    var probe = new CountingReceptor<ClearCacheCommand>();
    var (serviceProvider, jsonOptions) = _createServices(withOwnedDomain: false);
    serviceProvider.GetRequiredService<IReceptorRegistry>().Register<ClearCacheCommand>(probe, LifecycleStage.PreOutboxInline);

    var cacheKey = "cache-" + Guid.CreateVersion7();
    // A security context on the envelope: the drain worker runs no stage for a row without one
    await serviceProvider.GetRequiredService<IDispatcher>().AsSystem().ForTenant("tenant-system-broadcast")
      .SendAsync(new ClearCacheCommand(CacheKey: cacheKey));

    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(0)
      .Because("nothing handles the command locally, so the send only queues it");

    var published = await _drainTheSentRowAsync(serviceProvider, jsonOptions, bulkPublish);

    await Assert.That(published.Count).IsEqualTo(1);
    await Assert.That(probe.CountFor(cacheKey)).IsEqualTo(1)
      .Because("an outbox copy nothing dispatched locally is where the sender's own handler runs");
  }

  /// <summary>
  /// Leases the row the send wrote to this instance, runs the real drain worker over it, and waits
  /// for the row's completion. Returns the ids the fake transport published.
  /// </summary>
  private async Task<IReadOnlyCollection<Guid>> _drainTheSentRowAsync(
      ServiceProvider serviceProvider, System.Text.Json.JsonSerializerOptions jsonOptions, bool bulkPublish) {
    var instance = serviceProvider.GetRequiredService<IServiceInstanceProvider>();
    var (messageId, streamId) = await _leaseTheSentRowAsync(instance);

    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new SignalingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var publish = new SucceedingPublishStrategy(bulkPublish);
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 };
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
      governor: OutboxDrainWorker.CreateDefaultGovernor(options));

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await drainChannel.WriteAsync(streamId, cts.Token);
      // The row's completion: its inline PreOutbox stage has run and it was published
      await completion.WaitForAsync(messageId).WaitAsync(_signalTimeout);
      await Assert.That(failure.All).IsEmpty()
        .Because("the row must reach its stages; a failure here (a missing security context, say) means nothing was tested");
      return [.. publish.PublishedIds];
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  /// <summary>
  /// The send wrote exactly one row. Lease it to this instance (and register the instance), the way
  /// the claim leaves a row for the drainer, and return its message and stream ids.
  /// </summary>
  private async Task<(Guid MessageId, Guid StreamId)> _leaseTheSentRowAsync(IServiceInstanceProvider instance) {
    await using var dbContext = CreateDbContext();
    var commandType = TypeNameFormatter.AssemblyQualifiedName(typeof(ClearCacheCommand));
    var row = await dbContext.Outbox.AsNoTracking().SingleAsync(m => m.MessageType == commandType);

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
    await using (var lease = connection.CreateCommand()) {
      lease.CommandText = """
        UPDATE wh_outbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes', status = 1, attempts = 0
        WHERE message_id = @msg
        """;
      lease.Parameters.AddWithValue("inst", instance.InstanceId);
      lease.Parameters.AddWithValue("msg", row.MessageId);
      await lease.ExecuteNonQueryAsync();
    }
    return (row.MessageId, row.StreamId ?? Guid.Empty);
  }

  private (ServiceProvider Provider, System.Text.Json.JsonSerializerOptions JsonOptions) _createServices(bool withOwnedDomain) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()));
    services.AddScoped(_ => CreateDbContext());
    if (withOwnedDomain) {
      services.Configure<RoutingOptions>(options => options.OwnDomains(OWNED_DOMAIN));
    }

    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();
    services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

    services.AddScoped<IWorkCoordinator>(sp =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
        sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
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

  /// <summary>Counts invocations per cache key. Generic so the source generator does not discover it.</summary>
  private sealed class CountingReceptor<TCommand> : IReceptor<TCommand> where TCommand : ClearCacheCommand {
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public int CountFor(string cacheKey) => _counts.TryGetValue(cacheKey, out var n) ? n : 0;

    public ValueTask HandleAsync(TCommand message, CancellationToken cancellationToken = default) {
      _counts.AddOrUpdate(message.CacheKey!, 1, static (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }

  // --- fakes (transport edge and worker channels only) ---

  private sealed class FakeOutboxDrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

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
