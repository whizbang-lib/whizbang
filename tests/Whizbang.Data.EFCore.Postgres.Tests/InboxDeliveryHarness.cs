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
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Delivers a wire envelope the drain worker published to a receiving service's REAL
/// <see cref="InboxDispatchWorker"/>, the way a transport would: the envelope is serialized and read back
/// with the receiver's JSON options (as the transport and <c>wh_inbox</c> do), then handed to the worker.
/// Only the inbox channel and the commit channel are faked.
/// </summary>
internal static class InboxDeliveryHarness {
  /// <summary>
  /// Runs the receiving service's inbox worker over the delivered message until <paramref name="handled"/>
  /// completes: a signal for the transition under test (a receptor running, or its stage being skipped).
  /// </summary>
  public static async Task DeliverAsync(IServiceProvider receiver, JsonSerializerOptions jsonOptions, OutboxWork published, Task handled) {
    var wire = JsonSerializer.Serialize(published.Envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var envelope = (MessageEnvelope<JsonElement>)JsonSerializer.Deserialize(wire, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)))!;
    var work = new InboxWork {
      MessageId = published.MessageId,
      Envelope = envelope,
      MessageType = published.MessageType,
      StreamId = published.StreamId ?? published.MessageId,
      PartitionNumber = 0,
      Attempts = 1,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None
    };

    var inbox = new FakeInboxChannelWriter();
    var failure = new RecordingFailureChannel();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var worker = new InboxDispatchWorker(
      scopeFactory: receiver.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: receiver.GetRequiredService<IServiceInstanceProvider>(),
      inboxChannelWriter: inbox,
      handlerCommitChannel: new DiscardingHandlerCommitChannel(),
      failureChannel: failure,
      schemaReadyGate: gate,
      options: Options.Create(new InboxDispatchWorkerOptions()),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: NullLogger<InboxDispatchWorker>.Instance,
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: receiver.GetRequiredService<ILifecycleMessageDeserializer>(),
      leaseHandleOptions: Options.Create(new LeaseHandleOptions { LeaseGraceSeconds = 30, MaxRenewalsPerWork = 6 }),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions { LeaseSeconds = 60 }),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      leaseRegistry: new LeaseRegistry());

    // The worker's poll loop has no ambient context: what the receiver knows comes off the envelope
    ScopeContextAccessor.CurrentInitiatingContext = null;
    ScopeContextAccessor.CurrentContext = null;
    using var cts = new CancellationTokenSource();
    try {
      await worker.StartAsync(cts.Token);
      await inbox.WriteAsync(work, cts.Token);
      await handled.WaitAsync(OutboxDrainHarness.SignalTimeout);
      await Assert.That(failure.All).IsEmpty()
        .Because("the delivered message must reach its stages; a failure here means nothing was tested");
    } finally {
      await cts.CancelAsync();
      await worker.StopAsync(CancellationToken.None);
      ScopeContextAccessor.CurrentInitiatingContext = null;
      ScopeContextAccessor.CurrentContext = null;
    }
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

  private sealed class DiscardingHandlerCommitChannel : IInboxHandlerCommitChannel {
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
  }

  private sealed class RecordingFailureChannel : IFailureChannel {
    public ConcurrentBag<MessageFailure> All { get; } = [];
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Add(failure);
      return ValueTask.CompletedTask;
    }
  }
}

/// <summary>
/// Completes a signal when the receptor invoker reports it skipped a stage as a same-service PostInbox
/// copy for a given message: the exact transition a "not run again" assertion waits on.
/// </summary>
internal sealed class SkippedPostInboxSignal : ILoggerProvider {
  private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _skipped = new();

  public Task For(Guid messageId) => _signal(messageId).Task;

  public ILogger CreateLogger(string categoryName) => new Listener(this);

  public void Dispose() { }

  private TaskCompletionSource _signal(Guid messageId) =>
    _skipped.GetOrAdd(messageId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

  private sealed class Listener(SkippedPostInboxSignal owner) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (state is not IReadOnlyList<KeyValuePair<string, object?>> values
          || !formatter(state, exception).Contains("same-service PostInbox", StringComparison.Ordinal)) {
        return;
      }
      foreach (var (key, value) in values) {
        if (key == "MessageId" && value is Guid messageId) {
          owner._signal(messageId).TrySetResult();
        }
      }
    }
  }
}
