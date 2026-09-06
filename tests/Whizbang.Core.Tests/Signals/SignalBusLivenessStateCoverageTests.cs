using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Signals;

namespace Whizbang.Core.Tests.Signals;

/// <summary>
/// Coverage-round tests for <see cref="SignalBusLivenessState"/> members that
/// <see cref="SignalBusProbeTests"/> does not exercise: the pre-first-probe and
/// pre-first-signal default readings, and the total-judged-edges counter behind
/// <see cref="SignalBusLivenessState.DoorbellEvaluated"/>.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Signals/SignalBusLivenessState.cs</code-under-test>
public class SignalBusLivenessStateCoverageTests {

  // If a fresh, not-yet-probed state ever reported true or false here instead of null, the
  // signal-bus health source could claim the wire route is verified (or failed) before the
  // startup self-test has run at all -- an operator would trust a verdict that does not exist.
  [Test]
  public async Task WireRouteVerified_BeforeAnyProbe_IsNullAsync() {
    var state = new SignalBusLivenessState();

    await Assert.That(state.WireRouteVerified).IsNull()
      .Because("null must mean 'no probe has run yet', distinct from a confirmed pass or fail");
  }

  // If this defaulted to a real instant instead of null, an operator could not tell "no wire
  // signal has ever arrived" from "one arrived at the epoch". And once a signal does arrive,
  // losing the recorded instant hides exactly how stale the liveness reading is.
  [Test]
  public async Task LastWireSignalAt_NullBeforeAnySignal_ThenReflectsMarkedInstantAsync() {
    var state = new SignalBusLivenessState();
    await Assert.That(state.LastWireSignalAt).IsNull()
      .Because("no wire signal has arrived yet, so there is nothing to report");

    var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    state.MarkWireSignalReceived(at);

    await Assert.That(state.LastWireSignalAt).IsEqualTo((DateTimeOffset?)at)
      .Because("the recorded instant is what an operator uses to judge how stale the liveness reading is");
  }

  // If this defaulted to a real instant instead of null, an operator could not tell "the
  // wire-route probe has never run" from "it last ran at the epoch". And once it does run,
  // losing the recorded instant hides how overdue the next periodic re-probe already is.
  [Test]
  public async Task LastProbeAt_NullBeforeAnyProbe_ThenReflectsMarkedInstantAsync() {
    var state = new SignalBusLivenessState();
    await Assert.That(state.LastProbeAt).IsNull()
      .Because("no probe has run yet, so there is nothing to report");

    var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    state.MarkProbeResult(success: true, at: at);

    await Assert.That(state.LastProbeAt).IsEqualTo((DateTimeOffset?)at)
      .Because("the recorded probe instant is what tells an operator whether the periodic re-probe is overdue");
  }

  // ConsecutiveMissedDoorbells alone cannot distinguish "healthy, never exercised" from
  // "healthy, just reset by a wake" -- both read zero. DoorbellEvaluations is the count that
  // resolves that ambiguity, so if it stopped incrementing on either a wake or a miss, an
  // operator could not tell a route that has judged edges cleanly from one that has simply
  // never seen any work -- which is the whole reason this counter exists alongside the streak.
  [Test]
  public async Task DoorbellEvaluations_CountsEachEdgeAcrossWakeAndMissedAsync() {
    var state = new SignalBusLivenessState();
    await Assert.That(state.DoorbellEvaluations).IsEqualTo(0)
      .Because("no edge has been judged yet");

    state.RecordDoorbellWake();
    await Assert.That(state.DoorbellEvaluations).IsEqualTo(1)
      .Because("a healthy wake is still a judged edge, not a no-op");

    state.RecordMissedDoorbell();
    await Assert.That(state.DoorbellEvaluations).IsEqualTo(2)
      .Because("a missed doorbell is a judged edge just as much as a healthy wake");
  }
}
