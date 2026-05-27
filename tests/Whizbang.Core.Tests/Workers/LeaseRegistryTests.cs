using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 9 slice 1 — RED-first locks for <see cref="LeaseRegistry"/>.
/// LeaseRenewalWorker looks up handles by (category, work_id) when it renews a DB lease so the
/// in-process CT deadline tracks. Disposal removes from the registry — this is the "single
/// source of truth" for which leases are currently in flight on this instance.
/// </summary>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public class LeaseRegistryTests {

  private static FakeTimeProvider _provider() =>
    new(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));

  private static LeaseHandle _newHandle(FakeTimeProvider time, WorkCategory category, Guid workId) =>
    new(
      workId: workId,
      category: category,
      deadline: time.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: 6,
      timeProvider: time,
      linkedTokens: []);

  [Test]
  public async Task Register_ThenTryGet_ReturnsHandleAsync() {
    var time = _provider();
    var registry = new LeaseRegistry();
    var workId = (Guid)TrackedGuid.NewMedo();
    using var lease = _newHandle(time, WorkCategory.Inbox, workId);

    registry.Register(lease);

    var found = registry.TryGet(WorkCategory.Inbox, workId, out var resolved);

    await Assert.That(found).IsTrue();
    await Assert.That(resolved).IsSameReferenceAs(lease);
  }

  [Test]
  public async Task TryGet_NotRegistered_ReturnsFalseAsync() {
    var registry = new LeaseRegistry();

    var found = registry.TryGet(WorkCategory.Inbox, (Guid)TrackedGuid.NewMedo(), out var resolved);

    await Assert.That(found).IsFalse();
    await Assert.That(resolved).IsNull();
  }

  [Test]
  public async Task Dispose_RemovesHandleFromRegistryAsync() {
    var time = _provider();
    var registry = new LeaseRegistry();
    var workId = (Guid)TrackedGuid.NewMedo();
    var lease = _newHandle(time, WorkCategory.Inbox, workId);
    registry.Register(lease);

    lease.Dispose();

    var found = registry.TryGet(WorkCategory.Inbox, workId, out _);
    await Assert.That(found).IsFalse()
      .Because("disposing a registered handle must auto-remove it from the registry");
  }

  [Test]
  public async Task DifferentCategories_SameWorkId_AreIndependentAsync() {
    var time = _provider();
    var registry = new LeaseRegistry();
    var workId = (Guid)TrackedGuid.NewMedo();
    using var inboxLease = _newHandle(time, WorkCategory.Inbox, workId);
    using var perspectiveLease = _newHandle(time, WorkCategory.PerspectiveEvent, workId);

    registry.Register(inboxLease);
    registry.Register(perspectiveLease);

    await Assert.That(registry.TryGet(WorkCategory.Inbox, workId, out var inbox)).IsTrue();
    await Assert.That(registry.TryGet(WorkCategory.PerspectiveEvent, workId, out var persp)).IsTrue();
    await Assert.That(inbox).IsSameReferenceAs(inboxLease);
    await Assert.That(persp).IsSameReferenceAs(perspectiveLease);
  }

  [Test]
  public async Task Register_DuplicateKey_ThrowsAsync() {
    var time = _provider();
    var registry = new LeaseRegistry();
    var workId = (Guid)TrackedGuid.NewMedo();
    using var first = _newHandle(time, WorkCategory.Inbox, workId);
    using var second = _newHandle(time, WorkCategory.Inbox, workId);
    registry.Register(first);

    Action act = () => registry.Register(second);

    await Assert.That(act).Throws<InvalidOperationException>()
      .Because("a stream-pinned lease must be uniquely registered per (category, work_id) — a duplicate signals a bug in the dispatch worker");
  }

  [Test]
  public async Task Count_ReflectsRegisteredHandlesAsync() {
    var time = _provider();
    var registry = new LeaseRegistry();
    var l1 = _newHandle(time, WorkCategory.Inbox, (Guid)TrackedGuid.NewMedo());
    var l2 = _newHandle(time, WorkCategory.Inbox, (Guid)TrackedGuid.NewMedo());
    var l3 = _newHandle(time, WorkCategory.Outbox, (Guid)TrackedGuid.NewMedo());

    registry.Register(l1);
    registry.Register(l2);
    registry.Register(l3);

    await Assert.That(registry.Count).IsEqualTo(3);

    l2.Dispose();
    await Assert.That(registry.Count).IsEqualTo(2);

    l1.Dispose();
    l3.Dispose();
    await Assert.That(registry.Count).IsEqualTo(0);
  }
}
