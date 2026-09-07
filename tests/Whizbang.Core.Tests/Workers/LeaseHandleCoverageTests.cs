using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="LeaseHandle"/> construction/renewal edge cases that
/// <see cref="LeaseHandleTests"/> does not exercise: the negative-<c>maxRenewals</c> guard, a
/// deadline that has already elapsed at construction time, and a renewal call whose requested
/// deadline is already in the past.
/// </summary>
public class LeaseHandleCoverageTests {

  private static FakeTimeProvider _provider(out FakeTimeProvider fake) {
    fake = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
    return fake;
  }

  // A negative renewal cap has no meaning: TryExtendDeadline compares _renewalCount (always >= 0)
  // against it, so a negative cap would refuse every renewal from the very first call. Accepting
  // it silently would hide a caller bug as "the handler never got its lease extended" instead of
  // failing loud at construction.
  [Test]
  public async Task Constructor_NegativeMaxRenewals_ThrowsArgumentOutOfRangeExceptionAsync() {
    var provider = _provider(out var fake);

    await Assert.That(() => new LeaseHandle(
      workId: Guid.NewGuid(),
      category: WorkCategory.Inbox,
      deadline: fake.GetUtcNow() + TimeSpan.FromMinutes(5),
      maxRenewals: -1,
      timeProvider: provider,
      linkedTokens: [])).Throws<ArgumentOutOfRangeException>()
      .Because("a negative renewal cap can never be satisfied by TryExtendDeadline's non-negative counter, so it must fail loudly at construction rather than silently disabling every future renewal.");
  }

  // A lease constructed with a deadline that has already elapsed (clock skew, or a dispatch that
  // already burned its entire lease before the handle was even created) must cancel its token
  // immediately. Handing back a token that reports "not canceled" for an already-expired lease
  // would let the caller believe it still has exclusive ownership of a row it does not.
  [Test]
  public async Task Constructor_DeadlineAlreadyInThePast_TokenIsImmediatelyCanceledAsync() {
    var provider = _provider(out var fake);
    var pastDeadline = fake.GetUtcNow() - TimeSpan.FromSeconds(1);

    using var lease = new LeaseHandle(
      workId: Guid.NewGuid(),
      category: WorkCategory.Inbox,
      deadline: pastDeadline,
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    await Assert.That(lease.Token.IsCancellationRequested).IsTrue()
      .Because("a deadline that is already in the past at construction time must cancel the token synchronously — never hand out a live-looking token for a lease that is already expired.");
  }

  // A renewal call carrying an already-past target deadline (e.g. a stale DB lease-renewal result
  // racing a slow round trip) must not pretend to have extended anything: honoring it would report
  // success for a lease that is effectively already expired, and letting it consume the renewal
  // budget would starve the handler of a real extension because of a no-op call.
  [Test]
  public async Task TryExtendDeadline_NewDeadlineAlreadyPast_ReturnsFalseWithoutCountingAsync() {
    var provider = _provider(out var fake);
    using var lease = new LeaseHandle(
      workId: Guid.NewGuid(),
      category: WorkCategory.Inbox,
      deadline: fake.GetUtcNow() + TimeSpan.FromSeconds(60),
      maxRenewals: 6,
      timeProvider: provider,
      linkedTokens: []);

    var extended = lease.TryExtendDeadline(fake.GetUtcNow() - TimeSpan.FromSeconds(1));

    await Assert.That(extended).IsFalse()
      .Because("a renewal targeting a deadline that is already in the past must be refused rather than reported as a successful extension.");
    await Assert.That(lease.RenewalCount).IsEqualTo(0)
      .Because("a renewal that did not actually push the deadline out must not consume the maxRenewals budget — otherwise a no-op call would starve the handler of a real renewal later.");
  }
}
