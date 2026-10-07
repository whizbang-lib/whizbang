// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="IntegrityCheckpointReceptor"/>: a host that registered no
/// integrity options, no repair policy and no metrics still confirms gaps under the documented
/// defaults; a fully wired host counts every stage on the meter and sends the directed repair; and
/// each missing piece of the repair infrastructure withholds the send and names itself in the log.
/// </summary>
/// <remarks>
/// Both arms of every decision are taken inside this class: the CI coverage merge keeps the best
/// per-line condition count of any single shard, so an arm exercised only in another shard does
/// not count. No database: every collaborator is an in-memory fake.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IntegrityCheckpointReceptor.cs</code-under-test>
[Category("Shard2")]
public class IntegrityCheckpointReceptorBranchTests {

  private sealed record BranchCoverageEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  private static readonly string _verifiedType = TypeNameFormatter.Format(typeof(BranchCoverageEvent));

  // A host that wired the receptor but nothing else must still detect gaps: the options default
  // to detection on, report-only, publishing off, and the per-process fallback policy stands in
  // for the unregistered one. Detection must not silently depend on optional registrations.
  [Test]
  public async Task NothingOptionalRegistered_ConfirmsTheGapUnderTheDefaultsAsync() {
    var run = await _runConfirmedGapAsync(new Wiring {
      RegisterOptions = false,
      RegisterPolicy = false,
      RegisterMetrics = false,
    });

    await Assert.That(run.Logger.HasEvent(50)).IsTrue()
      .Because("the second checkpoint recounts the still-missing events and confirms the gap");
    await Assert.That(run.Dispatcher.Published).IsEmpty()
      .Because("report events are opt-in and the default options leave them off");
    await Assert.That(run.Transport.Published).IsEmpty()
      .Because("the default repair mode is report-only, so no repair is requested");
  }

