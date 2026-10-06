// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="RedeliveryRequestReceptor"/>'s metering: a served request and a
/// declined one, each on a host with and without <see cref="StreamIntegrityMetrics"/>. The metered
/// host counts what it received, shipped or discarded; the unmetered host behaves identically.
/// </summary>
/// <remarks>
/// Both arms are taken inside this class because the CI coverage merge keeps the best per-line
/// condition count of any single shard. No database.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptor.cs</code-under-test>
[NotInParallel("RedeliveryBuildGate")]
[Category("Shard4")]
public class RedeliveryRequestReceptorBranchTests {

  [Test]
  public async Task ServedRequest_IsMeteredOnAMeteredHost_AndShippedEitherWayAsync() {
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RedeliveryRequestsReceived.Meter);

    var metered = await _handleAsync(IntegrityRepairMode.AutoRepairCapped, metrics);
    var unmetered = await _handleAsync(IntegrityRepairMode.AutoRepairCapped, metrics: null);

    await Assert.That(metered.Published.Count).IsEqualTo(1);
    await Assert.That(unmetered.Published.Count).IsEqualTo(1)
      .Because("metrics are observation only; an unmetered host serves the same request");
    await Assert.That(probe.CounterTotal(metrics.RedeliveryRequestsReceived.Instrument)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.RedeliveryEventsShipped.Instrument)).IsEqualTo(1);
    await Assert.That(probe.HistogramCount(metrics.RedeliveryBuildDuration)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.RepairTrafficDiscarded.Instrument)).IsEqualTo(0);
  }

  [Test]
  public async Task DeclinedRequest_IsCountedAsDiscardedOnAMeteredHostAsync() {
    var metrics = _newMetrics();
    using var probe = new MeterProbe(metrics.RepairTrafficDiscarded.Meter);

    var metered = await _handleAsync(IntegrityRepairMode.ReportOnly, metrics);
    var unmetered = await _handleAsync(IntegrityRepairMode.ReportOnly, metrics: null);

    await Assert.That(metered.Published).IsEmpty()
      .Because("a report-only origin declines automatic repair traffic");
    await Assert.That(unmetered.Published).IsEmpty();
    await Assert.That(probe.CounterTotal(metrics.RedeliveryRequestsReceived.Instrument)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.RepairTrafficDiscarded.Instrument)).IsEqualTo(1);
    await Assert.That(probe.CounterTotal(metrics.RedeliveryEventsShipped.Instrument)).IsEqualTo(0);
  }

  // ── fixture ─────────────────────────────────────────────────────────────

  private static async Task<CaptureTransport> _handleAsync(IntegrityRepairMode mode, StreamIntegrityMetrics? metrics) {
    var streamId = TrackedGuid.New().Value;
    var coordinator = new SelectingCoordinator { Selection = [_evt(streamId, TrackedGuid.New().Value, 1)] };
    var transport = new CaptureTransport();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(new CaptureSerializer());
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairMode = mode }));
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    await using var sp = services.BuildServiceProvider();
    var receptor = new RedeliveryRequestReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

    await receptor.HandleAsync(new RequestRedeliveryCommand {
      TenantScope = "tenant-a",
      EventTypes = ["Contracts.ProbeHappened"],
      StreamIds = [streamId],
      FromCommitSequence = 1,
      ToCommitSequence = 99,
      MaxEvents = 5,
      RequesterService = "damaged-svc",
      Topic = "events-topic",
      StateOnly = true
    });
    return transport;
  }

  private static StreamIntegrityMetrics _newMetrics() =>
    new(new WhizbangMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  private static RedeliveryEvent _evt(Guid streamId, Guid eventId, long version) => new() {
    EventId = eventId,
    StreamId = streamId,
    Version = version,
    CommitSequence = version,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
    Scope = null,
    Flags = 0
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

    /// <summary>How many values a histogram recorded since the probe started.</summary>
    public int HistogramCount(Instrument instrument) {
      lock (_readings) {
        return _readings.Count(r => ReferenceEquals(r.Instrument, instrument));
      }
    }

    public void Dispose() => _listener.Dispose();

    private void _record(Instrument instrument, double value) {
      lock (_readings) {
        _readings.Add((instrument, value));
      }
    }
  }

  /// <summary>Serves the canned selection with the real keyset-paging contract.</summary>
  private sealed class SelectingCoordinator : IWorkCoordinator {
    public IReadOnlyList<RedeliveryEvent> Selection { get; init; } = [];
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;

    public Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(RedeliveryRequest request, CancellationToken cancellationToken = default) {
      IEnumerable<RedeliveryEvent> query = Selection;
      if (request.AfterStreamId is { } afterStream) {
        var afterVersion = request.AfterVersion ?? 0L;
        query = query.Where(e => e.StreamId.CompareTo(afterStream) > 0
          || (e.StreamId == afterStream && e.Version > afterVersion));
      }
      return Task.FromResult<IReadOnlyList<RedeliveryEvent>>([.. query.Take(request.MaxEvents)]);
    }

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(LocalServiceId);

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken cancellationToken = default) => Task.FromResult(true);
  }

  /// <summary>Returns a field-copied JsonElement envelope, as the real serializer does.</summary>
  private sealed class CaptureSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
          StateOnly = envelope.StateOnly
        },
        $"Whizbang.Core.Observability.MessageEnvelope`1[[{payloadType.AssemblyQualifiedName}]], Whizbang.Core",
        payloadType.AssemblyQualifiedName!);
    }

    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
  }

  private sealed class CaptureTransport : ITransport {
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
