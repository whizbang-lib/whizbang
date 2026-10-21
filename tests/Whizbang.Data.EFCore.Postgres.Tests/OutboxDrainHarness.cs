// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Runs the REAL <see cref="OutboxDrainWorker"/> over one row a dispatch wrote to a real
/// <c>wh_outbox</c>: leases the row to this instance the way the claim leaves it for the drainer,
/// drains it, and waits for the row's completion signal. Only the transport and the worker's channels
/// are faked; the publish strategy reports success and records what it published.
/// </summary>
internal static class OutboxDrainHarness {
  public static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

  /// <summary>
  /// Registers the instance and leases the row to it, as the claim does before the drainer sees it.
  /// </summary>
  public static async Task LeaseAsync(string connectionString, IServiceInstanceProvider instance, Guid messageId) {
    await using var connection = new NpgsqlConnection(connectionString);
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
    await using var lease = connection.CreateCommand();
    lease.CommandText = """
      UPDATE wh_outbox SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes', status = 1, attempts = 0
      WHERE message_id = @msg
      """;
    lease.Parameters.AddWithValue("inst", instance.InstanceId);
    lease.Parameters.AddWithValue("msg", messageId);
    await lease.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// The scheduled time arrives: the row becomes due on the database's clock, which is the clock the
  /// drain query compares against. Stands in for waiting until then.
  /// </summary>
  public static async Task MakeDueAsync(string connectionString, Guid messageId) {
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var due = connection.CreateCommand();
    due.CommandText = "UPDATE wh_outbox SET scheduled_for = NOW() - INTERVAL '1 second' WHERE message_id = @msg";
    due.Parameters.AddWithValue("msg", messageId);
    await due.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Drains the leased row's stream with the real worker and waits for the row's completion: its inline
  /// PreOutbox stage has run and it was published. Returns the ids the fake transport published.
  /// </summary>
  public static async Task<IReadOnlyCollection<Guid>> DrainAsync(
      IServiceProvider serviceProvider, JsonSerializerOptions jsonOptions, bool bulkPublish, Guid messageId, Guid streamId) =>
    [.. (await DrainAndCaptureAsync(serviceProvider, jsonOptions, bulkPublish, messageId, streamId)).Select(work => work.MessageId)];

  /// <summary>
  /// As <see cref="DrainAsync"/>, returning what the fake transport was handed to publish: the wire
  /// envelope the drain worker built, which is what a receiving service's inbox gets.
  /// </summary>
  public static async Task<IReadOnlyList<OutboxWork>> DrainAndCaptureAsync(
      IServiceProvider serviceProvider, JsonSerializerOptions jsonOptions, bool bulkPublish, Guid messageId, Guid streamId) {
    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new SignalingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var publish = new SucceedingPublishStrategy(bulkPublish);
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 };
    var worker = new OutboxDrainWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
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
      await completion.WaitForAsync(messageId).WaitAsync(SignalTimeout);
      await Assert.That(failure.All).IsEmpty()
        .Because("the row must reach its stages; a failure here (a missing security context, say) means nothing was tested");
      return [.. publish.Published];
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  /// <summary>
  /// Offers the leased row's stream to the REAL worker while the transport reports not ready, waits for the
  /// worker's held-pass signal and runs <paramref name="whileHeld"/>; then reports ready, offers the stream again
  /// (as the claim's next poll does) and waits for the row's completion (#1558). Returns the held pass and what
  /// the fake transport published.
  /// </summary>
  public static async Task<(OutboxPublishHeldEvent Held, IReadOnlyList<OutboxWork> Published)> HoldThenDrainAsync(
      IServiceProvider serviceProvider, JsonSerializerOptions jsonOptions, Guid messageId, Guid streamId,
      int renewalLeaseSeconds, Func<Task> whileHeld) {
    var drainChannel = new FakeOutboxDrainChannel();
    var completion = new SignalingCompletionChannel();
    var failure = new RecordingFailureChannel();
    var publish = new SucceedingPublishStrategy(supportsBulkPublish: false);
    publish.SetReady(false);
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 100 };
    var worker = new OutboxDrainWorker(
      scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: serviceProvider.GetRequiredService<IServiceInstanceProvider>(),
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
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = renewalLeaseSeconds }));
    var held = new TaskCompletionSource<OutboxPublishHeldEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    worker.OnOutboxPublishHeld += e => held.TrySetResult(e);

    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await drainChannel.WriteAsync(streamId, cts.Token);
      var heldPass = await held.Task.WaitAsync(SignalTimeout);
      await Assert.That(publish.Published).IsEmpty().Because("a transport that reported not ready is handed nothing");
      await Assert.That(failure.All).IsEmpty().Because("a held row is not a failed publish");
      await whileHeld();

      publish.SetReady(true);
      await drainChannel.WriteAsync(streamId, cts.Token);
      await completion.WaitForAsync(messageId).WaitAsync(SignalTimeout);
      await Assert.That(failure.All).IsEmpty();
      return (heldPass, [.. publish.Published]);
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
  }

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
    private volatile bool _ready = true;
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public bool SupportsBulkPublish => supportsBulkPublish;
    public void SetReady(bool ready) => _ready = ready;
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_ready);

    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) =>
      Task.FromResult(_succeed(work));

    public Task<IReadOnlyList<MessagePublishResult>> PublishBatchAsync(IReadOnlyList<OutboxWork> workItems, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<MessagePublishResult>>([.. workItems.Select(_succeed)]);

    private MessagePublishResult _succeed(OutboxWork work) {
      Published.Enqueue(work);
      return new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      };
    }
  }
}