  // Fully wired: every meter stage counts, and the confirmed gap's repair goes to the origin's
  // own request topic.
  [Test]
  public async Task FullyWired_CountsEveryStageAndSendsTheDirectedRepairAsync() {
    var run = await _runConfirmedGapAsync(new Wiring { RegisterMetrics = true });

    await Assert.That(run.Transport.Published.Count).IsEqualTo(1)
      .Because("the confirmed gap's repair request is sent once, directed at the origin");
    await Assert.That(run.Transport.Published[0].Destination.Address).IsEqualTo("origin.requests");
    await Assert.That(run.Transport.Published[0].Envelope.Target).IsEqualTo("origin-svc");
    var probe = run.Probe!;
    var metrics = run.Metrics!;
    await Assert.That(probe.CounterTotal(metrics.CheckpointsReceived.Instrument)).IsEqualTo(2)
      .Because("both checkpoints were received from a remote origin");
    await Assert.That(probe.CounterTotal(metrics.GapsDetected.Instrument)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.RepairsRequested.Instrument)).IsEqualTo(1);
  }

  [Test]
  public async Task RepairWithoutTransport_IsWithheldAndNamesTheTransportAsync() {
    var run = await _runConfirmedGapAsync(new Wiring { RegisterTransport = false });

    await Assert.That(run.Logger.HasEvent(51, "transport=True")).IsTrue()
      .Because("the skip must name the missing transport so an operator can fix the right thing");
  }

  [Test]
  public async Task RepairWithoutSerializer_IsWithheldAndNamesTheSerializerAsync() {
    var run = await _runConfirmedGapAsync(new Wiring { RegisterSerializer = false });

    await Assert.That(run.Transport.Published).IsEmpty();
    await Assert.That(run.Logger.HasEvent(51, "serializer=True")).IsTrue();
    await Assert.That(run.Logger.HasEvent(51, "transport=False")).IsTrue();
  }

  // Without a service identity the request cannot name its requester, so the origin would have no
  // return address for the redelivery.
  [Test]
  public async Task RepairWithoutServiceIdentity_IsWithheldAndNamesTheRequesterAsync() {
    var run = await _runConfirmedGapAsync(new Wiring { RegisterInstanceProvider = false });

    await Assert.That(run.Transport.Published).IsEmpty();
    await Assert.That(run.Logger.HasEvent(51, "requester=True")).IsTrue();
    await Assert.That(run.Logger.HasEvent(51, "serializer=False")).IsTrue();
  }

  [Test]
  public async Task RepairWithoutOwnReplyTopic_IsWithheldAndNamesTheTopicAsync() {
    var run = await _runConfirmedGapAsync(new Wiring { RegisterConsumerOptions = false });

    await Assert.That(run.Transport.Published).IsEmpty();
    await Assert.That(run.Logger.HasEvent(51, "topic=True")).IsTrue();
    await Assert.That(run.Logger.HasEvent(51, "requester=False")).IsTrue();
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private sealed class Wiring {
    public bool RegisterOptions { get; init; } = true;
    public bool RegisterPolicy { get; init; } = true;
    public bool RegisterMetrics { get; init; }
    public bool RegisterTransport { get; init; } = true;
    public bool RegisterSerializer { get; init; } = true;
    public bool RegisterInstanceProvider { get; init; } = true;
    public bool RegisterConsumerOptions { get; init; } = true;
  }

  private sealed record Run(
    FakeTransport Transport, FakeDispatcher Dispatcher, CaptureLogger Logger,
    StreamIntegrityMetrics? Metrics, MeterProbe? Probe);

  /// <summary>
  /// Two checkpoints from one origin for a subscribed type that never arrives: the first records
  /// the deficit as pending, the second recounts it and confirms the gap.
  /// </summary>
  private static async Task<Run> _runConfirmedGapAsync(Wiring wiring) {
    var transport = new FakeTransport();
    var dispatcher = new FakeDispatcher();
    var logger = new CaptureLogger();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new FakeCoordinator());
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton(new IntegrityGapTracker());
    services.AddSingleton<IntegrityRepairLedger>();
    services.AddSingleton<IEventTypeProvider>(new FakeEventTypeProvider());
    if (wiring.RegisterOptions) {
      services.AddSingleton(Options.Create(new StreamIntegrityOptions {
        RepairMode = IntegrityRepairMode.AutoRepairCapped,
      }));
    }
    if (wiring.RegisterPolicy) {
      services.AddSingleton(new IntegrityRepairPolicy(new IntegrityRepairPolicy.Settings()));
    }
    StreamIntegrityMetrics? metrics = null;
    MeterProbe? probe = null;
    if (wiring.RegisterMetrics) {
      metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
      probe = new MeterProbe(metrics.CheckpointsReceived.Meter);
      services.AddSingleton(metrics);
    }
    if (wiring.RegisterTransport) {
      services.AddSingleton<ITransport>(transport);
    }
    if (wiring.RegisterSerializer) {
      services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    }
    if (wiring.RegisterInstanceProvider) {
      services.AddSingleton<IServiceInstanceProvider>(new FakeInstanceProvider("consumer-svc"));
    }
    if (wiring.RegisterConsumerOptions) {
      var consumerOptions = new TransportConsumerOptions();
      consumerOptions.Destinations.Add(new TransportDestination("inbox"));
      services.AddSingleton(consumerOptions);
    }
    await using var sp = services.BuildServiceProvider();
    var receptor = new IntegrityCheckpointReceptor(sp.GetRequiredService<IServiceScopeFactory>(), logger);

    var originId = Guid.NewGuid();
    await receptor.HandleAsync(_checkpoint(originId, from: 10, to: 20, count: 4, emptyBuckets: false));
    await receptor.HandleAsync(_checkpoint(originId, from: 20, to: 20, count: 0, emptyBuckets: true));
    return new Run(transport, dispatcher, logger, metrics, probe);
  }

  private static IntegrityCheckpoint _checkpoint(Guid originId, long from, long to, int count, bool emptyBuckets) => new() {
    CheckpointStreamId = originId,
    OriginServiceId = originId,
    OriginServiceName = "origin-svc",
    RequestTopic = "origin.requests",
    FromCommitSequence = from,
    ToCommitSequence = to,
    Buckets = emptyBuckets
      ? []
      : [new CheckpointBucket { TenantScope = "tenant-a", EventType = _verifiedType, Count = count }],
  };

  // ── fakes ───────────────────────────────────────────────────────────────

  /// <summary>Reads one meter's instruments by identity, so sibling meters of the same name
  /// (other tests' metrics instances) never leak into a reading.</summary>
  private sealed class MeterProbe : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly List<(Instrument Instrument, double Value)> _readings = [];

    public MeterProbe(Meter meter) {
      _listener.InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument.Meter, meter)) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _record(instrument, value));
      _listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => _record(instrument, value));
      _listener.Start();
    }

    /// <summary>The cumulative total of a passive (observable) counter, across all its series.</summary>
    public double CounterTotal(Instrument instrument) {
      lock (_readings) {
        _readings.RemoveAll(r => ReferenceEquals(r.Instrument, instrument));
      }
      _listener.RecordObservableInstruments();
      lock (_readings) {
        return _readings.Where(r => ReferenceEquals(r.Instrument, instrument)).Sum(r => r.Value);
      }
    }

    public void Dispose() => _listener.Dispose();

    private void _record(Instrument instrument, double value) {
      lock (_readings) {
        _readings.Add((instrument, value));
      }
    }
  }

  private sealed class FakeEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(BranchCoverageEvent)];
  }

  private sealed class FakeInstanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>Counts nothing received from any origin, so every expected bucket stays short.</summary>
  private sealed class FakeCoordinator : IWorkCoordinator {
    public Guid LocalServiceId { get; } = Guid.NewGuid();

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public ValueTask<ServiceBacklog?> CountServiceBacklogAsync(CancellationToken cancellationToken = default) =>
      ValueTask.FromResult<ServiceBacklog?>(null);

    public Task<IReadOnlyList<CheckpointBucket>> CountReceivedFromOriginAsync(
      Guid originServiceId, long fromCommitSequence, long toCommitSequence, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<CheckpointBucket>>([]);

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class FakeTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  /// <summary>Captures PublishAsync payloads; every other dispatcher member is unused here.</summary>
  private sealed class FakeDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new Receipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    private sealed class Receipt : IDeliveryReceipt {
      public MessageId MessageId => MessageId.New();
      public CorrelationId? CorrelationId => null;
      public MessageId? CausationId => null;
      public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
      public string Destination => "test";
      public DeliveryStatus Status => DeliveryStatus.Delivered;
      public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
      public Guid? StreamId => null;
    }
  }

  private sealed class CaptureLogger : Microsoft.Extensions.Logging.ILogger<IntegrityCheckpointReceptor> {
    private readonly List<(int EventId, string Message)> _entries = [];

    public bool HasEvent(int eventId, string? fragment = null) {
      lock (_entries) {
        return _entries.Any(e => e.EventId == eventId
          && (fragment is null || e.Message.Contains(fragment, StringComparison.Ordinal)));
      }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_entries) {
        _entries.Add((eventId.Id, formatter(state, exception)));
      }
    }
  }
}
