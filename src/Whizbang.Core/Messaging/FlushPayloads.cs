// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The payloads one flush has read, so its five Distribute stages parse each message once between them instead of once
/// each (#1526, plans/message-cache.md step 1).
/// </summary>
/// <remarks>
/// <para>
/// An entry is keyed by the envelope instance, never by the message id: two envelopes that carry the same id with
/// different bodies (an upcast, a repair, a rehydrated offload) are two entries, each read from its own bytes.
/// </para>
/// <para>
/// The detached stages run in the background while the inline ones run, so the first stage to reach a message reads it
/// and the others wait for that read and share it. A read that fails is not remembered: the next stage reads the payload
/// again, and the failure is that stage's own, as it was before.
/// </para>
/// <para>
/// The parsed payload is shared, not the envelope: each stage still builds an envelope of its own around it, so the hops
/// and stamps one stage adds are never seen by another. Nothing is held past the flush: the instance belongs to the
/// flush's <see cref="DistributeLifecycleContext"/> and is collected with it.
/// </para>
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/DistributeFlushParseOnceTests.cs</tests>
internal sealed class FlushPayloads {
  private readonly ConcurrentDictionary<IMessageEnvelope<JsonElement>, Entry> _entries = new(ReferenceEqualityComparer.Instance);

  /// <summary>
  /// The payload of <paramref name="envelope"/>, read through <paramref name="deserializer"/> the first time any stage
  /// of the flush asks for it, and the same instance every time after.
  /// </summary>
  public object GetOrRead(IMessageEnvelope<JsonElement> envelope, string messageType, Guid messageId, ILifecycleMessageDeserializer deserializer) {
    var entry = _entries.GetOrAdd(envelope, static _ => new Entry());
    lock (entry.Gate) {
      if (entry.Payload is { } read) {
        return read;
      }
      var payload = deserializer.DeserializeFromJsonElement(envelope.Payload, messageType);
      MessageParseDiagnostics.Record(messageId, MessageParseSource.LifecycleStage, MessageParseKind.Payload);
      entry.Payload = payload;
      return payload;
    }
  }

  private sealed class Entry {
    public Lock Gate { get; } = new();
    public object? Payload { get; set; }
  }
}
