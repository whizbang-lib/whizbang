using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IPinnedConnectionPool"/>.
/// Holds <see cref="WhizbangPinnedPoolOptions.Size"/> long-lived
/// <see cref="NpgsqlConnection"/> instances open against the configured
/// direct PG endpoint (NOT pgbouncer). Workers borrow per transaction and
/// return on dispose; the connections themselves stay open for
/// <see cref="WhizbangPinnedPoolOptions.ConnectionLifetimeSeconds"/> before
/// being recycled.
/// </summary>
/// <remarks>
/// <para>
/// Worker eligibility is delegated to <see cref="PinnedWorkerRegistry"/>
/// using the snapshotted <see cref="WhizbangPinnedPoolOptions"/>. Ineligible
/// callers receive a no-op borrow with a <c>null</c> connection so the
/// borrow-site code reads identically regardless of eligibility.
/// </para>
/// <para>
/// The pool is realised by Npgsql's own
/// <c>NpgsqlDataSource</c> with <c>MinPoolSize=MaxPoolSize=Size</c> — letting
/// the SDK handle physical connection management, keepalives, and recycle
/// timing instead of reimplementing them. <c>NpgsqlDataSource.OpenConnectionAsync</c>
/// blocks (honouring the supplied CT) when the pool is at capacity.
/// </para>
/// </remarks>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public sealed class PinnedConnectionPool : IPinnedConnectionPool, IAsyncDisposable {
  private readonly WhizbangPinnedPoolOptions _options;
  private readonly PinnedWorkerRegistry _registry;
  private readonly ILogger<PinnedConnectionPool>? _logger;
  private readonly NpgsqlDataSource _dataSource;
  private bool _disposed;

  /// <summary>Builds the pool from options + worker registry. The Npgsql connection pool is constructed once and reused for the pool's lifetime.</summary>
  /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="registry"/> is null.</exception>
  /// <exception cref="InvalidOperationException">Options are misconfigured (no connection string set when constructing the real pool).</exception>
  public PinnedConnectionPool(
      WhizbangPinnedPoolOptions options,
      PinnedWorkerRegistry registry,
      ILogger<PinnedConnectionPool>? logger = null) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(registry);

    _options = options;
    _registry = registry;
    _logger = logger;

    if (string.IsNullOrWhiteSpace(options.ConnectionString)) {
      throw new InvalidOperationException(
        "PinnedConnectionPool requires a non-empty WhizbangPinnedPoolOptions.ConnectionString; " +
        "register NoOpPinnedConnectionPool when the pool is disabled instead.");
    }

    var poolSize = options.Size < 1 ? 1 : options.Size;
    var connStrBuilder = new NpgsqlConnectionStringBuilder(options.ConnectionString) {
      MinPoolSize = poolSize,
      MaxPoolSize = poolSize,
      ConnectionLifetime = options.ConnectionLifetimeSeconds,
    };
    _dataSource = NpgsqlDataSource.Create(connStrBuilder.ConnectionString);
  }

  /// <inheritdoc />
  public async ValueTask<IBorrowedConnection> TryPinForAsync(Type workerType, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(workerType);
    ObjectDisposedException.ThrowIf(_disposed, this);
    cancellationToken.ThrowIfCancellationRequested();

    if (!_registry.IsEligible(workerType, _options)) {
      return new _noOpBorrow();
    }

    var borrowDeadline = _options.BorrowTimeoutMilliseconds > 0
      ? _options.BorrowTimeoutMilliseconds
      : Timeout.Infinite;
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    if (borrowDeadline > 0) {
      linked.CancelAfter(borrowDeadline);
    }

    NpgsqlConnection conn;
    try {
      conn = await _dataSource.OpenConnectionAsync(linked.Token).ConfigureAwait(false);
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      // Caller's CT didn't fire — the borrow timeout deadline did. Surface as OCE
      // rooted at the original CT so callers don't confuse a pool-starvation timeout
      // with their own cancellation.
      throw new OperationCanceledException(
        $"Pinned pool borrow timed out after {_options.BorrowTimeoutMilliseconds} ms (worker={workerType.Name}). " +
        "Raise WhizbangPinnedPoolOptions.Size or investigate worker that's holding too long.");
    }

    return new _activeBorrow(conn);
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    await _dataSource.DisposeAsync().ConfigureAwait(false);
  }

  /// <summary>Borrow handle for an ineligible worker; <see cref="Connection"/> is always null and dispose is a no-op.</summary>
  private sealed class _noOpBorrow : IBorrowedConnection {
    public DbConnection? Connection => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  /// <summary>Borrow handle for an eligible worker; <see cref="Connection"/> is the borrowed Npgsql conn; dispose returns it to the pool.</summary>
  private sealed class _activeBorrow : IBorrowedConnection {
    private readonly NpgsqlConnection _conn;
    private bool _disposed;

    public _activeBorrow(NpgsqlConnection conn) {
      _conn = conn;
    }

    public DbConnection? Connection => _conn;

    public async ValueTask DisposeAsync() {
      if (_disposed) {
        return;
      }
      _disposed = true;
      await _conn.DisposeAsync().ConfigureAwait(false);
    }
  }
}
