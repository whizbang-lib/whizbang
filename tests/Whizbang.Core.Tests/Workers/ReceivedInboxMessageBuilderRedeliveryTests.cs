// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A dead-lettered message re-enters the inbox as the row a delivery of the same envelope is built as (#1664): the
/// one classification and the one builder the consumer workers use, naming the dead letter it came from.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
public class ReceivedInboxMessageBuilderRedeliveryTests {
  /// <summary>An event type of the receiving service's catalog, named by the dead letter.</summary>
  public sealed record RedeliveredOrderPlaced([property: StreamId] Guid OrderId) : IEvent;

  /// <summary>A message the catalog does not name.</summary>
  public sealed record RedeliveredCommand(Guid OrderId) : ICommand;

  private static readonly string _eventType =
    typeof(RedeliveredOrderPlaced).FullName + ", " + typeof(RedeliveredOrderPlaced).Assembly.GetName().Name;

  private static readonly Guid _producer = Guid.Parse("0198f5d2-0000-7000-8000-000000000001");

  [Test]
  public async Task BuildRedelivery_IsTheRowADeliveryOfTheSameEnvelopeIsBuiltAs_NamingItsDeadLetterAsync() {
    var messageId = (Guid)TrackedGuid.New();
    var streamId = (Guid)TrackedGuid.New();
    var deadLetterId = (Guid)TrackedGuid.New();
    await using var provider = _services(new FixedEventTypeProvider(typeof(RedeliveredOrderPlaced)), registerOptions: false);
    var envelope = _envelope(messageId, streamId);

    var delivered = ReceivedInboxMessageBuilder.Build(
      new ReceivedInboxMessageBuilder.ReceivedEnvelope(envelope, envelope, EnvelopeTypeNameHelper.Format(_eventType), _eventType,
        ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, _eventType, envelope.Payload)),
      ReceivedInboxMessageBuilder.Classify(provider, envelope, _eventType),
      "TransportConsumer.Inbox", null, null);
    var recovered = ReceivedInboxMessageBuilder.BuildRedelivery(provider,
      new DeadLetterRedelivery(deadLetterId, DeadLetterSourceTable.BROKER, messageId, _eventType, _wire(envelope)));

