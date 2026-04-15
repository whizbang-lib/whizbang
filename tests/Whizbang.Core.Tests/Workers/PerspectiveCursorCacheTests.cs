using TUnit.Core;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// TDD tests for PerspectiveCursorCache — in-memory cache of cursor positions
/// to eliminate redundant GetPerspectiveCursorAsync DB calls in drain mode.
/// </summary>
public class PerspectiveCursorCacheTests {

  private sealed class FakePerspectiveA;
  private sealed class FakePerspectiveB;

  [Test]
  public async Task TryGet_EmptyCache_ReturnsFalseAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    var found = cache.TryGet<FakePerspectiveA>(streamId, out var lastEventId);

    await Assert.That(found).IsFalse();
    await Assert.That(lastEventId).IsNull();
  }

  [Test]
  public async Task Set_ThenTryGet_ReturnsValueAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, eventId);
    var found = cache.TryGet<FakePerspectiveA>(streamId, out var lastEventId);

    await Assert.That(found).IsTrue();
    await Assert.That(lastEventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task Set_NullEventId_CachesNullAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, null);
    var found = cache.TryGet<FakePerspectiveA>(streamId, out var lastEventId);

    await Assert.That(found).IsTrue();
    await Assert.That(lastEventId).IsNull();
  }

  [Test]
  public async Task TryGet_DifferentPerspective_ReturnsFalseAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, Guid.NewGuid());
    var found = cache.TryGet<FakePerspectiveB>(streamId, out _);

    await Assert.That(found).IsFalse();
  }

  [Test]
  public async Task TryGet_DifferentStream_ReturnsFalseAsync() {
    var cache = new PerspectiveCursorCache();

    cache.Set<FakePerspectiveA>(Guid.NewGuid(), Guid.NewGuid());
    var found = cache.TryGet<FakePerspectiveA>(Guid.NewGuid(), out _);

    await Assert.That(found).IsFalse();
  }

  [Test]
  public async Task Set_OverwritesPreviousValueAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId1 = Guid.NewGuid();
    var eventId2 = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, eventId1);
    cache.Set<FakePerspectiveA>(streamId, eventId2);
    cache.TryGet<FakePerspectiveA>(streamId, out var lastEventId);

    await Assert.That(lastEventId).IsEqualTo(eventId2);
  }

  [Test]
  public async Task InvalidateStream_RemovesAllPerspectivesForStreamAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, Guid.NewGuid());
    cache.Set<FakePerspectiveB>(streamId, Guid.NewGuid());

    cache.InvalidateStream(streamId);

    await Assert.That(cache.TryGet<FakePerspectiveA>(streamId, out _)).IsFalse();
    await Assert.That(cache.TryGet<FakePerspectiveB>(streamId, out _)).IsFalse();
  }

  [Test]
  public async Task InvalidateStream_DoesNotAffectOtherStreamsAsync() {
    var cache = new PerspectiveCursorCache();
    var stream1 = Guid.NewGuid();
    var stream2 = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(stream1, Guid.NewGuid());
    cache.Set<FakePerspectiveA>(stream2, eventId);

    cache.InvalidateStream(stream1);

    await Assert.That(cache.TryGet<FakePerspectiveA>(stream1, out _)).IsFalse();
    await Assert.That(cache.TryGet<FakePerspectiveA>(stream2, out var val)).IsTrue();
    await Assert.That(val).IsEqualTo(eventId);
  }

  [Test]
  public async Task InvalidatePerspective_RemovesSingleEntryAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();

    cache.Set<FakePerspectiveA>(streamId, Guid.NewGuid());
    cache.Set<FakePerspectiveB>(streamId, Guid.NewGuid());

    cache.Invalidate<FakePerspectiveA>(streamId);

    await Assert.That(cache.TryGet<FakePerspectiveA>(streamId, out _)).IsFalse();
    await Assert.That(cache.TryGet<FakePerspectiveB>(streamId, out _)).IsTrue();
  }

  [Test]
  public async Task Clear_RemovesAllEntriesAsync() {
    var cache = new PerspectiveCursorCache();

    cache.Set<FakePerspectiveA>(Guid.NewGuid(), Guid.NewGuid());
    cache.Set<FakePerspectiveB>(Guid.NewGuid(), Guid.NewGuid());

    cache.Clear();

    await Assert.That(cache.Count).IsEqualTo(0);
  }

  [Test]
  public async Task Count_ReturnsNumberOfEntriesAsync() {
    var cache = new PerspectiveCursorCache();

    cache.Set<FakePerspectiveA>(Guid.NewGuid(), Guid.NewGuid());
    cache.Set<FakePerspectiveB>(Guid.NewGuid(), Guid.NewGuid());

    await Assert.That(cache.Count).IsEqualTo(2);
  }

  [Test]
  public async Task SetByName_ThenTryGetByName_ReturnsValueAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();

    cache.Set(streamId, "MyPerspective", eventId);
    var found = cache.TryGet(streamId, "MyPerspective", out var lastEventId);

    await Assert.That(found).IsTrue();
    await Assert.That(lastEventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task SetByType_TryGetByName_UsesTypeNameFormatterAsync() {
    var cache = new PerspectiveCursorCache();
    var streamId = Guid.NewGuid();
    var eventId = Guid.NewGuid();
    // TypeNameFormatter.Format produces "FullName, AssemblyName"
    var normalizedName = Whizbang.Core.TypeNameFormatter.Format(typeof(FakePerspectiveA));

    cache.Set<FakePerspectiveA>(streamId, eventId);
    var found = cache.TryGet(streamId, normalizedName, out var lastEventId);

    await Assert.That(found).IsTrue()
      .Because("Type-based Set should be retrievable by TypeNameFormatter.Format name");
    await Assert.That(lastEventId).IsEqualTo(eventId);
  }
}
