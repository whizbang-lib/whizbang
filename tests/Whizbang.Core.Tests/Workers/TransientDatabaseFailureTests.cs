using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The classifier every worker loop asks before deciding that a database failure is the kind that
/// passes: a deadlock, a serialization failure, a canceled statement, a lock timeout, a lost
/// connection, exhausted resources, a command timeout the provider wrapped, or anything the provider
/// itself marks transient. Everything else is a defect the loop reports as such.
/// </summary>
public class TransientDatabaseFailureTests {
  [Test]
  [Arguments("40P01", TransientDatabaseFailure.DEADLOCK)]
  [Arguments("40001", TransientDatabaseFailure.SERIALIZATION_FAILURE)]
  [Arguments("57014", TransientDatabaseFailure.STATEMENT_CANCELED)]
  [Arguments("55P03", TransientDatabaseFailure.LOCK_TIMEOUT)]
  [Arguments("08006", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("08001", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("57P01", TransientDatabaseFailure.CONNECTION_LOST)]
  [Arguments("53300", TransientDatabaseFailure.INSUFFICIENT_RESOURCES)]
  public async Task ASqlStateThatPasses_ClassifiesByItsReasonAsync(string sqlState, string reason) {
    var found = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState(sqlState), out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(reason);
    await Assert.That(failure.SqlState).IsEqualTo(sqlState);
    await Assert.That(failure.Cause).IsTypeOf<FakeDbException>();
  }

  [Test]
  public async Task AProviderTransientFlag_PassesWithoutAKnownSqlStateAsync() {
    var found = TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState("XX000", isTransient: true), out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(TransientDatabaseFailure.PROVIDER_TRANSIENT);
  }

  [Test]
  public async Task ACommandTimeoutWrappedByTheProvider_IsACommandTimeoutAsync() {
    // The shape a provider gives a command timeout: a database exception with no SQLSTATE whose
    // inner exception is the timeout on the stream.
    var wrapped = FakeDbException.WithSqlState(null, message: "Exception while reading from stream",
      inner: new TimeoutException("Timeout during reading attempt"));

    var found = TransientDatabaseFailure.TryClassify(wrapped, out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(TransientDatabaseFailure.COMMAND_TIMEOUT);
    await Assert.That(failure.SqlState).IsNull();
  }

  [Test]
  public async Task ALostSocketBeneathTheProvider_IsALostConnectionAsync() {
    var wrapped = FakeDbException.WithSqlState(null, inner: new IOException("broken pipe", new SocketException()));

    var found = TransientDatabaseFailure.TryClassify(wrapped, out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(TransientDatabaseFailure.CONNECTION_LOST);
  }

  [Test]
  public async Task ADeadlockInsideAnAggregateOrAWrapper_IsStillFoundAsync() {
    var deadlock = FakeDbException.WithSqlState("40P01");
    var wrapped = new InvalidOperationException("apply failed",
      new AggregateException(new InvalidOperationException("other"), deadlock));

    var found = TransientDatabaseFailure.TryClassify(wrapped, out var failure);

    await Assert.That(found).IsTrue();
    await Assert.That(failure!.Reason).IsEqualTo(TransientDatabaseFailure.DEADLOCK);
    await Assert.That(ReferenceEquals(failure.Cause, deadlock)).IsTrue();
  }

  [Test]
  public async Task AConstraintViolation_IsNotTransientAsync() {
    await Assert.That(TransientDatabaseFailure.IsTransient(FakeDbException.WithSqlState("23505"))).IsFalse();
    await Assert.That(TransientDatabaseFailure.TryClassify(FakeDbException.WithSqlState("42P01"), out var failure)).IsFalse();
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task ATimeoutOutsideTheProvider_IsNotADatabaseFailureAsync() {
    // A wait that timed out in application code is not the database failing; only a timeout the
    // provider raised counts.
    await Assert.That(TransientDatabaseFailure.IsTransient(new TimeoutException("a wait elapsed"))).IsFalse();
    await Assert.That(TransientDatabaseFailure.IsTransient(new IOException("a file"))).IsFalse();
    await Assert.That(TransientDatabaseFailure.IsTransient(new InvalidOperationException("a bug"))).IsFalse();
  }

  [Test]
  public async Task ACancellation_IsNeverTransientAsync() {
    await Assert.That(TransientDatabaseFailure.IsTransient(new OperationCanceledException())).IsFalse();
  }

  [Test]
  public async Task ANullException_IsRefusedAsync() {
    await Assert.That(() => TransientDatabaseFailure.TryClassify(null!, out _)).Throws<ArgumentNullException>();
    await Assert.That(() => TransientDatabaseFailure.IsTransient(null!)).Throws<ArgumentNullException>();
  }
}
