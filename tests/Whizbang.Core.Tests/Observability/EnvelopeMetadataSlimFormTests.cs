// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// #1586: the slim stored form of envelope metadata keeps what readers take from a stored event (message id,
/// message timestamp, correlation, causation, scope, ephemeral TTL) and drops the rest, and it reads back through
/// the source-generated context without a service instance on its hop.
/// </summary>
/// <docs>messaging/message-envelopes#stored-form</docs>
[Category("Observability")]
public class EnvelopeMetadataSlimFormTests {
  private static readonly DateTimeOffset _sent = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
  private static readonly MessageId _id = MessageId.From(Guid.Parse("01900000-0000-7000-8000-000000000001"));
  private static readonly MessageId _cause = MessageId.From(Guid.Parse("01900000-0000-7000-8000-000000000002"));
  private static readonly CorrelationId _correlation = CorrelationId.FromExternal(Guid.Parse("01900000-0000-7000-8000-000000000003"));

  private static readonly ServiceInstanceInfo _instance = new() {
    ServiceName = "orders", InstanceId = Guid.Parse("01900000-0000-7000-8000-000000000004"), HostName = "host-1", ProcessId = 42,
  };

  private static EnvelopeMetadata _full() => new() {
    MessageId = _id,
    EphemeralTtlSeconds = 3600,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Local },
    ReceptorInvocations = [],
    Hops = [
      new MessageHop {
        Type = HopType.Current, ServiceInstance = _instance, Timestamp = _sent, Topic = "orders", StreamId = "order-1",
        CorrelationId = _correlation, CausationId = _cause, TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        CallerMemberName = "Place", CallerFilePath = "/src/Orders.cs", CallerLineNumber = 10, ExecutionStrategy = "Serial",
        PartitionIndex = 3, SequenceNumber = 7, Duration = TimeSpan.FromMilliseconds(5),
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a" }),
      },
      new MessageHop {
        Type = HopType.Causation, ServiceInstance = _instance, Timestamp = _sent.AddSeconds(1),
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "causation-tenant" }),
      },
      new MessageHop {
        Type = HopType.Current, ServiceInstance = _instance, Timestamp = _sent.AddSeconds(2), StreamId = "order-1",
        Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a", UserId = "user-b" }),
      },
    ],
  };

  [Test]
  public async Task ToSlim_KeepsWhatReadersTakeFromAStoredEventAsync() {
    var full = _full();
    var slim = full.ToSlim();
    var fullEnvelope = _envelope(full);
    var slimEnvelope = _envelope(slim);

    await Assert.That(slim.MessageId).IsEqualTo(_id);
    await Assert.That(slim.EphemeralTtlSeconds).IsEqualTo(3600);
    await Assert.That(slimEnvelope.GetMessageTimestamp()).IsEqualTo(fullEnvelope.GetMessageTimestamp());
    await Assert.That(slimEnvelope.GetCorrelationId()).IsEqualTo(fullEnvelope.GetCorrelationId());
    await Assert.That(slimEnvelope.GetCausationId()).IsEqualTo(fullEnvelope.GetCausationId());

    var fullScope = EnvelopeContextExtractor.ExtractScope(full.Hops);
    var slimScope = EnvelopeContextExtractor.ExtractScope(slim.Hops);
    await Assert.That(slimScope!.Scope?.TenantId).IsEqualTo(fullScope!.Scope?.TenantId);
    await Assert.That(slimScope.Scope?.UserId).IsEqualTo("user-b")
      .Because("the scope is folded over every Current hop, so a value an earlier hop set and a later one changed reads the same");
    await Assert.That(slimScope.Scope?.TenantId).IsEqualTo("tenant-a")
      .Because("a causation hop's scope is not part of the event's scope, slim or full");
  }

  [Test]
  public async Task ToSlim_DropsEverythingElseAsync() {
    var slim = _full().ToSlim();
    var hop = slim.Hops.Single();

    await Assert.That(hop.Type).IsEqualTo(HopType.Current);
    await Assert.That(hop.StreamId).IsEqualTo("order-1");
    await Assert.That(hop.ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
    await Assert.That(hop.TraceParent).IsNull();
    await Assert.That(hop.CallerMemberName).IsNull();
    await Assert.That(hop.Topic).IsEmpty();
    await Assert.That(slim.DispatchContext).IsNull();
    await Assert.That(slim.ReceptorInvocations).IsNull();

    var json = JsonSerializer.Serialize(slim, InfrastructureJsonContext.Default.EnvelopeMetadata);
    await Assert.That(json).DoesNotContain("\"si\"")
      .Because("an unknown service instance is not written, which is most of a hop's bytes");
    await Assert.That(json.Length).IsLessThan(JsonSerializer.Serialize(_full(), InfrastructureJsonContext.Default.EnvelopeMetadata).Length / 2);
  }

  [Test]
  public async Task SlimDocument_RoundTripsThroughTheGeneratedContextAsync() {
    var slim = _full().ToSlim();

    var json = JsonSerializer.Serialize(slim, InfrastructureJsonContext.Default.EnvelopeMetadata);
    var read = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.EnvelopeMetadata)!;

    await Assert.That(read.MessageId).IsEqualTo(_id);
    await Assert.That(read.Hops.Single().ServiceInstance).IsEqualTo(ServiceInstanceInfo.Unknown);
    await Assert.That(read.Hops.Single().CorrelationId).IsEqualTo(_correlation);
    await Assert.That(read.Hops.Single().Timestamp).IsEqualTo(_sent);
    await Assert.That(EnvelopeContextExtractor.ExtractScope(read.Hops)!.Scope?.UserId).IsEqualTo("user-b");
  }

  [Test]
  public async Task ToSlim_NoHopsOrNoScope_KeepsTheMessageIdAloneAsync() {
    var empty = new EnvelopeMetadata { MessageId = _id, Hops = [] }.ToSlim();
    var unscoped = new EnvelopeMetadata {
      MessageId = _id, Hops = [new MessageHop { ServiceInstance = _instance, Timestamp = _sent }],
    }.ToSlim();

    await Assert.That(empty.Hops).IsEmpty();
    await Assert.That(unscoped.Hops.Single().Scope).IsNull();
    await Assert.That(unscoped.Hops.Single().Timestamp).IsEqualTo(_sent);
  }

  [Test]
  public async Task ADocumentWithoutAMessageId_StillFailsAsync() {
    const string json = """{"Hops":[{"ts":"2026-01-01T12:00:00+00:00"}]}""";

    await Assert.That(() => JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.EnvelopeMetadata))
      .Throws<JsonException>()
      .Because("the message id is how every reader finds the event; a document without one is corrupt, slim or not");
  }

  private static MessageEnvelope<JsonElement> _envelope(EnvelopeMetadata metadata) => new() {
    MessageId = metadata.MessageId,
    Payload = default,
    Hops = metadata.Hops,
    DispatchContext = metadata.DispatchContext ?? new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };
}
