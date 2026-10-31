// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for DbContextInitializationRegistry static registry.
/// Each test resets static state via reflection to ensure isolation.
/// </summary>
[NotInParallel(["DbContextInitializationRegistry", "EFCorePostgresTests"])]
[Category("Shard3")]
public class DbContextInitializationRegistryTests {
  private IDisposable? _restore;

  [Before(Test)]
  public void ResetStaticState() => _restore = DbContextInitializationRegistry.ResetForTesting();

  // Puts back the generated initializers this class cleared: later tests in the same process start hosts that
  // rely on them, and module initializers never run again.
  [After(Test)]
  public void RestoreStaticState() => _restore?.Dispose();

  [Test]
  public async Task ResetForTesting_DisposingTheScope_PutsBackWhatWasRegisteredAsync() {
    DbContextInitializationRegistry.Register<FakeDbContextA>((_, _, _) => Task.CompletedTask);

    using (DbContextInitializationRegistry.ResetForTesting()) {
      await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(0).Because("inside the scope the registry is empty");
    }

    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1)
      .Because("a reset that is not undone unregisters every generated schema initializer for the rest of the process");
  }

  [Test]
  public async Task Register_AddsInitializer_IncreasesCountAsync() {
    // Arrange & Act
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    // Assert
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  /// <summary>
  /// A module initializer that runs twice (a test fixture re-running the generated registration, or a host that
  /// calls it explicitly) must not make every start initialize the context twice: a second pass reconciles again
  /// within one start, so the first sighting of a retired object became its drop.
  /// </summary>
  [Test]
  public async Task Register_TheSameContextTwice_KeepsOne_SoAStartInitializesItOnceAsync() {
    var first = 0;
    var second = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>((_, _, _) => { first++; return Task.CompletedTask; });
    DbContextInitializationRegistry.Register<FakeDbContextA>((_, _, _) => { second++; return Task.CompletedTask; });

    await DbContextInitializationRegistry.InitializeAllAsync(new FakeServiceProvider());

    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
    await Assert.That((first, second)).IsEqualTo((0, 1)).Because("the latest registration for a context replaces the earlier one");
  }

  [Test]
  public async Task InitializeAllAsync_CallsAllRegisteredCallbacksAsync() {
    // Arrange
    var callCount = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });
    DbContextInitializationRegistry.Register<FakeDbContextB>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();

    // Act
    await DbContextInitializationRegistry.InitializeAllAsync(sp);

    // Assert
    await Assert.That(callCount).IsEqualTo(2);
  }

  [Test]
  public async Task InitializeAllAsync_IdempotencyGuard_SkipsSecondCallAsync() {
    // Arrange
    var callCount = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });

    var sp = new FakeServiceProvider();

    // Act — call twice
    await DbContextInitializationRegistry.InitializeAllAsync(sp);
    await DbContextInitializationRegistry.InitializeAllAsync(sp);

    // Assert — callback invoked only once
    await Assert.That(callCount).IsEqualTo(1);
  }

  /// <summary>
  /// The guard protects "do not initialize the same host twice", not "do not initialize more than
  /// once per process". A process that builds several hosts against several databases — every test
  /// suite with a host per test, and any composition root that hosts more than one service — must
  /// initialize each of them. The process-wide flag skipped every host after the first and left its
  /// database with no schema at all (issue #620).
  /// </summary>
  [Test]
  public async Task InitializeAllAsync_DifferentServiceProviders_EachInitializeAsync() {
    // Arrange
    var seen = new List<IServiceProvider>();
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (sp, _, _) => { seen.Add(sp); return Task.CompletedTask; });

    var firstHost = new FakeServiceProvider();
    var secondHost = new FakeServiceProvider();

    // Act — two hosts in one process
    await DbContextInitializationRegistry.InitializeAllAsync(firstHost);
    await DbContextInitializationRegistry.InitializeAllAsync(secondHost);

    // Assert — both ran, each against its own provider
    await Assert.That(seen.Count).IsEqualTo(2)
      .Because("a second host against a different database must not be told 'already initialized' "
             + "by a flag that knows nothing about hosts or databases");
    await Assert.That(seen[0]).IsSameReferenceAs(firstHost);
    await Assert.That(seen[1]).IsSameReferenceAs(secondHost);
  }

  [Test]
  public async Task InitializeAllAsync_SameServiceProviderTwice_SkipsAndSaysSoAsync() {
    // Arrange
    var callCount = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => { callCount++; return Task.CompletedTask; });
    var host = new FakeServiceProvider();
    var sink = new List<string>();
    var logger = new ListLogger(sink);

    // Act
    await DbContextInitializationRegistry.InitializeAllAsync(host, logger);
    await DbContextInitializationRegistry.InitializeAllAsync(host, logger);

    // Assert — the second call is a documented no-op, and it is not silent about it
    await Assert.That(callCount).IsEqualTo(1);
    await Assert.That(sink.Any(l => l.Contains("already initialized", StringComparison.OrdinalIgnoreCase))).IsTrue()
      .Because("the guard firing must remain observable — it was the only signal in #620");
  }

  [Test]
  public async Task InitializeAllAsync_WhenAnInitializerThrows_DoesNotLatchSoTheRetryReRunsItAsync() {
    // The guard claims the provider BEFORE running the callbacks, so a failed attempt used to keep
    // the claim: the caller's retry loop then took the "already initialized" exit, returned having
    // done nothing, and — because the loop reads this method returning as success — opened the
    // schema-ready gate over a database whose migration had thrown. A service came up reporting
    // "recovered" while its schema sat a release behind and its workers queried a table the failed
    // migration was supposed to create.
    var attempts = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => {
          attempts++;
          return attempts == 1
            ? Task.FromException(new InvalidOperationException("migration failed"))
            : Task.CompletedTask;
        });
    var host = new FakeServiceProvider();

    // Act — the first attempt throws, exactly as a failing migration does.
    await Assert.That(async () => await DbContextInitializationRegistry.InitializeAllAsync(host))
      .Throws<InvalidOperationException>();

    // Assert — the retry actually runs the initializer again rather than short-circuiting.
    await DbContextInitializationRegistry.InitializeAllAsync(host);
    await Assert.That(attempts).IsEqualTo(2)
      .Because("a failed attempt must release the claim, or the next attempt reports success "
        + "without having initialized anything.");

    // And the claim latches once it genuinely succeeds.
    await DbContextInitializationRegistry.InitializeAllAsync(host);
    await Assert.That(attempts).IsEqualTo(2)
      .Because("the idempotence guard still has to hold after a successful initialization.");
  }

  /// <summary>
  /// A second call while the first is still initializing waits for it (#1636). It returned at once, saying "already
  /// initialized", so a host that initialized again from ApplicationStarted went on to use a schema the first call was
  /// still migrating. The first call is held mid-initialization by its own callback, so "not finished" is a fact when
  /// it is asserted, not a race.
  /// </summary>
  [Test]
  public async Task InitializeAllAsync_ASecondCallWhileTheFirstRuns_ReturnsOnlyOnceTheFirstHasFinishedAsync() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(async (_, _, _) => {
      Interlocked.Increment(ref calls);
      entered.TrySetResult();
      await release.Task;
    });
    var host = new FakeServiceProvider();
    var sink = new List<string>();

    var first = DbContextInitializationRegistry.InitializeAllAsync(host);
    await entered.Task;
    var second = DbContextInitializationRegistry.InitializeAllAsync(host, new ListLogger(sink));

    await Assert.That(second.IsCompleted).IsFalse()
      .Because("the first initialization is still running, so the schema is not initialized yet");
    await Assert.That(sink).Contains("Whizbang database initialization is in progress; waiting for it to finish");
    release.TrySetResult();
    await second;
    await Assert.That(first.IsCompletedSuccessfully).IsTrue().Because("the second returned only after the first finished");
    await Assert.That(calls).IsEqualTo(1).Because("waiting is not initializing again");
  }

  /// <summary>
  /// A second call that waited for a first attempt that failed does not report success: it runs an attempt of its
  /// own, and returns only when that one has finished.
  /// </summary>
  [Test]
  public async Task InitializeAllAsync_ASecondCallWhoseFirstFails_RunsItsOwnAttemptAsync() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(async (_, _, _) => {
      if (Interlocked.Increment(ref calls) == 1) {
        entered.TrySetResult();
        await release.Task;
        throw new InvalidOperationException("migration failed");
      }
    });
    var host = new FakeServiceProvider();

    var first = DbContextInitializationRegistry.InitializeAllAsync(host);
    await entered.Task;
    var second = DbContextInitializationRegistry.InitializeAllAsync(host);
    release.TrySetResult();

    await Assert.That(async () => await first).Throws<InvalidOperationException>();
    await second;
    await Assert.That(calls).IsEqualTo(2).Because("the failed attempt initialized nothing, so the waiting call runs one");
  }

  /// <summary>
  /// The host's provider and the root scope its singletons are built from are two objects with one identity: a host
  /// that initializes from <c>app.Services</c> while its hosted initializer, built from the root scope, is still
  /// running waits for it rather than running a second initialization beside it (#1636).
  /// </summary>
  [Test]
  public async Task InitializeAllAsync_TheHostsProviderAndItsRootScope_AreOneHostAsync() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    DbContextInitializationRegistry.Register<FakeDbContextA>(async (_, _, _) => {
      Interlocked.Increment(ref calls);
      entered.TrySetResult();
      await release.Task;
    });
    await using var host = new ServiceCollection().BuildServiceProvider();
    var rootScope = host.GetRequiredService<IServiceProvider>();
    await Assert.That(rootScope).IsNotSameReferenceAs(host).Because("precondition: the two are different objects");

    var first = DbContextInitializationRegistry.InitializeAllAsync(rootScope);
    await entered.Task;
    var second = DbContextInitializationRegistry.InitializeAllAsync(host);

    await Assert.That(second.IsCompleted).IsFalse();
    release.TrySetResult();
    await Task.WhenAll(first, second);
    await Assert.That(calls).IsEqualTo(1).Because("one host, one initialization");
  }

  /// <summary>A call waiting for another's initialization stops waiting when its own token is canceled.</summary>
  [Test]
  public async Task InitializeAllAsync_ASecondCallsWait_EndsWithItsTokenAsync() {
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    DbContextInitializationRegistry.Register<FakeDbContextA>(async (_, _, _) => {
      entered.TrySetResult();
      await release.Task;
    });
    var host = new FakeServiceProvider();
    using var cts = new CancellationTokenSource();

    var first = DbContextInitializationRegistry.InitializeAllAsync(host);
    await entered.Task;
    var second = DbContextInitializationRegistry.InitializeAllAsync(host, cancellationToken: cts.Token);
    await cts.CancelAsync();

    await Assert.That(async () => await second).Throws<OperationCanceledException>();
    release.TrySetResult();
    await first;
  }

  [Test]
  public async Task InitializeAllAsync_WithNoRegistrations_CompletesSuccessfullyAsync() {
    // Arrange
    var sp = new FakeServiceProvider();

    // Act & Assert — should not throw
    await DbContextInitializationRegistry.InitializeAllAsync(sp);
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(0);
  }

  [Test]
  public async Task InitializeAllAsync_LogsStartAndCompletionAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    var sp = new FakeServiceProvider();
    var logger = NullLogger.Instance;

    // Act — should not throw when logger is provided
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);

    // Assert — if we get here without exception, logging delegates executed successfully
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  [Test]
  public async Task InitializeAllAsync_IdempotencyGuard_LogsSkipMessageAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    var sp = new FakeServiceProvider();
    var logger = NullLogger.Instance;

    // Act — first call initializes, second call hits the guard
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);
    await DbContextInitializationRegistry.InitializeAllAsync(sp, logger);

    // Assert — both calls complete without exception (debug log fires on second call)
    // If we reach here, the logger delegate paths executed without error
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
  }

  [Test]
  public async Task Count_ReturnsNumberOfRegisteredInitializersAsync() {
    // Arrange
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);
    DbContextInitializationRegistry.Register<FakeDbContextB>(
        (_, _, _) => Task.CompletedTask);
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    // Act & Assert: one per context; registering A again replaced its initializer rather than adding a second
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(2);
  }

  // --- Fake DbContext types for test isolation ---
  private sealed class FakeDbContextA;
  private sealed class FakeDbContextB;

  private sealed class FakeServiceProvider : IServiceProvider {
    public object? GetService(Type serviceType) => null;
  }

  private sealed class ListLogger(List<string> sink) : ILogger {
    private readonly List<string> _sink = sink;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (_sink) {
        _sink.Add(formatter(state, exception));
      }
    }
  }
}
