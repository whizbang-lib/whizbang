// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The operator's repair for events lost in transport (#1028): ask an origin to republish the stored events of named
/// streams. It sends the request the integrity ledger's drain sends, directed at the origin, marked operator-requested
/// so report-only services on both sides serve and apply it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamRedeliveryRequester.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryRequestDispatch.cs</code-under-test>
[Category("Unit")]
[Category("StreamIntegrity")]
public class StreamRedeliveryRequesterTests {
  private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  [Test]
  public async Task Request_SendsOneDirectedOperatorRequestPerChunkOfStreamsAsync() {
    var fixture = new Fixture { RequestTopic = "origin.requests" };
    var streams = Enumerable.Range(0, StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST + 1).Select(_ => TrackedGuid.New().Value).ToList();
    streams.Add(streams[0]);

    var receipt = await fixture.Requester().RequestAsync(new StreamRedeliveryRequest {
      OriginService = "origin-svc",
      StreamIds = streams,
      TenantScope = "tenant-a",
      EventTypes = ["Contracts.OrderPlaced"],
      StateOnly = true,
    });

    await Assert.That(receipt).IsEqualTo(new StreamRedeliveryReceipt(
      "origin-svc", "origin.requests", "inbox", StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST + 1, 2));
    await Assert.That(fixture.Transport.Published).Count().IsEqualTo(2)
      .Because("A long list is split so each request stays within a broker message; a repeated stream is asked for once.");
    var (envelope, destination, _) = fixture.Transport.Published[0];
    var command = _command(envelope);
    await Assert.That(envelope.Target).IsEqualTo("origin-svc");
    await Assert.That(destination.Address).IsEqualTo("origin.requests");
    await Assert.That(command.StreamIds).Count().IsEqualTo(StreamRedeliveryRequester.MAX_STREAMS_PER_REQUEST);
    await Assert.That(command.OperatorRequested).IsTrue();
    await Assert.That(command.RequesterService).IsEqualTo("receiver-svc");
    await Assert.That(command.Topic).IsEqualTo("inbox");
    await Assert.That(command.TenantScope).IsEqualTo("tenant-a");
    await Assert.That(command.EventTypes).IsEquivalentTo(["Contracts.OrderPlaced"]);
    await Assert.That(command.StateOnly).IsTrue();
    await Assert.That(_command(fixture.Transport.Published[1].Envelope).StreamIds).IsEquivalentTo([streams[^2]]);
  }

  [Test]
  public async Task Request_NamedTopics_WinOverTheLearnedOnesAsync() {
    var fixture = new Fixture { RequestTopic = "learned.requests", RepairTopic = "repair" };

    var receipt = await fixture.Requester().RequestAsync(new StreamRedeliveryRequest {
      OriginService = "origin-svc",
      StreamIds = [TrackedGuid.New().Value],
      OriginRequestTopic = "named.requests",
      ReplyTopic = "named.replies",
    });

    await Assert.That(receipt.RequestTopic).IsEqualTo("named.requests");
    await Assert.That(receipt.ReplyTopic).IsEqualTo("named.replies");
  }

  [Test]
  public async Task Request_ReplyTopic_IsTheRepairTopicBeforeTheInboxAsync() {
    var fixture = new Fixture { RequestTopic = "origin.requests", RepairTopic = "repair" };

    var receipt = await fixture.Requester().RequestAsync(new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [TrackedGuid.New().Value] });

