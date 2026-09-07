using System.Data;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Data;
using Whizbang.Data.Dapper.Custom;

namespace Whizbang.Data.Tests;

/// <summary>
/// Coverage for <see cref="DapperSequenceProviderBase.EnsureConnectionOpen"/>. Every existing
/// exerciser of <see cref="DapperSequenceProviderBase"/> (the SQLite contract-test suite in
/// <see cref="DapperSequenceProviderTests"/>) goes through <c>SqliteConnectionFactory</c>, which
/// always hands back an already-open connection — so the "connection was closed" branch has never
/// actually run. Exercised here with a hand-rolled <see cref="IDbConnection"/> fake; no database
/// engine involved at all.
/// </summary>
public class DapperSequenceProviderBaseCoverageTests {

  // If a connection factory ever returns a connection that hasn't been opened yet (a pooled or
  // lazily-opened implementation, unlike the eager SqliteConnectionFactory every other test here
  // exercises), skipping the Open() call would fail every subsequent command on it -- getting the
  // next sequence value would throw instead of allocating a stream's next number.
  [Test]
  public async Task EnsureConnectionOpen_WithClosedConnection_OpensItAsync() {
    var connection = new _fakeDbConnection(ConnectionState.Closed);

    _testableSequenceProvider.CallEnsureConnectionOpen(connection);

    await Assert.That(connection.OpenCallCount).IsEqualTo(1)
      .Because("a closed connection handed to the sequence provider must be opened before any command runs against it");
  }

  // The counterpart: a connection the factory already opened must not be re-opened. Re-opening an
  // already-open ADO.NET connection is at best wasted work and at worst throws, depending on the
  // provider -- the guard exists specifically to avoid calling Open() unconditionally.
  [Test]
  public async Task EnsureConnectionOpen_WithAlreadyOpenConnection_DoesNotReopenItAsync() {
    var connection = new _fakeDbConnection(ConnectionState.Open);

    _testableSequenceProvider.CallEnsureConnectionOpen(connection);

    await Assert.That(connection.OpenCallCount).IsEqualTo(0)
      .Because("an already-open connection must be left alone -- calling Open() again is redundant at best and provider-dependent-unsafe at worst");
  }

  /// <summary>Exists solely to reach the protected static <c>EnsureConnectionOpen</c> under test; never instantiated.</summary>
  private sealed class _testableSequenceProvider : DapperSequenceProviderBase {
    public _testableSequenceProvider(IDbConnectionFactory connectionFactory, IDbExecutor executor)
      : base(connectionFactory, executor) { }

    protected override string GetUpdateSequenceSql() => "";
    protected override string GetInsertOrUpdateSequenceSql() => "";
    protected override string GetCurrentSequenceSql() => "";
    protected override string GetResetSequenceSql() => "";

    public static void CallEnsureConnectionOpen(IDbConnection connection) => EnsureConnectionOpen(connection);
  }

  private sealed class _fakeDbConnection(ConnectionState initialState) : IDbConnection {
    public int OpenCallCount { get; private set; }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public string ConnectionString { get; set; } = "";
    public int ConnectionTimeout => 0;
    public string Database => "";
    public ConnectionState State { get; private set; } = initialState;

    public void Open() {
      OpenCallCount++;
      State = ConnectionState.Open;
    }

    public void Close() => State = ConnectionState.Closed;
    public void ChangeDatabase(string databaseName) { }
    public IDbTransaction BeginTransaction() => throw new NotSupportedException();
    public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException();
    public IDbCommand CreateCommand() => throw new NotSupportedException();
    public void Dispose() { }
  }
}
