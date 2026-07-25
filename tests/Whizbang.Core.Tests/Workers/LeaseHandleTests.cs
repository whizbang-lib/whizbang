using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Phase H step 9 slice 1 — RED-first locks for <see cref="LeaseHandle"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix audit on a consumer service (2026-05-02): every dispatch path passed only the worker's
/// <c>stoppingToken</c> (graceful shutdown) to receptor / lifecycle / publish operations.
/// LeaseRenewalWorker indefinitely extended DB leases for "in-flight" rows with no notion of
/// "is the handler making progress". Result: a hung handler parked the entire stream's FIFO
/// line forever and no metric (including the slice 8 attempts increment) ever bumped.
/// </para>
/// <para>
/// <strong>Locked invariants:</strong>
/// </para>
/// <list type="bullet">
/// <item><description><c>Token</c> reports cancellation when wall-clock crosses the deadline.</description></item>
/// <item><description><c>TryExtendDeadline</c> pushes the cancellation point out and returns <c>true</c>.</description></item>
/// <item><description><c>TryExtendDeadline</c> returns <c>false</c> after <c>MaxRenewalsPerWork</c> calls (the cap that surfaces hung handlers without requiring a progress-heartbeat protocol).</description></item>
/// <item><description><c>Disposal</c> cancels the token (so any stragglers see it) AND is idempotent.</description></item>
/// <item><description>The token is also linked to the worker's <c>stoppingToken</c> — shutdown cancels the lease's token even before the deadline.</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/work-coordinator/lease-cancellation</docs>
public class LeaseHandleTests {

  private static FakeTimeProvider _provider(out FakeTimeProvider fake) {
    fake = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
    return fake;
  }

  [Test]
  public async Task Token_BeforeDeadline_NotCancelledAsync() {
    var provider = _provider(out var fake);
    var deadline = fake.GetUtcNow() + TimeSpan.FromSeconds(60);
    using var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: deadline,
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    fake.Advance(TimeSpan.FromSeconds(30));

    await Assert.That(lease.Token.IsCancellationRequested).IsFalse()
      .Because("we're 30 s into a 60 s deadline; cancellation must not have fired yet");
  }

  [Test]
  public async Task Token_AtDeadline_CancelsAsync() {
    var provider = _provider(out var fake);
    var deadline = fake.GetUtcNow() + TimeSpan.FromSeconds(60);
    using var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: deadline,
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    fake.Advance(TimeSpan.FromSeconds(61));

    await Assert.That(lease.Token.IsCancellationRequested).IsTrue();
  }

  [Test]
  public async Task TryExtendDeadline_PushesCancellationOutAsync() {
    var provider = _provider(out var fake);
    var firstDeadline = fake.GetUtcNow() + TimeSpan.FromSeconds(60);
    using var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: firstDeadline,
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    fake.Advance(TimeSpan.FromSeconds(45));
    var extended = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(60));
    fake.Advance(TimeSpan.FromSeconds(30)); // total 75 s past start, but only 30 s past extension

    await Assert.That(extended).IsTrue();
    await Assert.That(lease.Token.IsCancellationRequested).IsFalse()
      .Because("extension pushed deadline to 105 s past start; we're only 75 s past");
    await Assert.That(lease.RenewalCount).IsEqualTo(1);
  }

  [Test]
  public async Task TryExtendDeadline_AfterMaxRenewals_ReturnsFalseAsync() {
    var provider = _provider(out var fake);
    using var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: fake.GetUtcNow() + TimeSpan.FromSeconds(60),
      maxRenewals: 3,
      timeProvider: provider,
      linkedTokens: []);

    var ok1 = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(120));
    var ok2 = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(180));
    var ok3 = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(240));
    var ok4 = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(300));

    await Assert.That(ok1).IsTrue();
    await Assert.That(ok2).IsTrue();
    await Assert.That(ok3).IsTrue();
    await Assert.That(ok4).IsFalse()
      .Because("the 4th extension exceeds maxRenewals=3 — TryExtendDeadline must refuse so the DB lease eventually expires and the failure path runs");
    await Assert.That(lease.RenewalCount).IsEqualTo(3);
  }

  [Test]
  public async Task Disposal_CancelsTokenAsync() {
    var provider = _provider(out _);
    var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: provider.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    lease.Dispose();

    await Assert.That(lease.Token.IsCancellationRequested).IsTrue()
      .Because("disposal cancels the token so any stragglers awaiting it see cancellation");
  }

  [Test]
  public async Task Disposal_IsIdempotentAsync() {
    var provider = _provider(out _);
    var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: provider.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    lease.Dispose();
    lease.Dispose();
    lease.Dispose();
    // No throw is the assertion.
    await Assert.That(lease.Token.IsCancellationRequested).IsTrue();
  }

  [Test]
  public async Task TryExtendDeadline_AfterDispose_ReturnsFalseAsync() {
    var provider = _provider(out var fake);
    var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: fake.GetUtcNow() + TimeSpan.FromSeconds(60),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    lease.Dispose();
    var extended = lease.TryExtendDeadline(fake.GetUtcNow() + TimeSpan.FromSeconds(300));

    await Assert.That(extended).IsFalse()
      .Because("a disposed handle must not be reactivated by a late renewal arriving after disposal");
  }

  [Test]
  public async Task Token_LinkedToken_Cancelled_LeaseTokenAlsoCancelsAsync() {
    var provider = _provider(out _);
    using var stoppingTokenSource = new CancellationTokenSource();
    using var lease = new LeaseHandle(
      workId: (Guid)TrackedGuid.NewMedo(),
      category: WorkCategory.Inbox,
      deadline: provider.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: [stoppingTokenSource.Token]);

    await stoppingTokenSource.CancelAsync();

    await Assert.That(lease.Token.IsCancellationRequested).IsTrue()
      .Because("worker shutdown via stoppingToken must cancel the lease token so the dispatch returns promptly");
  }

  [Test]
  public async Task Properties_ExposeConstructorArgsAsync() {
    var provider = _provider(out _);
    var workId = (Guid)TrackedGuid.NewMedo();
    using var lease = new LeaseHandle(
      workId: workId,
      category: WorkCategory.PerspectiveEvent,
      deadline: provider.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    await Assert.That(lease.WorkId).IsEqualTo(workId);
    await Assert.That(lease.Category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(lease.RenewalCount).IsEqualTo(0);
  }
}
