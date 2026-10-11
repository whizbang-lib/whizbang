// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// Which failures of a stamp call <see cref="PgCommitOrderStamperWorker"/> answers with a smaller batch (#1217): the
/// ones that mean the call ran out of time, and no others.
/// </summary>
/// <remarks>
/// A timed-out call is canceled and rolls back, so the worker halves the batch and retries as leader. Any other
/// failure keeps its existing handling, which gives up the lock and elects again, so the classification has to be
/// exact in both directions: a timeout missed repeats the same size forever, and a broken connection misread as a
/// timeout would retry on a connection that cannot serve it.
/// </remarks>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
/// <tests>Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs</tests>
public class PgCommitOrderStamperWorkerTimeoutTests {
  [Test]
  public async Task IsTimeout_TheClientCommandTimeout_IsATimeoutAsync() =>
    await Assert.That(PgCommitOrderStamperWorker.IsTimeout(
        new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt"))))
      .IsTrue()
      .Because("Npgsql reports its command timeout as an NpgsqlException carrying a TimeoutException, after canceling "
        + "the statement on the server");

  [Test]
  public async Task IsTimeout_TheServerStatementTimeout_IsATimeoutAsync() =>
    await Assert.That(PgCommitOrderStamperWorker.IsTimeout(
        new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)))
      .IsTrue()
      .Because("a statement_timeout set on the role or the database cancels the stamp the same way, with 57014");

  [Test]
  public async Task IsTimeout_ABrokenConnection_IsNotAsync() =>
    await Assert.That(PgCommitOrderStamperWorker.IsTimeout(
        new NpgsqlException("Exception while reading from stream", new System.IO.EndOfStreamException())))
      .IsFalse()
      .Because("a connection that dropped cannot serve a retry; it goes to the outer loop, which opens a new one");

  [Test]
  public async Task IsTimeout_AnotherServerError_IsNotAsync() =>
    await Assert.That(PgCommitOrderStamperWorker.IsTimeout(
        new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)))
      .IsFalse()
      .Because("only a cancellation for time means a smaller batch can finish");
}
