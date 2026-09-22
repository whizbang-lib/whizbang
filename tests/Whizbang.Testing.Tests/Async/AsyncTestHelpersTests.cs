using Whizbang.Testing.Async;

namespace Whizbang.Testing.Tests.Async;

/// <summary>
/// Tests for <see cref="AsyncTestHelpers"/>. Poll-driven paths use a zero poll interval and
/// call-counting conditions so no wall-clock waiting is involved; timeout paths use zero
/// timeouts (the helper's own shortest configurable timer).
/// </summary>
public class AsyncTestHelpersTests {
  private static readonly TimeSpan _longTimeout = TimeSpan.FromSeconds(30);

  // ============== WaitForConditionAsync (sync condition) ==============

  [Test]
  public async Task WaitForCondition_AlreadyTrue_ReturnsImmediatelyAsync() {
    var calls = 0;

    await AsyncTestHelpers.WaitForConditionAsync(
      () => {
        calls++;
        return true;
      },
      _longTimeout);

    await Assert.That(calls).IsEqualTo(1);
  }

  [Test]
  public async Task WaitForCondition_BecomesTrueAfterPolls_ReturnsAsync() {
    var calls = 0;

    await AsyncTestHelpers.WaitForConditionAsync(
      () => ++calls >= 3,
      _longTimeout,
      pollInterval: TimeSpan.Zero);

    await Assert.That(calls).IsEqualTo(3);
  }

  [Test]
  public async Task WaitForCondition_NeverTrue_ZeroTimeout_ThrowsTimeoutAsync() {
    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync(
        () => false,
        TimeSpan.Zero,
        pollInterval: TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Condition not met within");
  }

  [Test]
  public async Task WaitForCondition_CustomTimeoutMessage_IsUsedAsync() {
    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync(
        () => false,
        TimeSpan.Zero,
        pollInterval: TimeSpan.Zero,
        timeoutMessage: "custom timeout message"));

    await Assert.That(ex!.Message).IsEqualTo("custom timeout message");
  }

