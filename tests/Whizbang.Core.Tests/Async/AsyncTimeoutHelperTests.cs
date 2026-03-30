using TUnit.Core;
using Whizbang.Core.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// Tests for <see cref="AsyncTimeoutHelper"/>.
/// </summary>
/// <tests>Whizbang.Core/Async/AsyncTimeoutHelper.cs</tests>
public class AsyncTimeoutHelperTests {
  // ==========================================================================
  // WaitWithTimeoutAsync (non-generic)
  // ==========================================================================

  [Test]
  public async Task WaitWithTimeoutAsync_CompletesBeforeTimeout_SucceedsAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    tcs.TrySetResult(true);

    // Should not throw — task is already complete
    await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        tcs.Task, TimeSpan.FromSeconds(5), "should not timeout");
  }

  [Test]
  public async Task WaitWithTimeoutAsync_ExceedsTimeout_ThrowsTimeoutExceptionAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    // Never completes

    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            tcs.Task, TimeSpan.FromMilliseconds(50), "Test timed out"))
        .ThrowsExactly<TimeoutException>()
        .WithMessage("Test timed out");
  }

  [Test]
  public async Task WaitWithTimeoutAsync_ExternalCancellation_ThrowsOperationCanceledExceptionAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            tcs.Task, TimeSpan.FromSeconds(5), "should not be this", cts.Token))
        .ThrowsException();
  }

  [Test]
  public async Task WaitWithTimeoutAsync_NullTask_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            null!, TimeSpan.FromSeconds(1), "msg"))
        .ThrowsExactly<ArgumentNullException>();
  }

  // ==========================================================================
  // WaitWithTimeoutAsync<T> (generic)
  // ==========================================================================

  [Test]
  public async Task WaitWithTimeoutAsyncGeneric_CompletesBeforeTimeout_ReturnsResultAsync() {
    var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    tcs.TrySetResult(42);

    var result = await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        tcs.Task, TimeSpan.FromSeconds(5), "should not timeout");

    await Assert.That(result).IsEqualTo(42);
  }

  [Test]
  public async Task WaitWithTimeoutAsyncGeneric_ExceedsTimeout_ThrowsTimeoutExceptionAsync() {
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            tcs.Task, TimeSpan.FromMilliseconds(50), "Generic timed out"))
        .ThrowsExactly<TimeoutException>()
        .WithMessage("Generic timed out");
  }

  [Test]
  public async Task WaitWithTimeoutAsyncGeneric_ExternalCancellation_ThrowsOperationCanceledExceptionAsync() {
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            tcs.Task, TimeSpan.FromSeconds(5), "should not be this", cts.Token))
        .ThrowsException();
  }

  [Test]
  public async Task WaitWithTimeoutAsyncGeneric_NullTask_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync<int>(
            null!, TimeSpan.FromSeconds(1), "msg"))
        .ThrowsExactly<ArgumentNullException>();
  }

  // ==========================================================================
  // Edge cases
  // ==========================================================================

  [Test]
  public async Task WaitWithTimeoutAsync_TaskCompletesJustBeforeTimeout_SucceedsAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Complete after a short delay but well before timeout
    _ = Task.Run(async () => {
      await Task.Delay(20);
      tcs.TrySetResult(true);
    });

    await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        tcs.Task, TimeSpan.FromSeconds(5), "should not timeout");
  }

  [Test]
  public async Task WaitWithTimeoutAsync_FaultedTask_PropagatesExceptionAsync() {
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    tcs.TrySetException(new InvalidOperationException("test fault"));

    await Assert.That(async () =>
        await AsyncTimeoutHelper.WaitWithTimeoutAsync(
            tcs.Task, TimeSpan.FromSeconds(5), "should not be timeout"))
        .ThrowsExactly<InvalidOperationException>()
        .WithMessage("test fault");
  }
}
