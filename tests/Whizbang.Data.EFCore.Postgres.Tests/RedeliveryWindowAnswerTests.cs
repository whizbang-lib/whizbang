// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the origin's side of a paced backfill window (#2092, #2098): a request carrying a
/// backfill id selects by commit sequence from the requester's position, ships the window in as few bundles as the size
/// bounds allow on the backfill's session, and then answers with where the window stopped and whether more remains, so
/// an answer that stops at the origin's bound never truncates silently.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/RedeliveryRequestReceptor.cs</code-under-test>
[NotInParallel("RedeliveryBuildGate")]   // the receptor's per-process build gate is shared state
[Category("Shard2")]
public class RedeliveryWindowAnswerTests {
  private static readonly Guid _s1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
  private static readonly Guid _s2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
  private static readonly Guid _s3 = Guid.Parse("33333333-3333-3333-3333-333333333333");

  [Test]
  public async Task Window_SelectsByCommitSequenceFromThePosition_ShipsOneBundle_AndAnswersAsync() {
    var backfill = TrackedGuid.New().Value;
    var coordinator = new WindowCoordinator {
      Events = [_evt(_s1, 1, 1), _evt(_s2, 1, 2), _evt(_s3, 1, 3), _evt(_s1, 2, 4), _evt(_s2, 2, 5)],
    };
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, transport, serializer);

    await _receptor(sp).HandleAsync(_window(backfill, window: 2, from: 1, max: 100));