    await Assert.That(recovered.IsEvent).IsTrue()
      .Because("the catalog names the type, so the recovered message is an event, as its delivery was");
    await Assert.That(recovered.StreamId).IsEqualTo(streamId)
      .Because("the stream is the first hop's, as a delivery reads it");
    await Assert.That(recovered.MessageId).IsEqualTo(delivered.MessageId);
    await Assert.That(recovered.IsEvent).IsEqualTo(delivered.IsEvent);
    await Assert.That(recovered.StreamId).IsEqualTo(delivered.StreamId);
    await Assert.That(recovered.Flags).IsEqualTo(delivered.Flags);
    await Assert.That(recovered.HandlerName).IsEqualTo(delivered.HandlerName);
    await Assert.That(recovered.MessageType).IsEqualTo(delivered.MessageType);
    await Assert.That(recovered.EnvelopeType).IsEqualTo(delivered.EnvelopeType);
    await Assert.That(recovered.Priority).IsEqualTo(delivered.Priority);
    await Assert.That(recovered.SourceServiceId).IsEqualTo(_producer);
    await Assert.That(recovered.SourceServiceId).IsEqualTo(delivered.SourceServiceId);
    await Assert.That(recovered.SourceCommitSequence).IsEqualTo(delivered.SourceCommitSequence);
    await Assert.That(recovered.Scope?.TenantId).IsEqualTo("tenant-a");
    await Assert.That(recovered.Scope?.TenantId).IsEqualTo(delivered.Scope?.TenantId);
    await Assert.That(recovered.RecoveredFromDeadLetterId).IsEqualTo(deadLetterId);
    await Assert.That(delivered.RecoveredFromDeadLetterId).IsNull();
  }

  [Test]
  public async Task BuildRedelivery_OfARowThatRecordedItsEnvelopeType_NamesThePayloadAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var provider = _services(new FixedEventTypeProvider(typeof(RedeliveredOrderPlaced)), registerOptions: true);
    var envelope = _envelope(messageId, (Guid)TrackedGuid.New());

    // A broker import made before #934 recorded the wire's envelope type name.
    var recovered = ReceivedInboxMessageBuilder.BuildRedelivery(provider,
      new DeadLetterRedelivery((Guid)TrackedGuid.New(), DeadLetterSourceTable.BROKER, messageId,
        EnvelopeTypeNameHelper.Format(_eventType), _wire(envelope)));

    await Assert.That(recovered.MessageType).IsEqualTo(_eventType);
    await Assert.That(recovered.IsEvent).IsTrue();
  }

  [Test]
  public async Task BuildRedelivery_OfATypeTheCatalogDoesNotName_IsNotAnEventAsync() {
    var messageId = (Guid)TrackedGuid.New();
    await using var provider = _services(new FixedEventTypeProvider(typeof(RedeliveredOrderPlaced)), registerOptions: true);
    var commandType = typeof(RedeliveredCommand).FullName + ", " + typeof(RedeliveredCommand).Assembly.GetName().Name;

    var recovered = ReceivedInboxMessageBuilder.BuildRedelivery(provider,
      new DeadLetterRedelivery((Guid)TrackedGuid.New(), DeadLetterSourceTable.INBOX, messageId, commandType,
        _wire(_envelope(messageId, (Guid)TrackedGuid.New()))));

    await Assert.That(recovered.IsEvent).IsFalse();
  }

  [Test]
  public async Task ClassifiesAsEvent_WithoutACatalog_FallsBackToThePayloadsMarkerAsync() {
    await using var provider = _services(NullEventTypeProvider.Instance, registerOptions: false);

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, _eventType, new RedeliveredOrderPlaced(Guid.Empty))).IsTrue();
    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, _eventType, new RedeliveredCommand(Guid.Empty))).IsFalse();
  }

  [Test]
  public async Task BuildRedelivery_OfAnEnvelopeForAnotherMessage_IsRefusedAsync() {
    await using var provider = _services(new FixedEventTypeProvider(typeof(RedeliveredOrderPlaced)), registerOptions: true);
    var redelivery = new DeadLetterRedelivery((Guid)TrackedGuid.New(), DeadLetterSourceTable.BROKER, (Guid)TrackedGuid.New(),
      _eventType, _wire(_envelope((Guid)TrackedGuid.New(), (Guid)TrackedGuid.New())));

    await Assert.That(() => ReceivedInboxMessageBuilder.BuildRedelivery(provider, redelivery))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task BuildRedelivery_OfAnEmptyEnvelope_IsRefusedAsync() {
    await using var provider = _services(new FixedEventTypeProvider(typeof(RedeliveredOrderPlaced)), registerOptions: true);
    var redelivery = new DeadLetterRedelivery((Guid)TrackedGuid.New(), DeadLetterSourceTable.BROKER, (Guid)TrackedGuid.New(),
      _eventType, "null");

    await Assert.That(() => ReceivedInboxMessageBuilder.BuildRedelivery(provider, redelivery))
      .Throws<InvalidOperationException>();
  }

  // ===== Helpers =====

  private static ServiceProvider _services(IEventTypeProvider eventTypes, bool registerOptions) {
    var services = new ServiceCollection();
    services.AddSingleton(eventTypes);
    if (registerOptions) {
      services.AddSingleton(JsonContextRegistry.CreateCombinedOptions());
    }
    return services.BuildServiceProvider();
  }

  private static MessageEnvelope<JsonElement> _envelope(Guid messageId, Guid streamId) => new() {
    MessageId = MessageId.From(messageId),
    Payload = JsonElement.Parse($$"""{"orderId":"{{streamId}}"}"""),
    SourceServiceId = _producer,
    SourceCommitSequence = 7,
    Hops = [new MessageHop {
      Type = HopType.Current,
      Timestamp = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.Parse("0198f5d2-0000-7000-8000-000000000002"), ServiceName = "upstream-service", HostName = "upstream-host", ProcessId = 1,
      },
      Metadata = new Dictionary<string, JsonElement> { ["AggregateId"] = JsonSerializer.SerializeToElement(streamId.ToString(), _stringInfo) },
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = "tenant-a", UserId = "SYSTEM" }),
    }],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
  };

  private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<string> _stringInfo =
    (System.Text.Json.Serialization.Metadata.JsonTypeInfo<string>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(string));

  /// <summary>The envelope as the wire carries it and dead-letter custody keeps it.</summary>
  private static string _wire(MessageEnvelope<JsonElement> envelope) =>
    JsonSerializer.Serialize(envelope, JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));

  private sealed class FixedEventTypeProvider(params Type[] eventTypes) : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => eventTypes;
  }
}
