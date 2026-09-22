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

  /// <summary>
  /// An aggregate holding nothing the database raised is not transient, however many faults it
  /// carries.
  /// </summary>
  /// <remarks>
  /// The other side of the search through an aggregate, and the answer that decides whether a loop
  /// treats a failure as the database's doing or reports it as a defect. Answering "transient"
  /// because an aggregate was the outer shape would back off over a defect and report it as the
  /// database's, which is a page for something no operator can fix.
  /// </remarks>
  [Test]
  public async Task AnAggregateOfFailuresThatAreNotTheDatabases_IsNotTransientAsync() {
    var aggregate = new AggregateException(
      new InvalidOperationException("one defect"),
      new ArgumentException("another defect"));

    var found = TransientDatabaseFailure.TryClassify(aggregate, out var failure);

    await Assert.That(found).IsFalse()
      .Because("every fault in it was the process's own, so there is nothing to wait out");
    await Assert.That(failure).IsNull();
    await Assert.That(TransientDatabaseFailure.IsTransient(aggregate)).IsFalse();
  }

  /// <summary>An empty aggregate is not transient either, and does not throw on the way to saying so.</summary>
  [Test]
  public async Task AnEmptyAggregate_IsNotTransientAsync() =>
    await Assert.That(TransientDatabaseFailure.IsTransient(new AggregateException())).IsFalse()
      .Because("a loop must get an answer for whatever shape it caught, including an empty one");

  /// <summary>
  /// An inner chain that holds neither a timeout nor a socket failure leaves the failure the
  /// database's own answer, which here is "not transient".
  /// </summary>
  /// <remarks>
  /// The walk down the inner exceptions exists for the providers that bury a command timeout or a
  /// dropped socket under an exception of their own. Reaching the end of that chain having found
  /// neither has to mean "not the database's doing": were the walk to answer transient for any
  /// wrapped exception, a defect wrapped by a provider would read as transient and a loop would
  /// back off over it for ever, reporting a database problem nobody can fix.
  /// </remarks>
  [Test]
  public async Task AnInnerChainWithoutATimeoutOrASocketFailureIsNotTransientAsync() {
    var db = FakeDbException.WithSqlState(
      "42601", isTransient: false, message: "syntax error", inner: new InvalidOperationException("a defect"));

    var found = TransientDatabaseFailure.TryClassify(db, out var failure);

    await Assert.That(found).IsFalse()
      .Because("the SQLSTATE names no transient class and the chain holds nothing the walk looks for");
    await Assert.That(failure).IsNull();
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
