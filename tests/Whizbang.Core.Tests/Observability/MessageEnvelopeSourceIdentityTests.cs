using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Slice 26.5 — RED-first locks for the source-identity fields on <see cref="MessageEnvelope{TMessage}"/>.
///
/// <para>The outbox publish path (slice 26.5 continued) populates <c>SourceServiceId</c>
/// and <c>SourceCommitSequence</c> from <c>wh_event_store.commit_sequence</c> +
/// <c>wh_service_config.service_id</c> (or the original origin if forwarded). Downstream
/// consumers persist these into <c>wh_inbox.source_service_id</c> /
/// <c>source_commit_sequence</c> at receive-time and use them as the per-source cursor.</para>
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
/// <item><description>Envelope has <c>SourceServiceId</c> (Guid, json key <c>sid</c>) and
/// <c>SourceCommitSequence</c> (long, json key <c>sseq</c>) as init-only properties.</description></item>
/// <item><description>Optional <c>CausedByServiceId</c> / <c>CausedByCommitSequence</c>
/// (nullable, json keys <c>cbid</c>/<c>cbseq</c>) for causality tracing — recorded but
/// not enforced as an ordering constraint.</description></item>
/// <item><description>JSON round-trip preserves all four fields with their short keys.</description></item>
/// <item><description>Backward compat: legacy envelopes (no source-identity fields) get
/// safe defaults (<see cref="Guid.Empty"/> + 0 + nulls) so existing deserialization paths
/// keep working.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class MessageEnvelopeSourceIdentityTests {

  [Test]
  public async Task NewEnvelope_DefaultsSourceIdentityToZeroValuesAsync() {
    var env = _newEnvelope(payload: "hi");

    await Assert.That(env.SourceServiceId).IsEqualTo(Guid.Empty)
      .Because("in-process envelopes default to zero; outbox publish populates before crossing service boundaries");
    await Assert.That(env.SourceCommitSequence).IsEqualTo(0L);
    await Assert.That(env.CausedByServiceId).IsNull();
    await Assert.That(env.CausedByCommitSequence).IsNull();
  }

  [Test]
  public async Task Envelope_AcceptsExplicitSourceIdentityViaInitAsync() {
    var serviceId = Guid.NewGuid();
    var env = new MessageEnvelope<string> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = "hi",
      Hops = [_anchorHop()],
      DispatchContext = _defaultDispatch(),
      SourceServiceId = serviceId,
      SourceCommitSequence = 42L,
      CausedByServiceId = Guid.NewGuid(),
      CausedByCommitSequence = 41L,
    };

    await Assert.That(env.SourceServiceId).IsEqualTo(serviceId);
    await Assert.That(env.SourceCommitSequence).IsEqualTo(42L);
    await Assert.That(env.CausedByServiceId).IsNotNull();
    await Assert.That(env.CausedByCommitSequence).IsEqualTo((long?)41L);
  }

  [Test]
  public async Task Envelope_RoundtripsSourceIdentityThroughJsonAsync() {
    var serviceId = Guid.NewGuid();
    var causedBy = Guid.NewGuid();
    var env = new MessageEnvelope<string> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = "hi",
      Hops = [_anchorHop()],
      DispatchContext = _defaultDispatch(),
      SourceServiceId = serviceId,
      SourceCommitSequence = 42L,
      CausedByServiceId = causedBy,
      CausedByCommitSequence = 41L,
    };

    var json = JsonSerializer.Serialize(env);
    var roundtripped = JsonSerializer.Deserialize<MessageEnvelope<string>>(json)!;

    await Assert.That(roundtripped.SourceServiceId).IsEqualTo(serviceId);
    await Assert.That(roundtripped.SourceCommitSequence).IsEqualTo(42L);
    await Assert.That(roundtripped.CausedByServiceId).IsEqualTo(causedBy);
    await Assert.That(roundtripped.CausedByCommitSequence).IsEqualTo((long?)41L);
  }

  [Test]
  public async Task Envelope_UsesShortJsonKeysForSourceIdentityAsync() {
    // Locks the wire format. Long keys would bloat every envelope; we use the same
    // short-key convention as MessageId ("id"), Payload ("p"), Hops ("h"), etc.
    var env = new MessageEnvelope<string> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = "hi",
      Hops = [_anchorHop()],
      DispatchContext = _defaultDispatch(),
      SourceServiceId = Guid.NewGuid(),
      SourceCommitSequence = 42L,
      CausedByServiceId = Guid.NewGuid(),
      CausedByCommitSequence = 41L,
    };

    var json = JsonSerializer.Serialize(env);

    await Assert.That(json).Contains("\"sid\"")
      .Because("wire format uses short key 'sid' for SourceServiceId");
    await Assert.That(json).Contains("\"sseq\"")
      .Because("wire format uses short key 'sseq' for SourceCommitSequence");
    await Assert.That(json).Contains("\"cbid\"");
    await Assert.That(json).Contains("\"cbseq\"");
  }

  [Test]
  public async Task Envelope_LegacyJsonWithoutSourceIdentity_DeserializesWithDefaultsAsync() {
    // Backward compat: envelopes serialized before slice 26.5 lack the new fields.
    // Strategy: serialize a fresh envelope (gives us a valid envelope JSON shape with
    // all the right converters), then strip the source-identity keys to simulate a
    // pre-slice-26 payload, then deserialize and confirm defaults kick in.
    var fresh = _newEnvelope("hi");
    var json = JsonSerializer.Serialize(fresh);
    var trimmed = json
      .Replace(",\"sid\":\"00000000-0000-0000-0000-000000000000\"", "", StringComparison.Ordinal)
      .Replace(",\"sseq\":0", "", StringComparison.Ordinal)
      .Replace(",\"cbid\":null", "", StringComparison.Ordinal)
      .Replace(",\"cbseq\":null", "", StringComparison.Ordinal);

    var env = JsonSerializer.Deserialize<MessageEnvelope<string>>(trimmed)!;

    await Assert.That(env.SourceServiceId).IsEqualTo(Guid.Empty);
    await Assert.That(env.SourceCommitSequence).IsEqualTo(0L);
    await Assert.That(env.CausedByServiceId).IsNull();
    await Assert.That(env.CausedByCommitSequence).IsNull();
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static MessageEnvelope<T> _newEnvelope<T>(T payload) {
    return new MessageEnvelope<T> {
      MessageId = MessageId.From((Guid)TrackedGuid.NewMedo()),
      Payload = payload,
      Hops = [_anchorHop()],
      DispatchContext = _defaultDispatch(),
    };
  }

  private static MessageDispatchContext _defaultDispatch() {
    return new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local };
  }

  private static MessageHop _anchorHop() {
    return new MessageHop {
      ServiceInstance = new ServiceInstanceInfo {
        InstanceId = Guid.NewGuid(),
        ServiceName = "test-svc",
        HostName = "test-host",
        ProcessId = 1
      },
      Timestamp = DateTimeOffset.UtcNow,
      Type = HopType.Current
    };
  }
}
