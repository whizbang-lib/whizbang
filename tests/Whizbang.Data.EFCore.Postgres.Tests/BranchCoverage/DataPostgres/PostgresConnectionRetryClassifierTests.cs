// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Net.Sockets;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// Every arm of the startup retry's transient classifier. A transient error misread as permanent
/// fails a pod's startup on a database that is merely restarting; a permanent error misread as
/// transient retries a bad password or a syntax error until the startup budget runs out and hides the
/// real cause behind a timeout.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresConnectionRetry.cs</code-under-test>
[Category("Shard5")]
public class PostgresConnectionRetryClassifierTests {

  [Test]
  public async Task NpgsqlException_ThatNpgsqlItselfCallsTransient_IsTransientAsync() {
    var ex = new NpgsqlException("server closed", new TimeoutException());

    await Assert.That(ex.IsTransient).IsTrue()
      .Because("the precondition: Npgsql flags a failure caused by a timeout as transient");
    await Assert.That(PostgresConnectionRetry.IsTransientException(ex)).IsTrue();
  }

  [Test]
  [Arguments("lost the Connection to the server")]
  [Arguments("Timeout during reading attempt")]
  [Arguments("the server refused the attempt")]
  public async Task NpgsqlException_WhoseMessageNamesAConnectionFailure_IsTransientAsync(string message) {
    var ex = new NpgsqlException(message);

    await Assert.That(ex.IsTransient).IsFalse()
      .Because("the precondition: only the message marks this one as connection-related");
    await Assert.That(PostgresConnectionRetry.IsTransientException(ex)).IsTrue();
  }

  [Test]
  public async Task NpgsqlException_ThatIsNeitherFlaggedNorConnectionRelated_IsNotTransientAsync() {
    var ex = new NpgsqlException("syntax error at or near SELEC");

    await Assert.That(PostgresConnectionRetry.IsTransientException(ex)).IsFalse()
      .Because("retrying a statement that can never succeed only delays the real error");
  }

  [Test]
  public async Task WrapperAroundATransientCause_IsTransientAsync() {
    var ex = new InvalidOperationException("startup step failed", new SocketException((int)SocketError.ConnectionRefused));

    await Assert.That(PostgresConnectionRetry.IsTransientException(ex)).IsTrue()
      .Because("the classifier looks through wrappers to the cause");
  }

  [Test]
  public async Task WrapperAroundAPermanentCause_IsNotTransientAsync() {
    var ex = new InvalidOperationException("startup step failed", new ArgumentException("bad option"));

    await Assert.That(PostgresConnectionRetry.IsTransientException(ex)).IsFalse();
  }

  [Test]
  public async Task PermanentExceptionWithNoCause_IsNotTransientAsync() {
    await Assert.That(PostgresConnectionRetry.IsTransientException(new InvalidOperationException("no"))).IsFalse();
  }
}