  [Test]
  public async Task WaitForCondition_ExternalCancellation_PropagatesAsCancellationAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<TaskCanceledException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync(
        () => false,
        _longTimeout,
        pollInterval: TimeSpan.Zero,
        cancellationToken: cts.Token));
  }

  [Test]
  public async Task WaitForCondition_NullCondition_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync((Func<bool>)null!, _longTimeout));

    await Assert.That(ex!.ParamName).IsEqualTo("condition");
  }

  // ============== WaitForConditionAsync (async condition) ==============

  [Test]
  public async Task WaitForConditionAsyncOverload_BecomesTrueAfterPolls_ReturnsAsync() {
    var calls = 0;

    await AsyncTestHelpers.WaitForConditionAsync(
      () => Task.FromResult(++calls >= 2),
      _longTimeout,
      pollInterval: TimeSpan.Zero);

    await Assert.That(calls).IsEqualTo(2);
  }

  [Test]
  public async Task WaitForConditionAsyncOverload_NeverTrue_ZeroTimeout_ThrowsTimeoutAsync() {
    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync(
        () => Task.FromResult(false),
        TimeSpan.Zero,
        pollInterval: TimeSpan.Zero,
        timeoutMessage: "async condition never met"));

    await Assert.That(ex!.Message).IsEqualTo("async condition never met");
  }

  [Test]
  public async Task WaitForConditionAsyncOverload_NullCondition_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.WaitForConditionAsync((Func<Task<bool>>)null!, _longTimeout));

    await Assert.That(ex!.ParamName).IsEqualTo("condition");
  }

  // ============== AssertNeverAsync (sync condition) ==============

  [Test]
  public async Task AssertNever_ConditionFalse_ZeroDuration_PassesViaFinalCheckAsync() {
    var calls = 0;

    await AsyncTestHelpers.AssertNeverAsync(
      () => {
        calls++;
        return false;
      },
      TimeSpan.Zero);

    // Only the final deadline check runs for a zero duration.
    await Assert.That(calls >= 1).IsTrue();
  }

  [Test]
  public async Task AssertNever_ConditionTrueImmediately_ThrowsAssertionAsync() {
    var ex = await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => true,
        TimeSpan.FromSeconds(30)));

    await Assert.That(ex!.Message).Contains("Condition became true");
  }

  [Test]
  public async Task AssertNever_ConditionTrueAtFinalCheck_ZeroDuration_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => true,
        TimeSpan.Zero,
        failureMessage: "final check caught it"));

    await Assert.That(ex!.Message).IsEqualTo("final check caught it");
  }

  [Test]
  public async Task AssertNever_ConditionBecomesTrueAfterPolls_ThrowsAsync() {
    var calls = 0;

    var ex = await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => ++calls >= 2,
        TimeSpan.FromSeconds(30),
        pollInterval: TimeSpan.Zero,
        failureMessage: "flipped mid-loop"));

    await Assert.That(ex!.Message).IsEqualTo("flipped mid-loop");
    await Assert.That(calls).IsEqualTo(2);
  }

  [Test]
  public async Task AssertNever_ConditionStaysFalse_ShortDuration_PassesAsync() {
    // Shortest practical duration: the helper must genuinely wait out its own deadline here.
    await AsyncTestHelpers.AssertNeverAsync(
      () => false,
      TimeSpan.FromMilliseconds(25),
      pollInterval: TimeSpan.FromMilliseconds(1));
  }

  [Test]
  public async Task AssertNever_PreCanceledToken_ThrowsOperationCanceledAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => false,
        TimeSpan.FromSeconds(30),
        cancellationToken: cts.Token));
  }

  [Test]
  public async Task AssertNever_NullCondition_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync((Func<bool>)null!, TimeSpan.Zero));

    await Assert.That(ex!.ParamName).IsEqualTo("condition");
  }

  // ============== AssertNeverAsync (async condition) ==============

  [Test]
  public async Task AssertNeverAsyncOverload_ConditionFalse_ZeroDuration_PassesAsync() {
    var calls = 0;

    await AsyncTestHelpers.AssertNeverAsync(
      () => {
        calls++;
        return Task.FromResult(false);
      },
      TimeSpan.Zero);

    await Assert.That(calls >= 1).IsTrue();
  }

  [Test]
  public async Task AssertNeverAsyncOverload_ConditionTrueImmediately_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => Task.FromResult(true),
        TimeSpan.FromSeconds(30),
        failureMessage: "async immediately true"));

    await Assert.That(ex!.Message).IsEqualTo("async immediately true");
  }

  [Test]
  public async Task AssertNeverAsyncOverload_ConditionTrueAtFinalCheck_ZeroDuration_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => Task.FromResult(true),
        TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Condition became true");
  }

  [Test]
  public async Task AssertNeverAsyncOverload_ConditionBecomesTrueAfterPolls_ThrowsAsync() {
    var calls = 0;

    await Assert.ThrowsAsync<AssertionException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => Task.FromResult(++calls >= 2),
        TimeSpan.FromSeconds(30),
        pollInterval: TimeSpan.Zero));

    await Assert.That(calls).IsEqualTo(2);
  }

  [Test]
  public async Task AssertNeverAsyncOverload_ConditionStaysFalse_ShortDuration_PassesAsync() {
    await AsyncTestHelpers.AssertNeverAsync(
      () => Task.FromResult(false),
      TimeSpan.FromMilliseconds(25),
      pollInterval: TimeSpan.FromMilliseconds(1));
  }

  [Test]
  public async Task AssertNeverAsyncOverload_PreCanceledToken_ThrowsAsync() {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync(
        () => Task.FromResult(false),
        TimeSpan.FromSeconds(30),
        cancellationToken: cts.Token));
  }

  [Test]
  public async Task AssertNeverAsyncOverload_NullCondition_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.AssertNeverAsync((Func<Task<bool>>)null!, TimeSpan.Zero));

    await Assert.That(ex!.ParamName).IsEqualTo("condition");
  }

  // ============== WaitForValueAsync ==============

  [Test]
  public async Task WaitForValue_AlreadySatisfied_ReturnsValueAsync() {
    var value = await AsyncTestHelpers.WaitForValueAsync(
      () => 42,
      v => v == 42,
      _longTimeout);

    await Assert.That(value).IsEqualTo(42);
  }

  [Test]
  public async Task WaitForValue_SatisfiedAfterPolls_ReturnsFinalValueAsync() {
    var counter = 0;

    var value = await AsyncTestHelpers.WaitForValueAsync(
      () => ++counter,
      v => v >= 3,
      _longTimeout,
      pollInterval: TimeSpan.Zero);

    await Assert.That(value).IsEqualTo(3);
  }

  [Test]
  public async Task WaitForValue_NeverSatisfied_ZeroTimeout_ThrowsTimeoutAsync() {
    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await AsyncTestHelpers.WaitForValueAsync(
        () => 0,
        v => v > 0,
        TimeSpan.Zero,
        pollInterval: TimeSpan.Zero));

    await Assert.That(ex!.Message).Contains("Value condition not met within");
  }

  [Test]
  public async Task WaitForValue_CustomTimeoutMessage_IsUsedAsync() {
    var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
      await AsyncTestHelpers.WaitForValueAsync(
        () => 0,
        v => v > 0,
        TimeSpan.Zero,
        pollInterval: TimeSpan.Zero,
        timeoutMessage: "value never arrived"));

    await Assert.That(ex!.Message).IsEqualTo("value never arrived");
  }

  [Test]
  public async Task WaitForValue_NullGetValue_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.WaitForValueAsync<int>(null!, _ => true, _longTimeout));

    await Assert.That(ex!.ParamName).IsEqualTo("getValue");
  }

  [Test]
  public async Task WaitForValue_NullPredicate_ThrowsAsync() {
    var ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
      await AsyncTestHelpers.WaitForValueAsync(() => 1, null!, _longTimeout));

    await Assert.That(ex!.ParamName).IsEqualTo("predicate");
  }

  // ============== DefaultPollInterval / AssertionException ==============

  [Test]
  public async Task DefaultPollInterval_IsTenMillisecondsAsync() {
    await Assert.That(AsyncTestHelpers.DefaultPollInterval).IsEqualTo(TimeSpan.FromMilliseconds(10));
  }

  [Test]
  public async Task AssertionException_DefaultCtor_CreatesInstanceAsync() {
    var ex = new AssertionException();

    await Assert.That(ex).IsNotNull();
  }

  [Test]
  public async Task AssertionException_MessageCtor_StoresMessageAsync() {
    var ex = new AssertionException("failed hard");

    await Assert.That(ex.Message).IsEqualTo("failed hard");
  }

  [Test]
  public async Task AssertionException_InnerExceptionCtor_StoresBothAsync() {
    var inner = new InvalidOperationException("cause");

    var ex = new AssertionException("outer", inner);

    await Assert.That(ex.Message).IsEqualTo("outer");
    await Assert.That(ReferenceEquals(ex.InnerException, inner)).IsTrue();
  }
}
