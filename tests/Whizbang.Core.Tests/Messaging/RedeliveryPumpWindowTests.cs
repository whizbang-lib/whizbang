// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
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

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2092: a windowed backfill's answer ships many events per bundle across streams, every bundle and the window answer
/// on the backfill's own session, instead of one bundle per stream.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/RedeliveryPump.cs</code-under-test>
public class RedeliveryPumpWindowTests {

  [Test]
  public async Task Publish_WithABundleStream_ShipsManyStreamsInOneBundle_OnTheBackfillSessionAsync() {
    var backfill = TrackedGuid.New().Value;
    var s1 = TrackedGuid.New().Value;
    var s2 = TrackedGuid.New().Value;
    var s3 = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory()) { BundleStreamId = backfill };

    var published = await pump.PublishAsync(
      [_evt(s1, 1, 10), _evt(s2, 1, 11), _evt(s3, 1, 12), _evt(s3, 2, 13)], topic: "inbox.whizbang.service.billing", target: "billing");

    await Assert.That(published).IsEqualTo(1)
      .Because("one bundle per stream was the flood: three streams' events fit one bundle under the size bounds");
    var composite = serializer.Composites[0];
    await Assert.That(composite.StreamId).IsEqualTo(backfill)
      .Because("the bundle's own stream is the backfill's, so every bundle of the backfill shares one session");
    await Assert.That(composite.InnerStreamIds).IsEquivalentTo([s1, s2, s3, s3])
      .Because("each child is still filed under the stream it is stored on");
    await Assert.That(composite.InnerCommitSequences).IsEquivalentTo([(long?)10, 11, 12, 13]);
    await Assert.That(transport.Published[0].Destination.Metadata!["StreamId"].GetString()).IsEqualTo(backfill.ToString());
  }

  [Test]
  public async Task Publish_WithABundleStream_StillSplitsAtTheCountBoundAndByScopeAsync() {
    var backfill = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory(),
      new RedeliveryPumpOptions { MaxInnerEventsPerComposite = 2 }) { BundleStreamId = backfill };

    var published = await pump.PublishAsync([
      _evt(TrackedGuid.New().Value, 1, 1), _evt(TrackedGuid.New().Value, 1, 2), _evt(TrackedGuid.New().Value, 1, 3),
      _evt(TrackedGuid.New().Value, 1, 4) with { Scope = /*lang=json,strict*/ "{\"t\":\"tenant-b\"}" },
    ], topic: "t", target: "billing");

    await Assert.That(published).IsEqualTo(3)
      .Because("three unscoped events split at the bound of two, and the other tenant's event never shares a bundle");
    await Assert.That(serializer.Composites.All(c => c.StreamId == backfill)).IsTrue();
  }

  [Test]
  public async Task PublishWindowAnswer_SendsTheAnswerToTheRequester_OnTheBackfillSessionAsync() {
    var backfill = TrackedGuid.New().Value;
    var transport = new CaptureTransport();
    var serializer = new CaptureSerializer();
    var pump = new RedeliveryPump(transport, serializer, _instance(), new CompositeFactory()) {
      AnswerTimeToLive = TimeSpan.FromMinutes(60),
    };
    var answer = new BackfillWindowAnswer {
      BackfillId = backfill,
      Window = 3,
      EventCount = 5,
      FirstCommitSequence = 20,
      StoppedAtCommitSequence = 40,
      HasMore = true,
    };

    await pump.PublishWindowAnswerAsync(answer, "inbox.whizbang.service.billing", "billing");

    await Assert.That(transport.Published).Count().IsEqualTo(1);
    var (envelope, destination, _) = transport.Published[0];
    await Assert.That(envelope.Target).IsEqualTo("billing");
    await Assert.That(destination.Address).IsEqualTo("inbox.whizbang.service.billing");
    await Assert.That(destination.Metadata!["StreamId"].GetString()).IsEqualTo(backfill.ToString())
      .Because("the answer rides the backfill's session, behind the window's bundles");
    await Assert.That(ControlMessageTtl.FromMetadata(destination.Metadata)).IsEqualTo(TimeSpan.FromMinutes(60));
    await Assert.That(serializer.Answers[0].Payload).IsEqualTo(answer);
    await Assert.That(serializer.Answers[0].Priority).IsEqualTo(Whizbang.Core.Priority.WorkPriority.BACKGROUND);
  }

  [Test]
  public async Task PublishWindowAnswer_WithoutATimeToLive_LeavesTheBrokerExpiryAsync() {
    var transport = new CaptureTransport();
    var pump = new RedeliveryPump(transport, new CaptureSerializer(), _instance(), new CompositeFactory());

    await pump.PublishWindowAnswerAsync(
      new BackfillWindowAnswer { BackfillId = TrackedGuid.New().Value, Window = 1 }, "t", "billing");

    await Assert.That(ControlMessageTtl.FromMetadata(transport.Published[0].Destination.Metadata)).IsNull();
  }

  private static ServiceInstanceProvider _instance() => new(configuration: new ConfigurationBuilder().Build());

  private static RedeliveryEvent _evt(Guid streamId, long version, long commitSequence) => new() {
    EventId = TrackedGuid.New().Value,
    StreamId = streamId,
    Version = version,
    CommitSequence = commitSequence,
    EventType = "Contracts.ProbeHappened",
    EventData = /*lang=json,strict*/ "{\"seeded\":true}",
    Metadata = "{}",
  };

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
        new MessageEnvelope<JsonElement> {
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

    public object DeserializeMessage(MessageEnvelope<JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotSupportedException();
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
}
