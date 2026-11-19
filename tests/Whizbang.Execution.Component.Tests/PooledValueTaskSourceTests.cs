// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks.Sources;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Execution;
using Whizbang.Core.Pooling;

namespace Whizbang.Execution.Tests;

/// <summary>
/// Comprehensive tests for PooledValueTaskSource{T} to ensure zero-allocation
/// async patterns work correctly and safely.
/// </summary>
public class PooledValueTaskSourceTests {
  // ============================================================================
  // LIFECYCLE TESTS: Rent, Reset, Return, Reuse
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_NewInstance_StartsWithVersionZeroAsync() {
    // Arrange & Act
    var source = new PooledValueTaskSource<int>();

    // Assert
    await Assert.That(source.Token).IsEqualTo((short)0);
  }

  [Test]
  public async Task PooledValueTaskSource_Reset_IncrementsVersionTokenAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var initialToken = source.Token;

    // Act
    source.Reset();

    // Assert
    await Assert.That(source.Token).IsNotEqualTo(initialToken);
    await Assert.That(source.Token).IsEqualTo((short)(initialToken + 1));
  }

  [Test]
  [Arguments(1)]
  [Arguments(5)]
  [Arguments(10)]
  public async Task PooledValueTaskSource_MultipleResets_TokenIncreasesMonotonicallyAsync(int resetCount) {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var initialToken = source.Token;

    // Act
    for (int i = 0; i < resetCount; i++) {
      source.Reset();
    }

    // Assert
    await Assert.That(source.Token).IsEqualTo((short)(initialToken + resetCount));
  }

  [Test]
  public async Task PooledValueTaskSource_ReuseAfterReset_WorksCorrectlyAsync() {
    // Arrange
    var source = new PooledValueTaskSource<string>();

    // First use
    source.SetResult("first");
    var firstTask = new ValueTask<string>(source, source.Token);
    var firstResult = await firstTask;

    // Reset for reuse
    source.Reset();

    // Second use
    source.SetResult("second");
    var secondTask = new ValueTask<string>(source, source.Token);
    var secondResult = await secondTask;

    // Assert
    await Assert.That(firstResult).IsEqualTo("first");
    await Assert.That(secondResult).IsEqualTo("second");
  }

  // ============================================================================
  // SUCCESS PATH TESTS: SetResult → GetResult
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_SetResult_CompletesValueTaskAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var valueTask = new ValueTask<int>(source, source.Token);

    // Act
    source.SetResult(42);
    var result = await valueTask;

    // Assert
    await Assert.That(result).IsEqualTo(42);
  }

  [Test]
  [Arguments(0)]
  [Arguments(42)]
  [Arguments(int.MaxValue)]
  [Arguments(int.MinValue)]
  public async Task PooledValueTaskSource_SetResult_VariousIntegers_ReturnsCorrectValueAsync(int value) {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var valueTask = new ValueTask<int>(source, source.Token);

    // Act
    source.SetResult(value);
    var result = await valueTask;

    // Assert
    await Assert.That(result).IsEqualTo(value);
  }

  [Test]
  [Arguments("")]
  [Arguments("hello")]
  [Arguments("lorem ipsum dolor sit amet")]
  public async Task PooledValueTaskSource_SetResult_VariousStrings_ReturnsCorrectValueAsync(string value) {
    // Arrange
    var source = new PooledValueTaskSource<string>();
    var valueTask = new ValueTask<string>(source, source.Token);

    // Act
    source.SetResult(value);
    var result = await valueTask;

    // Assert
    await Assert.That(result).IsEqualTo(value);
  }

  [Test]
  public async Task PooledValueTaskSource_SetResultNull_ReturnsNullAsync() {
    // Arrange
    var source = new PooledValueTaskSource<string?>();
    var valueTask = new ValueTask<string?>(source, source.Token);

    // Act
    source.SetResult(null);
    var result = await valueTask;

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task PooledValueTaskSource_GetStatus_Pending_ReturnsCorrectStatusAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var token = source.Token;

    // Act
    var status = source.GetStatus(token);

    // Assert
    await Assert.That(status).IsEqualTo(ValueTaskSourceStatus.Pending);
  }

  [Test]
  public async Task PooledValueTaskSource_GetStatus_AfterSetResult_ReturnsSucceededAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var token = source.Token;

    // Act
    source.SetResult(42);
    var status = source.GetStatus(token);

    // Assert
    await Assert.That(status).IsEqualTo(ValueTaskSourceStatus.Succeeded);
  }

  // ============================================================================
  // EXCEPTION HANDLING TESTS: SetException → GetResult throws
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_SetException_ThrowsOnAwaitAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var valueTask = new ValueTask<int>(source, source.Token);
    var expectedException = new InvalidOperationException("Test exception");

    // Act
    source.SetException(expectedException);

    // Assert
    await Assert.That(async () => await valueTask)
      .Throws<InvalidOperationException>()
      .WithMessage("Test exception");
  }

  [Test]
  [Arguments(typeof(ArgumentException))]
  [Arguments(typeof(InvalidOperationException))]
  [Arguments(typeof(NotSupportedException))]
  public async Task PooledValueTaskSource_SetException_VariousExceptionTypes_PreservesExceptionTypeAsync([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type exceptionType) {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var valueTask = new ValueTask<int>(source, source.Token);
    var exception = (Exception)Activator.CreateInstance(exceptionType, "Test")!;

    // Act
    source.SetException(exception);

    // Assert
    // TUnit doesn't support dynamic exception types - just verify it throws
    await Assert.That(async () => await valueTask)
      .ThrowsException();
  }

  [Test]
  public async Task PooledValueTaskSource_GetStatus_AfterSetException_ReturnsFaultedAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var token = source.Token;

    // Act
    source.SetException(new InvalidOperationException());
    var status = source.GetStatus(token);

    // Assert
    await Assert.That(status).IsEqualTo(ValueTaskSourceStatus.Faulted);
  }

  // ============================================================================
  // TOKEN VALIDATION TESTS: Prevents stale reuse
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_GetResult_WithStaleToken_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var staleToken = source.Token;

    // Reset, which invalidates the token
    source.Reset();
    source.SetResult(42);

    // Act & Assert - using stale token should throw
    await Assert.That(() => source.GetResult(staleToken))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PooledValueTaskSource_GetStatus_WithStaleToken_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var staleToken = source.Token;

    // Reset, which invalidates the token
    source.Reset();

    // Act & Assert - using stale token should throw
    await Assert.That(() => source.GetStatus(staleToken))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PooledValueTaskSource_AwaitWithStaleToken_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var staleToken = source.Token;

    // Reset, which invalidates the token
    source.Reset();
    source.SetResult(42);

    // Create ValueTask with stale token
    var valueTask = new ValueTask<int>(source, staleToken);

    // Act & Assert - awaiting with stale token should throw
    await Assert.That(async () => await valueTask)
      .Throws<InvalidOperationException>();
  }

  // ============================================================================
  // CONCURRENT REUSE SAFETY TESTS
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_ConcurrentAwait_OnSameToken_WorksCorrectlyAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var token = source.Token;
    var valueTask1 = new ValueTask<int>(source, token);
    var valueTask2 = valueTask1; // Share the same ValueTask

    // Act
    source.SetResult(42);
    var result1 = await valueTask1;
    var result2 = await valueTask2;

    // Assert
    await Assert.That(result1).IsEqualTo(42);
    await Assert.That(result2).IsEqualTo(42);
  }

  [Test]
  [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S5034:ValueTask should be consumed correctly", Justification = "Awaiting the pooled source twice after completion is the behavior this test pins.")]
  public async Task PooledValueTaskSource_MultipleAwaitsAfterCompletion_WorksAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var token = source.Token;
    var valueTask = new ValueTask<int>(source, token);

    // Act - Complete the source
    source.SetResult(42);

    // Multiple awaits after completion is allowed (returns cached result)
    var result1 = await valueTask;
    var result2 = await valueTask;

    // Assert - Both awaits return the same result
    await Assert.That(result1).IsEqualTo(42);
    await Assert.That(result2).IsEqualTo(42);
  }

  // ============================================================================
  // POOLING INTEGRATION TESTS: With ArrayPool
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_SimplePoolingPattern_WorksCorrectlyAsync() {
    // Arrange - Simple pool using a bag for demonstration
    var pool = new System.Collections.Concurrent.ConcurrentBag<PooledValueTaskSource<int>>();

    // Create and use first instance
    var source = new PooledValueTaskSource<int>();
    source.SetResult(10);
    var firstTask = new ValueTask<int>(source, source.Token);
    var firstResult = await firstTask;

    // Return to pool
    source.Reset();
    pool.Add(source);

    // Rent from pool
    var gotSource = pool.TryTake(out var source2);
    await Assert.That(gotSource).IsTrue();

    // Verify it's the same instance (pooling working)
    await Assert.That(object.ReferenceEquals(source, source2)).IsTrue();

    // Second use with different value
    source2!.SetResult(20);
    var secondTask = new ValueTask<int>(source2, source2.Token);
    var secondResult = await secondTask;

    // Assert
    await Assert.That(firstResult).IsEqualTo(10);
    await Assert.That(secondResult).IsEqualTo(20);
  }

  [Test]
  [Arguments(10)]
  [Arguments(50)]
  [Arguments(100)]
  public async Task PooledValueTaskSource_ManyOperations_WithPooling_NoErrorsAsync(int operationCount) {
    // Arrange - Simple pool for demonstration
    var pool = new System.Collections.Concurrent.ConcurrentBag<PooledValueTaskSource<int>>();
    var results = new List<int>();

    // Act
    for (int i = 0; i < operationCount; i++) {
      // Try to get from pool, or create new
      if (!pool.TryTake(out var source)) {
        source = new PooledValueTaskSource<int>();
      }

      source.Reset();
      source.SetResult(i);
      var valueTask = new ValueTask<int>(source, source.Token);
      var result = await valueTask;
      results.Add(result);

      // Return to pool for reuse
      source.Reset();
      pool.Add(source);
    }

    // Assert
    await Assert.That(results.Count).IsEqualTo(operationCount);
    for (int i = 0; i < operationCount; i++) {
      await Assert.That(results[i]).IsEqualTo(i);
    }

    // Verify pooling worked (should have far fewer instances than operations)
    await Assert.That(pool.Count).IsLessThan(operationCount);
  }

  // ============================================================================
  // EDGE CASE TESTS
  // ============================================================================

  [Test]
  public async Task PooledValueTaskSource_DoubleSetResult_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    source.SetResult(42);

    // Act & Assert - setting result twice should throw
    await Assert.That(() => source.SetResult(99))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PooledValueTaskSource_SetResultAfterSetException_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    source.SetException(new InvalidOperationException());

    // Act & Assert - setting result after exception should throw
    await Assert.That(() => source.SetResult(42))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PooledValueTaskSource_SetExceptionAfterSetResult_ThrowsAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    source.SetResult(42);

    // Act & Assert - setting exception after result should throw
    await Assert.That(() => source.SetException(new InvalidOperationException()))
      .Throws<InvalidOperationException>();
  }

  [Test]
  public async Task PooledValueTaskSource_GetResult_BeforeCompletion_BlocksUntilSetAsync() {
    // Arrange
    var source = new PooledValueTaskSource<int>();
    var valueTask = new ValueTask<int>(source, source.Token);
    var resultReceived = false;
    var result = 0;

    // Signal: fires when the consumer registers a continuation (begins awaiting)
    var continuationRegistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    source.OnContinuationRegistered += () => continuationRegistered.TrySetResult();

    // Act - start awaiting (will block until SetResult)
    var awaitTask = Task.Run(async () => {
      result = await valueTask;
      resultReceived = true;
    });

    // Wait for the consumer to register its continuation (proves it's blocked)
    await continuationRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Assert.That(resultReceived).IsFalse();

    // Now complete it
    source.SetResult(42);
    await awaitTask;

    // Assert
    await Assert.That(resultReceived).IsTrue();
    await Assert.That(result).IsEqualTo(42);
  }

  // ============================================================================
  // CONCURRENCY: completion racing the awaiter's registration
  // ============================================================================

  /// <summary>
  /// The completing thread and the awaiting thread hand off through the source: one writes the result and looks for a
  /// continuation, the other registers a continuation and looks for the result. Whatever order they land in, the
  /// awaiter must be resumed, and only once. A source whose two sides each write one field and read the other with no
  /// fence between can lose the wake-up (each side reads the other's field before its own write is visible, so neither
  /// runs the continuation and the awaiter waits forever), which is how a serial executor's caller hung on an arm64
  /// runner. A bounded race rather than a scripted one: the reordering happens in the processor, where no hook can
  /// place it, so many aligned races are run and every one must resume exactly once. A correct source passes every
  /// time; the wait on each resumption is bounded only so a lost one fails here instead of hanging the run.
  /// </summary>
  [Test]
  [Timeout(120000)]
  public async Task PooledValueTaskSource_CompletionRacingRegistration_ResumesTheAwaiterExactlyOnceAsync(
      CancellationToken cancellationToken) {
    const int lanes = 4;
    const int racesPerLane = 50_000;

    var results = await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Task.Factory.StartNew(
      () => _raceLane(racesPerLane, cancellationToken),
      cancellationToken,
      TaskCreationOptions.LongRunning,
      TaskScheduler.Default)));

    var withoutState = Volatile.Read(ref _resumedWithoutItsState);
    await Assert.That(withoutState).IsEqualTo(0)
      .Because("a continuation must run with the state it was registered with; a real awaiter's state is its state "
             + "machine, and resuming it with nothing is an unhandled exception on a pool thread");
    var lost = results.Sum(r => r.Lost);
    var resumedTwice = results.Sum(r => r.ResumedTwice);
    await Assert.That(lost).IsEqualTo(0)
      .Because("an awaiter whose completion raced its registration must still be resumed; a lost wake-up is a caller "
             + "that waits forever");
    await Assert.That(resumedTwice).IsEqualTo(0)
      .Because("a continuation run twice resumes one async state machine twice");
  }

  private static int _resumedWithoutItsState;

  private sealed class RaceAwaiter : IDisposable {
    public readonly ManualResetEventSlim Resumed = new();
    public int Runs;

    public void Dispose() => Resumed.Dispose();
  }

  private static (int Lost, int ResumedTwice) _raceLane(int races, CancellationToken cancellationToken) {
    using var aligned = new Barrier(2);
    var awaiters = new RaceAwaiter[races];
    PooledValueTaskSource<int>? current = null;
    var completer = new Thread(() => {
      for (var i = 0; i < races; i++) {
        aligned.SignalAndWait(cancellationToken);
        Volatile.Read(ref current)!.SetResult(i);
      }
    }) { IsBackground = true };
    completer.Start();

    var lost = 0;
    for (var i = 0; i < races; i++) {
      var source = new PooledValueTaskSource<int>();
      var awaiter = new RaceAwaiter();
      awaiters[i] = awaiter;
      Volatile.Write(ref current, source);
      aligned.SignalAndWait(cancellationToken);
      // Registered whatever the status reads: registering after completion is legal and must run the continuation.
      source.OnCompleted(static state => {
        if (state is not RaceAwaiter a) {
          // The continuation ran without the state it was registered with: the completer read the continuation before
          // the awaiter's state was visible. A real awaiter's state is its state machine, so this is a crash there.
          Interlocked.Increment(ref _resumedWithoutItsState);
          return;
        }
        Interlocked.Increment(ref a.Runs);
        a.Resumed.Set();
      }, awaiter, source.Token, ValueTaskSourceOnCompletedFlags.None);
      if (!awaiter.Resumed.Wait(TimeSpan.FromSeconds(5), cancellationToken)) {
        lost++;
      }
    }
    completer.Join();
    var resumedTwice = awaiters.Count(a => Volatile.Read(ref a.Runs) > 1);
    foreach (var a in awaiters) {
      a.Dispose();
    }
    return (lost, resumedTwice);
  }
}
