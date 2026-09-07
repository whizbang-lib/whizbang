using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.AutoPopulate;

/// <summary>
/// Coverage round 23 — closes gaps in <see cref="AutoPopulateProcessor"/>'s defensive "unknown kind"
/// arms: each extraction switch (top-level PopulateKind, and the four per-category sub-kinds) ends in
/// a default arm reachable only when a registration carries a kind value outside the set the switch
/// knows about (a future enum member added without a matching case, or a value assembled off a stale
/// contract). Also covers the "nothing extracted" early-out that follows.
/// </summary>
/// <code-under-test>src/Whizbang.Core/AutoPopulate/AutoPopulateProcessor.cs</code-under-test>
[Category("Core")]
[Category("AutoPopulate")]
public class AutoPopulateProcessorCoverageTests {

  private sealed class _registry(AutoPopulateRegistration registration) : IAutoPopulateRegistry {
    public IEnumerable<AutoPopulateRegistration> GetRegistrationsFor(Type messageType) =>
      registration.MessageType == messageType ? [registration] : [];
    public IEnumerable<AutoPopulateRegistration> GetAllRegistrations() => [registration];
  }

  private static MessageEnvelope<TMessage> _createEnvelope<TMessage>(TMessage payload, ScopeDelta? scope = null) =>
    new() {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = new ServiceInstanceInfo {
            ServiceName = "TestService",
            InstanceId = Guid.NewGuid(),
            HostName = "localhost",
            ProcessId = 12345
          },
          Timestamp = DateTimeOffset.UtcNow,
          Scope = scope
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

  // Each scenario below uses its own private message type so the process-global AutoPopulateRegistry
  // (registrations accumulate for the process lifetime, keyed by exact message type) can never pick
  // up a registration contributed by a different test or a different file.
  private sealed record _unknownPopulateKindMessage(Guid Id);
  private sealed record _unknownTimestampKindMessage(Guid Id);
  private sealed record _unknownContextKindMessage(Guid Id);
  private sealed record _unknownServiceKindMessage(Guid Id);
  private sealed record _unknownIdentifierKindMessage(Guid Id);

  /// <summary>
  /// A PopulateKind value the top-level switch does not recognize (a future kind added without a
  /// matching case here) must be treated as "nothing to populate," not crash message processing. If
  /// this default ever started throwing, every message carrying that one unrecognized attribute would
  /// fail to dispatch; if it ever started fabricating a value, downstream code would trust provenance
  /// data that was never actually extracted. This also exercises the "no value extracted → add no hop"
  /// early-out: a message that gains no metadata must not gain an empty auto-populate hop either.
  /// </summary>
  [Test]
  public async Task ProcessAutoPopulate_UnknownPopulateKind_SkipsFieldAndAddsNoHopAsync() {
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(_unknownPopulateKindMessage),
      PropertyName = "Unknown",
      PropertyType = typeof(string),
      PopulateKind = (PopulateKind)(-1)
    };
    AutoPopulateRegistry.Register(new _registry(registration), priority: 9201);
    var envelope = _createEnvelope(new _unknownPopulateKindMessage(Guid.NewGuid()));
    var initialHopCount = envelope.Hops.Count;
    var processor = new AutoPopulateProcessor();

    processor.ProcessAutoPopulate(envelope, typeof(_unknownPopulateKindMessage));

    await Assert.That(envelope.GetMetadata("auto:Unknown")).IsNull()
      .Because("an unrecognized PopulateKind must extract nothing, never a fabricated value");
    await Assert.That(envelope.Hops.Count).IsEqualTo(initialHopCount)
      .Because("with zero values extracted, no auto-populate hop should be appended at all");
  }

  /// <summary>
  /// Same "fail closed to nothing" contract, one level down: an unrecognized TimestampKind (the
  /// switch already returns null for the two known-but-deferred kinds QueuedAt/DeliveredAt) must not
  /// throw or invent a timestamp that never happened.
  /// </summary>
  [Test]
  public async Task ProcessAutoPopulate_UnknownTimestampKind_SkipsFieldAsync() {
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(_unknownTimestampKindMessage),
      PropertyName = "UnknownTimestamp",
      PropertyType = typeof(DateTimeOffset?),
      PopulateKind = PopulateKind.Timestamp,
      TimestampKind = (TimestampKind)(-1)
    };
    AutoPopulateRegistry.Register(new _registry(registration), priority: 9202);
    var envelope = _createEnvelope(new _unknownTimestampKindMessage(Guid.NewGuid()));
    var processor = new AutoPopulateProcessor();

