#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.Dapper.Postgres.Tests.Collective;

/// <summary>
/// Unit tests (no database) for what both drivers read off a contended apply lock: whether PostgreSQL
/// refused the wait, and which table the batch was waiting on.
/// </summary>
/// <remarks>
/// Both answers decide what a caller does next. Mistaking a refused lock wait for a failed apply counts
/// an attempt against the work item and moves it toward dead-lettering, when nothing was wrong with the
/// event; and the table is the first thing an operator asks about contention. The contended paths
/// themselves are covered by each driver's integration test, which can hold a real lock — these lock the
/// arms that a live apply reaches only when something else has already gone wrong.
/// </remarks>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveApplyContentionTests {

  private static PostgresException _lockNotAvailable() =>
    new(messageText: "canceling statement due to lock timeout", severity: "ERROR",
      invariantSeverity: "ERROR", sqlState: "55P03");

  [Test]
  public async Task IsLockTimeout_TheDriversRefusal_IsRecognizedAsync() {
    await Assert.That(CollectiveApplyContention.IsLockTimeout(_lockNotAvailable())).IsTrue()
      .Because("55P03 is PostgreSQL refusing a wait that ran past lock_timeout, which is a busy lock "
             + "and not a failed apply");
  }

  [Test]
  public async Task IsLockTimeout_WrappedAtAnyDepth_IsStillRecognizedAsync() {
    // Entity Framework hands back its own exception with the driver's underneath, so the check cannot
    // look only at the top: a wrapped refusal read as a failure is the bug this guards.
    var wrapped = new InvalidOperationException("an error occurred while saving",
      new InvalidOperationException("execution failed", _lockNotAvailable()));

    await Assert.That(CollectiveApplyContention.IsLockTimeout(wrapped)).IsTrue()
      .Because("the refusal is the same refusal however deep the provider wrapped it");
  }

  [Test]
  public async Task IsLockTimeout_AnyOtherFailure_IsNotABusyLockAsync() {
    await Assert.That(CollectiveApplyContention.IsLockTimeout(
        new PostgresException(messageText: "deadlock detected", severity: "ERROR",
          invariantSeverity: "ERROR", sqlState: "40P01"))).IsFalse()
      .Because("another SQLSTATE is a real failure; reporting it as a busy lock would drop it silently");
    await Assert.That(CollectiveApplyContention.IsLockTimeout(new TimeoutException())).IsFalse()
      .Because("a client-side timeout is not the server refusing a lock wait");
    await Assert.That(CollectiveApplyContention.IsLockTimeout(null)).IsFalse()
      .Because("no exception is no refusal");
  }

  [Test]
  [Arguments("SELECT id FROM wh_per_job WHERE scope->>'t' = @p_t ORDER BY id LIMIT 1000", "wh_per_job")]
  [Arguments("SELECT id\n  FROM wh_per_job\n  WHERE id > @wb_lastid", "wh_per_job")]
  [Arguments("SELECT id FROM wh_per_job", "wh_per_job")]
  public async Task TableOf_TheBatchesSelect_NamesTheTableAsync(string selectSql, string expected) {
    await Assert.That(CollectiveApplyContention.TableOf(selectSql)).IsEqualTo(expected)
      .Because("the message names where the contention is, whatever whitespace follows the table");
  }

  [Test]
  public async Task TableOf_AStatementWithNoFrom_SaysSoRatherThanFailingAsync() {
    // Reached only if the applier's own SELECT ever stops looking like one. A placeholder in the message
    // still reports the busy lock; an exception raised while explaining one would lose it entirely.
    await Assert.That(CollectiveApplyContention.TableOf("SELECT 1")).IsEqualTo("(unknown)")
      .Because("a refusal reported with a placeholder beats a refusal lost to a parsing failure");
    await Assert.That(CollectiveApplyContention.TableOf(null)).IsEqualTo("(unknown)")
      .Because("same reason: there is nothing to parse and still a refusal to report");
  }
}
