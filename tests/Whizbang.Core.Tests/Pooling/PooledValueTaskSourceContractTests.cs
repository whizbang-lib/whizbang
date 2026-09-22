using System.Threading.Tasks.Sources;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Pooling;

namespace Whizbang.Core.Tests.Pooling;

/// <summary>
/// The three <see cref="IValueTaskSource{TResult}"/> contract points a pooled source has to get
/// right and that awaiting it normally never exercises: reading a result before completion,
/// registering a second continuation, and registering the first one after the source has already
/// completed.
/// </summary>
/// <remarks>
/// These matter because a pooled source is reused. A source handed out again while a stale
/// <see cref="ValueTask"/> still points at it is the classic pooling defect, and each of these
/// three points is where that defect surfaces: reading an unset result would hand the caller a
/// default value that looks like a real one; accepting a second continuation would silently drop
/// the first awaiter, hanging it forever; and dropping the post-completion registration would hang
/// an awaiter that arrived a moment after the producer finished.
/// </remarks>
public class PooledValueTaskSourceContractTests {

  [Test]
  public async Task GetResult_BeforeCompletion_ThrowsRatherThanReturningTheDefaultAsync() {
    var source = new PooledValueTaskSource<int>();

    await Assert.That(() => source.GetResult(source.Token))
      .ThrowsExactly<InvalidOperationException>()
      .Because("returning default(T) from an incomplete source would be indistinguishable from a "
             + "genuine zero result, so an awaiter woken by a stale token would silently consume "
             + "a value the producer never set");
  }

  [Test]
  public async Task GetResult_BeforeCompletion_ThrowsEvenForAReferenceResultTypeAsync() {
    // The status check, not a null check, is what rejects this: a reference-typed source has a
    // perfectly usable "unset" value (null) that would otherwise pass straight through.
    var source = new PooledValueTaskSource<string>();

    await Assert.That(source.GetStatus(source.Token)).IsEqualTo(ValueTaskSourceStatus.Pending)
      .Because("the premise of this test is that the source has not completed");
    await Assert.That(() => source.GetResult(source.Token))
      .ThrowsExactly<InvalidOperationException>()
      .Because("an unset reference result must fault the read, not surface as null");
  }

  [Test]
  public async Task OnCompleted_CalledTwice_RejectsTheSecondRegistrationAsync() {
    var source = new PooledValueTaskSource<int>();
    var firstRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    source.OnCompleted(_ => firstRan.TrySetResult(), state: null, source.Token, ValueTaskSourceOnCompletedFlags.None);

    await Assert.That(() => source.OnCompleted(_ => secondRan.TrySetResult(), state: null, source.Token, ValueTaskSourceOnCompletedFlags.None))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a second continuation would overwrite the first, and the first awaiter — already "
             + "suspended on this source — would never be resumed");

    source.SetResult(7);

    // The source dispatches continuations through the thread pool, so this is a wait, not a poll.
    await firstRan.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(secondRan.Task.IsCompleted).IsFalse()
      .Because("the rejected continuation must never be installed OR invoked — the guard has to "
             + "leave the ORIGINAL registration in place, not swap it and then complain");
  }

  [Test]
  public async Task OnCompleted_AfterCompletion_InvokesTheContinuationInsteadOfParkingItAsync() {
    var source = new PooledValueTaskSource<int>();
    source.SetResult(42);

    var resumed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var marker = new object();

    // No awaiter was registered when SetResult ran, so nothing was signaled then. Registering
    // now is the "producer finished first" race, and the source has to complete the awaiter
    // itself rather than wait for a completion that has already happened.
    source.OnCompleted(state => resumed.TrySetResult(state), marker, source.Token, ValueTaskSourceOnCompletedFlags.None);

    var observedState = await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(observedState).IsSameReferenceAs(marker)
      .Because("the continuation must be invoked with the state it was registered with — a source "
             + "that parked it instead would hang every awaiter that arrived after the producer");
    await Assert.That(source.GetResult(source.Token)).IsEqualTo(42)
      .Because("the resumed awaiter must be able to read the result the producer already set");
  }

  [Test]
  public async Task OnCompleted_AfterCompletion_AlsoResumesAFaultedSourceAsync() {
    // Faulted is a completion too: the same "already finished" branch has to fire, or an awaiter
    // that registered late on a failed operation would hang instead of observing the exception.
    var source = new PooledValueTaskSource<int>();
    source.SetException(new InvalidTimeZoneException("boom"));

    var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    source.OnCompleted(_ => resumed.TrySetResult(), state: null, source.Token, ValueTaskSourceOnCompletedFlags.None);

    await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10));

    await Assert.That(() => source.GetResult(source.Token))
      .ThrowsExactly<InvalidTimeZoneException>()
      .Because("the late awaiter must be resumed and then observe the producer's exception, not a "
             + "swallowed failure");
  }
}