    processor.ProcessAutoPopulate(envelope, typeof(_unknownTimestampKindMessage));

    await Assert.That(envelope.GetMetadata("auto:UnknownTimestamp")).IsNull()
      .Because("an unrecognized TimestampKind must not fabricate a SentAt-shaped value");
  }

  /// <summary>
  /// An unrecognized ContextKind, reached only once the hop actually carries a scope (otherwise the
  /// method already returns null before the switch), must still resolve to nothing rather than
  /// leaking an unrelated scope value under the wrong property name.
  /// </summary>
  [Test]
  public async Task ProcessAutoPopulate_UnknownContextKind_SkipsFieldAsync() {
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(_unknownContextKindMessage),
      PropertyName = "UnknownContext",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Context,
      ContextKind = (ContextKind)(-1)
    };
    AutoPopulateRegistry.Register(new _registry(registration), priority: 9203);
    var scope = ScopeDelta.FromSecurityContext(new SecurityContext { UserId = "user-1", TenantId = "tenant-1" });
    var envelope = _createEnvelope(new _unknownContextKindMessage(Guid.NewGuid()), scope);
    var processor = new AutoPopulateProcessor();

    processor.ProcessAutoPopulate(envelope, typeof(_unknownContextKindMessage));

    await Assert.That(envelope.GetMetadata("auto:UnknownContext")).IsNull()
      .Because("an unrecognized ContextKind must not leak UserId/TenantId (or anything else) under the wrong key");
  }

  /// <summary>
  /// An unrecognized ServiceKind must not leak an unrelated ServiceInstance field under the wrong
  /// property name.
  /// </summary>
  [Test]
  public async Task ProcessAutoPopulate_UnknownServiceKind_SkipsFieldAsync() {
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(_unknownServiceKindMessage),
      PropertyName = "UnknownService",
      PropertyType = typeof(string),
      PopulateKind = PopulateKind.Service,
      ServiceKind = (ServiceKind)(-1)
    };
    AutoPopulateRegistry.Register(new _registry(registration), priority: 9204);
    var envelope = _createEnvelope(new _unknownServiceKindMessage(Guid.NewGuid()));
    var processor = new AutoPopulateProcessor();

    processor.ProcessAutoPopulate(envelope, typeof(_unknownServiceKindMessage));

    await Assert.That(envelope.GetMetadata("auto:UnknownService")).IsNull()
      .Because("an unrecognized ServiceKind must not leak ServiceName/InstanceId/HostName/ProcessId under the wrong key");
  }

  /// <summary>
  /// An unrecognized IdentifierKind must not leak an unrelated identifier (MessageId, CorrelationId,
  /// CausationId, StreamId) under the wrong property name.
  /// </summary>
  [Test]
  public async Task ProcessAutoPopulate_UnknownIdentifierKind_SkipsFieldAsync() {
    var registration = new AutoPopulateRegistration {
      MessageType = typeof(_unknownIdentifierKindMessage),
      PropertyName = "UnknownIdentifier",
      PropertyType = typeof(Guid?),
      PopulateKind = PopulateKind.Identifier,
      IdentifierKind = (IdentifierKind)(-1)
    };
    AutoPopulateRegistry.Register(new _registry(registration), priority: 9205);
    var envelope = _createEnvelope(new _unknownIdentifierKindMessage(Guid.NewGuid()));
    var processor = new AutoPopulateProcessor();

    processor.ProcessAutoPopulate(envelope, typeof(_unknownIdentifierKindMessage));

    await Assert.That(envelope.GetMetadata("auto:UnknownIdentifier")).IsNull()
      .Because("an unrecognized IdentifierKind must not leak MessageId/CorrelationId/CausationId/StreamId under the wrong key");
  }
}
