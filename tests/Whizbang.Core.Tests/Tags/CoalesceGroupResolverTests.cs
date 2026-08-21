using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tags;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Tags;

/// <summary>
/// Tests for <see cref="CoalesceGroupResolver"/> — the AOT tag lookup the outbox mint seams
/// consult. A message type whose tag carries an enabled coalesce binding gets
/// <c>CoalesceGroup = tag</c> and the <c>ScheduledFor = now + MaxDelaySeconds</c> safety floor
/// stamped at mint; everything else passes through untouched. Resolution rides the generated
/// MessageTagRegistry via <see cref="EventTypeMatchingHelper"/> (never reflection) and caches
/// per type-name string.
/// </summary>
[Category("Core")]
[Category("Tags")]
public class CoalesceGroupResolverTests {
  [Test]
  public async Task Apply_BoundTag_StampsGroupAndMaxDelayFloorAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
    var options = new TagOptions();
    options.Coalesce("record-digest", c => c.MaxDelaySeconds = 120);
    var resolver = new CoalesceGroupResolver(options, time, () => [TagRegistration(typeof(TestDigestEvent), "record-digest")]);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!);

    var stamped = resolver.ApplyCoalescePolicy(message);

    await Assert.That(stamped.CoalesceGroup).IsEqualTo("record-digest");
    await Assert.That(stamped.ScheduledFor).IsEqualTo(time.GetUtcNow().AddSeconds(120))
      .Because("the floor is what keeps a pending single invisible to the claim pump yet never lost");
    await Assert.That(message.CoalesceGroup).IsNull()
      .Because("OutboxMessage is immutable — stamping returns a copy");
  }

  [Test]
  public async Task Apply_UnboundTag_PassesThroughUntouchedAsync() {
    var options = new TagOptions();
    options.Coalesce("record-digest", c => { });
    var resolver = new CoalesceGroupResolver(options, null, () => [TagRegistration(typeof(TestDigestEvent), "some-other-tag")]);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!);

    var result = resolver.ApplyCoalescePolicy(message);

    await Assert.That(result.CoalesceGroup).IsNull();
    await Assert.That(result.ScheduledFor).IsNull();
  }

  [Test]
  public async Task Apply_UntaggedType_PassesThroughUntouchedAsync() {
    var options = new TagOptions();
    options.Coalesce("record-digest", c => { });
    var resolver = new CoalesceGroupResolver(options, null, () => []);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!);

    var result = resolver.ApplyCoalescePolicy(message);

    await Assert.That(result.CoalesceGroup).IsNull();
    await Assert.That(result.ScheduledFor).IsNull();
  }

  [Test]
  public async Task Apply_DisabledBinding_SlideZero_PassesThroughUntouchedAsync() {
    // SlideSeconds = 0 disables the group: no stamp, no floor — today's immediate individual
    // shipping, exactly the SystemEventOptions.AuditShipSlideSeconds = 0 bypass generalized.
    var options = new TagOptions();
    options.Coalesce("record-digest", c => c.SlideSeconds = 0);
    var resolver = new CoalesceGroupResolver(options, null, () => [TagRegistration(typeof(TestDigestEvent), "record-digest")]);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!);

    var result = resolver.ApplyCoalescePolicy(message);

    await Assert.That(result.CoalesceGroup).IsNull();
    await Assert.That(result.ScheduledFor).IsNull();
  }

  [Test]
  public async Task Apply_AlreadyScheduledMessage_IsNotCoalescedAsync() {
    // A caller-scheduled message has its own timeline; folding it into a group would ship it
    // before (or hold it past) the schedule the caller declared. Scheduled dispatch wins.
    var explicitSchedule = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    var options = new TagOptions();
    options.Coalesce("record-digest", c => { });
    var resolver = new CoalesceGroupResolver(options, null, () => [TagRegistration(typeof(TestDigestEvent), "record-digest")]);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!) with { ScheduledFor = explicitSchedule };

    var result = resolver.ApplyCoalescePolicy(message);

    await Assert.That(result.CoalesceGroup).IsNull();
    await Assert.That(result.ScheduledFor).IsEqualTo(explicitSchedule);
  }

  [Test]
  public async Task Apply_AlreadyStampedMessage_IsNotRestampedAsync() {
    var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
    var floor = time.GetUtcNow().AddSeconds(60);
    var options = new TagOptions();
    options.Coalesce("record-digest", c => c.MaxDelaySeconds = 999);
    var resolver = new CoalesceGroupResolver(options, time, () => [TagRegistration(typeof(TestDigestEvent), "record-digest")]);
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!) with {
      CoalesceGroup = "record-digest",
      ScheduledFor = floor
    };

    var result = resolver.ApplyCoalescePolicy(message);

    await Assert.That(result.ScheduledFor).IsEqualTo(floor)
      .Because("a second seam seeing an already-stamped message must not move its floor");
  }

  [Test]
  public async Task Apply_NormalizedTypeNameForms_AllResolveAsync() {
    // The stored MessageType string may carry Version/Culture/PublicKeyToken or not — the
    // resolver must match every form BuildTypeLookup covers, never hand-rolled comparisons.
    var options = new TagOptions();
    options.Coalesce("record-digest", c => { });
    var resolver = new CoalesceGroupResolver(options, null, () => [TagRegistration(typeof(TestDigestEvent), "record-digest")]);
    var fullNameForm = $"{typeof(TestDigestEvent).FullName}, {typeof(TestDigestEvent).Assembly.GetName().Name}";

    var viaAqn = resolver.ApplyCoalescePolicy(_outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!));
    var viaFullName = resolver.ApplyCoalescePolicy(_outboxMessage(fullNameForm));

    await Assert.That(viaAqn.CoalesceGroup).IsEqualTo("record-digest");
    await Assert.That(viaFullName.CoalesceGroup).IsEqualTo("record-digest");
  }

  [Test]
  public async Task Apply_ResolutionIsCachedPerTypeNameAsync() {
    // The mint path is hot: the registry walk happens once per distinct type-name string,
    // then the cached answer (bound group or null) serves every later mint.
    var enumerations = 0;
    var options = new TagOptions();
    options.Coalesce("record-digest", c => { });
    var resolver = new CoalesceGroupResolver(options, null, () => {
      enumerations++;
      return [TagRegistration(typeof(TestDigestEvent), "record-digest")];
    });
    var message = _outboxMessage(typeof(TestDigestEvent).AssemblyQualifiedName!);

    resolver.ApplyCoalescePolicy(message);
    resolver.ApplyCoalescePolicy(message);
    resolver.ApplyCoalescePolicy(message);

    await Assert.That(enumerations).IsEqualTo(1);
  }

  [Test]
  public async Task GetBinding_BoundGroup_ReturnsThePolicyAsync() {
    var options = new TagOptions();
    options.Coalesce("record-digest", c => c.MaxBatchCount = 42);
    var resolver = new CoalesceGroupResolver(options, null, () => []);

    var binding = resolver.GetBinding("record-digest");

    await Assert.That(binding).IsNotNull();
    await Assert.That(binding!.MaxBatchCount).IsEqualTo(42);
  }

  [Test]
  public async Task GetBinding_UnknownOrDisabledGroup_ReturnsNullAsync() {
    var options = new TagOptions();
    options.Coalesce("disabled-digest", c => c.SlideSeconds = 0);
    var resolver = new CoalesceGroupResolver(options, null, () => []);

    await Assert.That(resolver.GetBinding("nope")).IsNull();
    await Assert.That(resolver.GetBinding("disabled-digest")).IsNull();
  }

  #region Helpers

  internal static MessageTagRegistration TagRegistration(Type messageType, string tag) => new() {
    MessageType = messageType,
    AttributeType = typeof(SignalTagAttribute),
    Tag = tag,
    PayloadBuilder = _ => JsonSerializer.SerializeToElement(new { }),
    AttributeFactory = () => new SignalTagAttribute { Tag = tag }
  };

  private static OutboxMessage _outboxMessage(string messageType) {
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.New(),
      Payload = JsonSerializer.SerializeToElement(new { test = "data" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = "test-destination",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [] },
      EnvelopeType = "TestEnvelopeType",
      StreamId = Guid.NewGuid(),
      IsEvent = true,
      MessageType = messageType
    };
  }

  internal sealed record TestDigestEvent : IEvent;

  #endregion
}
