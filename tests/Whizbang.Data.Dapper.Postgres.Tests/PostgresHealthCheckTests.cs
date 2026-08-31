using System.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresHealthCheck"/>. The healthy path is only
/// meaningful against a real server — the check exists to prove the database answers a
/// query, so faking the connection would assert nothing about the thing it reports on.
/// </summary>
[Category("Integration")]
public class PostgresHealthCheckTests : PostgresTestBase {

  /// <summary>A factory whose connections always fail to open, standing in for a server
  /// that is down or a connection string that no longer resolves.</summary>
  private sealed class FailingConnectionFactory(Exception failure) : IDbConnectionFactory {
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
      => Task.FromException<IDbConnection>(failure);
  }

  [Test]
  public async Task CheckHealthAsync_AgainstALiveDatabase_ReportsHealthyAsync() {
    var check = new PostgresHealthCheck(ConnectionFactory);

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
    await Assert.That(result.Description).Contains("accessible");
  }

  [Test]
  public async Task CheckHealthAsync_WhenTheConnectionFails_ReportsUnhealthyWithTheCauseAsync() {
    // The exception has to survive onto the result: an unhealthy report with no cause
    // tells an operator the database is down but nothing about why.
    var failure = new InvalidOperationException("server is down");
    var check = new PostgresHealthCheck(new FailingConnectionFactory(failure));

    var result = await check.CheckHealthAsync(new HealthCheckContext());

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Exception).IsSameReferenceAs(failure);
  }


  [Test]
  public async Task Constructor_WithNullFactory_ThrowsAsync() {
    await Assert.That(() => new PostgresHealthCheck(null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CheckHealthAsync_WithACancelledToken_ReportsUnhealthyAsync() {
    // Regression lock. The probe query used to be issued through Dapper's string overload,
    // which takes no token, so a cancelled probe ran to completion regardless — during
    // shutdown that means waiting on a server that may already be unreachable, with the
    // health pipeline held open behind it. Opening the connection alone is not enough of a
    // check: a pooled connection is handed back synchronously and never observes the token.
    var check = new PostgresHealthCheck(ConnectionFactory);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var result = await check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
    await Assert.That(result.Exception).IsNotNull();
  }
}
