#pragma warning disable CA1859 // tests deliberately hold fakes behind their interface types

using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Whizbang.Core.Data;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Targeted, no-database coverage for <see cref="Whizbang.Data.Dapper.Custom.DapperRequestResponseStoreBase"/>
/// branches the contract-test suite and the integration suites never reach: the closed-connection open
/// path, the non-generic AOT guard, the "row not found" and pre-canceled-token early returns, and the
/// plain-<see cref="OperationCanceledException"/> catch clause (distinct from the
/// <see cref="TaskCanceledException"/> one). Every test here drives
/// <see cref="DapperPostgresRequestResponseStore"/> — a concrete subclass reachable transitively through
/// this project's reference to Whizbang.Data.Dapper.Postgres — against hand-rolled
/// <see cref="IDbConnectionFactory"/>/<see cref="IDbExecutor"/> fakes, never a real database.
/// </summary>
public class DapperRequestResponseStoreBaseCoverageTests {

  private sealed record _testMessage;

  // ── Fakes ────────────────────────────────────────────────────────────────

  /// <summary>Minimal <see cref="IDbConnection"/> that tracks whether/how many times it was opened.</summary>
  private sealed class _fakeConnection : IDbConnection {
    private string _connectionString = "";

    [AllowNull]
    string IDbConnection.ConnectionString {
      get => _connectionString;
      set => _connectionString = value ?? "";
    }

    public ConnectionState State { get; private set; } = ConnectionState.Closed;

    public int OpenCallCount { get; private set; }

    public int ConnectionTimeout => 0;

    public string Database => "";

    public IDbTransaction BeginTransaction() => throw new NotSupportedException("not exercised by these tests");

    public IDbTransaction BeginTransaction(IsolationLevel il) => throw new NotSupportedException("not exercised by these tests");

    public void ChangeDatabase(string databaseName) { }

    public void Close() => State = ConnectionState.Closed;

    public IDbCommand CreateCommand() => throw new NotSupportedException("not exercised by these tests");

    public void Open() {
      OpenCallCount++;
      State = ConnectionState.Open;
    }

    public void Dispose() { }
  }

  /// <summary>Always hands back the same (initially closed) connection.</summary>
  private sealed class _singleConnectionFactory(IDbConnection connection) : IDbConnectionFactory {
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
      Task.FromResult(connection);
  }

  /// <summary>Fails every call — proves a code path never actually reaches connection acquisition.</summary>
  private sealed class _throwingConnectionFactory : IDbConnectionFactory {
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
      Task.FromException<IDbConnection>(
        new InvalidOperationException("This factory must never be called by the scenario under test."));
  }

  /// <summary>Simulates a cancellation surfaced as a plain <see cref="OperationCanceledException"/>
  /// rather than a <see cref="TaskCanceledException"/> — a different exception shape the base class
  /// catches in a separate clause.</summary>
  private sealed class _operationCanceledConnectionFactory : IDbConnectionFactory {
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
      Task.FromException<IDbConnection>(
        new OperationCanceledException("Simulated cancellation not surfaced as TaskCanceledException."));
  }

