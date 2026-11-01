// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Drives <see cref="InboxDispatchWorker.ProcessOneInnerAsync"/> in one flow with every collaborator captured, so a
/// dispatch decision can be asserted without a hosted worker. Shared by the inbox lease, logging and memory tests.
/// </summary>
internal sealed class InboxDispatchUnitHarness {
  public ConcurrentQueue<(LogLevel Level, int EventId, string Message, Exception? Exception, string? EventName)> Logs { get; } = new();
  public CapturingCommitChannel Commits { get; } = new();
  public CapturingFailureChannel Failures { get; } = new();
  public IInboxChannelWriter Inbox { get; }
  public CapturingDeadLetterStore DeadLetters { get; } = new();
  public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
  public FakeTimeProvider Time { get; }
  public Guid InstanceId { get; }
  public InboxDispatchWorker Worker { get; }
  public InboxDeserializeCache? Cache { get; }
  public WorkCompletionMeter Meter { get; } = new();

  public InboxDispatchUnitHarness(
      IInboxChannelWriter? inbox = null,
      InboxDispatchWorkerOptions? options = null,
      IDeadLetterStore? deadLetters = null,
      InboxDeserializeCache? cache = null,
      IServiceCollection? services = null,
      LeaseRegistry? leaseRegistry = null,
      LeaseHandleOptions? leaseHandleOptions = null,
      FakeTimeProvider? time = null,
      ILifecycleMessageDeserializer? deserializer = null) {
    Time = time ?? new FakeTimeProvider(Start);
    Inbox = inbox ?? new FakeInboxChannelWriter();
    var instance = new FakeInstanceProvider();
    InstanceId = instance.InstanceId;
    Cache = cache;
    var sp = (services ?? new ServiceCollection()).BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    Worker = new InboxDispatchWorker(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: instance,
      inboxChannelWriter: Inbox,
      handlerCommitChannel: Commits,
      failureChannel: Failures,
      schemaReadyGate: gate,
      options: Options.Create(options ?? new InboxDispatchWorkerOptions { MaxInboxAttempts = 10 }),
      coordinatorOptions: Options.Create(new WorkCoordinatorOptions()),
      logger: new RecordingLogger(Logs),
      integrityOptions: Options.Create(new StreamIntegrityOptions()),
      lifecycleMessageDeserializer: deserializer ?? new JsonLifecycleMessageDeserializer(),
      leaseHandleOptions: Options.Create(leaseHandleOptions ?? new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      receptorRegistry: new PermissiveReceptorRegistryQuery(),
      discardPolicy: new MessageDiscardPolicy(new PermissiveReceptorRegistryQuery(), NullLogger<MessageDiscardPolicy>.Instance,
        new System.Diagnostics.Metrics.Meter("test"), Options.Create(new RoutingOptions()), new EventMarkerResolver(NullMessageTypeCatalog.Instance)),
      runtimeReceptorRegistry: NullReceptorRegistry.Instance,
      deadLetterStore: deadLetters ?? DeadLetters,
      generationProvider: new DefaultGenerationProvider(),
      leaseRegistry: leaseRegistry,
      timeProvider: Time,
      deserializeCache: cache,
      completionMeter: Meter);
  }

  /// <summary>An inbox row with an empty JSON payload, the given attempts, last error and message type.</summary>
  public static InboxWork Work(int attempts = 1, string? error = null, string? messageType = null, string payload = "{}") {
    var id = (Guid)TrackedGuid.New();
    return new InboxWork {
      MessageId = id,
      Envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(id),
        Payload = JsonElement.Parse(payload),
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Inbox },
      },
      MessageType = messageType ?? "System.Text.Json.JsonElement, System.Text.Json",
      StreamId = (Guid)TrackedGuid.New(),
      PartitionNumber = 1,
      Attempts = attempts,
      Status = MessageProcessingStatus.Stored,
      Flags = WorkBatchOptions.None,
      Error = error,
    };
  }

  public sealed class FakeInboxChannelWriter : IInboxChannelWriter {
    private readonly Channel<InboxWork> _channel = Channel.CreateUnbounded<InboxWork>();
    public ConcurrentQueue<Guid> Released { get; } = new();
    public ChannelReader<InboxWork> Reader => _channel.Reader;
    public ValueTask WriteAsync(InboxWork work, CancellationToken ct = default) => _channel.Writer.WriteAsync(work, ct);
    public bool TryWrite(InboxWork work) => _channel.Writer.TryWrite(work);
    public bool IsInFlight(Guid messageId) => false;
    public void RemoveInFlight(Guid messageId) => Released.Enqueue(messageId);
    public bool ShouldRenewLease(Guid messageId) => false;
    public void Complete() => _channel.Writer.Complete();
    public event Action? OnNewInboxWorkAvailable;
    public void SignalNewInboxWorkAvailable() => OnNewInboxWorkAvailable?.Invoke();
  }

  public sealed class CapturingCommitChannel : IInboxHandlerCommitChannel {
    public ConcurrentQueue<HandlerCommitRequest> All { get; } = new();
    public Action<HandlerCommitRequest>? OnEnqueue { get; set; }
    public Exception? Throw { get; set; }
    public ValueTask EnqueueAsync(HandlerCommitRequest request, CancellationToken cancellationToken = default) {
      if (Throw is not null) {
        return ValueTask.FromException(Throw);
      }
      OnEnqueue?.Invoke(request);
      All.Enqueue(request);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class CapturingFailureChannel : IFailureChannel {
    public ConcurrentQueue<MessageFailure> All { get; } = new();
    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      All.Enqueue(failure);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class CapturingDeadLetterStore : IDeadLetterStore {
    public ConcurrentQueue<(Guid SourceId, MessageFailureReason Reason, string? ErrorText)> Moves { get; } = new();
    public Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId,
        MessageFailureReason failureReason, string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      Moves.Enqueue((sourceId, failureReason, errorText));
      return Task.FromResult<Guid?>(deadLetterId);
    }
  }

  private sealed class FakeInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.New();
    public string ServiceName => "consumer-svc";
    public string HostName => "consumer-host";
    public int ProcessId => 12;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  private sealed class RecordingLogger(ConcurrentQueue<(LogLevel Level, int EventId, string Message, Exception? Exception, string? EventName)> entries) : ILogger<InboxDispatchWorker> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      => entries.Enqueue((logLevel, eventId.Id, formatter(state, exception), exception, eventId.Name));
  }
}
