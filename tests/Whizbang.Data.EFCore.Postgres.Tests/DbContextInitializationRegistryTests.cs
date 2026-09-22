using System.Reflection;
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
[NotInParallel("DbContextInitializationRegistry")]
[Category("Shard3")]
public class DbContextInitializationRegistryTests {
  [Before(Test)]
  public void ResetStaticState() => DbContextInitializationRegistry.ResetForTesting();

  [Test]
  public async Task Register_AddsInitializer_IncreasesCountAsync() {
    // Arrange & Act
    DbContextInitializationRegistry.Register<FakeDbContextA>(
        (_, _, _) => Task.CompletedTask);

    // Assert
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(1);
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

    // Act & Assert
    await Assert.That(DbContextInitializationRegistry.Count).IsEqualTo(3);
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
