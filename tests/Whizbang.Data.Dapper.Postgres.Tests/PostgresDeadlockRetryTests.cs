using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Unit tests for PostgresDeadlockRetry utility.
/// Uses Npgsql's public PostgresException constructor to simulate deadlocks.
/// </summary>
public class PostgresDeadlockRetryTests {

  private static PostgresException _createDeadlockException() =>
    new("deadlock detected", "ERROR", "ERROR", "40P01");

  private static PostgresException _createOtherPostgresException() =>
    new("relation does not exist", "ERROR", "ERROR", "42P01");

  [Test]
  public async Task ExecuteAsync_NoException_RunsOnceAsync() {
    var callCount = 0;
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      callCount++;
      await Task.CompletedTask;
    });

    await Assert.That(callCount).IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_DeadlockOnFirstAttempt_RetriesAndSucceedsAsync() {
    var callCount = 0;
    await PostgresDeadlockRetry.ExecuteAsync(async () => {
      callCount++;
      if (callCount == 1) {
        throw _createDeadlockException();
      }
      await Task.CompletedTask;
    });

    await Assert.That(callCount).IsEqualTo(2);
  }

  [Test]
  public async Task ExecuteAsync_DeadlockExhaustsAttempts_ThrowsOriginalExceptionAsync() {
    var callCount = 0;
    var act = async () => {
      await PostgresDeadlockRetry.ExecuteAsync(async () => {
        callCount++;
        await Task.CompletedTask;
        throw _createDeadlockException();
      }, maxAttempts: 3);
    };

    await Assert.That(act).ThrowsExactly<PostgresException>();
    await Assert.That(callCount).IsEqualTo(3);
  }

  [Test]
  public async Task ExecuteAsync_NonDeadlockException_DoesNotRetryAsync() {
    var callCount = 0;
    var act = async () => {
      await PostgresDeadlockRetry.ExecuteAsync(async () => {
        callCount++;
        await Task.CompletedTask;
        throw _createOtherPostgresException();
      });
    };

    await Assert.That(act).ThrowsExactly<PostgresException>();
    await Assert.That(callCount).IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_WithReturnValue_ReturnsResultOnRetryAsync() {
    var callCount = 0;
    var result = await PostgresDeadlockRetry.ExecuteAsync(async () => {
      callCount++;
      if (callCount == 1) {
        throw _createDeadlockException();
      }
      await Task.CompletedTask;
      return 42;
    });

    await Assert.That(result).IsEqualTo(42);
    await Assert.That(callCount).IsEqualTo(2);
  }

  [Test]
  public async Task ExecuteAsync_CancellationToken_HonoredBetweenRetriesAsync() {
    using var cts = new CancellationTokenSource();
    var callCount = 0;

    var act = async () => {
      await PostgresDeadlockRetry.ExecuteAsync(async () => {
        callCount++;
        if (callCount == 1) {
          cts.Cancel(); // Cancel before retry delay
          throw _createDeadlockException();
        }
        await Task.CompletedTask;
      }, cancellationToken: cts.Token);
    };

    await Assert.That(act).Throws<OperationCanceledException>();
    await Assert.That(callCount).IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_NonPostgresException_DoesNotRetryAsync() {
    var callCount = 0;
    var act = async () => {
      await PostgresDeadlockRetry.ExecuteAsync(async () => {
        callCount++;
        await Task.CompletedTask;
        throw new InvalidOperationException("not a postgres error");
      });
    };

    await Assert.That(act).ThrowsExactly<InvalidOperationException>();
    await Assert.That(callCount).IsEqualTo(1);
  }
}