    await Assert.That(receipt.ReplyTopic).IsEqualTo("repair");
  }

  [Test]
  public async Task Request_NoIntegrityOptions_RepliesOnTheInboxAsync() {
    var fixture = new Fixture { RequestTopic = "origin.requests", WithDefaults = false, WithIntegrityOptions = false };

    var receipt = await fixture.Requester().RequestAsync(new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [TrackedGuid.New().Value] });

    await Assert.That(receipt.ReplyTopic).IsEqualTo("inbox")
      .Because("with no options system at all, the reply still comes back on a topic this service consumes");
  }

  [Test]
  public async Task Request_WithoutMetrics_StillSendsAsync() {
    var fixture = new Fixture { RequestTopic = "origin.requests", WithDefaults = false };

    var receipt = await fixture.Requester().RequestAsync(new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [TrackedGuid.New().Value] });

    await Assert.That(receipt.Requests).IsEqualTo(1);
  }

  [Test]
  public async Task Request_AnOriginWhoseTopicIsUnknown_IsRefusedWithTheFixAsync() {
    var fixture = new Fixture { RequestTopic = null };

    await Assert.That(() => (Task)fixture.Requester().RequestAsync(new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [Guid.NewGuid()] }))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("origin request topic");
    await Assert.That(fixture.Transport.Published).IsEmpty();
  }

  [Test]
  public async Task Request_WithoutAGapTracker_NeedsTheTopicNamedAsync() {
    var fixture = new Fixture { RequestTopic = null, WithTracker = false };

    await Assert.That(() => (Task)fixture.Requester().RequestAsync(new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [Guid.NewGuid()] }))
      .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task Request_WithoutTheMeansToSend_IsRefusedAndSaysWhyAsync() {
    var request = new StreamRedeliveryRequest { OriginService = "origin-svc", StreamIds = [Guid.NewGuid()], OriginRequestTopic = "t" };

    await Assert.That(() => (Task)new Fixture { WithTransport = false }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("no transport");
    await Assert.That(() => (Task)new Fixture { WithSerializer = false, WithDefaults = false }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("serializer");
    await Assert.That(() => (Task)new Fixture { ServiceName = null }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("no service name");
    await Assert.That(() => (Task)new Fixture { ServiceName = " " }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("no service name");
    await Assert.That(() => (Task)new Fixture { WithInstance = false, WithDefaults = false }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("no service name");
    await Assert.That(() => (Task)new Fixture { WithConsumer = false, WithIntegrityOptions = false }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("reply topic");
    await Assert.That(() => (Task)new Fixture { ConsumerTopic = null }.Requester().RequestAsync(request))
      .ThrowsExactly<InvalidOperationException>().WithMessageContaining("reply topic")
      .Because("A consumer with no destination consumes nothing the origin could reply on.");
  }

  [Test]
  public async Task Request_WithNoOriginOrNoStreams_IsRefusedAsync() {
    var requester = new Fixture { RequestTopic = "t" }.Requester();

    await Assert.That(() => (Task)requester.RequestAsync(new StreamRedeliveryRequest { OriginService = " ", StreamIds = [Guid.NewGuid()] }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(() => (Task)requester.RequestAsync(new StreamRedeliveryRequest { OriginService = "o", StreamIds = [] }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(() => (Task)requester.RequestAsync(new StreamRedeliveryRequest { OriginService = "o", StreamIds = null! }))
      .ThrowsExactly<ArgumentException>();
    await Assert.That(() => (Task)requester.RequestAsync(null!)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => new StreamRedeliveryRequester(null!, NullLogger<StreamRedeliveryRequester>.Instance))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AddWhizbang_RegistersTheRequesterAsync() {
    var services = new ServiceCollection();
    services.AddWhizbang();

    await Assert.That(services.Any(d => d.ServiceType == typeof(IStreamRedeliveryRequester)
      && d.ImplementationType == typeof(StreamRedeliveryRequester))).IsTrue();
  }

  private static RequestRedeliveryCommand _command(IMessageEnvelope envelope) {
    var options = JsonContextRegistry.CreateCombinedOptions();
    return (RequestRedeliveryCommand)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(RequestRedeliveryCommand)))!;
  }

  private sealed class Fixture {
    public string? RequestTopic { get; init; }
    public string? RepairTopic { get; init; }
    public string? ServiceName { get; init; } = "receiver-svc";
    public bool WithTransport { get; init; } = true;
    public bool WithSerializer { get; init; } = true;
    public bool WithTracker { get; init; } = true;
    public bool WithConsumer { get; init; } = true;
    public bool WithDefaults { get; init; } = true;
    public bool WithInstance { get; init; } = true;
    public bool WithIntegrityOptions { get; init; } = true;
    public string? ConsumerTopic { get; init; } = "inbox";
    public CaptureTransport Transport { get; } = new();

    public StreamRedeliveryRequester Requester() {
      var services = new ServiceCollection();
      if (WithDefaults) {
        services.TryAddWhizbangDefaults();
        services.AddSingleton<WhizbangMetrics>();
        services.AddSingleton<StreamIntegrityMetrics>();
      }
      if (WithTransport) {
        services.AddSingleton<ITransport>(Transport);
      }
      if (WithSerializer) {
        services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(JsonContextRegistry.CreateCombinedOptions()));
      }
      if (WithTracker) {
        var tracker = new IntegrityGapTracker();
        tracker.RecordCheckpoint(Guid.NewGuid(), "other-svc", _now, "other.requests");
        tracker.RecordCheckpoint(Guid.NewGuid(), "origin-svc", _now, RequestTopic);
        services.AddSingleton(tracker);
      }
      if (WithConsumer) {
        var consumer = new TransportConsumerOptions();
        if (ConsumerTopic is not null) {
          consumer.Destinations.Add(new TransportDestination(ConsumerTopic));
        }
        services.AddSingleton(consumer);
      }
      if (WithIntegrityOptions) {
        services.AddSingleton(Options.Create(new StreamIntegrityOptions { RepairTopic = RepairTopic }));
      }
      if (WithInstance) {
        services.AddSingleton<IServiceInstanceProvider>(new InstanceProvider(ServiceName));
      }
      var provider = services.BuildServiceProvider();
      return new StreamRedeliveryRequester(
        provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StreamRedeliveryRequester>.Instance, new FixedClock(_now));
    }
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private sealed class CaptureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      Published.Add((envelope, destination, envelopeType));
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class InstanceProvider(string? serviceName) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = TrackedGuid.New().Value;
    public string ServiceName => serviceName!;
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName ?? "", HostName = HostName, ProcessId = ProcessId };
  }
}
