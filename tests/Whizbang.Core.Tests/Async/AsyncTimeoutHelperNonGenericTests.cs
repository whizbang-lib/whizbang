using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Async;

namespace Whizbang.Core.Tests.Async;

/// <summary>
/// The void-returning overload of the timeout helper, and the distinction it draws between a task
/// that ran out of time and a caller that stopped waiting.
/// <para>
/// Both arrive as <see cref="OperationCanceledException"/> from the underlying wait, and the filter
/// on the caller's token is the only thing separating them. Getting that backwards is expensive in
/// both directions: a shutdown reported as <see cref="TimeoutException"/> sends an operator looking
/// for a slow dependency that was never slow, and a real timeout reported as cancellation is
/// swallowed by every caller that treats cancellation as "we are going away anyway".
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Async/AsyncTimeoutHelper.cs</code-under-test>
public class AsyncTimeoutHelperNonGenericTests {

  [Test]
  public async Task ATaskThatCompletesInTime_PassesThroughAsync() {
    await AsyncTimeoutHelper.WaitWithTimeoutAsync(
      Task.CompletedTask, TimeSpan.FromSeconds(30), "should not be reported");
  }

  [Test]
  public async Task ATaskThatOverrunsTheBudget_SurfacesAsTimeoutNotCancellationAsync() {
    // The message matters as much as the type: it is what names the operation that overran, and a
    // bare TimeoutException at this level tells an operator nothing about which wait it was.
    var never = new TaskCompletionSource().Task;

    await Assert.That(async () => await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        never, TimeSpan.FromMilliseconds(50), "the widget settle never arrived"))
      .Throws<TimeoutException>()
      .WithMessage("the widget settle never arrived")
      .Because("a timeout reported as cancellation is swallowed by every caller that treats "
             + "cancellation as shutdown");
  }

  [Test]
  public async Task ACallerWhoStopsWaiting_GetsCancellationNotAFabricatedTimeoutAsync() {
    // The other side of the filter. Nothing was slow — the caller withdrew — and calling that a
    // timeout invents a performance problem in whatever the caller was waiting on.
    var never = new TaskCompletionSource().Task;
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        never, TimeSpan.FromSeconds(30), "should not be reported", stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the caller going away is not evidence that the awaited work was slow");
  }

  [Test]
  public async Task ANullTask_IsRejectedAtTheCallSiteAsync() {
    await Assert.That(async () => await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        null!, TimeSpan.FromSeconds(1), "unused"))
      .Throws<ArgumentNullException>()
      .Because("the guard runs eagerly rather than inside the async state machine, so the caller "
             + "sees the mistake at the call site instead of on an await far away");
  }
}