  /// <summary>Benign no-op executor: satisfies every <see cref="IDbExecutor"/> member the base class
  /// might call, always reporting "no row" / "one row affected" without touching a database.</summary>
  private sealed class _fakeExecutor : IDbExecutor {
    public Task<IReadOnlyList<T>> QueryAsync<T>(
        IDbConnection connection, string sql, object? param = null,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("DapperRequestResponseStoreBase never calls QueryAsync.");

    public Task<T?> QuerySingleOrDefaultAsync<T>(
        IDbConnection connection, string sql, object? param = null,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
      Task.FromResult<T?>(default);

    public Task<int> ExecuteAsync(
        IDbConnection connection, string sql, object? param = null,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(1);

    public Task<T?> ExecuteScalarAsync<T>(
        IDbConnection connection, string sql, object? param = null,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("DapperRequestResponseStoreBase never calls ExecuteScalarAsync.");
  }

  // ── EnsureConnectionOpen + "row not found" (lines 41-42, 133) ──────────────

  [Test]
  public async Task WaitForResponseAsync_UnknownCorrelationId_OpensClosedConnectionAndReturnsNullAsync() {
    // A request/response store must treat "no row for this correlation id" as a routine miss — a
    // request that legitimately never existed, or was already cleaned up. If this regressed into an
    // exception, a caller polling for a response that was reaped by TTL cleanup would see an unhandled
    // fault instead of the clean "not found" signal the contract promises. This also proves the store
    // opens a connection the factory hands back closed, per IDbConnectionFactory's own contract that
    // opening is the caller's responsibility.
    var connection = new _fakeConnection();
    var store = new DapperPostgresRequestResponseStore(
      new _singleConnectionFactory(connection), new _fakeExecutor(), new JsonSerializerOptions());

    var result = await store.WaitForResponseAsync<_testMessage>(CorrelationId.New());

    await Assert.That(result).IsNull()
      .Because("an unknown correlation id must resolve to null, not throw.");
    await Assert.That(connection.OpenCallCount).IsEqualTo(1)
      .Because("EnsureConnectionOpen must open a connection the factory returned closed, before it is used.");
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Open)
      .Because("the connection must actually be left open after EnsureConnectionOpen runs.");
  }

  // ── Pre-canceled token short-circuit (line 152) ─────────────────────────

  [Test]
  public async Task WaitForResponseAsync_TokenAlreadyCanceled_ReturnsNullWithoutOpeningAConnectionAsync() {
    // The polling loop checks cancellation before each attempt. If this regressed, a caller whose
    // token was already canceled before the very first attempt would still pay for a connection
    // acquisition and a query whose result could never be used — wasted work on a request that was
    // already abandoned.
    var store = new DapperPostgresRequestResponseStore(
      new _throwingConnectionFactory(), new _fakeExecutor(), new JsonSerializerOptions());
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    var result = await store.WaitForResponseAsync<_testMessage>(CorrelationId.New(), cts.Token);

    await Assert.That(result).IsNull()
      .Because("an already-canceled wait must resolve to null instead of throwing or hanging — " +
        "and the throwing connection factory proves the loop body never ran.");
  }

  // ── Plain OperationCanceledException catch clause (lines 156, 158) ──────

  [Test]
  public async Task WaitForResponseAsync_ConnectionAcquisitionThrowsPlainOperationCanceled_ReturnsNullAsync() {
    // TaskCanceledException and a bare OperationCanceledException are caught in two separate clauses.
    // If the plain-OperationCanceledException clause regressed, a cancellation surfaced through any
    // path other than Task's own cancellation machinery (a driver throwing it directly, for example)
    // would escape as an unhandled exception instead of resolving to null like every other
    // cancellation shape the contract promises.
    var store = new DapperPostgresRequestResponseStore(
      new _operationCanceledConnectionFactory(), new _fakeExecutor(), new JsonSerializerOptions());

    var result = await store.WaitForResponseAsync<_testMessage>(CorrelationId.New());

    await Assert.That(result).IsNull()
      .Because("a plain OperationCanceledException from connection acquisition must be swallowed, not rethrown.");
  }

  // ── Non-generic AOT guard (lines 105-108) ───────────────────────────────

  [Test]
  public async Task WaitForResponseAsync_NonGenericOverload_ThrowsNotSupportedAsync() {
    // The non-generic overload exists only to fail loudly and immediately, because deserializing an
    // unknown message type without reflection isn't possible in an AOT scenario. If this regressed
    // into silently returning null (or attempting a reflection-based deserialization), a caller who
    // reached it by mistake would get a confusing failure far from its actual cause instead of being
    // pointed straight at the generic overload.
    var store = new DapperPostgresRequestResponseStore(
      new _throwingConnectionFactory(), new _fakeExecutor(), new JsonSerializerOptions());

    await Assert.That(() => store.WaitForResponseAsync(CorrelationId.New()))
      .Throws<NotSupportedException>();
  }
}