    await Assert.That(coordinator.Requests.All(r => r.OrderByCommitSequence)).IsTrue()
      .Because("a window walks the owner's history by commit sequence, so its last event is exactly where the next starts");
    await Assert.That(coordinator.Requests[0].FromCommitSequence).IsEqualTo(1L);
    await Assert.That(coordinator.Requests[0].AuthoredBy).IsEqualTo("orders-svc");
    await Assert.That(serializer.Composites).Count().IsEqualTo(1)
      .Because("four events on three streams ride one bundle, not one bundle per stream");
    await Assert.That(serializer.Composites[0].StreamId).IsEqualTo(backfill);
    await Assert.That(serializer.Composites[0].InnerCommitSequences).IsEquivalentTo([(long?)4, 2, 5, 3])
      .Because("inside the window the bundle is in (stream, version) order, as the receiver's fan-out expects");
    var answer = serializer.Answers.Single();
    await Assert.That(answer.Target).IsEqualTo("damaged-svc");
    await Assert.That(answer.Payload.BackfillId).IsEqualTo(backfill);
    await Assert.That(answer.Payload.Window).IsEqualTo(2L);
    await Assert.That(answer.Payload.EventCount).IsEqualTo(4);
    await Assert.That(answer.Payload.FirstCommitSequence).IsEqualTo(2L);
    await Assert.That(answer.Payload.StoppedAtCommitSequence).IsEqualTo(5L);
    await Assert.That(answer.Payload.HasMore).IsFalse();
    await Assert.That(answer.Payload.HeadCommitSequence).IsEqualTo(5L);
    await Assert.That(answer.Payload.OriginServiceId).IsEqualTo(coordinator.LocalServiceId);
    await Assert.That(answer.Payload.OriginServiceName).IsEqualTo("orders-svc");
    await Assert.That(transport.Published[^1].Envelope.Target).IsEqualTo("damaged-svc")
      .Because("the answer is sent after the window's bundles");
  }

  [Test]
  public async Task Window_PagesByCommitSequence_NeverByStreamKeysetAsync() {
    var coordinator = new WindowCoordinator {
      Events = [_evt(_s1, 1, 1), _evt(_s2, 1, 2), _evt(_s3, 1, 3), _evt(_s1, 2, 4), _evt(_s2, 2, 5)],
    };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, new RedeliveryPumpOptions { SelectPageSize = 2 });

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 1, from: null, max: 100));

    await Assert.That(coordinator.Requests.Select(r => r.FromCommitSequence)).IsEquivalentTo([(long?)null, 2, 4])
      .Because("each page continues after the last commit sequence of the one before");
    await Assert.That(coordinator.Requests.All(r => r.AfterStreamId is null)).IsTrue();
    await Assert.That(serializer.Composites.SelectMany(c => c.InnerCommitSequences!)).IsEquivalentTo([(long?)1, 2, 3, 4, 5]);
  }

  [Test]
  public async Task Window_StoppedAtTheOwnerBound_SaysWhereAndThatMoreRemains_AndTheNextWindowsReceiveTheRestAsync() {
    // #2098: an owner capped every answer at its bound and stopped without a word, so a type larger than the bound never
    // completed. Here the bound is 3 and the type has 7 events: every event arrives, across three windows.
    var coordinator = new WindowCoordinator {
      Events = [.. Enumerable.Range(1, 7).Select(i => _evt(i % 2 == 0 ? _s1 : _s2, i, i))],
    };
    var serializer = new CaptureSerializer();
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var capped = new MetricProbe(metrics.RedeliveryAnswersCapped.Instrument, "kind");
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer,
      new RedeliveryPumpOptions { MaxEventsPerRequest = 3, SelectPageSize = 2 }, metrics);
    var receptor = _receptor(sp);
    var backfill = TrackedGuid.New().Value;

    long? position = null;
    var window = 0L;
    BackfillWindowAnswer answer;
    do {
      window++;
      await receptor.HandleAsync(_window(backfill, window, position, max: 1_000));
      answer = serializer.Answers[^1].Payload;
      position = answer.StoppedAtCommitSequence ?? position;
    } while (answer.HasMore && window < 10);

    await Assert.That(window).IsEqualTo(3L).Because("3 + 3 + 1: the requester's 1,000 is clamped to the owner's bound of 3");
    await Assert.That(serializer.Answers.Select(a => (a.Payload.StoppedAtCommitSequence, a.Payload.HasMore)))
      .IsEquivalentTo([((long?)3, true), ((long?)6, true), ((long?)7, false)]);
    await Assert.That(serializer.Composites.SelectMany(c => c.InnerCommitSequences!).Order())
      .IsEquivalentTo([(long?)1, 2, 3, 4, 5, 6, 7])
      .Because("every event of the type arrives exactly once across the windows");
    await Assert.That(capped.Total("window")).IsEqualTo(2L).Because("the two windows that stopped at the bound are counted; the last, short window is not");
  }

  [Test]
  public async Task Window_SelectingNothing_AnswersThatNothingRemains_WithoutABundleAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1)] };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer);

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 4, from: 1, max: 100));

    await Assert.That(serializer.Composites).IsEmpty();
    var answer = serializer.Answers.Single().Payload;
    await Assert.That(answer.EventCount).IsEqualTo(0);
    await Assert.That(answer.StoppedAtCommitSequence).IsNull().Because("nothing selected, so the requester's position does not move");
    await Assert.That(answer.FirstCommitSequence).IsNull();
    await Assert.That(answer.HasMore).IsFalse();
  }

  [Test]
  public async Task Window_OfEventsThisServiceDoesNotOwn_ShipsNothing_ButStillMovesThePositionAsync() {
    var coordinator = new WindowCoordinator {
      Events = [_evt(_s1, 1, 1) with { EventType = "App.Contracts.Billing.Raised, App.Contracts" }],
    };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, ownedDomains: "App.Contracts.Orders");

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 1, from: null, max: 100));

    await Assert.That(serializer.Composites).IsEmpty().Because("events another service authored are refused by every receiver (#1844)");
    var answer = serializer.Answers.Single().Payload;
    await Assert.That(answer.EventCount).IsEqualTo(1);
    await Assert.That(answer.StoppedAtCommitSequence).IsEqualTo(1L).Because("the next window must not select the same rows again");
  }

  [Test]
  public async Task OrdinaryRequest_StoppedAtTheBoundWithMoreLeft_IsCountedAsCappedAsync() {
    // An ordinary request has no window to continue from, so stopping at the bound with more selected is reported, never
    // silent.
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1), _evt(_s1, 2, 2), _evt(_s1, 3, 3)] };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var capped = new MetricProbe(metrics.RedeliveryAnswersCapped.Instrument, "kind");
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer,
      new RedeliveryPumpOptions { MaxEventsPerRequest = 2 }, metrics);

    await _receptor(sp).HandleAsync(new RequestRedeliveryCommand { RequesterService = "damaged-svc", Topic = "inbox" });

    await Assert.That(capped.Total("ordinary")).IsEqualTo(1L);
    await Assert.That(serializer.Answers).IsEmpty().Because("only a windowed request is answered with a window answer");
  }

  [Test]
  public async Task OrdinaryRequest_ThatSelectedEverything_IsNotCappedAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1), _evt(_s1, 2, 2)] };
    var metrics = _metrics(out var meterServices);
    await using var ownedMeters = meterServices;
    using var capped = new MetricProbe(metrics.RedeliveryAnswersCapped.Instrument, "kind");
    await using var sp = _provider(coordinator, new CaptureTransport(), new CaptureSerializer(),
      new RedeliveryPumpOptions { MaxEventsPerRequest = 2 }, metrics);

    await _receptor(sp).HandleAsync(new RequestRedeliveryCommand { RequesterService = "damaged-svc", Topic = "inbox" });

    await Assert.That(capped.Sum()).IsEqualTo(0L).Because("exactly the bound, with nothing after it, is a complete answer");
  }

  [Test]
  public async Task OrdinaryRequest_StoppedAtTheBound_WithoutMetrics_StillAnswersAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1), _evt(_s1, 2, 2), _evt(_s1, 3, 3)] };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, new RedeliveryPumpOptions { MaxEventsPerRequest = 2 });

    await _receptor(sp).HandleAsync(new RequestRedeliveryCommand { RequesterService = "damaged-svc", Topic = "inbox" });

    await Assert.That(serializer.Composites.SelectMany(c => c.InnerEventIds)).Count().IsEqualTo(2);
  }

  [Test]
  public async Task Window_AtTheBound_WithoutMetrics_StillAnswersAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1), _evt(_s1, 2, 2), _evt(_s1, 3, 3)] };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer, new RedeliveryPumpOptions { MaxEventsPerRequest = 2 });

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 1, from: null, max: null));

    await Assert.That(serializer.Answers.Single().Payload.HasMore).IsTrue()
      .Because("an unspecified window size is the owner's bound, and stopping there may leave more");
  }

  [Test]
  public async Task Window_NamingNoEventType_IsNotAnsweredAsync() {
    var coordinator = new WindowCoordinator { Events = [_evt(_s1, 1, 1)] };
    var serializer = new CaptureSerializer();
    await using var sp = _provider(coordinator, new CaptureTransport(), serializer);

    await _receptor(sp).HandleAsync(_window(TrackedGuid.New().Value, window: 1, from: null, max: 10) with { EventTypes = null });

    await Assert.That(coordinator.Requests).IsEmpty().Because("a window walks one type's history; with no type there is nothing to walk");
    await Assert.That(serializer.Answers).IsEmpty();
  }

  // ── helpers / fakes ─────────────────────────────────────────────────────

  private static RequestRedeliveryCommand _window(Guid backfill, long window, long? from, int? max) => new() {
    EventTypes = ["Contracts.ProbeHappened"],
    RequesterService = "damaged-svc",
    Topic = "inbox.whizbang.service.damaged-svc",
    StateOnly = true,
    FromCommitSequence = from,
    MaxEvents = max,
    BackfillId = backfill,
    BackfillWindow = window,
  };

  private static RedeliveryRequestReceptor _receptor(ServiceProvider sp) =>
    new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);

  private static ServiceProvider _provider(
      WindowCoordinator coordinator, CaptureTransport transport, CaptureSerializer serializer,
      RedeliveryPumpOptions? options = null, StreamIntegrityMetrics? metrics = null, params string[] ownedDomains) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IEnvelopeSerializer>(serializer);
    services.AddSingleton(options ?? new RedeliveryPumpOptions());
    services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairMode = IntegrityRepairMode.AutoRepairCapped }));
    services.AddSingleton<IServiceInstanceProvider>(new NamedInstance("orders-svc"));
    if (ownedDomains.Length > 0) {
      services.AddSingleton(Options.Create(new Whizbang.Core.Routing.RoutingOptions().OwnDomains(ownedDomains)));
    }
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    return services.BuildServiceProvider();
  }

  private static StreamIntegrityMetrics _metrics(out ServiceProvider meterServices) {
    meterServices = new ServiceCollection().AddMetrics().BuildServiceProvider();
    return new StreamIntegrityMetrics(new WhizbangMetrics(meterServices.GetRequiredService<IMeterFactory>()));
  }

  /// <summary>
  /// Reads one passive counter's cumulative totals by one tag. The counters are observable, so each read asks the
  /// listener to observe them now; the probe is pinned to that instrument instance, since parallel tests build meters
  /// of the same name.
  /// </summary>
  private sealed class MetricProbe : IDisposable {
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = new(StringComparer.Ordinal);
    private readonly string _tagKey;

    public MetricProbe(Instrument instrument, string tagKey) {
      _tagKey = tagKey;
      _listener.InstrumentPublished = (published, l) => {
        if (ReferenceEquals(published, instrument)) {
          l.EnableMeasurementEvents(published);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        var tag = string.Empty;
        foreach (var kv in tags) {
          if (kv.Key == _tagKey) {
            tag = kv.Value as string ?? string.Empty;
          }
        }
        lock (_totals) {
          _totals[tag] = value;
        }
      });
      _listener.Start();
    }

    public long Total(string tag) {
      _listener.RecordObservableInstruments();
      lock (_totals) {
        return _totals.TryGetValue(tag, out var total) ? total : 0;
      }
    }

    public long Sum() {
      _listener.RecordObservableInstruments();
      lock (_totals) {
        return _totals.Values.Sum();
      }
    }

    public void Dispose() => _listener.Dispose();
  }

  private static RedeliveryEvent _evt(Guid streamId, long version, long commitSequence) => new() {
    EventId = TrackedGuid.New().Value,
    StreamId = streamId,
    Version = version,
    CommitSequence = commitSequence,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
  };

  /// <summary>Serves the canned events with the real selection contracts: commit-sequence order from the floor when
  /// asked, (stream, version) keyset order otherwise, at most MaxEvents.</summary>
  private sealed class WindowCoordinator : IWorkCoordinator {
    public List<RedeliveryRequest> Requests { get; } = [];
    public IReadOnlyList<RedeliveryEvent> Events { get; init; } = [];
    public Guid LocalServiceId { get; } = TrackedGuid.New().Value;

    public Task<IReadOnlyList<RedeliveryEvent>> SelectRedeliveryEventsAsync(RedeliveryRequest request, CancellationToken cancellationToken = default) {
      Requests.Add(request);
      IEnumerable<RedeliveryEvent> query = Events;
      if (request.FromCommitSequence is { } from) {
        query = query.Where(e => e.CommitSequence > from);
      }
      if (request.OrderByCommitSequence) {
        query = query.OrderBy(e => e.CommitSequence);
      } else {
        query = query.OrderBy(e => e.StreamId).ThenBy(e => e.Version);
        if (request.AfterStreamId is { } afterStream) {
          var afterVersion = request.AfterVersion ?? 0L;
          query = query.Where(e => e.StreamId.CompareTo(afterStream) > 0 || (e.StreamId == afterStream && e.Version > afterVersion));
        }
      }
      return Task.FromResult<IReadOnlyList<RedeliveryEvent>>([.. query.Take(request.MaxEvents)]);
    }

    public Task<long?> GetRedeliveryHeadCommitSequenceAsync(IReadOnlyList<string> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(Events.Max(e => e.CommitSequence));

    public Task<Guid> GetLocalServiceIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(LocalServiceId);

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

  private sealed class CaptureSerializer : IEnvelopeSerializer {
    public List<RedeliveryComposite> Composites { get; } = [];
    public List<IMessageEnvelope<BackfillWindowAnswer>> Answers { get; } = [];

    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      if (envelope is IMessageEnvelope<RedeliveryComposite> composite) {
        Composites.Add(composite.Payload);
      } else {
        Answers.Add((IMessageEnvelope<BackfillWindowAnswer>)envelope);
      }
      var payloadType = envelope.Payload!.GetType();
      return new SerializedEnvelope(
        new MessageEnvelope<System.Text.Json.JsonElement> {
          MessageId = envelope.MessageId,
          Payload = default,
          Hops = [.. envelope.Hops],
          DispatchContext = envelope.DispatchContext,
          Target = envelope.Target,
          StateOnly = envelope.StateOnly,
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

  private sealed class NamedInstance(string serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.Parse("0d000000-0000-0000-0000-00000000000d");
    public string ServiceName => serviceName;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }
}
