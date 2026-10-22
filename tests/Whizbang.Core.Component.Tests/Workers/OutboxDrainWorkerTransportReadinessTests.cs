// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention.

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The default publisher honors <see cref="Whizbang.Core.Transports.ITransportReadinessCheck"/> (#1558):
/// <see cref="OutboxDrainWorker"/> asks the publish strategy whether the transport is ready before a
/// publish pass. While it is not, the rows this instance holds are kept, their leases renewed, and
/// nothing is published or reported as failed, so no attempt is charged. Once it is ready, the next
/// offer of the stream publishes them. Every wait is on the worker's own signals.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxDrainWorker.cs</code-under-test>
/// <docs>messaging/transports/transports</docs>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class OutboxDrainWorkerTransportReadinessTests {
  private static readonly TimeSpan _signal = TimeSpan.FromSeconds(30);

  [Test]
  public async Task NotReady_HoldsTheRowsWithLeasesRenewed_ThenPublishesThemOnceReadyAsync() {
    var streamId = (Guid)TrackedGuid.New();
    Guid[] ids = [(Guid)TrackedGuid.New(), (Guid)TrackedGuid.New(), (Guid)TrackedGuid.New()];
    var coordinator = new RecordingCoordinator();
    coordinator.RowsByStream[streamId] = [.. ids.Select(id => _row(id, streamId))];
    var publish = new ToggleReadyPublishStrategy();
    var harness = _build(coordinator, publish, Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 120 }));
    var held = new TaskCompletionSource<OutboxPublishHeldEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Worker.OnOutboxPublishHeld += e => held.TrySetResult(e);

    using var cts = new CancellationTokenSource();
    try {
      await harness.Worker.StartAsync(cts.Token);
      await harness.Drain.WriteAsync(streamId);
      var heldEvent = await held.Task.WaitAsync(_signal);

      await Assert.That(publish.Published).IsEmpty()
        .Because("a transport that reported not ready is not handed a message");
      await Assert.That(harness.Failures.All).IsEmpty()
        .Because("a held row is not a failed publish; reporting one releases the lease and the re-claim charges an attempt");
      await Assert.That(heldEvent.StreamIds).IsEquivalentTo([streamId]);
      await Assert.That(heldEvent.HeldMessageIds).IsEquivalentTo(ids)
        .Because("every row held on the stream is kept, past the per-stream page of 1, or the rows left out lapse and are charged");
      await Assert.That(coordinator.Renewals).Count().IsEqualTo(1);
      var renewal = coordinator.Renewals.Single();
      await Assert.That(renewal.Category).IsEqualTo(WorkCategory.Outbox);
      await Assert.That(renewal.Ids).IsEquivalentTo(ids)
        .Because("the held rows' leases are renewed, so they are not re-claimed and charged an attempt while the transport is down");
      await Assert.That(renewal.LeaseSeconds).IsEqualTo(120)
        .Because("a renewal extends the lease by the configured renewal length");

      // The claim offers the stream again on its next poll; by then the transport is ready.
      publish.SetReady(true);
      await harness.Drain.WriteAsync(streamId);
      await harness.Completions.WaitForCountAsync(ids.Length, _signal);

      await Assert.That(publish.Published.Select(w => w.MessageId)).IsEquivalentTo(ids, TUnit.Assertions.Enums.CollectionOrdering.Matching)
        .Because("once the transport is ready the held rows publish, in stream order");
      await Assert.That(harness.Failures.All).IsEmpty();
    } finally {
      await cts.CancelAsync();
      await harness.Worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task NotReady_WithDefaultRenewalOptions_RenewsForTheDefaultLeaseAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    var coordinator = new RecordingCoordinator();
    coordinator.RowsByStream[streamId] = [_row(id, streamId)];
    var publish = new ToggleReadyPublishStrategy();
    var harness = _build(coordinator, publish, Options.Create(new LeaseRenewalWorkerOptions()));
    var held = new TaskCompletionSource<OutboxPublishHeldEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Worker.OnOutboxPublishHeld += e => held.TrySetResult(e);

    using var cts = new CancellationTokenSource();
    try {
      await harness.Worker.StartAsync(cts.Token);
      await harness.Drain.WriteAsync(streamId);
      await held.Task.WaitAsync(_signal);

      await Assert.That(publish.Published).IsEmpty();
      await Assert.That(coordinator.Renewals.Single().LeaseSeconds).IsEqualTo(new LeaseRenewalWorkerOptions().LeaseSeconds)
        .Because("with the default renewal options the lease is renewed for the renewal worker's default length");
    } finally {
      await cts.CancelAsync();
      await harness.Worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  public async Task NotReady_StreamWithNothingHeldHere_RenewsNothingAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var coordinator = new RecordingCoordinator();
    var publish = new ToggleReadyPublishStrategy();
    var harness = _build(coordinator, publish, Options.Create(new LeaseRenewalWorkerOptions()));
    var held = new TaskCompletionSource<OutboxPublishHeldEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Worker.OnOutboxPublishHeld += e => held.TrySetResult(e);

    using var cts = new CancellationTokenSource();
    try {
      await harness.Worker.StartAsync(cts.Token);
      await harness.Drain.WriteAsync(streamId);
      var heldEvent = await held.Task.WaitAsync(_signal);

      await Assert.That(heldEvent.HeldMessageIds).IsEmpty();
      await Assert.That(coordinator.Renewals).IsEmpty()
        .Because("with no row held there is no lease to renew and no round trip to make");
      await Assert.That(publish.Published).IsEmpty();
    } finally {
      await cts.CancelAsync();
      await harness.Worker.StopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// A held pass with nobody listening on <see cref="OutboxDrainWorker.OnOutboxPublishHeld"/>, the shape of every
  /// host that does not monitor holds: the rows are still kept with their leases renewed, and the pass completes
  /// cleanly. A hold that failed on the missing subscriber would reach the batch guard, which logs it at Error
  /// before the worker goes idle, so the test waits on the idle transition and then asserts nothing was logged
  /// at Error.
  /// </summary>
  [Test]
  public async Task NotReady_WithNoHeldSubscriber_StillRenewsTheLeasesAndCompletesThePassAsync() {
    var streamId = (Guid)TrackedGuid.New();
    var id = (Guid)TrackedGuid.New();
    var coordinator = new RecordingCoordinator();
    coordinator.RowsByStream[streamId] = [_row(id, streamId)];
    var publish = new ToggleReadyPublishStrategy();
    var logger = new ErrorCountingLogger();
    var harness = _build(coordinator, publish, Options.Create(new LeaseRenewalWorkerOptions()), logger);
    var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Worker.OnWorkProcessingIdle += () => idle.TrySetResult();

    using var cts = new CancellationTokenSource();
    try {
      await harness.Worker.StartAsync(cts.Token);
      await harness.Drain.WriteAsync(streamId);
      await idle.Task.WaitAsync(_signal);

      await Assert.That(coordinator.Renewals).Count().IsEqualTo(1);
      await Assert.That(coordinator.Renewals.Single().Ids).IsEquivalentTo([id])
        .Because("with no subscriber the held row's lease is renewed all the same");
      await Assert.That(publish.Published).IsEmpty();
      await Assert.That(harness.Failures.All).IsEmpty();
      await Assert.That(logger.Errors).IsEqualTo(0)
        .Because("a held pass with no subscriber completes; it does not fail into the batch guard");
    } finally {
      await cts.CancelAsync();
      await harness.Worker.StopAsync(CancellationToken.None);
    }
  }

  // ── harness ──────────────────────────────────────────────────────────────────────────────────

  private sealed record Harness(
      OutboxDrainWorker Worker, DrainChannel Drain, CompletionChannel Completions, FailureChannel Failures);

  private static Harness _build(
      RecordingCoordinator coordinator, ToggleReadyPublishStrategy publish, IOptions<LeaseRenewalWorkerOptions> leaseRenewalOptions,
      ILogger<OutboxDrainWorker>? logger = null) {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var drain = new DrainChannel();
    var completions = new CompletionChannel(coordinator);
    var failures = new FailureChannel();
    // A page of one row per stream: the hold must still keep every row the stream has here.
    var options = new OutboxDrainWorkerOptions { Enabled = true, MaxPerStream = 1 };
    var worker = new OutboxDrainWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: new ServiceInstanceProvider(Guid.CreateVersion7(), "readiness-svc", "readiness-host", 1),
      drainChannel: drain,
      completionChannel: completions,
      failureChannel: failures,
      schemaReadyGate: gate,
      options: Options.Create(options),
      jsonOptions: Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      logger: logger ?? NullLogger<OutboxDrainWorker>.Instance,
      publishStrategy: publish,
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      governor: OutboxDrainWorker.CreateDefaultGovernor(options),
      leaseRenewalOptions: leaseRenewalOptions);
    return new Harness(worker, drain, completions, failures);
  }

  private static readonly JsonSerializerOptions _jsonOptions = Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions();

  private static OutboxBatchRow _row(Guid messageId, Guid streamId) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    };
    var typeInfo = _jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>));
    return new OutboxBatchRow {
      MessageId = messageId,
      StreamId = streamId,
      Destination = "readiness-topic",
      MessageType = "ReadinessMessage",
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName ?? "MessageEnvelope",
      EventData = JsonSerializer.Serialize(envelope, typeInfo),
      Metadata = "{}",
      Scope = null,
      Status = 1,
      Attempts = 1,
      PartitionNumber = 0,
      IsEvent = false,
    };
  }

  private sealed class ToggleReadyPublishStrategy : IMessagePublishStrategy {
    private volatile bool _ready;
    public void SetReady(bool ready) => _ready = ready;
    public ConcurrentQueue<OutboxWork> Published { get; } = new();
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_ready);
    public Task<MessagePublishResult> PublishAsync(OutboxWork work, CancellationToken cancellationToken) {
      Published.Enqueue(work);
      return Task.FromResult(new MessagePublishResult {
        MessageId = work.MessageId,
        Success = true,
        CompletedStatus = MessageProcessingStatus.Published,
      });
    }
  }

  private sealed record Renewal(WorkCategory Category, IReadOnlyList<Guid> Ids, int LeaseSeconds);

  /// <summary>
  /// Returns the unprocessed rows held on each stream (capped by the page asked for), as
  /// <c>fetch_outbox_batch</c> does, and records every lease renewal. A completed row is processed and
  /// no longer returned.
  /// </summary>
  private sealed class RecordingCoordinator : IWorkCoordinator {
    private readonly Lock _gate = new();
    public Dictionary<Guid, List<OutboxBatchRow>> RowsByStream { get; } = [];
    public ConcurrentQueue<Renewal> Renewals { get; } = new();

    public void Complete(Guid messageId) {
      lock (_gate) {
        foreach (var rows in RowsByStream.Values) {
          rows.RemoveAll(r => r.MessageId == messageId);
        }
      }
    }

    public Task<IReadOnlyList<OutboxBatchRow>> FetchOutboxBatchAsync(
        IReadOnlyList<Guid> streamIds, Guid instanceId, int maxPerStream = 100, CancellationToken cancellationToken = default) {
      var result = new List<OutboxBatchRow>();
      lock (_gate) {
        foreach (var sid in streamIds) {
          if (RowsByStream.TryGetValue(sid, out var rows)) {
            result.AddRange(rows.Take(maxPerStream));
          }
        }
      }
      return Task.FromResult<IReadOnlyList<OutboxBatchRow>>(result);
    }

    public Task<int> RenewLeasesAsync(
        WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds = 300, CancellationToken cancellationToken = default) {
      Renewals.Enqueue(new Renewal(category, [.. ids], leaseSeconds));
      return Task.FromResult(ids.Count);
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Guid.CreateVersion7());
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class DrainChannel : IOutboxDrainChannel {
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ChannelReader<Guid> Reader => _channel.Reader;
    public ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) => _channel.Writer.WriteAsync(streamId, cancellationToken);
    public bool TryWrite(Guid streamId) => _channel.Writer.TryWrite(streamId);
  }

  private sealed class CompletionChannel(RecordingCoordinator coordinator) : IOutboxCompletionChannel {
    private readonly Lock _gate = new();
    private readonly List<Guid> _ids = [];
    private int _target = int.MaxValue;
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask EnqueueAsync(Guid outboxMessageId, CancellationToken cancellationToken = default) {
      coordinator.Complete(outboxMessageId);
      lock (_gate) {
        _ids.Add(outboxMessageId);
        if (_ids.Count >= _target) {
          _reached.TrySetResult();
        }
      }
      return ValueTask.CompletedTask;
    }

    public Task WaitForCountAsync(int count, TimeSpan timeout) {
      lock (_gate) {
        _target = count;
        if (_ids.Count >= count) {
          return Task.CompletedTask;
        }
      }
      return _reached.Task.WaitAsync(timeout);
    }
  }

  private sealed class FailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Counts the entries logged at Error or above.</summary>
  private sealed class ErrorCountingLogger : ILogger<OutboxDrainWorker> {
    private int _errors;
    public int Errors => Volatile.Read(ref _errors);
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel >= LogLevel.Error) {
        Interlocked.Increment(ref _errors);
      }
    }
  }
}
