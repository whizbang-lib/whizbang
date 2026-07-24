using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Transports.RabbitMQ;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Tests for RabbitMQConnectionRetry.
/// Verifies retry logic, exponential backoff, and error handling.
/// </summary>
public class RabbitMQConnectionRetryTests {

  /// <summary>
  /// Recording logger that lets a test observe retry log arms. When
  /// <see cref="CancelOnStillRetrying"/> is set, the logger cancels that source the first
  /// time the "still retrying" warning (attempt % 10 == 0) is emitted — a deterministic,
  /// delay-free way to break out of the otherwise-infinite indefinite-retry loop exactly
  /// once the periodic-status branch has executed.
  /// </summary>
  private sealed class RecordingLogger : ILogger {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public CancellationTokenSource? CancelOnStillRetrying { get; set; }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) {
      var message = formatter(state, exception);
      Entries.Add((logLevel, message));
      if (CancelOnStillRetrying is not null && message.Contains("still failing", StringComparison.Ordinal)) {
        CancelOnStillRetrying.Cancel();
      }
    }

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }
  #region Constructor Tests

  [Test]
  public async Task Constructor_WithNullOptions_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.That(() => new RabbitMQConnectionRetry(null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithValidOptions_CreatesInstanceAsync() {
    // Arrange
    var options = new RabbitMQOptions();

    // Act
    var retry = new RabbitMQConnectionRetry(options);

    // Assert
    await Assert.That(retry).IsNotNull();
  }

  #endregion

  #region CalculateNextDelay Tests

  [Test]
  public async Task CalculateNextDelay_WithDefaultMultiplier_DoublesDelayAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(1);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(2));
  }

  [Test]
  public async Task CalculateNextDelay_WithCustomMultiplier_AppliesMultiplierAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 3.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(2);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(6));
  }

  [Test]
  public async Task CalculateNextDelay_WhenExceedsMaxDelay_CapsAtMaxDelayAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(20);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert - Would be 40 seconds, but capped at 30
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task CalculateNextDelay_WhenBelowMaxDelay_ReturnsCalculatedValueAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(10);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(20));
  }

  [Test]
  public async Task CalculateNextDelay_WithMultiplierLessThanOne_DecreasesDelayAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 0.5,
      MaxRetryDelay = TimeSpan.FromSeconds(30)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var currentDelay = TimeSpan.FromSeconds(10);

    // Act
    var nextDelay = retry.CalculateNextDelay(currentDelay);

    // Assert
    await Assert.That(nextDelay).IsEqualTo(TimeSpan.FromSeconds(5));
  }

  #endregion

  #region CreateConnectionWithRetryAsync (ConnectionString) Tests

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithNullConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions();
    var retry = new RabbitMQConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync((string)null!))
      .Throws<ArgumentException>();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithEmptyConnectionString_ThrowsArgumentExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions();
    var retry = new RabbitMQConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(""))
      .Throws<ArgumentException>();
  }

  #endregion

  #region CreateConnectionWithRetryAsync (Factory) Tests

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithNullFactory_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions();
    var retry = new RabbitMQConnectionRetry(options);

    // Act & Assert
    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync((ConnectionFactory)null!))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_WhenCancelled_ThrowsOperationCanceledExceptionAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 5,
      InitialRetryDelay = TimeSpan.FromSeconds(1)
    };
    var retry = new RabbitMQConnectionRetry(options);
    var factory = new ConnectionFactory { Uri = new Uri("amqp://localhost:5672") };
    var cts = new CancellationTokenSource();
    cts.Cancel();

    // Act & Assert
    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(factory, cts.Token))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithRetryIndefinitelyFalse_TriesInitialAttemptsAndThrowsAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 1,  // Only one retry after initial attempt
      InitialRetryDelay = TimeSpan.FromMilliseconds(10),
      RetryIndefinitely = false   // Disable indefinite retry
    };
    var retry = new RabbitMQConnectionRetry(options);
    var factory = new ConnectionFactory { Uri = new Uri("amqp://invalid-host:5672") };

    // Act & Assert
    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(factory))
      .Throws<BrokerUnreachableException>();
  }

  #endregion

  #region RabbitMQOptions Default Values Tests

  [Test]
  public async Task RabbitMQOptions_DefaultInitialRetryAttempts_IsFiveAsync() {
    // Arrange & Act
    var options = new RabbitMQOptions();

    // Assert
    await Assert.That(options.InitialRetryAttempts).IsEqualTo(5);
  }

  [Test]
  public async Task RabbitMQOptions_DefaultInitialRetryDelay_IsOneSecondAsync() {
    // Arrange & Act
    var options = new RabbitMQOptions();

    // Assert
    await Assert.That(options.InitialRetryDelay).IsEqualTo(TimeSpan.FromSeconds(1));
  }

  [Test]
  public async Task RabbitMQOptions_DefaultMaxRetryDelay_Is120SecondsAsync() {
    // Arrange & Act
    var options = new RabbitMQOptions();

    // Assert
    await Assert.That(options.MaxRetryDelay).IsEqualTo(TimeSpan.FromSeconds(120));
  }

  [Test]
  public async Task RabbitMQOptions_DefaultBackoffMultiplier_IsTwoAsync() {
    // Arrange & Act
    var options = new RabbitMQOptions();

    // Assert
    await Assert.That(options.BackoffMultiplier).IsEqualTo(2.0);
  }

  [Test]
  public async Task RabbitMQOptions_DefaultRetryIndefinitely_IsTrueAsync() {
    // Arrange & Act
    var options = new RabbitMQOptions();

    // Assert
    await Assert.That(options.RetryIndefinitely).IsTrue();
  }

  #endregion

  #region Exponential Backoff Sequence Tests

  [Test]
  public async Task CalculateNextDelay_ExponentialSequence_FollowsExpectedPatternAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromMinutes(5)
    };
    var retry = new RabbitMQConnectionRetry(options);

    // Act - Simulate exponential backoff sequence
    var delay1 = TimeSpan.FromSeconds(1);
    var delay2 = retry.CalculateNextDelay(delay1);
    var delay3 = retry.CalculateNextDelay(delay2);
    var delay4 = retry.CalculateNextDelay(delay3);
    var delay5 = retry.CalculateNextDelay(delay4);

    // Assert
    await Assert.That(delay1).IsEqualTo(TimeSpan.FromSeconds(1));
    await Assert.That(delay2).IsEqualTo(TimeSpan.FromSeconds(2));
    await Assert.That(delay3).IsEqualTo(TimeSpan.FromSeconds(4));
    await Assert.That(delay4).IsEqualTo(TimeSpan.FromSeconds(8));
    await Assert.That(delay5).IsEqualTo(TimeSpan.FromSeconds(16));
  }

  [Test]
  public async Task CalculateNextDelay_ExponentialSequence_CapsAtMaxAsync() {
    // Arrange
    var options = new RabbitMQOptions {
      BackoffMultiplier = 2.0,
      MaxRetryDelay = TimeSpan.FromSeconds(10)
    };
    var retry = new RabbitMQConnectionRetry(options);

    // Act - Simulate exponential backoff that hits the cap
    var delay1 = TimeSpan.FromSeconds(1);
    var delay2 = retry.CalculateNextDelay(delay1);  // 2
    var delay3 = retry.CalculateNextDelay(delay2);  // 4
    var delay4 = retry.CalculateNextDelay(delay3);  // 8
    var delay5 = retry.CalculateNextDelay(delay4);  // 16 -> capped at 10
    var delay6 = retry.CalculateNextDelay(delay5);  // stays at 10

    // Assert
    await Assert.That(delay4).IsEqualTo(TimeSpan.FromSeconds(8));
    await Assert.That(delay5).IsEqualTo(TimeSpan.FromSeconds(10));  // Capped
    await Assert.That(delay6).IsEqualTo(TimeSpan.FromSeconds(10));  // Stays capped
  }

  #endregion

  #region Retry Loop Coverage Tests

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithValidConnectionString_BuildsFactoryAndAttemptsConnectionAsync() {
    // Exercises the connection-string overload: it must build a ConnectionFactory from the URI
    // and delegate to the factory overload. With RetryIndefinitely=false and an unreachable host,
    // the initial attempts are exhausted and a BrokerUnreachableException surfaces.
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false
    };
    var retry = new RabbitMQConnectionRetry(options);

    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync("amqp://invalid-host:5672"))
      .Throws<BrokerUnreachableException>();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_WithinInitialWindow_LogsRetryAttemptAsync() {
    // attempt <= InitialRetryAttempts → the warning-level "Retrying in {DelayMs}ms" log fires
    // for each failing attempt inside the initial window (exercises _logRetryAttempt with a
    // present logger). RetryIndefinitely=false ends the loop after the window.
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 2,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false
    };
    var logger = new RecordingLogger();
    var retry = new RabbitMQConnectionRetry(options, logger);
    var factory = new ConnectionFactory { Uri = new Uri("amqp://invalid-host:5672") };

    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(factory))
      .Throws<BrokerUnreachableException>();

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("Retrying in", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_WhenExhaustedWithLogger_LogsFinalFailureAndRethrowsAsync() {
    // attempt > InitialRetryAttempts && !RetryIndefinitely → _logAndRethrowConnectionFailure:
    // logs the error-level "Giving up" message (with a present logger) and rethrows via
    // ExceptionDispatchInfo.Throw.
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromMilliseconds(1),
      RetryIndefinitely = false
    };
    var logger = new RecordingLogger();
    var retry = new RabbitMQConnectionRetry(options, logger);
    var factory = new ConnectionFactory { Uri = new Uri("amqp://invalid-host:5672") };

    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(factory))
      .Throws<BrokerUnreachableException>();

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("Giving up", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task CreateConnectionWithRetryAsync_IndefiniteRetry_LogsPeriodicStatusAtTenthAttemptAsync() {
    // RetryIndefinitely=true with a tiny delay spins the loop; at attempt % 10 == 0 the
    // periodic "still failing ... Continuing to retry" warning fires (exercises
    // _logIndefiniteRetry). The recording logger cancels the token the first time that warning
    // is seen, so the otherwise-infinite loop terminates deterministically with an
    // OperationCanceledException — no polling, no Task.Delay in the test itself.
    var options = new RabbitMQOptions {
      InitialRetryAttempts = 1,
      InitialRetryDelay = TimeSpan.FromTicks(1),
      MaxRetryDelay = TimeSpan.FromTicks(1),
      BackoffMultiplier = 1.0,
      RetryIndefinitely = true
    };
    using var cts = new CancellationTokenSource();
    var logger = new RecordingLogger { CancelOnStillRetrying = cts };
    var retry = new RabbitMQConnectionRetry(options, logger);
    var factory = new ConnectionFactory { Uri = new Uri("amqp://invalid-host:5672") };

    await Assert.That(async () => await retry.CreateConnectionWithRetryAsync(factory, cts.Token))
      .Throws<OperationCanceledException>();

    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("still failing", StringComparison.Ordinal)))
      .IsTrue();
  }

  #endregion
}
