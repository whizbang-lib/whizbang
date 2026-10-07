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
  /// A payload type with no namespace cannot belong to an absorbed namespace, so an unconsumed one is
  /// dropped like any other rather than the namespace check failing on the missing name.
  /// </summary>
  /// <code-under-test>src/Whizbang.Transports.AzureServiceBus/AsbReceiveDecisionMaker.cs</code-under-test>
  public class AsbGlobalNamespacePayloadTests {
    private static readonly JsonSerializerOptions _jsonOptions = new() {
      TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    [Test]
    public async Task Decide_UnconsumedPayloadWithNoNamespace_IsNotAbsorbedAndIsDroppedAsync() {
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
      var absorbed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Absorbed.Contracts" };

      var decision = decider.Decide(
        props, body, (_, _) => typeInfo, _jsonOptions, isHandledLocally: static _ => false, absorbedNamespaces: absorbed);

      await Assert.That(decision.Action).IsEqualTo(AsbReceiveAction.AckAndDrop);
      await Assert.That(decision.Reason).IsEqualTo(AsbReceiveReason.NO_LOCAL_CONSUMER);
    }
  }
}
