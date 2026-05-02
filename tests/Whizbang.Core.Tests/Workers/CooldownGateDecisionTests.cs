using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 7 slice 5 — cooldown gate inside the perspective drainer.
/// Pins the decision logic that short-circuits <c>RunWithEventsAsync</c> when every
/// event_work_id is in the <see cref="RecentlyProcessedEventCache"/>. These tests target
/// the static helper directly so the rule can't silently regress under a future drainer
/// refactor — see <see cref="feedback_lock_invariants_in_tests"/>.
/// </summary>
public class CooldownGateDecisionTests {

  private sealed class TestEvent : IEvent {
    public Guid StreamId { get; set; }
  }

  private static MessageEnvelope<IEvent> _envelope(Guid messageId) => new() {
    MessageId = MessageId.From(messageId),
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
    Payload = new TestEvent()
  };

  private static StreamEventData _raw(Guid eventId, Guid workId) => new() {
    StreamId = (Guid)TrackedGuid.NewMedo(),
    EventId = eventId,
    EventType = "TestEvent",
    EventData = "{}",
    EventWorkId = workId,
    Metadata = null,
    Scope = null
  };

  private static SystemTimeProvider _provider() =>
    new(new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero)));

  [Test]
  public async Task ShouldSkip_NullCache_ReturnsFalseAsync() {
    var eventId = (Guid)TrackedGuid.NewMedo();
    var raw = new[] { _raw(eventId, (Guid)TrackedGuid.NewMedo()) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache: null);

    await Assert.That(skip).IsFalse();
  }

  [Test]
  public async Task ShouldSkip_EmptyEvents_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var raw = Array.Empty<StreamEventData>().ToLookup(r => r.EventId);

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown([], raw, cache);

    await Assert.That(skip).IsFalse();
  }

  [Test]
  public async Task ShouldSkip_AllWorkIdsInCache_ReturnsTrueAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    var work1 = (Guid)TrackedGuid.NewMedo();
    var work2 = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(work1);
    cache.MarkProcessed(work2);

    var raw = new[] { _raw(event1, work1), _raw(event2, work2) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(event1), _envelope(event2) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_OneFresh_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var event1 = (Guid)TrackedGuid.NewMedo();
    var event2 = (Guid)TrackedGuid.NewMedo();
    var work1 = (Guid)TrackedGuid.NewMedo();
    var work2 = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(work1); // only work1 is cooled

    var raw = new[] { _raw(event1, work1), _raw(event2, work2) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(event1), _envelope(event2) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse()
      .Because("any fresh event must drop us into the apply path so it gets processed");
  }

  [Test]
  public async Task ShouldSkip_EventMapsToMultipleWorkIds_AllCooled_ReturnsTrueAsync() {
    // Same event_id queued for multiple perspectives → multiple work_ids per event_id in lookup.
    // Every work_id must be cooled for skip to fire.
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workA);
    cache.MarkProcessed(workB);

    var raw = new[] { _raw(eventId, workA), _raw(eventId, workB) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsTrue();
  }

  [Test]
  public async Task ShouldSkip_EventMapsToMultipleWorkIds_OneFresh_ReturnsFalseAsync() {
    var cache = new RecentlyProcessedEventCache(_provider());
    var eventId = (Guid)TrackedGuid.NewMedo();
    var workA = (Guid)TrackedGuid.NewMedo();
    var workB = (Guid)TrackedGuid.NewMedo();
    cache.MarkProcessed(workA); // only workA cooled, workB fresh

    var raw = new[] { _raw(eventId, workA), _raw(eventId, workB) }.ToLookup(r => r.EventId);
    var events = new List<MessageEnvelope<IEvent>> { _envelope(eventId) };

    var skip = PerspectiveWorker._shouldSkipApplyDueToCooldown(events, raw, cache);

    await Assert.That(skip).IsFalse();
  }
}
