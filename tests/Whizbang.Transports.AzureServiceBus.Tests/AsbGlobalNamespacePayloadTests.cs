// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Transports.AzureServiceBus;

#pragma warning disable CA1050, S3903, RCS1110 // The payload sits in the global namespace on purpose: that is the case under test.
/// <summary>A message type declared outside any namespace, so its namespace is null.</summary>
public sealed record GlobalNamespacePayload(string Value);
#pragma warning restore CA1050, S3903, RCS1110

namespace Whizbang.Transports.AzureServiceBus.Tests {
  /// <summary>
  /// A payload type with no namespace is handed on to be stored like any other (#1720), rather than any check on its
  /// missing namespace failing.
  /// </summary>
  /// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs</code-under-test>
  public class AsbGlobalNamespacePayloadTests {
    private static readonly JsonSerializerOptions _jsonOptions = new() {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Test]
    public async Task Decide_UnconsumedPayloadWithNoNamespace_IsHandedOnToBeStoredAsync() {
      var decider = new AsbReceiveDecisionMaker();
      var props = new Dictionary<string, object> {
        [AsbMessageHeaderReader.ENVELOPE_TYPE_PROPERTY_KEY] = "Whizbang.Core.Observability.MessageEnvelope`1[[GlobalNamespacePayload]]",
      };
      var typeInfo = (JsonTypeInfo<MessageEnvelope<GlobalNamespacePayload>>)_jsonOptions.GetTypeInfo(typeof(MessageEnvelope<GlobalNamespacePayload>));
      var envelope = new MessageEnvelope<GlobalNamespacePayload> {
        MessageId = MessageId.From((Guid)TrackedGuid.New()),
        Payload = new GlobalNamespacePayload("v"),
        Hops = [new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = (Guid)TrackedGuid.New(),
            ServiceName = "test",
            HostName = "test-host",
            ProcessId = 1,
          },
          Timestamp = DateTimeOffset.UtcNow,
        }],
        DispatchContext = new MessageDispatchContext { Mode = Whizbang.Core.Dispatch.DispatchModes.Local, Source = MessageSource.Local },
      };
      var body = JsonSerializer.Serialize(envelope, typeInfo);

      var decision = decider.Decide(props, body, (_, _) => typeInfo, _jsonOptions);

      // #1720: the receive decision no longer asks whether anything here consumes the payload; the consumer stores
      // it and decides any skip after storage, a payload with no namespace included.
      await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.Process);
      await Assert.That(decision.Reason).IsEqualTo(AsbReceiveReason.OK);
    }
  }
}
