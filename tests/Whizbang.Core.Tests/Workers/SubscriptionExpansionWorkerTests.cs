using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Stream-integrity Phase S: the startup subscription-expansion reconciler. First boot BASELINES
/// the whole catalog (no backfill — nothing existed to miss); a type appearing on a later boot is
/// an EXPANSION — registered Pending and repaired via ONE broadcast STATE-ONLY re-delivery request
/// (every origin answers; duplicates converge by identity). Disabled = expansions stay Pending
/// (the audit surface); missing infrastructure leaves them Pending for the next boot's retry.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/SubscriptionExpansionWorker.cs</code-under-test>
public class SubscriptionExpansionWorkerTests {

  public sealed record ExpandedEvent : IEvent {
    [StreamId]
    public Guid Sid { get; init; }
  }

  private static readonly string _expandedType = TypeNameFormatter.FormatClrTypeName(typeof(ExpandedEvent));
  private static readonly string _expandedWireType = TypeNameFormatter.Format(typeof(ExpandedEvent));

  [Test]
  public async Task FirstBoot_BaselinesWholeCatalog_NoBackfillAsync() {
    var coordinator = new _registryCoordinator();   // empty registry = first boot
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, transport);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry[_expandedType]).IsEqualTo(ConsumedTypeBackfillStatus.Baseline)
      .Because("first boot means nothing existed before this service to miss — baseline, no backfill.");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task Expansion_BroadcastsStateOnlyBackfillAndMarksRequestedAsync() {
    var coordinator = new _registryCoordinator();
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;   // prior boot
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, transport);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry[_expandedType]).IsEqualTo(ConsumedTypeBackfillStatus.Requested)
      .Because("the expansion registered Pending and transitioned to Requested once the broadcast went out.");

    var (envelope, destination, _) = transport.Published.Single();
    await Assert.That(destination.Address).IsEqualTo("inbox");
    await Assert.That(envelope.Target).IsNull()
      .Because("the request BROADCASTS — an expanding consumer cannot know which origins hold the history.");
    var options = JsonContextRegistry.CreateCombinedOptions();
    var command = (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
    await Assert.That(command.StateOnly).IsTrue()
      .Because("backfill builds STATE — trigger receptors must never re-run over delivered history.");
    await Assert.That(command.EventTypes!).IsEquivalentTo([_expandedWireType])
      .Because("the registry keys on the no-assembly CLR name (persisted rows must not re-baseline), " +
               "but the ORIGIN matches event_type in the assembly-qualified wire form — a FullName-only " +
               "request silently backfills nothing.");
    await Assert.That(command.RequesterService).IsEqualTo("expanded-svc");
    await Assert.That(command.Topic).IsEqualTo("inbox");
  }

  [Test]
  public async Task Disabled_RecordsPendingWithoutRequestingAsync() {
    var coordinator = new _registryCoordinator();
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;
    var transport = new _captureTransport();
    var worker = _buildWorker(coordinator, transport,
      new StreamIntegrityOptions { BackfillOnSubscriptionGrowth = false });

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry[_expandedType]).IsEqualTo(ConsumedTypeBackfillStatus.Pending)
      .Because("disabling backfill still RECORDS the expansion — the audit reports 'pending backfill', " +
               "never silent divergence.");
    await Assert.That(transport.Published).IsEmpty();
  }

  [Test]
  public async Task MissingTransport_LeavesPendingForNextBootAsync() {
    var coordinator = new _registryCoordinator();
    coordinator.Registry["Contracts.PriorType"] = ConsumedTypeBackfillStatus.Baseline;
    var worker = _buildWorker(coordinator, transport: null);

    await worker.RunOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Registry[_expandedType]).IsEqualTo(ConsumedTypeBackfillStatus.Pending)
      .Because("a request that cannot be sent stays Pending — the next boot retries it.");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_AFailedPassIsNonFatalAsync(CancellationToken testToken) {
    // This runs at startup, before the app serves. A reconcile that cannot reach the registry
    // must cost the pass, not the process: the expansion stays recorded and is re-detected next
    // boot, and the audit phases surface anything still pending. Faulting here would take a
    // healthy service out of rotation over a transient read.
    var coordinator = new _registryCoordinator { FailReads = true };
    var worker = _buildWorker(coordinator, transport: null);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await worker.ExecuteTask!.WaitAsync(testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a startup reconcile failure is logged and swallowed; a faulted hosted service "
             + "stops the host from ever serving");
  }

  [Test]
  [Timeout(30000)]
  public async Task ExecuteAsync_ShutdownBeforeSchemaReady_TouchesNothingAsync(
      CancellationToken testToken) {
    // A host that fails during migration stops everything it built. This worker reads registry
    // tables that may not exist yet, so it waits on the schema gate -- and a shutdown arriving
    // during that wait must not be reported as a reconcile failure.
    var coordinator = new _registryCoordinator();
    var gate = new _blockingGate();
    var worker = _buildWorker(coordinator, transport: null, gate: gate);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // Without waiting for the worker to actually reach the gate, everything below is answered by
    // a worker that never ran -- the registry is untouched either way.
    await gate.WaitEntered.WaitAsync(testToken);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("shutdown during the schema wait is an ordinary stop, not an error to report");
    await Assert.That(coordinator.Registry).IsEmpty()
      .Because("nothing may be written before the schema those tables live in is ready");
  }

  private static SchemaReadyGate _readyGate() {
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return gate;
  }

  /// <summary>A gate that never opens, and reports when the worker began waiting on it.</summary>
  private sealed class _blockingGate : ISchemaReadyGate {
    private readonly TaskCompletionSource _waitEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitEntered => _waitEntered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _waitEntered.TrySetResult();
      await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static SubscriptionExpansionWorker _buildWorker(
      _registryCoordinator coordinator, _captureTransport? transport, StreamIntegrityOptions? options = null,
      ISchemaReadyGate? gate = null) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ => coordinator);
    services.AddSingleton<IEventTypeProvider>(new _typeProvider());
    if (transport is not null) {
      services.AddSingleton<ITransport>(transport);
    }
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider("expanded-svc"));
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("inbox"));
    services.AddSingleton(consumerOptions);
    var sp = services.BuildServiceProvider();
    return new SubscriptionExpansionWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate ?? _readyGate(),
      Options.Create(options ?? new StreamIntegrityOptions()),
      NullLogger<SubscriptionExpansionWorker>.Instance);
  }

  private sealed class _typeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ExpandedEvent)];
  }

  private sealed class _instanceProvider(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.NewMedo().Value;
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

  /// <summary>In-memory consumed-type registry with the production status semantics.</summary>
  private sealed class _registryCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public Dictionary<string, ConsumedTypeBackfillStatus> Registry { get; } = [];

    /// <summary>Set to make the registry read throw, as a database outage at startup would.</summary>
    public bool FailReads { get; set; }

    public Task<IReadOnlyList<ConsumedTypeRegistration>> GetConsumedTypeRegistrationsAsync(CancellationToken cancellationToken = default) =>
      FailReads
        ? Task.FromException<IReadOnlyList<ConsumedTypeRegistration>>(
            new InvalidOperationException("registry unavailable"))
        : Task.FromResult<IReadOnlyList<ConsumedTypeRegistration>>(
            [.. Registry.Select(kv => new ConsumedTypeRegistration { EventType = kv.Key, Status = kv.Value })]);

    public Task RegisterConsumedTypesAsync(IReadOnlyList<string> eventTypes, bool asBaseline, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        Registry.TryAdd(type, asBaseline ? ConsumedTypeBackfillStatus.Baseline : ConsumedTypeBackfillStatus.Pending);
      }
      return Task.CompletedTask;
    }

    public Task MarkConsumedTypeBackfillRequestedAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) {
      foreach (var type in eventTypes) {
        if (Registry.TryGetValue(type, out var status) && status == ConsumedTypeBackfillStatus.Pending) {
          Registry[type] = ConsumedTypeBackfillStatus.Requested;
        }
      }
      return Task.CompletedTask;
    }
  }

  private sealed class _captureTransport : ITransport {
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
}
