// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The inbox receive span: started under the producer's trace when something listens to the
/// transport source, and absent when nothing does.
/// </summary>
/// <remarks>
/// Serialized with every test that registers an activity listener, because listeners are
/// process-wide: one registered by a concurrent test would start the span the no-listener case
/// expects to be absent.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/TransportConsumerWorker.cs</code-under-test>
[NotInParallel(["WhizbangActivityListener", "ActivityListener"])]
public class TransportConsumerWorkerInboxActivityTests {
  private const string TRACE_PARENT = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

  [Test]
  public async Task StartInboxActivity_WithAListener_StartsTheTaggedSpanUnderTheProducersTraceAsync() {
    using var listener = new ActivityListener {
      // The literal name, not WhizbangActivitySource.Transport.Name: the listener is asked about each
      // source as it is created, which can be inside that type's own static initializer, before the
      // field this would read is assigned.
      ShouldListenTo = source => source.Name == "Whizbang.Transport",
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    };
    ActivitySource.AddActivityListener(listener);
    var envelope = _envelopeWithTraceParent();

    using var activity = TransportConsumerWorker.StartInboxActivity(envelope, "OrderPlaced");

    await Assert.That(activity).IsNotNull();
    await Assert.That(activity!.DisplayName).IsEqualTo("Inbox OrderPlaced");
    await Assert.That(activity.TraceId.ToString()).IsEqualTo("0af7651916cd43dd8448eb211c80319c");
    await Assert.That(activity.GetTagItem("messaging.message_id")).IsEqualTo(envelope.MessageId.ToString());
    await Assert.That(activity.GetTagItem("messaging.operation")).IsEqualTo("receive");
    await Assert.That(activity.GetTagItem("whizbang.hop_count")).IsEqualTo(1);
  }

  [Test]
  public async Task StartInboxActivity_WithNothingListening_ReturnsNoSpanAsync() {
    using var activity = TransportConsumerWorker.StartInboxActivity(_envelopeWithTraceParent(), "OrderPlaced");

    await Assert.That(activity).IsNull()
      .Because("with no listener on the transport source there is no span to tag");
  }

  private static MessageEnvelope<JsonElement> _envelopeWithTraceParent() => new() {
    MessageId = MessageId.New(),
    Payload = JsonDocument.Parse("{}").RootElement,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        Timestamp = DateTimeOffset.UtcNow,
        TraceParent = TRACE_PARENT,
        ServiceInstance = new ServiceInstanceInfo {
          InstanceId = Guid.NewGuid(),
          ServiceName = "TestService",
          HostName = "test-host",
          ProcessId = 1234
        }
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };
}
