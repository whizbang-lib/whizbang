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
/// <para>
/// Note there is no cancellation test: the check accepts a token and passes it to
/// CreateConnectionAsync, but the probe query itself is issued without one, so a cancelled
/// token does not shorten the call. Asserting the current behaviour would enshrine that.
/// </para>
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
}
